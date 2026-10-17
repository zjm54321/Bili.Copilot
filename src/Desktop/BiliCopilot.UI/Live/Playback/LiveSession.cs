#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live.Playback;

/// <summary>
/// UI-dispatcher admission, serialized media operations, independently owned chat/send tasks.
/// A session never migrates accounts or rooms. Refresh replaces only its own room generation.
/// Public commands other than CloseAsync must be called on the supplied dispatcher.
/// </summary>
public sealed class LiveSession : ILiveSession
{
    private readonly IClassicPlaybackBackend _backend;
    private readonly Func<ClassicPlaybackRequest, CancellationToken, Task<ClassicSourceResult>> _resolve;
    private readonly ILiveChatClient _chat;
    private readonly Func<LiveAccountStamp> _account;
    private readonly DispatcherQueue _dispatcher;
    private readonly Guid _id = Guid.NewGuid();
    private readonly ObservableCollection<LiveChatMessage> _messages = new();
    // D5: CompleteCloseAsync drains cancellation/media/connect/send/owner disposal/account unregister before disposing this shutdown CTS.
    #pragma warning disable CA2213
    private readonly CancellationTokenSource _shutdown = new();
    #pragma warning restore CA2213
    private readonly object _closeSync = new();
    private readonly object _inboxSync = new();
    private readonly Queue<(Scope Scope, LiveChatMessage Message)> _inbox = new();
    private (Scope Scope, ClassicBackendStatus Status)? _backendStatus;
    private (Scope Scope, LiveChatStatus Status)? _chatStatus;
    private (Scope Scope, LiveAudienceUpdate Update)? _audience;
    private LiveAudienceMetric? _audienceMetric;
    private bool _inboxPosted;
    private Task _tail = Task.CompletedTask;
    private Task? _refresh;
    private Task? _close;
    private Task _cancelShutdown = Task.CompletedTask;
    // D6: Replacement retains the prior scope; StopScopeCoreAsync/terminal close disposes it after actual work, and Scope.Dispose releases its CTS.
    #pragma warning disable CA2213
    private Scope? _scope;
    #pragma warning restore CA2213
    private LiveInformation? _live;
    private LiveAccountStamp? _stamp;
    private CancellationTokenRegistration _accountRegistration;
    private long _generation;
    private int? _preferredQuality;
    private bool _sending;
    private bool _controlPending;
    private bool _releaseBlocked;

    public LiveSession(IClassicPlaybackBackend backend, ClassicMediaSourceService sources,
        ILiveChatClient chat, Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
        : this(backend, BindSources(sources), chat, account, dispatcher) { }

    // Narrow fake-resolver seam; production always binds the unchanged Classic source service.
    internal LiveSession(IClassicPlaybackBackend backend,
        Func<ClassicPlaybackRequest, CancellationToken, Task<ClassicSourceResult>> resolve,
        ILiveChatClient chat, Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (backend.Kind != ClassicBackendKind.Island)
            throw new InvalidOperationException("Live requires the Island backend.");
        Messages = new(_messages);
        SelectQualityCommand = new AsyncRelayCommand<PlayerFormatInformation>(SelectQualityFromCommandAsync,
            quality => CanChangeQuality && quality is not null && Qualities.Any(item => item.Quality == quality.Quality));
    }

    private static Func<ClassicPlaybackRequest, CancellationToken, Task<ClassicSourceResult>> BindSources(ClassicMediaSourceService sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Preferences.AutoLoadHistory || sources.Preferences.AutoPlayRecommendations || sources.Preferences.ReportProgress)
            throw new InvalidOperationException("Live source preferences must disable history, recommendations and reporting.");
        return (request, token) => sources.ResolveAsync(request, ClassicBackendKind.Island, token);
    }

