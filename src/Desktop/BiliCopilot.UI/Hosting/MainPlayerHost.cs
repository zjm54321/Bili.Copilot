using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace BiliCopilot.UI.Hosting;

/// <summary>Owns a render child and one persistent island; borrows (never destroys) MainWindow.</summary>
public sealed class MainPlayerHost : IPlayerHost
{
    private readonly MainWindow _owner;
    private readonly FrameworkElement _slot;
    private readonly DispatcherQueue _queue;
    private readonly CursorGrid _root = new();
    private DesktopWindowXamlSource? _source;
    private IntPtr _renderHwnd;
    private bool _layoutQueued;
    private bool _closing;
    private bool _ready;
    private Action<bool>? _activeChanged;
    private OverlappedPresenter? _savedPresenter;
    private RectInt32 _savedBounds;
    private bool _savedMaximized;
    private PlayerPresentationMode _layout = PlayerPresentationMode.Normal;
    private PlayerPresentationMode? _publishedPresentation;
    private PlayerPresentationMode? _publishingPresentation;
    private long _publishingPresentationVersion;
    private long _presentationPublicationVersion;
    // TEMP Debug17 ready forensics: per-host attempt sequence, no global registry.
    private int _readyDiagnosticSequence;

    public MainPlayerHost(MainWindow owner, FrameworkElement slot)
    {
        _owner = owner;
        _slot = slot;
        _queue = owner.DispatcherQueue;
    }

    public bool IsEmbedded => true;
    public bool IsDisposed { get; private set; }
    public bool IsClosed => _closing || IsDisposed;
    public bool IsPresenterChanging { get; private set; }
    public IntPtr OwnerHwnd => _owner.GetWindowHandle();
    public IntPtr RenderHwnd => _renderHwnd;
    public IntPtr Handle => OwnerHwnd;
    public XamlRoot? XamlRoot => _source?.Content?.XamlRoot;
    public PlayerPresentationMode Presentation => _owner.AppWindow.Presenter.Kind switch
    {
        AppWindowPresenterKind.FullScreen => PlayerPresentationMode.FullScreen,
        AppWindowPresenterKind.CompactOverlay => PlayerPresentationMode.CompactOverlay,
        _ => _layout,
    };
    public event EventHandler<Size>? SizeChanged;
    public event EventHandler? PresentationChanged;
    public event EventHandler? CloseRequested;

