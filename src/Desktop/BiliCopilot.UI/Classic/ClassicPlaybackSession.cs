using BiliCopilot.UI.Classic.Backends.External;
using BiliCopilot.UI.Classic.Input;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Classic;

/// <summary>One backend and one dispatcher; no mutable New-player state is shared.</summary>
public sealed class ClassicPlaybackSession : ObservableObject, IClassicPlaybackSession
{
    private readonly ClassicSessionOptions _options;
    private readonly IClassicPlaybackBackend _backend;
    private readonly ClassicMediaSourceService _sources;
    private readonly ClassicProgressReporter _progress;
    private readonly DispatcherQueue _dispatcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Pure managed operation gate; no AvailableWaitHandle is created. In-flight finally Release and explicit retry after unconfirmed engine disposal must retain this same gate.")]
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TemporaryPlaybackRateState _temporaryRate = new();
    private readonly object _closeSync = new();
    private Task? _closeTask;
    private CancellationTokenSource? _sourceCancellation;
    private ClassicSourceResult? _source;
    private EventHandler<ClassicBackendStatus>? _statusHandler;
    private long _generation;
    private long _lastNavigation;
    private bool _private;
    private bool _ended;
    private bool _disposed;
    private volatile bool _closeRequested;

    public ClassicPlaybackSession(ClassicSessionOptions options, IClassicPlaybackBackend backend,
        ClassicMediaSourceService sources, ClassicProgressReporter progress, DispatcherQueue dispatcher)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (backend.Kind != options.Backend) throw new ArgumentException("Backend selection does not match captured options.");
        PlayCommand = new AsyncRelayCommand(() => ControlAsync(ClassicCapabilities.Transport, ct => _backend.PlayAsync(ct)), () => Can(ClassicCapabilities.Transport));
        PauseCommand = new AsyncRelayCommand(() => ControlAsync(ClassicCapabilities.Transport, ct => _backend.PauseAsync(ct)), () => Can(ClassicCapabilities.Transport));
        SeekCommand = new AsyncRelayCommand<double>(value => ControlAsync(ClassicCapabilities.Seek,
            ct => _backend.SeekAsync(Math.Clamp(value, 0, Math.Max(0, DurationSeconds)), ct), double.IsFinite(value)), value => Can(ClassicCapabilities.Seek) && double.IsFinite(value));
        SetVolumeCommand = new AsyncRelayCommand<double>(value => ControlAsync(ClassicCapabilities.Volume,
            ct => _backend.SetVolumeAsync(Math.Clamp(value, 0, 1), ct), double.IsFinite(value)), value => Can(ClassicCapabilities.Volume) && double.IsFinite(value));
        SetSpeedCommand = new AsyncRelayCommand<double>(value => ControlAsync(ClassicCapabilities.Speed,
            ct => _backend.SetSpeedAsync(value, ct), double.IsFinite(value) && value > 0), value => Can(ClassicCapabilities.Speed) && double.IsFinite(value) && value > 0);
        NextCommand = new AsyncRelayCommand(() => NavigateAsync(true), () => !IsBusy && !IsClosing && CanGoNext);
        PreviousCommand = new AsyncRelayCommand(() => NavigateAsync(false), () => !IsBusy && !IsClosing && CanGoPrevious);
        SelectPlaylistItemCommand = new AsyncRelayCommand<int>(index => ChangeAsync(() => ClassicNavigationPolicy.SelectPlaylist(Request, index)),
            index => Ready && index >= 0 && index < Request.Playlist.Count);
        SelectPartCommand = new AsyncRelayCommand<VideoPart>(part => ChangeAsync(() => part is not null && Parts.Contains(part)
            ? Request with { Item = Request.Item with { PreferPart = part, StartPosition = 0 } } : null), part => Ready && part is not null && Parts.Contains(part));
        SelectEpisodeCommand = new AsyncRelayCommand<EpisodeInformation>(episode => ChangeAsync(() => episode is not null && Episodes.Contains(episode)
            ? Request with { Item = Request.Item with { Episode = episode, StartPosition = 0 } } : null), episode => Ready && episode is not null && Episodes.Contains(episode));
        SelectQualityCommand = new AsyncRelayCommand<PlayerFormatInformation>(quality => ChangeAsync(() => Can(ClassicCapabilities.Quality) && quality is not null && Qualities.Contains(quality)
            ? Request with { Item = Request.Item with { PreferQuality = quality.Quality, StartPosition = PositionSeconds } } : null),
            quality => Can(ClassicCapabilities.Quality) && quality is not null && Qualities.Contains(quality));
        CloseCommand = new AsyncRelayCommand(CloseAsync, () => !IsClosing);
        Input = new ClassicPlayerInputController(this);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ClassicBackendKind Backend => _backend.Kind;
    public ClassicPlaybackRequest Request { get; private set; } = new(new(BiliMediaType.Video, null, null, null, null, null, null, null, false), Array.Empty<ClassicMediaItem>(), null);
    public ClassicCapabilities Capabilities => _backend.Capabilities;
    public ClassicPlaybackState State { get; private set; } = ClassicPlaybackState.Idle;
    public string Title => Request.Item.Video?.Identifier.Title ?? Request.Item.Episode?.Identifier.Title ?? Request.Item.Live?.Identifier.Title ?? string.Empty;
    public string? ErrorMessage { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsClosing => _closeRequested;
    public bool CanGoNext => FindNavigation(true) is not null;
    public bool CanGoPrevious => FindNavigation(false) is not null;
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public double Volume { get; private set; } = 1;
    public double Speed { get; private set; } = 1;
    public IReadOnlyList<PlayerFormatInformation> Qualities => _source?.Qualities ?? Array.Empty<PlayerFormatInformation>();
    public PlayerFormatInformation? SelectedQuality => _source?.SelectedQuality;
    public IReadOnlyList<VideoPart> Parts => _source?.Parts ?? Array.Empty<VideoPart>();
    public VideoPart? SelectedPart => Request.Item.PreferPart;
    public IReadOnlyList<EpisodeInformation> Episodes => _source?.Episodes ?? Array.Empty<EpisodeInformation>();
    public EpisodeInformation? SelectedEpisode => Request.Item.Episode;
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand<double> SeekCommand { get; }
    public IAsyncRelayCommand<double> SetVolumeCommand { get; }
    public IAsyncRelayCommand<double> SetSpeedCommand { get; }
    public IAsyncRelayCommand<PlayerFormatInformation> SelectQualityCommand { get; }
    public IAsyncRelayCommand<VideoPart> SelectPartCommand { get; }
    public IAsyncRelayCommand<EpisodeInformation> SelectEpisodeCommand { get; }
    public IAsyncRelayCommand<int> SelectPlaylistItemCommand { get; }
    public IAsyncRelayCommand CloseCommand { get; }
    public ClassicPlayerInputController Input { get; }
    internal long Generation => _generation;
    public bool BackendReleaseConfirmed => _disposed;
    private bool Ready => !IsClosing && !IsBusy && _source is not null && State is not (ClassicPlaybackState.Loading or ClassicPlaybackState.Failed or ClassicPlaybackState.Closed);
    internal bool Can(ClassicCapabilities capability) => Ready && (Capabilities & capability) == capability;

    public Task OpenAsync(ClassicPlaybackRequest request, CancellationToken cancellationToken)
        => OnDispatcherAsync(() => RunOpenAsync(request, cancellationToken));

    private Task ChangeAsync(Func<ClassicPlaybackRequest?> select) => OnDispatcherAsync(async () =>
    {
        if (!Ready) return;
        var request = select();
        if (request is not null) await RunOpenAsync(request, _lifetime.Token);
    });

    private ClassicPlaybackRequest? FindNavigation(bool forward) => IsClosing || _source is null ? null
        : ClassicNavigationPolicy.Find(_source, _options, forward, _sources.Preferences.AutoPlayRecommendations);

    private Task NavigateAsync(bool forward) => ChangeAsync(() =>
    {
        var now = Environment.TickCount64;
        if (_lastNavigation != 0 && now - _lastNavigation < 1000) return null;
        var next = FindNavigation(forward);
        if (next is not null) _lastNavigation = now;
        return next;
    });

    private async Task RunOpenAsync(ClassicPlaybackRequest request, CancellationToken token)
    {
        if (Backend == ClassicBackendKind.External) { await RunExternalOpenAsync(request, token); return; }
        if (IsClosing || IsBusy) return;
        IsBusy = true;
        Notify();
        await _operations.WaitAsync();
        var generation = _generation;
        try
        {
            if (IsClosing) return;
            token.ThrowIfCancellationRequested();
            // Restore against the old generation/backend before advancing or reusing it.
            await Input.CancelAsync();
            if (IsClosing) return;
            generation = ++_generation;
            DetachStatus();
            if (_sourceCancellation is not null) await _sourceCancellation.CancelAsync();
            if (!Current(generation)) return;
            await _progress.StopAsync();
            if (!Current(generation)) return;
            await _backend.StopAsync(_lifetime.Token);
            if (!Current(generation)) return;
            _sourceCancellation?.Dispose();
            _sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            var cancellation = _sourceCancellation.Token;
            _private |= request.Item.IsPrivate;
            request = request with { Item = request.Item with { IsPrivate = _private } };
            State = ClassicPlaybackState.Loading;
            ErrorMessage = null;
            _source = null;
            Request = request;
            Notify();
            var source = await _sources.ResolveAsync(request, Backend, cancellation);
            if (!Current(generation)) return;
            cancellation.ThrowIfCancellationRequested();
            _source = source;
            Request = source.Media.Request;
            PositionSeconds = source.Media.StartSeconds;
            DurationSeconds = source.Media.Dash?.Duration ?? 0;
            _ended = false;
            AttachStatus(generation);
            await _backend.OpenAsync(source.Media, cancellation);
            if (!Current(generation)) return;
            cancellation.ThrowIfCancellationRequested();
            _progress.Update(PositionSeconds, State == ClassicPlaybackState.Playing);
            _progress.Start(source.ProgressIdentity, _sources.Preferences.ReportProgress
                && (Capabilities & ClassicCapabilities.Progress) != 0);
        }
        catch (OperationCanceledException) { if (Current(generation)) await AbortOpenAsync(generation, false); }
        catch (ClassicSourceException ex) { if (Current(generation)) await AbortOpenAsync(generation, true, ex.Message); }
        catch (Exception) { if (Current(generation)) await AbortOpenAsync(generation, true); }
        finally { IsBusy = false; _operations.Release(); Notify(); AdvanceEnded(); }
    }

    public ClassicPlaybackRequest? ResolvingRequest { get; private set; }

    private async Task RunExternalOpenAsync(ClassicPlaybackRequest request, CancellationToken token)
    {
        if (IsClosing || IsBusy) return;
        IsBusy = true;
        Notify();
        await _operations.WaitAsync();
        var generation = _generation;
        var oldState = State;
        try
        {
            if (IsClosing) return;
            await Input.CancelAsync(); // Restore before advancing generation, even on this branch.
            if (IsClosing) return;
            generation = ++_generation;
            DetachStatus();
            if (_sourceCancellation is not null) await _sourceCancellation.CancelAsync();
            if (!Current(generation)) return;
            _sourceCancellation?.Dispose();
            _sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            var cancellation = _sourceCancellation.Token;
            _private |= request.Item.IsPrivate;
            request = request with { Item = request.Item with { IsPrivate = _private } };
            ResolvingRequest = request;
            // Do NOT stop or overwrite the old request/source/process before approval.
            var candidate = await _sources.ResolveAsync(request, Backend, cancellation);
            if (!Current(generation)) return;
            cancellation.ThrowIfCancellationRequested();
            await _backend.OpenAsync(candidate.Media, cancellation);
            if (!Current(generation)) return;
            _source = candidate;
            Request = candidate.Media.Request;
            PositionSeconds = DurationSeconds = 0; // External cannot measure transport.
            State = ClassicPlaybackState.Idle;
            ErrorMessage = null;
            AttachStatus(generation);
        }
        catch (ExternalReplacementException ex)
        {
            if (!Current(generation)) return;
            if (!ex.PreviousOwnerRetained) { _source = null; Request = request; }
            State = ClassicPlaybackState.Failed;
            ErrorMessage = ex.Message;
            AttachStatus(generation);
        }
        catch (Exception ex)
        {
            if (!Current(generation)) return;
            // Source validation / approval cancellation / denial made no replacement effects.
            State = oldState;
            ErrorMessage = ex is OperationCanceledException ? "External replacement cancelled; previous playback retained."
                : ex is ClassicSourceException ? ex.Message : "External replacement was not approved; previous playback retained.";
            AttachStatus(generation);
        }
        finally { ResolvingRequest = null; IsBusy = false; _operations.Release(); Notify(); }
    }

    private async Task AbortOpenAsync(long generation, bool failed, string? safeError = null)
    {
        DetachStatus();
        _source = null;
        try { await _backend.StopAsync(CancellationToken.None); } catch (Exception) { failed = true; }
        if (!Current(generation)) return;
        State = failed ? ClassicPlaybackState.Failed : ClassicPlaybackState.Idle;
        ErrorMessage = failed ? safeError ?? "Classic playback could not be opened." : null;
    }

    private Task ControlAsync(ClassicCapabilities capability, Func<CancellationToken, Task> action, bool valid = true)
        => OnDispatcherAsync(async () =>
        {
            if (!valid || !Can(capability)) return;
            IsBusy = true;
            Notify();
            var generation = _generation;
            await _operations.WaitAsync();
            try
            {
                if (!Current(generation)) return;
                await CancelRateAsync();
                if (!Current(generation)) return;
                await action(_lifetime.Token);
                if (!Current(generation)) return;
            }
            catch (OperationCanceledException) when (IsClosing) { }
            catch (Exception) { if (Current(generation)) ErrorMessage = "Classic playback operation failed."; }
            finally { IsBusy = false; _operations.Release(); Notify(); }
        });

    internal Task HoldRateAsync(int owner, double rate) => OnDispatcherAsync(async () =>
    {
        if (!Can(ClassicCapabilities.Speed) || State != ClassicPlaybackState.Playing || !double.IsFinite(rate) || rate <= 0) return;
        var generation = _generation;
        await _temporaryRate.HoldAsync(owner, Speed, rate,
            speed => _backend.SetSpeedAsync(speed, CancellationToken.None),
            () => generation == _generation && !_disposed);
    });
    internal Task ReleaseRateAsync(int owner) => OnDispatcherAsync(() => _temporaryRate.ReleaseAsync(owner));
    internal Task CancelRateAsync() => OnDispatcherAsync(() => _temporaryRate.CancelAsync());

    private bool Current(long generation) => generation == _generation && !IsClosing && !_disposed;
    private void AttachStatus(long generation)
    {
        _statusHandler = (sender, status) =>
        {
            if (!ReferenceEquals(sender, _backend)) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (!Current(generation)) return;
                State = status.State;
                PositionSeconds = double.IsFinite(status.PositionSeconds) ? Math.Max(0, status.PositionSeconds) : 0;
                DurationSeconds = double.IsFinite(status.DurationSeconds) ? Math.Max(0, status.DurationSeconds) : 0;
                Volume = double.IsFinite(status.Volume) ? Math.Clamp(status.Volume, 0, 1) : Volume;
                Speed = double.IsFinite(status.Speed) && status.Speed > 0 ? status.Speed : Speed;
                ErrorMessage = status.SafeError;
                _progress.Update(PositionSeconds, State == ClassicPlaybackState.Playing);
                Notify();
                AdvanceEnded();
            });
        };
        _backend.StatusChanged += _statusHandler;
    }
    private void DetachStatus()
    {
        if (_statusHandler is not null) _backend.StatusChanged -= _statusHandler;
        _statusHandler = null;
    }

    private void AdvanceEnded()
    {
        if (IsBusy || IsClosing || State != ClassicPlaybackState.Ended || _ended) return;
        _ended = true;
        Observe(NavigateAsync(true));
    }

    public Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (_closeSync)
        {
            if (_closeTask is not null && (!_closeTask.IsCompleted || _disposed || _closeTask.IsCompletedSuccessfully)) return _closeTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task;
            _closeRequested = true;
        }
        // Publish before entering dispatcher code: PropertyChanged can reenter CloseAsync.
        Observe(CompleteCloseAsync(completion));
        return completion.Task;
    }
    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try { await OnDispatcherAsync(CloseCoreAsync); completion.TrySetResult(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private async Task CloseCoreAsync()
    {
        State = ClassicPlaybackState.Closing;
        Exception? failure = null;
        var lifetimeCancelled = Task.CompletedTask;
        var sourceCancelled = Task.CompletedTask;
        try { lifetimeCancelled = _lifetime.CancelAsync(); } catch (Exception ex) { failure = ex; }
        try { sourceCancelled = _sourceCancellation?.CancelAsync() ?? Task.CompletedTask; } catch (Exception ex) { failure ??= ex; }
        var progressStopped = _progress.StopAsync();
        Notify();
#pragma warning disable VSTHRD003 // This close initiated lifetime cancellation; no operation gate is held while joining its callbacks.
        try { await lifetimeCancelled; } catch (Exception ex) { failure ??= ex; }
#pragma warning restore VSTHRD003
#pragma warning disable VSTHRD003 // This close initiated source cancellation; callback completion is joined before acquiring the operation gate.
        try { await sourceCancelled; } catch (Exception ex) { failure ??= ex; }
#pragma warning restore VSTHRD003
        await _operations.WaitAsync();
        try
        {
            try { await Input.CancelAsync(); } catch (Exception ex) { failure ??= ex; }
            ++_generation;
            try { await progressStopped; } catch (Exception ex) { failure ??= ex; }
            DetachStatus();
            // Dispose owns stop/drain/release. An unsuccessful release is not a closed session.
            try { await _backend.DisposeAsync(); }
            catch
            {
                State = ClassicPlaybackState.Failed;
                ErrorMessage = "Classic engine release is unconfirmed; keep this window/surface alive. Explicit close retry is required (Island may be unretryable).";
                throw new InvalidOperationException(ErrorMessage);
            }
            _disposed = true;
            _source = null;
            _sourceCancellation?.Dispose();
            _lifetime.Dispose();
            State = ClassicPlaybackState.Closed;
            ErrorMessage = failure is null ? null : "Classic playback cleanup failed.";
        }
        finally { _operations.Release(); Notify(); }
        if (failure is not null) throw new InvalidOperationException("Classic playback cleanup failed.");
    }
    public ValueTask DisposeAsync() => new(CloseAsync());

    internal Task OnDispatcherAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("Classic dispatcher is unavailable."));
        return completion.Task;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Single-await observer owns no gate and only observes an already-started session operation. It cannot be a prerequisite of that operation and does not change the original task's fault.")]
    private static async void Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic session observed operation failed."); }
    }
    private void Notify()
    {
        OnPropertyChanged(string.Empty);
        foreach (var command in new IRelayCommand[] { PlayCommand, PauseCommand, NextCommand, PreviousCommand,
            SeekCommand, SetVolumeCommand, SetSpeedCommand, SelectQualityCommand, SelectPartCommand,
            SelectEpisodeCommand, SelectPlaylistItemCommand, CloseCommand }) command.NotifyCanExecuteChanged();
    }
}