    public LiveBinding? Binding { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string AnchorName { get; private set; } = string.Empty;
    public Uri? AnchorAvatar { get; private set; }
    public bool? IsFollowing { get; private set; }
    public long? AudienceCount { get; private set; }
    public string AudienceLabel => _audienceMetric == LiveAudienceMetric.OnlineRank ? "高能在线" : "看过";
    public IReadOnlyList<PlayerFormatInformation> Qualities { get; private set; } = Array.Empty<PlayerFormatInformation>();
    public PlayerFormatInformation? SelectedQuality { get; private set; }
    public bool CanChangeQuality => !IsClosing && !IsBusy && !_controlPending && !_releaseBlocked
        && Binding is not null && _scope?.Token.IsCancellationRequested == false && Qualities.Count > 1;
    public IAsyncRelayCommand<PlayerFormatInformation> SelectQualityCommand { get; }
    public LivePlaybackState PlaybackState { get; private set; }
    public LiveChatState ChatState { get; private set; }
    public string? PlaybackError { get; private set; }
    public string? ChatError { get; private set; }
    public double Volume { get; private set; } = 1;
    public bool IsBusy { get; private set; }
    public bool CanSend => !IsClosing && !_sending && !IsBusy && Binding is not null
        && _scope?.Token.IsCancellationRequested == false
        && ChatState == LiveChatState.Connected && !string.IsNullOrWhiteSpace(_stamp?.AccountId)
        && _stamp?.Lifetime.IsCancellationRequested == false;
    public ReadOnlyObservableCollection<LiveChatMessage> Messages { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<LiveChatMessage>? MessageReceived;
    public bool IsClosing => Volatile.Read(ref _close) is not null;
    public bool ReleaseConfirmed { get; private set; }

    public Task OpenAsync(LiveInformation live, CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        ArgumentNullException.ThrowIfNull(live);
        if (_live is not null) throw new InvalidOperationException("A Live session can open only one room. Use Refresh for fresh media.");
        _stamp = _account();
        _stamp.Value.Lifetime.ThrowIfCancellationRequested();
        _live = live;
        ApplyAnchor(live);
        // Cancellation invalidates admission even before the dispatcher can deliver Closing.
        _accountRegistration = _stamp.Value.Lifetime.Register(() => _ = Observe(CloseAsync()));
        return BeginOpen(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        if (_live is null) throw new InvalidOperationException("Live has no room to refresh.");
        if (_refresh is { IsCompleted: false })
            // T4-01: Return the retained actual refresh operation; no replacement is queued and no synchronous UI wait is introduced.
            #pragma warning disable VSTHRD003
            return _refresh; // One pending replacement, no refresh queue.
            #pragma warning restore VSTHRD003
        return BeginOpen(cancellationToken, isRefresh: true);
    }

    public Task SelectQualityAsync(PlayerFormatInformation quality, CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        ArgumentNullException.ThrowIfNull(quality);
        if (!CanChangeQuality) return Task.CompletedTask;
        if (!Qualities.Any(item => item.Quality == quality.Quality))
            throw new ArgumentException("Live quality was not advertised by this room.", nameof(quality));
        if (SelectedQuality?.Quality == quality.Quality) return Task.CompletedTask;
        return BeginOpen(cancellationToken, isRefresh: true, quality: quality.Quality);
    }

    private async Task SelectQualityFromCommandAsync(PlayerFormatInformation? quality)
    {
        if (quality is null || !CanChangeQuality || !Qualities.Any(item => item.Quality == quality.Quality)) return;
        try { await SelectQualityAsync(quality, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // BeginOpen already publishes the retryable playback error.
    }

    private Task BeginOpen(CancellationToken token, bool isRefresh = false, int? quality = null)
    {
        RequireSameAccount();
        var requestedQuality = quality ?? _preferredQuality;
        var previous = _scope;
        previous?.Invalidate();
        var scope = new Scope(++_generation, _stamp!.Value, token, _shutdown.Token);
        _scope = scope;
        Binding = null;
        IsBusy = true;
        PlaybackState = LivePlaybackState.Opening;
        ChatState = LiveChatState.Idle;
        PlaybackError = ChatError = null;
        AudienceCount = null;
        _audienceMetric = null;
        var operation = Enqueue(async () =>
        {
            // This operation sits AFTER the actual previous resolver/open/control task, not a
            // cancelled waiter. StopScope also joins the independent connect and manual send.
            var failureStage = FailureStage.LivePreviousStop;
            try
            {
                if (previous is not null) await StopScopeAsync(previous);
                EnsureCurrent(scope);
                failureStage = FailureStage.LiveResolve;
                var request = ClassicRequestMapper.FromLive(_live!);
                request = request with { Item = request.Item with { PreferQuality = requestedQuality } };
                var result = await _resolve(request, scope.Token);
                EnsureCurrent(scope); // SDK parsing can complete after cancellation.
                failureStage = FailureStage.LiveValidateSource;
                var room = CanonicalRoom(result);
                if (scope.PreviousRoom is not null && room != scope.PreviousRoom)
                    throw new InvalidOperationException("Live room identity changed during refresh.");
                failureStage = FailureStage.LivePublishBinding;
                scope.Binding = new(_id, scope.Generation, room, scope.Account.Generation, scope.Account.AccountId);
                Binding = scope.Binding;
                ApplyAnchor(result.LivePage!.Information);
                Subscribe(scope);
                Notify();
                failureStage = FailureStage.LiveSetVolume;
                await _backend.SetVolumeAsync(Volume, scope.Token);
                EnsureCurrent(scope);
                failureStage = FailureStage.LiveBackendOpen;
                await _backend.OpenAsync(result.Media, scope.Token);
                EnsureCurrent(scope);
                // g_qn_desc includes global descriptions, not the room's supported qn list.
                var accepted = result.Media.Live!.Lines.SelectMany(line => line.AcceptQualities).ToHashSet();
                accepted.UnionWith(result.Media.Live.Lines.Select(line => line.Quality));
                Qualities = Array.AsReadOnly(result.Qualities.Where(format => accepted.Contains(format.Quality))
                    .DistinctBy(format => format.Quality).ToArray());
                SelectedQuality = Qualities.FirstOrDefault(format => format.Quality == result.SelectedQuality?.Quality);
                _preferredQuality = SelectedQuality?.Quality;
                failureStage = FailureStage.LivePublishPlaying;
                PlaybackState = LivePlaybackState.Playing;
                ChatState = LiveChatState.Connecting;
                scope.Connect = ConnectAsync(scope); // The session owns this task until StopScope/Close.
            }
            catch (OperationCanceledException ex)
            {
                SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<LiveSession>>());
                if (OwnsPublication(scope)) { PlaybackState = LivePlaybackState.Idle; ChatState = LiveChatState.Closed; }
                await CleanupFailedOpenAsync(scope);
                throw new OperationCanceledException("Live open was cancelled.");
            }
            catch (Exception ex)
            {
                SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<LiveSession>>());
                if (OwnsPublication(scope)) { PlaybackState = LivePlaybackState.Failed; PlaybackError = "Live playback is unavailable. Refresh to resolve a new stream."; }
                await CleanupFailedOpenAsync(scope);
                throw new InvalidOperationException("Live open failed.");
            }
            finally
            {
                if (OwnsPublication(scope)) { IsBusy = false; if (scope.Invalidated) Binding = null; Notify(); }
            }
        }, previous?.Binding?.RoomId);
        if (isRefresh) _refresh = operation;
        // Publish the real media tail (and refresh coalescer) before any UI observer can reenter.
        try { _messages.Clear(); } catch { RecordFailure(LiveFailureCategory.CollectionSubscriber); }
        ClearInbox();
        Notify();
        return operation;
    }

    private void ApplyAnchor(LiveInformation information)
    {
        Title = information.Identifier.Title ?? string.Empty;
        var user = information.User ?? _live?.User;
        AnchorName = user?.Name ?? string.Empty;
        AnchorAvatar = user?.Avatar?.Uri;
        IsFollowing = information.Relation switch
        {
            UserRelationStatus.Following or UserRelationStatus.Friends or UserRelationStatus.SpeciallyFollowed => true,
            UserRelationStatus.Unfollow or UserRelationStatus.BeFollowed or UserRelationStatus.Blocked => false,
            _ => null,
        };
    }

    private static long CanonicalRoom(ClassicSourceResult result)
    {
        var media = result.Media;
        if (!TryRoom(result.LivePage?.Information.Identifier.Id, out var room)
            || !TryRoom(media.Live?.Id, out var streamRoom) || streamRoom != room
            || !TryRoom(media.Request.Item.Live?.Identifier.Id, out var requestRoom) || requestRoom != room
            || media.StartSeconds != 0 || media.Dash is not null || result.ProgressIdentity is not null
            || media.Request.Item.Video is not null || media.Request.Item.Episode is not null
            || media.Request.Playlist.Count != 0 || media.Request.Item.StartPosition is not null)
            throw new InvalidOperationException("Live canonical room or source scope is invalid.");
        return room;
    }

    private static bool TryRoom(string? text, out long room)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out room) && room > 0;

    private async Task ConnectAsync(Scope scope)
    {
        try
        {
            EnsureCurrent(scope);
            await _chat.ConnectAsync(scope.Binding!.Value, scope.Token);
            EnsureCurrent(scope);
            ChatState = LiveChatState.Connected; // Facade returns only after positive op-8 acknowledgement.
            ChatError = null;
        }
        catch (OperationCanceledException) { if (IsCurrent(scope)) ChatState = LiveChatState.Closed; }
        catch
        {
            if (IsCurrent(scope)) { ChatState = LiveChatState.Unavailable; ChatError = "Live chat is unavailable."; }
        }
        finally { if (IsCurrent(scope)) Notify(); }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        RequireSameAccount();
        if (PlaybackState == LivePlaybackState.Playing) return Task.CompletedTask;
        // Island.PlayAsync can seek when EOF races its command queue. There is no atomic
        // resume-without-replay API. Resume at live edge with fresh resolution, including Paused.
        // Never call that backend method, even if an earlier UI status said Paused.
        return RefreshAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken)
        => ControlAsync(async (scope, token) =>
        {
            if (PlaybackState is not (LivePlaybackState.Playing or LivePlaybackState.Opening)) return;
            await _backend.PauseAsync(token);
            EnsureCurrent(scope);
            PlaybackState = LivePlaybackState.Paused;
        }, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Live volume must be between zero and one.");
        return ControlAsync(async (scope, token) =>
        {
            await _backend.SetVolumeAsync(volume, token);
            EnsureCurrent(scope);
            Volume = volume;
        }, cancellationToken);
    }

    private Task ControlAsync(Func<Scope, CancellationToken, Task> command, CancellationToken token)
    {
        CheckAdmission(token);
        RequireSameAccount();
        var scope = _scope ?? throw new InvalidOperationException("Live has no open scope.");
        if (IsBusy || _controlPending) return Task.CompletedTask; // No unbounded slider/transport queue.
        _controlPending = true;
        return Enqueue(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, token);
            try { EnsureCurrent(scope); await command(scope, linked.Token); EnsureCurrent(scope); }
            catch (OperationCanceledException) { throw new OperationCanceledException("Live command was cancelled."); }
            catch
            {
                if (IsCurrent(scope)) PlaybackError = "Live playback command failed.";
                throw new InvalidOperationException("Live playback command failed.");
            }
            finally { _controlPending = false; if (IsCurrent(scope)) Notify(); }
        });
    }

