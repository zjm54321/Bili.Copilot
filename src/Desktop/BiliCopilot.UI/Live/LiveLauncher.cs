#nullable enable

using System.Runtime.InteropServices;
using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Classic.Backends.Island;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Live.Hosting;
using BiliCopilot.UI.Live.Playback;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Live;

/// <summary>
/// REQUIRED parent composition seam. The lease serializes reciprocal Live/shared-active-VOD
/// routing, including pending opens. Before granting it, drain the replaced shared route through
/// its real owner close API. Do not close unrelated independent VOD windows. A VOD route taking
/// this same lease must await LiveLauncher.CloseCurrentAsync before replacing Live.
/// </summary>
public interface ILivePlaybackAdmission
{
    Task<IDisposable> EnterLiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// REQUIRED parent shutdown/accounting seam. Retain the managed owner before native initialization.
/// NativeWindow is the borrowed MainWindow: never register/remove/close it as a Live window. Release only
/// after ReleaseConfirmed. Release must be idempotent: explicit drains reconcile the notification.
/// Accounting/notification errors after confirmed release must not retain native ownership or
/// poison playback admission. Application shutdown must await the launcher and all retained owners.
/// </summary>
public interface ILiveWindowRegistry
{
    void Retain(LiveWindow window);
    void Release(LiveWindow window);
    void ReportSafeFailure(string message);
}

/// <summary>No automatic routing/DI/entry point. Parent composes exactly one application instance.</summary>
public sealed class LiveLauncher : IAsyncDisposable
{
    private readonly IPlayerService _player;
    private readonly IBiliCookiesResolver _cookies;
    private readonly IBiliTokenResolver _tokens;
    private readonly AccountViewModel _account;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILivePlaybackAdmission _admission;
    private readonly ILiveWindowRegistry _registry;
    private readonly Func<LiveAccountStamp, ILiveChatClient> _createChat;
    private readonly Func<MainWindow> _mainWindow;
    // D3: DisposeAsync awaits CloseCurrentAsync; the owned child releases and _current clears only after confirmed release.
    #pragma warning disable CA2213
    private LiveWindow? _current;
    #pragma warning restore CA2213
    private Task<LiveWindow>? _opening;
    // D4: OpenCoreAsync finally owns this CTS disposal; CloseCurrentAsync joins actual opening, including that finally.
    #pragma warning disable CA2213
    private CancellationTokenSource? _openingCancellation;
    #pragma warning restore CA2213
    private Task? _closeCurrent;
    private bool _disposed;

    public LiveLauncher(IPlayerService player, IBiliCookiesResolver cookies, IBiliTokenResolver tokens,
        AccountViewModel account, DispatcherQueue dispatcher, ILivePlaybackAdmission admission,
        ILiveWindowRegistry registry, Func<LiveAccountStamp, ILiveChatClient> createChat,
        Func<MainWindow> mainWindow)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _createChat = createChat ?? throw new ArgumentNullException(nameof(createChat));
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
    }

    public LiveWindow? Current => _current;
    public bool HasWork => _opening is { IsCompleted: false } || _closeCurrent is { IsCompleted: false }
        || _current is { ReleaseConfirmed: false };

    public Task<LiveWindow> OpenAsync(LiveInformation live, ClassicDecodePreference decode,
        CancellationToken cancellationToken)
    {
        CheckUi();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(live);
        if (_disposed || _opening is { IsCompleted: false } || _closeCurrent is { IsCompleted: false })
            throw new InvalidOperationException("Live launch admission is busy or closed.");
        if (!Enum.IsDefined(decode)) throw new InvalidOperationException("Live decode preference is invalid.");
        _openingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _opening = OpenCoreAsync(live, decode, _openingCancellation);
        return _opening;
    }