    public async Task ReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (_ready) return;
        if (_renderHwnd != IntPtr.Zero)
            throw new InvalidOperationException("Partial embedded host creation must be closed before retrying.");
        var diagnosticSequence = unchecked(++_readyDiagnosticSequence);
        var diagnosticCheckSequence = 0;
        var diagnosticBounds = default(ReadyBoundsSnapshot);
        ReadyBoundsSnapshot? deliveredFailure = null;
        var diagnosticCheckFaultLogged = false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool InspectBounds(ReadyDiagnosticTrigger trigger, out RectInt32 bounds)
        {
            var snapshot = new ReadyBoundsSnapshot
            {
                CheckSequence = unchecked(++diagnosticCheckSequence),
                CheckUtc = DateTime.UtcNow,
                Trigger = trigger,
            };
            try
            {
                var hasBounds = TryGetBounds(out bounds, ref snapshot);
                snapshot.BoundsResult = hasBounds;
                return hasBounds;
            }
            catch (Exception ex)
            {
                snapshot.FailureReason = ReadyFailureReason.CheckFault;
                snapshot.ExceptionType = ex.GetType().FullName;
                snapshot.ExceptionHResult = ex.HResult;
                throw;
            }
            finally
            {
                diagnosticBounds = snapshot;
            }
        }
        void Check(ReadyDiagnosticTrigger trigger)
        {
            try
            {
                if (InspectBounds(trigger, out _)) ready.TrySetResult();
            }
            catch (Exception ex)
            {
                // Initial returns to the await below; later events resume that same waiter.
                // Only successful delivery transfers ownership. A terminal TCS cannot own this error.
                var delivered = ready.TrySetException(ex);
                if (delivered) deliveredFailure = diagnosticBounds;
                if (trigger != ReadyDiagnosticTrigger.Initial && !diagnosticCheckFaultLogged)
                {
                    diagnosticCheckFaultLogged = true;
                    LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.CheckFault, diagnosticBounds, ex);
                }
                if (!delivered) throw;
            }
            finally
            {
                if (trigger == ReadyDiagnosticTrigger.Initial)
                    LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Initial, diagnosticBounds);
            }
        }
        void Loaded(object sender, RoutedEventArgs args) => Check(ReadyDiagnosticTrigger.Loaded);
        void SizeChanged(object sender, SizeChangedEventArgs args) => Check(ReadyDiagnosticTrigger.SizeChanged);
        void LayoutUpdated(object? sender, object args) => Check(ReadyDiagnosticTrigger.LayoutUpdated);
        var subscribeLoaded = !_slot.IsLoaded;
        // Subscribe before inspecting; an already-loaded slot needs only layout notifications.
        if (subscribeLoaded) _slot.Loaded += Loaded;
        _slot.SizeChanged += SizeChanged;
        _slot.LayoutUpdated += LayoutUpdated;
        using var cancellation = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
        try
        {
            Check(ReadyDiagnosticTrigger.Initial);
            await ready.Task;
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsClosed, this);
            var hasBounds = InspectBounds(ReadyDiagnosticTrigger.PostAwait, out var bounds);
            if (!hasBounds) throw new InvalidOperationException("Embedded video slot became unavailable.");
            var creationBoundsSnapshot = diagnosticBounds;
            _renderHwnd = CreateRenderWindow(bounds);
            _source = new DesktopWindowXamlSource();
            _source.Initialize(Win32Interop.GetWindowIdFromWindow(_renderHwnd));
            _source.Content = _root;
            _slot.LayoutUpdated += OnLayoutUpdated;
            _slot.Unloaded += OnSlotUnloaded;
            _slot.XamlRoot.Changed += OnRootChanged;
            _owner.AppWindow.Changed += OnOwnerChanged;
            _owner.Activated += OnOwnerActivated;
            UpdateBounds();
            _ready = true;
            LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Ready, creationBoundsSnapshot);
        }
        catch (OperationCanceledException ex)
        {
            LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Cancelled, deliveredFailure ?? diagnosticBounds, ex);
            throw;
        }
        catch (Exception ex)
        {
            LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Fault, deliveredFailure ?? diagnosticBounds, ex);
            throw;
        }
        finally
        {
            if (subscribeLoaded) _slot.Loaded -= Loaded;
            _slot.SizeChanged -= SizeChanged;
            _slot.LayoutUpdated -= LayoutUpdated;
        }
    }

    private unsafe IntPtr CreateRenderWindow(RectInt32 bounds)
    {
        // Standard Win32 class avoids a managed window-procedure lifetime in NativeAOT.
        fixed (char* className = "STATIC")
        fixed (char* title = "BiliCopilot.MpvRender")
        {
            var hwnd = PInvoke.CreateWindowEx(0, className, title,
                WINDOW_STYLE.WS_CHILDWINDOW | WINDOW_STYLE.WS_CLIPCHILDREN | WINDOW_STYLE.WS_CLIPSIBLINGS,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, new(OwnerHwnd), HMENU.Null, HINSTANCE.Null);
            if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating embedded mpv render window failed.");
            return hwnd;
        }
    }

    private bool TryGetBounds(out RectInt32 bounds)
    {
        var snapshot = default(ReadyBoundsSnapshot);
        return TryGetBounds(out bounds, ref snapshot);
    }

    private bool TryGetBounds(out RectInt32 bounds, ref ReadyBoundsSnapshot snapshot)
    {
        bounds = default;
        // TEMP: preserve the original short-circuit order and property reads. Unreached fields stay null.
        snapshot.Closed = IsClosed;
        if (snapshot.Closed.Value) { snapshot.FailureReason = ReadyFailureReason.Closed; return false; }
        snapshot.SlotLoaded = _slot.IsLoaded;
        if (!snapshot.SlotLoaded.Value) { snapshot.FailureReason = ReadyFailureReason.SlotUnloaded; return false; }
        var visibility = _slot.Visibility;
        snapshot.SlotVisibility = (int)visibility;
        if (visibility != Visibility.Visible) { snapshot.FailureReason = ReadyFailureReason.SlotHidden; return false; }
        snapshot.SlotRootPresent = _slot.XamlRoot is not null;
        if (!snapshot.SlotRootPresent.Value) { snapshot.FailureReason = ReadyFailureReason.NoRoot; return false; }
        snapshot.RootMatchesOwner = _slot.XamlRoot == _owner.Content.XamlRoot;
        if (!snapshot.RootMatchesOwner.Value) { snapshot.FailureReason = ReadyFailureReason.RootMismatch; return false; }
        snapshot.OwnerVisible = _owner.AppWindow.IsVisible;
        if (!snapshot.OwnerVisible.Value) { snapshot.FailureReason = ReadyFailureReason.OwnerHidden; return false; }
        snapshot.OwnerIconic = (bool)PInvoke.IsIconic(new(OwnerHwnd));
        if (snapshot.OwnerIconic.Value) { snapshot.FailureReason = ReadyFailureReason.Iconic; return false; }
        var scale = _slot.XamlRoot.RasterizationScale;
        snapshot.Scale = scale;
        var origin = _slot.TransformToVisual(_owner.Content).TransformPoint(new Point(0, 0));
        snapshot.OriginX = origin.X;
        snapshot.OriginY = origin.Y;
        var actualWidth = _slot.ActualWidth;
        snapshot.ActualWidth = actualWidth;
        var width = actualWidth * scale;
        snapshot.Width = width;
        var actualHeight = _slot.ActualHeight;
        snapshot.ActualHeight = actualHeight;
        var height = actualHeight * scale;
        snapshot.Height = height;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width < 1 || height < 1)
        {
            snapshot.FailureReason = ReadyFailureReason.InvalidGeometry;
            return false;
        }
        bounds = new RectInt32((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            (int)Math.Round(width), (int)Math.Round(height));
        return true;
    }

    // TEMP Debug17 ready forensics. Remove with the ReadyAsync call sites and bounds overload.
    private enum ReadyDiagnosticPhase { Initial, Ready, Cancelled, Fault, CheckFault }
    private enum ReadyDiagnosticTrigger { Initial, Loaded, SizeChanged, LayoutUpdated, PostAwait }
    private enum ReadyFailureReason { None, Closed, SlotUnloaded, SlotHidden, NoRoot, RootMismatch, OwnerHidden, Iconic, InvalidGeometry, CheckFault }
    private struct ReadyBoundsSnapshot
    {
        public int CheckSequence;
        public DateTime CheckUtc;
        public ReadyDiagnosticTrigger Trigger;
        public ReadyFailureReason FailureReason;
        public bool? BoundsResult;
        public bool? Closed;
        public bool? SlotLoaded;
        public int? SlotVisibility;
        public bool? SlotRootPresent;
        public bool? RootMatchesOwner;
        public bool? OwnerVisible;
        public bool? OwnerIconic;
        public double? Scale;
        public double? OriginX;
        public double? OriginY;
        public double? ActualWidth;
        public double? ActualHeight;
        public double? Width;
        public double? Height;
        public string? ExceptionType;
        public int? ExceptionHResult;
    }

    private void LogReadySnapshot(int sequence, ReadyDiagnosticPhase phase, ReadyBoundsSnapshot snapshot, Exception? outcomeException = null)
    {
        // A failed diagnostic read/provider must neither mask nor introduce a playback exception.
        try
        {
            var logger = _owner.Get<ILogger<App>>();
            var hostId = RuntimeHelpers.GetHashCode(this);
            // Frozen values only: never re-read WinUI state (including after unload/cancellation).
            logger.LogInformation(
                "TEMP HostReady Host={Host} Sequence={Sequence} Phase={Phase} CheckSequence={CheckSequence} CheckUtc={CheckUtc} Trigger={Trigger} " +
                "BoundsResult={BoundsResult} FailureReason={FailureReason} CheckExceptionType={CheckExceptionType} CheckHResult={CheckHResult} " +
                "CheckClosed={CheckClosed} CheckSlotLoaded={CheckSlotLoaded} CheckSlotVisibility={CheckSlotVisibility} CheckSlotRootPresent={CheckSlotRootPresent} " +
                "CheckRootMatchesOwner={CheckRootMatchesOwner} CheckOwnerVisible={CheckOwnerVisible} CheckOwnerIconic={CheckOwnerIconic} " +
                "CheckScale={CheckScale} CheckOriginX={CheckOriginX} CheckOriginY={CheckOriginY} CheckActualWidth={CheckActualWidth} CheckActualHeight={CheckActualHeight} " +
                "CheckWidth={CheckWidth} CheckHeight={CheckHeight} OutcomeExceptionType={OutcomeExceptionType} OutcomeHResult={OutcomeHResult}",
                hostId, sequence, phase, snapshot.CheckSequence, snapshot.CheckUtc, snapshot.Trigger,
                snapshot.BoundsResult, snapshot.FailureReason, snapshot.ExceptionType, snapshot.ExceptionHResult,
                snapshot.Closed, snapshot.SlotLoaded, snapshot.SlotVisibility, snapshot.SlotRootPresent,
                snapshot.RootMatchesOwner, snapshot.OwnerVisible, snapshot.OwnerIconic,
                snapshot.Scale, snapshot.OriginX, snapshot.OriginY, snapshot.ActualWidth, snapshot.ActualHeight,
                snapshot.Width, snapshot.Height, outcomeException?.GetType().FullName, outcomeException?.HResult);
        }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("TEMP HostReady diagnostics unavailable."); }
    }

    private void OnLayoutUpdated(object? sender, object args) => QueueBounds();
    private void OnSlotUnloaded(object sender, RoutedEventArgs args) => HideNative();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueBounds();
    private void OnOwnerActivated(object sender, WindowActivatedEventArgs args)
        => _activeChanged?.Invoke(args.WindowActivationState != WindowActivationState.Deactivated);
    private void OnOwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            if (!IsPresenterChanging)
            {
                if (sender.Presenter.Kind == AppWindowPresenterKind.Overlapped && _savedPresenter is not null)
                {
                    ApplyPresentation(_layout);
                    return;
                }
                PublishPresentation();
            }
        }
        QueueBounds();
    }

    private void QueueBounds()
    {
        if (IsClosed || _layoutQueued) return;
        _layoutQueued = true;
        if (!_queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _layoutQueued = false;
            if (!IsClosed)
            {
                try { UpdateBounds(); }
                catch (Exception ex) { HideNative(); _owner.ReportEmbeddedHostFailure(ex); }
            }
        })) _layoutQueued = false;
    }

    private RectInt32? _lastBounds;
    private void UpdateBounds()
    {
        if (_renderHwnd == IntPtr.Zero || _source?.SiteBridge is null) return;
        if (_owner.IsEmbeddedPlayerRenderingSuspended || !TryGetBounds(out var bounds)) { HideNative(); return; }
        if (_lastBounds?.Equals(bounds) == true) return;
        if (!PInvoke.SetWindowPos(new(_renderHwnd), new(0), bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Positioning embedded mpv render window failed.");
        _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, bounds.Width, bounds.Height));
        _source.SiteBridge.MoveInZOrderAtTop();
        _lastBounds = bounds;
        SizeChanged?.Invoke(this, new Size(bounds.Width, bounds.Height));
    }

    private void HideNative()
    {
        _lastBounds = null;
        if (_renderHwnd != IntPtr.Zero) PInvoke.ShowWindow(new(_renderHwnd), SHOW_WINDOW_CMD.SW_HIDE);
    }

    /// <summary>Main-root modal/popup scope. Never pauses, reinitializes or reparents media.</summary>
    public IDisposable SuspendRendering()
        => _owner.SuspendEmbeddedPlayerRendering();

    internal void UpdateRenderingOcclusion()
    {
        if (IsClosed) return;
        if (_owner.IsEmbeddedPlayerRenderingSuspended) HideNative();
        else QueueBounds();
    }

    public Task SetPresentationAsync(PlayerPresentationMode mode)
    {
        ApplyPresentation(mode);
        return Task.CompletedTask;
    }

    // TEMP: explicit per-call correlation, never attached to a later queued bounds callback.
    internal Task SetPresentationAsync(PlayerPresentationMode mode, PresentationDiagnostic diagnostic)
    {
        try
        {
            ApplyPresentation(mode, diagnostic);
            return Task.CompletedTask;
        }
        finally { LogPresentationDiagnostic(mode, diagnostic); }
    }

    private void LogPresentationDiagnostic(PlayerPresentationMode mode, PresentationDiagnostic diagnostic)
    {
        try
        {
            _owner.Get<ILogger<App>>().LogInformation(
                "TEMP PresentationPerf HostSync VM={VM} Request={Request} Host={Host} Mode={Mode} SetPresenterMs={SetPresenterMs} RestoreMs={RestoreMs} MoveResizeMs={MoveResizeMs} RootApplyMs={RootApplyMs} NotifyMs={NotifyMs} Publications={Publications} IdenticalSkips={IdenticalSkips}",
                diagnostic.Vm, diagnostic.Sequence, RuntimeHelpers.GetHashCode(this), mode,
                diagnostic.SetPresenterMs, diagnostic.RestoreMs, diagnostic.MoveResizeMs,
                diagnostic.RootApplyMs, diagnostic.NotifyMs, diagnostic.Publications, diagnostic.IdenticalSkips);
        }
        catch (Exception) { return; } // Best-effort diagnostics never change presentation control flow.
    }

    // TEMP monotonic durations and anonymous identities only; one instance per VM request.
    internal sealed class PresentationDiagnostic(long sequence, int vm, int client, int player, int window)
    {
        internal readonly long Sequence = sequence;
        internal readonly int Vm = vm, Client = client, Player = player, Window = window;
        internal double InputSyncMs, InputWallMs, HostSyncMs, HostWallMs, CompatibilitySyncMs, CompatibilityWallMs;
        internal double FullScreenSyncMs, FullScreenWallMs, CompactSyncMs, CompactWallMs;
        internal double SetPresenterMs, RestoreMs, MoveResizeMs, RootApplyMs, NotifyMs;
        internal int Publications, IdenticalSkips;
    }

    private void PublishPresentation(PresentationDiagnostic? diagnostic = null)
    {
        var mode = Presentation;
        if (_publishedPresentation == mode ||
            (_publishingPresentation == mode && _publishingPresentationVersion == _presentationPublicationVersion))
        {
            if (diagnostic is not null) ++diagnostic.IdenticalSkips;
            return;
        }
        var version = ++_presentationPublicationVersion;
        var previousPublishing = _publishingPresentation;
        var previousPublishingVersion = _publishingPresentationVersion;
        _publishingPresentation = mode;
        _publishingPresentationVersion = version;
        // A partially applied publication must be retryable, including a subscriber failure.
        _publishedPresentation = null;
        try
        {
            var start = Stopwatch.GetTimestamp();
            try { _owner.ApplyPlayerPresentation(mode); }
            finally { if (diagnostic is not null) diagnostic.RootApplyMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            start = Stopwatch.GetTimestamp();
            try { PresentationChanged?.Invoke(this, EventArgs.Empty); }
            finally { if (diagnostic is not null) diagnostic.NotifyMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            // A nested different-mode publication supersedes this one.
            if (version == _presentationPublicationVersion) _publishedPresentation = mode;
            if (diagnostic is not null) ++diagnostic.Publications;
        }
        finally
        {
            _publishingPresentation = previousPublishing;
            _publishingPresentationVersion = previousPublishingVersion;
        }
    }

    private void ApplyPresentation(PlayerPresentationMode mode, PresentationDiagnostic? diagnostic = null)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        IsPresenterChanging = true;
        _owner.UpdatePlayerBoundsSuppression(this, true);
        try
        {
            var transient = mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay;
            if (transient && _savedPresenter is null)
            {
                _savedPresenter = _owner.AppWindow.Presenter as OverlappedPresenter
                    ?? throw new InvalidOperationException("Main window has no restorable overlapped presenter.");
                var position = _owner.AppWindow.Position;
                var size = _owner.AppWindow.Size;
                _savedBounds = new(position.X, position.Y, size.Width, size.Height);
                _savedMaximized = _savedPresenter.State == OverlappedPresenterState.Maximized;
            }
            if (transient)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    _owner.AppWindow.SetPresenter(mode == PlayerPresentationMode.FullScreen
                        ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.CompactOverlay);
                }
                finally { if (diagnostic is not null) diagnostic.SetPresenterMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            }
            else
            {
                _layout = mode;
                RestorePresenter(diagnostic);
            }
            PublishPresentation(diagnostic);
            QueueBounds();
        }
        finally
        {
            IsPresenterChanging = false;
            // Release on a subsequent dispatcher turn, after restoration size/position notifications.
            _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                _owner.UpdatePlayerBoundsSuppression(this, _savedPresenter is not null || IsPresenterChanging));
        }
    }

    private void RestorePresenter(PresentationDiagnostic? diagnostic = null)
    {
        if (_savedPresenter is null) return;
        var start = Stopwatch.GetTimestamp();
        try { _owner.AppWindow.SetPresenter(_savedPresenter); }
        finally { if (diagnostic is not null) diagnostic.SetPresenterMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        start = Stopwatch.GetTimestamp();
        try
        {
            if (_savedMaximized) _savedPresenter.Maximize();
            else _savedPresenter.Restore();
        }
        finally { if (diagnostic is not null) diagnostic.RestoreMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (!_savedMaximized)
        {
            start = Stopwatch.GetTimestamp();
            try { _owner.AppWindow.MoveAndResize(_savedBounds); }
            finally { if (diagnostic is not null) diagnostic.MoveResizeMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        }
        _savedPresenter = null;
    }

    public ValueTask DisposeAsync()
    {
        if (IsDisposed) return ValueTask.CompletedTask;
        _closing = true;
        HideNative();
        _slot.LayoutUpdated -= OnLayoutUpdated;
        _slot.Unloaded -= OnSlotUnloaded;
        if (_slot.XamlRoot is { } root) root.Changed -= OnRootChanged;
        _owner.AppWindow.Changed -= OnOwnerChanged;
        _owner.Activated -= OnOwnerActivated;
        _source?.Dispose();
        _source = null;
        if (_renderHwnd != IntPtr.Zero)
        {
            if (!PInvoke.DestroyWindow(new(_renderHwnd)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Destroying embedded mpv render window failed.");
            _renderHwnd = IntPtr.Zero;
        }
        _owner.UpdatePlayerBoundsSuppression(this, true);
        RestorePresenter();
        _owner.ApplyPlayerPresentation(PlayerPresentationMode.Normal);
        _queue.TryEnqueue(DispatcherQueuePriority.Low, () => _owner.UpdatePlayerBoundsSuppression(this, false));
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public AppWindow GetWindow() => _owner.AppWindow;
    public void Show(bool restorePosition = true) => _owner.Activate();
    public void SetUIElement(UIElement? element)
    {
        _root.Children.Clear();
        if (element is not null) _root.Children.Add(element);
        _lastBounds = null;
        QueueBounds();
    }
    public void SetActiveAction(Action<bool>? action) => _activeChanged = action;
    public void SetTheme(ElementTheme theme) => _root.RequestedTheme = theme;
    public void SetCaptionButtonVisibility(bool visible) { /* Main shell owns its caption. */ }
    public void DelayUpdateTitleBarPadding(bool delaySec = false) { /* Never attach overlay titlebar to main HWND. */ }
    public void ResetFocus()
    {
        if (!IsClosed && _root.Children.FirstOrDefault() is Control control) control.Focus(FocusState.Programmatic);
    }
    public void ShowCursor() => _root.ShowCursor();
    public void HideCursor() => _root.HideCursor();
    public void ShowTip(string message, InfoType type)
    {
        if (_root.Children.FirstOrDefault() is PlayerOverlay overlay) overlay.ShowTip(message, type);
    }
    public void SetTopMost(bool value)
    {
        if (_owner.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = value;
    }
    public bool IsTopMost() => (_owner.AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop == true;
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public void EnterFullScreen() => ApplyPresentation(PlayerPresentationMode.FullScreen);
    public void ExitFullScreen() => ApplyPresentation(_layout);
    public void EnterCompactOverlay() => ApplyPresentation(PlayerPresentationMode.CompactOverlay);
    public void ExitCompactOverlay() => ApplyPresentation(_layout);
}