    public Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken)
    {
        CheckUi();
        if (cancellationToken.IsCancellationRequested || IsClosing || !CanSend || _scope is not { } scope
            || !MatchesAccount() || scope.Binding != binding || Binding != binding || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.NotSent, null, "Message was not sent."));
        _sending = true; // Publish before calling the facade; no asynchronous admission gap.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, cancellationToken);
        Task<LiveSendResult> actual;
        try { actual = _chat.SendManuallyAsync(binding, text, linked.Token); }
        catch { actual = Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown. Do not resend automatically.")); }
        var completion = FinishSendAsync(scope, binding, actual, linked);
        scope.Send = Observe(completion);
        Notify();
        return completion;
    }

    private async Task<LiveSendResult> FinishSendAsync(Scope scope, LiveBinding binding,
        Task<LiveSendResult> actual, CancellationTokenSource linked)
    {
        try
        {
            // Preserve an eventual known result for this attempt, even after its UI scope expires.
            // T4-02: The actual send attempt is independently owned; its eventual result remains observed after UI scope expiry.
            #pragma warning disable VSTHRD003
            var result = await actual;
            #pragma warning restore VSTHRD003
            return result.Binding == binding ? result
                : new(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown.");
        }
        catch { return new(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown. Do not resend automatically."); }
        finally
        {
            linked.Dispose();
            _sending = false;
            if (IsCurrent(scope)) Notify();
        }
    }

    private async Task CleanupFailedOpenAsync(Scope scope)
    {
        // A failure may follow allocation. Keep the scope owned; a later refresh can only start
        // after this real cleanup. Any release uncertainty makes replacement permanently fail closed.
        try { await StopScopeAsync(scope); }
        catch { _releaseBlocked = true; throw new InvalidOperationException("Live cleanup is unconfirmed; replacement is blocked."); }
    }

    private Task StopScopeAsync(Scope scope) => scope.Stop ??= StopScopeCoreAsync(scope);

    private async Task StopScopeCoreAsync(Scope scope)
    {
        if (scope.Stopped) return;
        scope.Invalidate();
        Unsubscribe(scope);
        var failed = false;
        try {
            // T4-03: Scope invalidation starts cancellation before this join; callbacks request/observe close without blocking on this consumer.
            #pragma warning disable VSTHRD003
            await scope.Cancellation;
            #pragma warning restore VSTHRD003
        } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
        // T4-04: The scope owns an independently started connect operation; it must finish before scope/backend release.
        #pragma warning disable VSTHRD003
        await Observe(scope.Connect);
        #pragma warning restore VSTHRD003
        // T4-05: The scope owns the actual send operation; cancellation does not replace its real completion before release.
        #pragma warning disable VSTHRD003
        await Observe(scope.Send);
        #pragma warning restore VSTHRD003
        try { await _chat.DisconnectAsync(); } catch { failed = true; RecordFailure(LiveFailureCategory.ChatRelease); }
        try { await _backend.StopAsync(CancellationToken.None); } catch { failed = true; RecordFailure(LiveFailureCategory.BackendRelease); }
        scope.Dispose();
        if (failed)
        {
            _releaseBlocked = true;
            throw new InvalidOperationException("Live cleanup is unconfirmed; replacement is blocked.");
        }
        scope.Stopped = true;
    }

    public Task CloseAsync()
    {
        lock (_closeSync)
        {
            if (_close is not null) return _close;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = completion.Task; // Admission closed before cancellation callbacks or awaits.
            _cancelShutdown = _shutdown.CancelAsync();
            if (!_dispatcher.TryEnqueue(() => _ = CompleteCloseAsync(completion)))
                completion.TrySetException(new InvalidOperationException("Live dispatcher is unavailable; owned resources are retained."));
            return _close;
        }
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            CheckUi();
            Binding = null;
            IsBusy = true;
            PlaybackState = LivePlaybackState.Closing;
            ChatState = LiveChatState.Closed;
            try { _messages.Clear(); } catch { RecordFailure(LiveFailureCategory.CollectionSubscriber); }
            ClearInbox();
            Notify();
            _scope?.Invalidate();
            try {
                // T4-06: Shutdown cancellation starts before close joins it; account callbacks never synchronously wait for this close consumer.
                #pragma warning disable VSTHRD003
                await _cancelShutdown;
                #pragma warning restore VSTHRD003
            } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
            // T4-07: The retained observer tail drains actual serial media work; producers do not await the close consumer.
            #pragma warning disable VSTHRD003
            await _tail; // Observer tail drains every actual media task, including late SDK results.
            #pragma warning restore VSTHRD003
            if (_scope is { } scope)
            {
                Unsubscribe(scope);
                try {
                    // T4-08: Terminal close joins already-started scope cancellation before owner disposal; callbacks do not block on close.
                    #pragma warning disable VSTHRD003
                    await scope.Cancellation;
                    #pragma warning restore VSTHRD003
                } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
                // T4-09: Terminal close retains and joins the independently owned connect task before releasing chat/backend owners.
                #pragma warning disable VSTHRD003
                await Observe(scope.Connect);
                #pragma warning restore VSTHRD003
                // T4-10: Terminal close joins the actual send task before release; advisory cancellation cannot stand in for its result.
                #pragma warning disable VSTHRD003
                await Observe(scope.Send);
                #pragma warning restore VSTHRD003
            }
            var chatReleased = false;
            var backendReleased = false;
            try { await _chat.DisposeAsync(); chatReleased = true; } catch { RecordFailure(LiveFailureCategory.ChatRelease); }
            try { await _backend.DisposeAsync(); backendReleased = true; } catch { RecordFailure(LiveFailureCategory.BackendRelease); }
            // Its callback requests/observes CloseAsync; it never waits synchronously for Close.
            // Drain it even after an owner fails, and before disposing tokens or confirming release.
            try { await _accountRegistration.DisposeAsync(); }
            catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            // Completed callback faults are not running work or evidence of native ownership.
            // Both actual disposals must succeed. Cached close is never a disposal retry.
            if (!chatReleased || !backendReleased)
                throw new InvalidOperationException("Live release is unconfirmed; the window and owners must remain retained.");
            try { _scope?.Dispose(); } catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            try { _shutdown.Dispose(); } catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            IsBusy = false;
            PlaybackState = LivePlaybackState.Closed;
            ReleaseConfirmed = true;
            Notify();
            completion.TrySetResult();
        }
        catch
        {
            PlaybackError = "Live close is incomplete. Replacement is blocked.";
            Notify();
            completion.TrySetException(new InvalidOperationException("Live close is incomplete. Owned resources are retained."));
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private Task Enqueue(Func<Task> action, long? previousRoom = null)
    {
        if (previousRoom is not null && _scope is { } scope) scope.PreviousRoom = previousRoom;
        var predecessor = _tail;
        var actual = RunAsync();
        _tail = Observe(actual);
        return actual;
        async Task RunAsync()
        {
            await Task.Yield(); // Publish ownership/tail before any reentrant callbacks.
            // T4-11: The serial runner captured the prior tail before publishing itself; this predecessor is not its own observer.
            #pragma warning disable VSTHRD003
            await predecessor;
            #pragma warning restore VSTHRD003
            await action();
        }
    }

    private bool OwnsPublication(Scope scope) => !IsClosing && ReferenceEquals(_scope, scope)
        && scope.Generation == _generation && !scope.Account.Lifetime.IsCancellationRequested;
    private bool IsCurrent(Scope scope) => OwnsPublication(scope) && !scope.Invalidated && !scope.Token.IsCancellationRequested;
    private void EnsureCurrent(Scope scope)
    {
        CheckUi();
        scope.Token.ThrowIfCancellationRequested();
        if (!IsCurrent(scope)) throw new OperationCanceledException("Live scope was replaced.");
        RequireSameAccount();
        if (_releaseBlocked) throw new InvalidOperationException("Live replacement is blocked by unconfirmed cleanup.");
    }
    private bool MatchesAccount()
    {
        if (_stamp is not { } captured || captured.Lifetime.IsCancellationRequested) return false;
        var current = _account();
        return current.Generation == captured.Generation && current.AccountId == captured.AccountId
            && current.Lifetime == captured.Lifetime && !current.Lifetime.IsCancellationRequested;
    }
    private void RequireSameAccount()
    {
        if (MatchesAccount()) return;
        _ = Observe(CloseAsync());
        throw new OperationCanceledException("Live account scope ended.");
    }
    private void CheckUi()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Live command requires its UI dispatcher.");
    }
    private void CheckAdmission(CancellationToken token)
    {
        CheckUi();
        token.ThrowIfCancellationRequested();
        if (IsClosing || _releaseBlocked) throw new InvalidOperationException("Live admission is closed.");
    }

    private void Subscribe(Scope scope)
    {
        scope.BackendHandler = (_, status) =>
        {
            lock (_inboxSync) { _backendStatus = (scope, status); PostInboxLocked(); }
        };
        scope.ChatHandler = (_, status) =>
        {
            if (scope.Binding != status.Binding) return;
            lock (_inboxSync) { _chatStatus = (scope, status); PostInboxLocked(); }
        };
        scope.MessageHandler = (_, message) =>
        {
            if (scope.Binding != message.Binding) return;
            lock (_inboxSync)
            {
                if (_inbox.Count < 256) _inbox.Enqueue((scope, message));
                PostInboxLocked();
            }
        };
        _backend.StatusChanged += scope.BackendHandler;
        _chat.StatusChanged += scope.ChatHandler;
        _chat.MessageReceived += scope.MessageHandler;
        if (_chat is ILiveAudienceSource audience)
        {
            scope.AudienceHandler = (_, update) =>
            {
                if (scope.Binding != update.Binding) return;
                lock (_inboxSync)
                {
                    if (_audience?.Update.Metric == LiveAudienceMetric.Watched && update.Metric != LiveAudienceMetric.Watched) return;
                    _audience = (scope, update);
                    PostInboxLocked();
                }
            };
            audience.AudienceChanged += scope.AudienceHandler;
        }
    }
    private void Unsubscribe(Scope scope)
    {
        // Observer detachment cannot skip independent owner drains/disposals.
        try { _backend.StatusChanged -= scope.BackendHandler; } catch { RecordFailure(LiveFailureCategory.StatusSubscriber); }
        try { _chat.StatusChanged -= scope.ChatHandler; } catch { RecordFailure(LiveFailureCategory.StatusSubscriber); }
        try { _chat.MessageReceived -= scope.MessageHandler; } catch { RecordFailure(LiveFailureCategory.MessageSubscriber); }
        try { if (_chat is ILiveAudienceSource audience) audience.AudienceChanged -= scope.AudienceHandler; }
        catch { RecordFailure(LiveFailureCategory.StatusSubscriber); }
    }
    private void PostInboxLocked()
    {
        if (_inboxPosted || IsClosing) return;
        _inboxPosted = true;
        if (!_dispatcher.TryEnqueue(DrainInbox)) { _inboxPosted = false; _inbox.Clear(); _backendStatus = null; _chatStatus = null; _audience = null; }
    }
    private void ClearInbox()
    {
        lock (_inboxSync) { _inbox.Clear(); _backendStatus = null; _chatStatus = null; _audience = null; }
    }
    private void DrainInbox()
    {
        (Scope Scope, LiveChatMessage Message)[] messages;
        (Scope Scope, ClassicBackendStatus Status)? backend;
        (Scope Scope, LiveChatStatus Status)? chat;
        (Scope Scope, LiveAudienceUpdate Update)? audience;
        lock (_inboxSync)
        {
            messages = _inbox.ToArray(); _inbox.Clear();
            backend = _backendStatus; chat = _chatStatus;
            audience = _audience; _audience = null;
            _backendStatus = null; _chatStatus = null; _inboxPosted = false;
        }
        if (IsClosing || !MatchesAccount()) return;
        if (backend is { } b && IsCurrent(b.Scope))
        {
            PlaybackState = b.Status.State switch
            {
                ClassicPlaybackState.Playing => LivePlaybackState.Playing,
                ClassicPlaybackState.Paused => LivePlaybackState.Paused,
                ClassicPlaybackState.Ended => LivePlaybackState.Ended,
                ClassicPlaybackState.Failed => LivePlaybackState.Failed,
                _ => LivePlaybackState.Opening,
            };
            if (b.Status.State == ClassicPlaybackState.Failed) PlaybackError = "Live playback failed. Refresh to resolve a new stream.";
            if (double.IsFinite(b.Status.Volume)) Volume = Math.Clamp(b.Status.Volume, 0, 1);
        }
        if (chat is { } c && IsCurrent(c.Scope) && Binding == c.Status.Binding)
        {
            ChatState = c.Status.State;
            ChatError = c.Status.SafeError is null ? null : "Live chat is unavailable.";
        }
        if (audience is { } a && IsCurrent(a.Scope) && Binding == a.Update.Binding
            && (_audienceMetric != LiveAudienceMetric.Watched || a.Update.Metric == LiveAudienceMetric.Watched))
        {
            AudienceCount = a.Update.Count;
            _audienceMetric = a.Update.Metric;
        }
        Notify();
        foreach (var item in messages)
        {
            if (!IsCurrent(item.Scope) || Binding != item.Message.Binding || !MatchesAccount()) continue;
            try { if (_messages.Count == 200) _messages.RemoveAt(0); } catch { RecordFailure(LiveFailureCategory.CollectionSubscriber); }
            if (!IsCurrent(item.Scope) || Binding != item.Message.Binding) continue;
            try { _messages.Add(item.Message); } catch { RecordFailure(LiveFailureCategory.CollectionSubscriber); }
            // A collection observer may synchronously refresh/close the room.
            if (IsCurrent(item.Scope) && Binding == item.Message.Binding)
                PublishMessage(item.Scope, item.Message);
        }
    }
    private void PublishMessage(Scope scope, LiveChatMessage message)
    {
        if (MessageReceived is not { } handlers) return;
        foreach (EventHandler<LiveChatMessage> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(scope) || Binding != message.Binding) return;
            try { handler(this, message); } catch { RecordFailure(LiveFailureCategory.MessageSubscriber); }
        }
    }
    private void Notify()
    {
        CheckUi();
        try { SelectQualityCommand.NotifyCanExecuteChanged(); } catch { RecordFailure(LiveFailureCategory.PropertySubscriber); }
        if (PropertyChanged is not { } handlers) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            try { handler(this, new PropertyChangedEventArgs(string.Empty)); } catch { RecordFailure(LiveFailureCategory.PropertySubscriber); }
    }
    private async Task Observe(Task task)
    {
        try {
            // T4-12: This observer joins already-issued owned work, not the completion it produces; errors remain observed asynchronously.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException) { return; }
        catch { RecordFailure(LiveFailureCategory.OwnedTaskFault); }
    }

    private void RecordFailure(LiveFailureCategory category)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveSession>>(),
            logger => logger.LogWarning("Live session operation failed: {Category}", category));

    private sealed class Scope : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private bool _disposed;
        public Scope(long generation, LiveAccountStamp account, CancellationToken request, CancellationToken shutdown)
        {
            Generation = generation; Account = account;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, account.Lifetime, shutdown);
            Token = _lifetime.Token;
        }
        public long Generation { get; }
        public LiveAccountStamp Account { get; }
        public CancellationToken Token { get; }
        public LiveBinding? Binding { get; set; }
        public long? PreviousRoom { get; set; }
        public bool Invalidated { get; private set; }
        public bool Stopped { get; set; }
        public Task Cancellation { get; private set; } = Task.CompletedTask;
        public Task Connect { get; set; } = Task.CompletedTask;
        public Task Send { get; set; } = Task.CompletedTask;
        public Task? Stop { get; set; }
        public EventHandler<ClassicBackendStatus>? BackendHandler { get; set; }
        public EventHandler<LiveChatStatus>? ChatHandler { get; set; }
        public EventHandler<LiveChatMessage>? MessageHandler { get; set; }
        public EventHandler<LiveAudienceUpdate>? AudienceHandler { get; set; }
        public void Invalidate()
        {
            if (Invalidated) return;
            Invalidated = true;
            if (!_disposed) Cancellation = _lifetime.CancelAsync();
        }
        public void Dispose() { if (!_disposed) { _disposed = true; _lifetime.Dispose(); } }
    }
}
