#nullable enable

using System.ComponentModel;
using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Live.Controls;
using BiliCopilot.UI.Live.Playback;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using WinUIEx;

namespace BiliCopilot.UI.Live.Hosting;

/// <summary>
/// Managed lifetime owner for a LiveView and native render child inside a borrowed MainWindow.
/// Register before InitializeNative. This owner borrows top-level presentation, but never creates or closes it.
/// No engine or native child is allocated in this ctor; MainWindow remains application-owned.
/// </summary>
public sealed partial class LiveWindow : ILivePresentation, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _report;
    private readonly MainWindow _mainWindow;
    // D2: CloseCoreAsync joins cancellation/opening/session/surface before CTS disposal; failed close retains the owner for confirmation.
    #pragma warning disable CA2213
    private readonly CancellationTokenSource _lifetime = new();
    #pragma warning restore CA2213
    private readonly object _closeSync = new();
    private LiveIslandSurface? _surface;
    private IClassicPlaybackBackend? _backend;
    private ILiveChatClient? _chat;
    private LiveSession? _session;
    private LiveView? _view;
    private Task _opening = Task.CompletedTask;
    private Task? _close;
    private Task _cancel = Task.CompletedTask;
    private bool _initialized;
    private bool _isPresented;
    private bool _parentClosed;

    public LiveWindow(MainWindow mainWindow, DispatcherQueue dispatcher, Action<string> report)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        CheckUi();
    }

    /// <summary>Borrowed application MainWindow; NEVER register, remove or close it as a Live-owned window.</summary>
    public WindowEx? NativeWindow => _mainWindow;
    public ILiveSession? Session => _session;
    public CancellationToken Lifetime => _lifetime.Token;
    public bool IsClosing => Volatile.Read(ref _close) is not null;
    public bool ReleaseConfirmed { get; private set; }
    /// <summary>Owned child initialized, not a new top-level or an activation-registration request.</summary>
    public event EventHandler? NativeCreated;
    public event EventHandler? Released;

    internal void InitializeNative()
    {
        CheckUi();
        if (_initialized || IsClosing) throw new InvalidOperationException("Live native initialization cannot be repeated.");
        _initialized = true;
        _mainWindow.LivePresentationChanged += OnPresentationChanged;
        _mainWindow.LiveOcclusionChanged += OnOcclusionChanged;
        _mainWindow.EmbeddedLiveBackRequested += OnBackRequested;
        _mainWindow.AppWindow.Changed += OnWindowChanged;
        _mainWindow.Closed += OnParentClosed;
        // Retain the partial child owner before fallible HWND/island initialization.
        _surface = new LiveIslandSurface(Report);
        _surface.Initialize(_mainWindow);
        NativeCreated?.Invoke(this, EventArgs.Empty);
    }

    internal LiveIslandSurface Surface => _surface ?? throw new InvalidOperationException("Live surface is unavailable.");
    internal void AdoptBackend(IClassicPlaybackBackend backend)
    {
        CheckUi();
        if (_backend is not null) throw new InvalidOperationException("Live backend adoption is unavailable.");
        _backend = backend;
    }
    internal void AdoptChat(ILiveChatClient chat)
    {
        CheckUi();
        if (_chat is not null) throw new InvalidOperationException("Live chat adoption is unavailable.");
        _chat = chat;
    }
    internal void AdoptSession(LiveSession session)
    {
        CheckUi();
        if (_session is not null || IsClosing) throw new InvalidOperationException("Live session adoption is unavailable.");
        _session = session;
        session.PropertyChanged += OnSessionChanged;
        // Backend/chat aliases remain retained for failed construction; session is sole releaser now.
    }

    internal Task OpenAsync(Richasy.BiliKernel.Models.Media.LiveInformation live, CancellationToken token)
    {
        CheckUi();
        if (_view is not null || _session is null || !_initialized || _parentClosed || IsClosing)
            throw new InvalidOperationException("Live window is not available for opening.");
        _opening = OpenCoreAsync();
        return _opening;
        async Task OpenCoreAsync()
        {
            await Task.Yield(); // Publish the owned opening task before fallible designer construction.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            _view = new LiveView(_session, Surface.Anchor, this); // Real designer implementation, not a stub.
            Surface.AttachOverlay(_view.DanmakuOverlay);
            _view.AttachTransportOverlay(Surface.TransportOverlayHost);
            AttachPresentationInput();
            if (!_mainWindow.TryShowEmbeddedLive(this, _view))
                throw new InvalidOperationException("The embedded Live presentation is owned by another view.");
            UpdatePresentation();
            await Surface.ReadyAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (IsClosing) throw new OperationCanceledException("Live window is closing.");
            await _session.OpenAsync(live, linked.Token);
        }
    }

    private void UpdatePresentation()
    {
        CheckUi();
        _surface?.SetPresented(_isPresented && !IsClosing && !_parentClosed
            && ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this)
            && _mainWindow.IsEmbeddedLiveVisible && _mainWindow.AppWindow.IsVisible);
    }

    private void OnPresentationChanged(object owner, bool isPresented)
    {
        if (!ReferenceEquals(owner, this)) return;
        // Consume false directly: Root emits it BEFORE changing XAML visibility/owner identity.
        // Only this identity-scoped event may grant presentation again, never a size/ready callback.
        _isPresented = isPresented && !IsClosing && !_parentClosed;
        UpdatePresentation();
        if (!_isPresented) RestorePresentation();
    }

    // The frozen MainWindow seam is Action (no payload). Re-read THIS owner and actual occlusion.
    private void OnOcclusionChanged() => UpdatePresentation();

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        try
        {
            UpdatePresentation();
            if (args.DidPresenterChange) SynchronizePresenter();
            _surface?.SafeUpdate();
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
                SafeFailureContext.TryWrite(FailureStage.LiveWindowChanged, error, () => this.Get<ILogger<LiveWindow>>());
            Report("Live window layout is unavailable.");
        }
    }
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_session?.IsClosing == true && !IsClosing) _ = Observe(CloseAsync());
    }
    private void OnBackRequested(object? sender, object owner)
    {
        if (!ReferenceEquals(owner, this) || !ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this)) return;
        // Root already hid the native presentation before restoring navigation. Do not gate navigation.
        _ = Observe(CloseAsync());
    }

    private void OnParentClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (ReleaseConfirmed) return; // A failed observer removal cannot undo an already-proven owned drain.
        _parentClosed = true;
        _isPresented = false;
        Report("Live parent closed before its owned drain; release cannot be inferred from parent closure.");
        _ = Observe(CloseAsync());
    }

    private void HidePresentation()
    {
        _isPresented = false;
        _surface?.SetPresented(false);
        RestorePresentation();
        if (ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this))
            _mainWindow.HideEmbeddedLive(this);
    }

    public Task CloseAsync()
    {
        lock (_closeSync)
        {
            if (_close is not null) return _close; // Including failure: no non-idempotent release retry.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = completion.Task;
            _cancel = _lifetime.CancelAsync();
            // Session admission must close now, not after the outstanding open completes.
            var sessionClose = _session?.CloseAsync();
            if (!_dispatcher.TryEnqueue(() => _ = CloseCoreAsync(completion, sessionClose)))
                completion.TrySetException(new InvalidOperationException("Live dispatcher is unavailable; window ownership is retained."));
            return _close;
        }
    }
    private async Task CloseCoreAsync(TaskCompletionSource completion, Task? sessionClose)
    {
        try
        {
            // Hide, but retain the attachment and render target until actual resource owners drain.
            try { HidePresentation(); } catch { Report("Live presentation hiding failed; resource drain continues."); }
            try {
                // T3-02: Cancellation was started before close joined it; callbacks request/observe close rather than synchronously waiting for it.
                #pragma warning disable VSTHRD003
                await _cancel;
                #pragma warning restore VSTHRD003
            } catch {
                Report("Live window cancellation callback failed; resource drain continues.");
            }
            try {
                // T3-03: Close joins the actual opening owner, including its finally; cancellation is not opening completion.
                #pragma warning disable VSTHRD003
                await _opening;
                #pragma warning restore VSTHRD003
            } catch { /* Real completion, not cancellation-as-drain. */ }
            if (_session is not null)
            {
                try { await (sessionClose ?? _session.CloseAsync()); }
                catch when (_session.ReleaseConfirmed) { Report("Live session notification failed after release."); }
                if (!_session.ReleaseConfirmed)
                    throw new InvalidOperationException("Live session release is unconfirmed.");
            }
            else
            {
                var failed = false;
                if (_chat is not null) try { await _chat.DisposeAsync(); } catch { failed = true; }
                if (_backend is not null) try { await _backend.DisposeAsync(); } catch { failed = true; }
                if (failed) throw new InvalidOperationException("Live partial construction release is unconfirmed.");
            }
            CheckUi();
            DetachPresentationInput();
            // No HWND/island teardown until both real owners have completed disposal.
            if (_surface is not null)
            {
                try { await _surface.CloseAsync(); }
                catch when (_surface.ReleaseConfirmed) { Report("Live surface notification failed after release."); }
                if (!_surface.ReleaseConfirmed)
                    throw new InvalidOperationException("Live surface release is unconfirmed.");
            }
            if (_session is not null)
                try { _session.PropertyChanged -= OnSessionChanged; } catch { Report("Live session observer detachment failed."); }
            // Only our slot may be cleared, and only after real session/surface disposal. A stale
            // owner must never clear a replacement. Parent closure is not child-release evidence.
            if (_parentClosed)
                throw new InvalidOperationException("Live parent closed before owned release could be confirmed.");
            try
            {
                if (ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this))
                    _mainWindow.ClearEmbeddedLive(this);
            }
            catch when (!ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this)
                && (_view is null || VisualTreeHelper.GetParent(_view) is null))
            {
                Report("Live view notification failed after detachment.");
            }
            if (ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this)
                || (_view is not null && VisualTreeHelper.GetParent(_view) is not null))
                throw new InvalidOperationException("Live view detachment is unconfirmed.");
            CompletePresentationRestoration();
            _view = null;
            try { _mainWindow.LivePresentationChanged -= OnPresentationChanged; } catch { Report("Live presentation observer detachment failed."); }
            try { _mainWindow.LiveOcclusionChanged -= OnOcclusionChanged; } catch { Report("Live occlusion observer detachment failed."); }
            try { _mainWindow.EmbeddedLiveBackRequested -= OnBackRequested; } catch { Report("Live back observer detachment failed."); }
            try { _mainWindow.AppWindow.Changed -= OnWindowChanged; } catch { Report("Live window observer detachment failed."); }
            try { _mainWindow.Closed -= OnParentClosed; } catch { Report("Live parent observer detachment failed."); }
            try { _lifetime.Dispose(); } catch { Report("Live lifetime bookkeeping failed after release."); }
            ReleaseConfirmed = true;
            if (Released is { } handlers)
                foreach (EventHandler handler in handlers.GetInvocationList())
                    try { handler(this, EventArgs.Empty); } catch { Report("Live registry release notification failed."); }
            completion.TrySetResult();
        }
        catch
        {
            Report("Live close is incomplete. The window and owners remain retained; replacement is blocked.");
            completion.TrySetException(new InvalidOperationException("Live close is incomplete; owners are retained."));
        }
    }
    private void CheckUi()
    {
        if (!_dispatcher.HasThreadAccess || !_mainWindow.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Live window requires its UI dispatcher.");
    }
    private void Report(string message)
    {
        try { _report(message); }
        catch { }
    }
    private async Task Observe(Task task) { try {
            // T3-05: This observer only joins an already-issued close task; it does not produce or block that close completion.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
    } catch { Report("Live close is incomplete; ownership is retained."); } }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
