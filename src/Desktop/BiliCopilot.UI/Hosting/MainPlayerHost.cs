using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private NormalRestoration? _normalRestore;
    private OverlappedPresenter? _compactPresenter;
    private RectInt32? _compactBounds;
    private bool _compactTopMost = true;
    private PlayerPresentationMode _presentation = PlayerPresentationMode.Normal;
    private PlayerPresentationMode _fullScreenReturn = PlayerPresentationMode.Normal;
    private long _presentationRequestVersion;
    private int _presentationDepth;
    private bool _immediateBoundsApplying;
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
    public bool IsPresenterChanging => _presentationDepth != 0;
    public IntPtr OwnerHwnd => _owner.GetWindowHandle();
    public IntPtr RenderHwnd => _renderHwnd;
    public IntPtr Handle => OwnerHwnd;
    public XamlRoot? XamlRoot => _source?.Content?.XamlRoot;
    // Custom compact is native Overlapped. Native kind is not the logical mode.
    public PlayerPresentationMode Presentation => _presentation;
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
                if (sender.Presenter.Kind == AppWindowPresenterKind.Overlapped && _normalRestore is not null &&
                    (_presentation == PlayerPresentationMode.FullScreen || !ReferenceEquals(sender.Presenter, _compactPresenter)))
                {
                    ApplyPresentation(_presentation == PlayerPresentationMode.FullScreen ? _fullScreenReturn : _layout);
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
        mode = PlayerPresentationModes.Normalize(mode);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!_owner.IsCurrentEmbeddedHost(this)) return;
        var requestVersion = ++_presentationRequestVersion;
        ++_presentationDepth;
        try
        {
            _owner.UpdatePlayerBoundsSuppression(this, true);
            var transient = mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay;
            if (transient && _normalRestore is null)
            {
                _normalRestore = CaptureNormalRestoration();
            }
            if (_presentation == PlayerPresentationMode.CompactOverlay &&
                ReferenceEquals(_owner.AppWindow.Presenter, _compactPresenter)) CaptureCompactState();
            if (mode == PlayerPresentationMode.FullScreen && _presentation != PlayerPresentationMode.FullScreen)
                _fullScreenReturn = PlayerPresentationModes.Normalize(_presentation);
            if (mode == PlayerPresentationMode.CompactOverlay)
            {
                ApplyCompactPresenter(requestVersion, diagnostic);
            }
            else if (mode == PlayerPresentationMode.FullScreen)
            {
                var start = Stopwatch.GetTimestamp();
                try { _owner.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); }
                finally { if (diagnostic is not null) diagnostic.SetPresenterMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            }
            else
            {
                RestorePresenter(requestVersion, diagnostic);
            }
            if (!IsCurrentPresentationRequest(requestVersion)) return;
            if (!transient) _layout = mode;
            _presentation = mode;
            PublishPresentation(diagnostic);
            if (!IsCurrentPresentationRequest(requestVersion) || _publishedPresentation != mode) return;
            // Retain the snapshot through native restoration AND fallible root/subscriber publication.
            ApplyImmediateBounds(requestVersion, _presentationPublicationVersion);
            if (!IsCurrentPresentationRequest(requestVersion)) return;
            if (!transient) _normalRestore = null;
            QueueBounds();
        }
        finally
        {
            --_presentationDepth;
            // Release on a subsequent dispatcher turn, after restoration size/position notifications.
            if (requestVersion == _presentationRequestVersion)
                _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (requestVersion == _presentationRequestVersion)
                        _owner.UpdatePlayerBoundsSuppression(this, _normalRestore is not null || IsPresenterChanging ||
                            _presentation is PlayerPresentationMode.CompactOverlay or PlayerPresentationMode.FullScreen);
                });
        }
    }

    private bool IsCurrentPresentationRequest(long version)
        => version == _presentationRequestVersion && !IsClosed && _owner.IsCurrentEmbeddedHost(this);

    private sealed record NormalRestoration(OverlappedPresenter Presenter, WINDOWPLACEMENT Placement,
        bool TopMost, double MinWidth, double MinHeight, bool HasBorder, bool HasTitleBar,
        TitleBarHeightOption TitleBarHeight);

    private NormalRestoration CaptureNormalRestoration()
    {
        var presenter = _owner.AppWindow.Presenter as OverlappedPresenter
            ?? throw new InvalidOperationException("Main window has no restorable overlapped presenter.");
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement(new(OwnerHwnd), ref placement))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading main window restore placement failed.");
        // rcNormalPosition is actual restore placement even while maximized; keep its native coordinates.
        return new(presenter, placement, presenter.IsAlwaysOnTop, _owner.MinWidth, _owner.MinHeight,
            presenter.HasBorder, presenter.HasTitleBar, _owner.AppWindow.TitleBar.PreferredHeightOption);
    }

    private void CaptureCompactState()
    {
        var position = _owner.AppWindow.Position;
        var size = _owner.AppWindow.Size;
        _compactBounds = new(position.X, position.Y, size.Width, size.Height);
        _compactTopMost = _compactPresenter!.IsAlwaysOnTop;
    }

    private void ApplyCompactPresenter(long version, PresentationDiagnostic? diagnostic = null)
    {
        if (_compactPresenter is null)
        {
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMaximizable = false;
            presenter.IsResizable = true;
            _compactPresenter = presenter;
        }
        _owner.MinWidth = 160;
        if (!IsCurrentPresentationRequest(version)) return;
        _owner.MinHeight = 90;
        if (!IsCurrentPresentationRequest(version)) return;
        var start = Stopwatch.GetTimestamp();
        try { _owner.AppWindow.SetPresenter(_compactPresenter); }
        finally { if (diagnostic is not null) diagnostic.SetPresenterMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (!IsCurrentPresentationRequest(version)) return;
        start = Stopwatch.GetTimestamp();
        try { _compactPresenter.Restore(); }
        finally { if (diagnostic is not null) diagnostic.RestoreMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (!IsCurrentPresentationRequest(version)) return;
        _compactPresenter.IsAlwaysOnTop = _compactTopMost;
        if (!IsCurrentPresentationRequest(version)) return;
        var area = DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest) ?? DisplayArea.Primary;
        var work = area.WorkArea; // physical screen pixels, as are AppWindow bounds.
        var scale = HwndExtensions.GetDpiForWindow(OwnerHwnd) / 96d;
        var bounds = _compactBounds ?? new RectInt32(work.X + work.Width - (int)Math.Round(480 * scale),
            work.Y + work.Height - (int)Math.Round(270 * scale), (int)Math.Round(480 * scale), (int)Math.Round(270 * scale));
        bounds.Width = Math.Clamp(bounds.Width, Math.Min(work.Width, (int)Math.Round(160 * scale)), work.Width);
        bounds.Height = Math.Clamp(bounds.Height, Math.Min(work.Height, (int)Math.Round(90 * scale)), work.Height);
        bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
        bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
        start = Stopwatch.GetTimestamp();
        try { _owner.AppWindow.MoveAndResize(bounds); }
        finally { if (diagnostic is not null) diagnostic.MoveResizeMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (IsCurrentPresentationRequest(version)) CaptureCompactState();
    }

    private void ApplyImmediateBounds(long requestVersion, long publicationVersion)
    {
        bool Current() => _queue.HasThreadAccess && _ready && !IsClosed &&
            _owner.IsCurrentEmbeddedHost(this) && requestVersion == _presentationRequestVersion &&
            publicationVersion == _presentationPublicationVersion && _publishedPresentation == Presentation;
        if (_immediateBoundsApplying || !Current()) return;
        _immediateBoundsApplying = true;
        try
        {
            _slot.UpdateLayout();
            if (Current()) UpdateBounds();
        }
        finally { _immediateBoundsApplying = false; }
    }

    private void RestorePresenter(long? requestVersion = null, PresentationDiagnostic? diagnostic = null)
    {
        if (_normalRestore is not { } saved) return;
        bool Current() => requestVersion is null || IsCurrentPresentationRequest(requestVersion.Value);
        var start = Stopwatch.GetTimestamp();
        try { _owner.AppWindow.SetPresenter(saved.Presenter); }
        finally { if (diagnostic is not null) diagnostic.SetPresenterMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (!Current()) return;
        _owner.MinWidth = saved.MinWidth;
        if (!Current()) return;
        _owner.MinHeight = saved.MinHeight;
        if (!Current()) return;
        saved.Presenter.SetBorderAndTitleBar(saved.HasBorder, saved.HasTitleBar);
        if (!Current()) return;
        saved.Presenter.IsAlwaysOnTop = saved.TopMost;
        if (!Current()) return;
        _owner.AppWindow.TitleBar.PreferredHeightOption = saved.TitleBarHeight;
        if (!Current()) return;
        start = Stopwatch.GetTimestamp();
        try
        {
            // WINDOWPLACEMENT restores the true normal rectangle AND showCmd/maximized state.
            var placement = saved.Placement;
            if (!PInvoke.SetWindowPlacement(new(OwnerHwnd), in placement))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Restoring main window placement failed.");
        }
        finally { if (diagnostic is not null) diagnostic.RestoreMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
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
        _presentation = PlayerPresentationMode.Normal;
        _normalRestore = null;
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
        if (_root.Children.FirstOrDefault() is PlayerOverlay overlay && overlay.ViewModel is { } owner)
            TryShowTip(owner, message, type);
    }
    internal bool TryShowTip(PlayerViewModel expectedOwner, string message, InfoType type)
    {
        if (!_queue.HasThreadAccess || !_ready || IsClosed || !_slot.IsLoaded ||
            _owner.IsEmbeddedPlayerRenderingSuspended || !_owner.IsCurrentEmbeddedHost(this, expectedOwner) ||
            _root.Children.FirstOrDefault() is not PlayerOverlay overlay || !overlay.IsLoaded ||
            overlay.XamlRoot is null || !ReferenceEquals(overlay.ViewModel, expectedOwner)) return false;
        overlay.ShowTip(message, type);
        return true;
    }

    public bool TryBeginCaptionDrag(PlayerViewModel expectedOwner, PointerRoutedEventArgs args, UIElement dragBackground)
    {
        bool Current() => _queue.HasThreadAccess && _ready && !IsClosed && !IsPresenterChanging &&
            Presentation == PlayerPresentationMode.CompactOverlay && _slot.IsLoaded &&
            _owner.IsCurrentEmbeddedHost(this, expectedOwner) && dragBackground is FrameworkElement { IsLoaded: true } element &&
            element.XamlRoot == XamlRoot;
        if (!Current() || dragBackground is Control || args.Handled ||
            args.Pointer.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Mouse ||
            !args.GetCurrentPoint(dragBackground).Properties.IsLeftButtonPressed) return false;
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && !ReferenceEquals(source, dragBackground))
        {
            // Controls and hyperlinks are not caption backgrounds, even if their template bubbles.
            if (source is Control || source is Microsoft.UI.Xaml.Documents.Hyperlink) return false;
            source = VisualTreeHelper.GetParent(source);
        }
        if (source is null) return false;
        var hwnd = OwnerHwnd;
        var version = _presentationRequestVersion;
        args.Handled = true;
        dragBackground.ReleasePointerCaptures();
        PInvoke.ReleaseCapture();
        if (!Current() || version != _presentationRequestVersion || hwnd != OwnerHwnd) return false;
        // Native move loop can reenter; no lock/capture is held and no post-loop owner writes occur.
        PInvoke.SendMessage(new(hwnd), 0x00A1u /* WM_NCLBUTTONDOWN */, new(2u) /* HTCAPTION */, new(0));
        return true;
    }

    public bool RequestOwnerClose(PlayerViewModel expectedOwner)
        => _queue.HasThreadAccess && _ready && !IsClosed && !IsPresenterChanging &&
            _owner.RequestEmbeddedOwnerClose(this, expectedOwner);

    public void SetTopMost(bool value)
    {
        if (IsClosed || IsPresenterChanging || !_owner.IsCurrentEmbeddedHost(this)) return;
        if (_owner.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            try { presenter.IsAlwaysOnTop = value; }
            finally
            {
                if (ReferenceEquals(presenter, _compactPresenter)) _compactTopMost = presenter.IsAlwaysOnTop;
            }
        }
    }
    public bool IsTopMost() => (_owner.AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop == true;
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public void EnterFullScreen() => ApplyPresentation(PlayerPresentationMode.FullScreen);
    public void ExitFullScreen() => ApplyPresentation(PlayerPresentationModes.Normalize(_fullScreenReturn));
    public void EnterCompactOverlay() => ApplyPresentation(PlayerPresentationMode.CompactOverlay);
    public void ExitCompactOverlay() => ApplyPresentation(_layout);
}