    private async Task<LiveWindow> OpenCoreAsync(LiveInformation live, ClassicDecodePreference decode,
        CancellationTokenSource opening)
    {
        await Task.Yield(); // Publish pending-open ownership before entering parent arbitration.
        LiveWindow? created = null;
        IDisposable? lease = null;
        var failureStage = FailureStage.LiveAdmission;
        try
        {
            lease = await _admission.EnterLiveAsync(opening.Token)
                ?? throw new InvalidOperationException("Live playback admission did not grant a lease.");
            opening.Token.ThrowIfCancellationRequested();
            if (_current is { } previous)
            {
                failureStage = FailureStage.LivePreviousClose;
                await CloseWindowAsync(previous);
            }
            failureStage = FailureStage.LiveSourceSetup;
            var stamp = CaptureAccount();
            stamp.Lifetime.ThrowIfCancellationRequested();
            // Actual existing eligibility: token presence for NeedLogin, profile.IsVip for NeedVip.
            // The VOD own-video exemption is deliberately not applicable to Live formats.
            var loggedIn = _tokens.GetToken() is not null;
            var vip = _account.MyProfile?.IsVip == true;
            var sources = new ClassicMediaSourceService(_player, _cookies,
                new ClassicSourcePreferences(false, false, false),
                format => (!format.NeedLogin || loggedIn) && (!format.NeedVip || vip));
            var options = new ClassicSessionOptions(PlaybackExperience.Classic, ClassicBackendKind.Island,
                ClassicDisplayMode.Default, decode, string.Empty, false, false);
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidOperationException("Live requires the packaged x64 or ARM64 Island engine."),
            };
            var library = Path.Combine(Package.Current.InstalledPath, "Assets", "libmpv", architecture, "libmpv-2.dll");
            failureStage = FailureStage.LiveWindowCreate;
            CheckUi();
            opening.Token.ThrowIfCancellationRequested();
            // Caller guards the actual existing main window. Never create/activate a top-level here.
            var mainWindow = _mainWindow()
                ?? throw new InvalidOperationException("The existing main window is unavailable for Live.");
            created = new LiveWindow(mainWindow, _dispatcher, Report);
            _current = created; // Retain managed partial-construction owner before any native allocation.
            created.Released += OnReleased;
            _registry.Retain(created);
            failureStage = FailureStage.LiveNativeInitialize;
            created.InitializeNative();
            failureStage = FailureStage.LiveBackendCreate;
            var backend = new IslandPlaybackBackend(created.Surface.RenderHwnd, library, options);
            created.AdoptBackend(backend);
            failureStage = FailureStage.LiveChatCreate;
            var chat = _createChat(stamp); // Factory must retain/clean any internal partial allocation on throw.
            created.AdoptChat(chat);
            if (CaptureAccount() != stamp) throw new OperationCanceledException("Live account changed before opening.");
            failureStage = FailureStage.LiveSessionCreate;
            var session = new LiveSession(backend, sources, chat, CaptureAccount, _dispatcher);
            created.AdoptSession(session);
            failureStage = FailureStage.LiveWindowOpen;
            await created.OpenAsync(live, opening.Token);
            failureStage = FailureStage.LiveAfterOpen;
            opening.Token.ThrowIfCancellationRequested();
            if (created.IsClosing) throw new OperationCanceledException("Live window closed during opening.");
            return created;
        }
        catch (Exception ex)
        {
            SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<LiveLauncher>>());
            if (created is not null)
            {
                try { await CloseWindowAsync(created); }
                catch (Exception cleanupError)
                {
                    SafeFailureContext.TryWrite(FailureStage.LiveOpenCleanup, cleanupError, () => this.Get<ILogger<LiveLauncher>>());
                    Report("Live opening failed and cleanup is incomplete; its owner remains retained.");
                }
            }
            throw new InvalidOperationException("Live could not open. No browser or alternate player was launched.");
        }
        finally
        {
            if (ReferenceEquals(_openingCancellation, opening)) _openingCancellation = null;
            opening.Dispose();
            // Arbitration covers failure cleanup too, not only the successful open path.
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Does NOT acquire the parent media lease (the VOD handoff already owns it). Invalidate
    /// pending launch before waiting; drain both the actual launch task and retained window.
    /// </summary>
    public Task CloseCurrentAsync()
    {
        CheckUi();
        if (_closeCurrent is { IsCompleted: false }) return _closeCurrent;
        var cancel = _openingCancellation?.CancelAsync() ?? Task.CompletedTask;
        var pending = _opening;
        var closing = _current;
        var close = closing is null ? Task.CompletedTask : CloseWindowAsync(closing);
        return _closeCurrent = CloseCoreAsync();
        async Task CloseCoreAsync()
        {
            await Task.Yield();
            try {
                // T3-06: Opening cancellation starts before this close join; its callbacks do not synchronously await the close consumer.
                #pragma warning disable VSTHRD003
                await cancel;
                #pragma warning restore VSTHRD003
            } catch { Report("Live opening cancellation callback failed; resource drain continues."); }
            if (pending is not null) try {
                // T3-07: The captured opening task must finish, including CTS disposal in finally, before its owner can be replaced.
                #pragma warning disable VSTHRD003
                await pending;
                #pragma warning restore VSTHRD003
            } catch { }
            var releaseFailed = false;
            try {
                // T3-08: The real window close starts before this join and does not reacquire launcher admission; release must be confirmed.
                #pragma warning disable VSTHRD003
                await close;
                #pragma warning restore VSTHRD003
            } catch { releaseFailed = true; }
            // A pending launch can adopt a window after the initial close snapshot. Drain it too,
            // even when the earlier attempt faulted; do not repeat native disposal on that attempt.
            if (_current is { } current && !ReferenceEquals(current, closing))
            {
                try { await CloseWindowAsync(current); } catch { releaseFailed = true; }
            }
            if (releaseFailed || _current is { ReleaseConfirmed: false })
                throw new InvalidOperationException("Live release is unconfirmed.");
        }
    }
    private async Task CloseWindowAsync(LiveWindow window)
    {
        try { await window.CloseAsync(); }
        catch when (window.ReleaseConfirmed) { Report("Live close notification failed after release."); }
        if (!window.ReleaseConfirmed) throw new InvalidOperationException("Live release is unconfirmed.");
        ReconcileReleased(window);
    }
    private void OnReleased(object? sender, EventArgs args)
    {
        if (sender is LiveWindow window) ReconcileReleased(window);
    }
    private void ReconcileReleased(LiveWindow window)
    {
        if (!window.ReleaseConfirmed) return;
        // Confirmed native release, not registry callback success, controls launcher ownership.
        // The registry may be called again by the explicit drain; it must reconcile idempotently.
        window.Released -= OnReleased;
        if (ReferenceEquals(_current, window)) _current = null;
        try { _registry.Release(window); }
        catch { Report("Live registry accounting failed after confirmed release."); }
    }
    private LiveAccountStamp CaptureAccount()
        => new(_account.AccountId, _account.AccountGeneration, _account.AccountCancellationToken);
    private void CheckUi()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Live launcher requires its UI dispatcher.");
    }
    private void Report(string message) { try { _registry.ReportSafeFailure(message); } catch { } }
    public ValueTask DisposeAsync()
    {
        CheckUi();
        _disposed = true;
        return new(CloseCurrentAsync());
    }
}
