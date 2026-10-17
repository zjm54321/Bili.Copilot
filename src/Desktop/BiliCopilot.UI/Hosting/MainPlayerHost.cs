using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace BiliCopilot.UI.Hosting;

/// <summary>Owns one render/island child host; borrows (never destroys) MainWindow.</summary>
public sealed partial class MainPlayerHost : IPlayerHost
{
    internal const double CompactMinWidth = 432;
    internal const double CompactMinHeight = 243;

    private readonly MainWindow _owner;
    private readonly FrameworkElement _slot;
    private readonly DispatcherQueue _queue;
    private readonly CursorGrid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private DesktopWindowXamlSource? _source;
    private IntPtr _renderHwnd;
    private IntPtr _islandHwnd;
    private Microsoft.UI.Content.DesktopChildSiteBridge? _bridge;
    private TaskCompletionSource? _firstPresentation;
    private int _bridgeClosedSeen;
    private bool _layoutQueued;
    private bool _boundsUpdating;
    private bool _boundsFollowup;
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

    public MainPlayerHost(MainWindow owner, FrameworkElement slot)
    {
        if (!owner.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Embedded surface requires its owning UI dispatcher.");
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
    private bool IsIslandUnavailable => IsClosed || Volatile.Read(ref _bridgeClosedSeen) != 0;
    public XamlRoot? XamlRoot => IsIslandUnavailable ? null : _root.XamlRoot;
    // Custom compact is native Overlapped. Native kind is not the logical mode.
    public PlayerPresentationMode Presentation => _presentation;
    public event EventHandler<Size>? SizeChanged;
    public event EventHandler? PresentationChanged;
    public event EventHandler? CloseRequested;

    public async Task ReadyAsync(CancellationToken cancellationToken)
    {
        if (!_queue.HasThreadAccess)
            throw new InvalidOperationException("Embedded surface requires its owning UI dispatcher.");
        ObjectDisposedException.ThrowIf(IsIslandUnavailable, this);
        if (_ready) return;
        if (_renderHwnd != IntPtr.Zero)
            throw new InvalidOperationException("Partial embedded host creation must be closed before retrying.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            try
            {
                if (TryGetBounds(out _)) ready.TrySetResult();
            }
            catch (Exception ex)
            {
                if (!ready.TrySetException(ex)) throw;
            }
        }
        void Loaded(object sender, RoutedEventArgs args) => Check();
        void SizeChanged(object sender, SizeChangedEventArgs args) => Check();
        void LayoutUpdated(object? sender, object args) => Check();
        var subscribeLoaded = !_slot.IsLoaded;
        // Subscribe before inspecting; an already-loaded slot needs only layout notifications.
        if (subscribeLoaded) _slot.Loaded += Loaded;
        _slot.SizeChanged += SizeChanged;
        _slot.LayoutUpdated += LayoutUpdated;
        using var cancellation = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
        try
        {
            Check();
            await ready.Task;
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsIslandUnavailable, this);
            if (!TryGetBounds(out _)) throw new InvalidOperationException("Embedded video slot became unavailable.");
            // Exactly the LiveIslandSurface topology: mpv's wid and the island's parent are
            // the SAME hidden STATIC child. A separate opaque parent would cover the video.
            _renderHwnd = CreateRenderWindow(new RectInt32(0, 0, 1, 1));
            _source = new DesktopWindowXamlSource();
            _source.Initialize(Win32Interop.GetWindowIdFromWindow(_renderHwnd));
            var bridge = _source.SiteBridge
                ?? throw new InvalidOperationException("Embedded XAML island has no site bridge.");
            _bridge = bridge;
            _islandHwnd = Win32Interop.GetWindowFromWindowId(bridge.WindowId);
            bridge.Closed += OnBridgeClosed;
            _source.Content = _root; // Fresh per-host root; never part of the owner's XAML tree.
            if (IsIslandUnavailable) throw new InvalidOperationException("Embedded XAML island site bridge is closed.");
            _slot.LayoutUpdated += OnLayoutUpdated;
            _slot.Unloaded += OnSlotUnloaded;
            _slot.XamlRoot.Changed += OnRootChanged;
            _owner.AppWindow.Changed += OnOwnerChanged;
            _owner.Activated += OnOwnerActivated;
            var firstPresentation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstPresentation = firstPresentation;
            using var presentationCancellation = cancellationToken.Register(() => firstPresentation.TrySetCanceled(cancellationToken));
            try { UpdateBounds(); }
            catch (Exception ex) { firstPresentation.TrySetException(ex); }
            // Occlusion release, layout and owner notifications drive UpdateBounds; no timer/polling or early Ready.
            await firstPresentation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsIslandUnavailable, this);
            _ready = true;
            RequestChromeRegionUpdate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeFailureContext.TryWrite(FailureStage.EmbeddedHostReady, ex, () => _owner.Get<ILogger<App>>());
            throw;
        }
        finally
        {
            if (subscribeLoaded) _slot.Loaded -= Loaded;
            _slot.SizeChanged -= SizeChanged;
            _slot.LayoutUpdated -= LayoutUpdated;
        }
    }

    private void OnBridgeClosed()
    {
        if (Interlocked.Exchange(ref _bridgeClosedSeen, 1) != 0) return;
        // Closed must never read source.Content or any other WinRT/XAML projection.
        if (IsClosed) return;
        SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            logger.LogError("Embedded XAML island site bridge is closed."));
        _firstPresentation?.TrySetException(new InvalidOperationException("Embedded XAML island site bridge is closed."));
        _queue.TryEnqueue(() => PublishBoundsFailure(new InvalidOperationException("Embedded XAML island site bridge is closed.")));
    }

    private static partial class NativeSnapshot
    {
        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(IntPtr value);

    }

    private unsafe IntPtr CreateRenderWindow(RectInt32 bounds, string windowTitle = "BiliCopilot.MpvRender")
    {
        // Standard Win32 class avoids a managed window-procedure lifetime in NativeAOT.
        fixed (char* className = "STATIC")
        fixed (char* title = windowTitle)
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
        bounds = default;
        if (IsClosed || !_slot.IsLoaded || _slot.Visibility != Visibility.Visible ||
            _slot.XamlRoot is null || _slot.XamlRoot != _owner.Content.XamlRoot ||
            !_owner.AppWindow.IsVisible || (bool)PInvoke.IsIconic(new(OwnerHwnd))) return false;
        var scale = _slot.XamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0) return false;
        var origin = _slot.TransformToVisual(_owner.Content).TransformPoint(new Point(0, 0));
        var width = _slot.ActualWidth * scale;
        var height = _slot.ActualHeight * scale;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width < 1 || height < 1) return false;
        bounds = new RectInt32(Pixel(origin.X * scale), Pixel(origin.Y * scale), Pixel(width), Pixel(height));
        return true;
    }

    private static int Pixel(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new InvalidOperationException("Embedded layout geometry is invalid.");
        return checked((int)Math.Round(value));
    }

    private bool TryGetClip(RectInt32 bounds, out RectInt32 clip)
    {
        clip = default;
        if (IsIslandUnavailable || _slot.XamlRoot is not { } root) return false;
        var scale = root.RasterizationScale;
        var visible = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        DependencyObject? node = _slot;
        while (node is FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0) return false;
            var transform = element.TransformToVisual(_owner.Content);
            var limits = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            visible.Intersect(new Rect(limits.X * scale, limits.Y * scale, limits.Width * scale, limits.Height * scale));
            if (element.Clip is RectangleGeometry rectangle)
            {
                var localClip = transform.TransformBounds(rectangle.Rect);
                visible.Intersect(new Rect(localClip.X * scale, localClip.Y * scale, localClip.Width * scale, localClip.Height * scale));
            }
            else if (element.Clip is not null) return false;
            if (ReferenceEquals(element, _owner.Content)) break;
            node = VisualTreeHelper.GetParent(element);
        }

        if (!ReferenceEquals(node, _owner.Content) || !PInvoke.GetClientRect(new(OwnerHwnd), out var client)) return false;
        visible.Intersect(new Rect(client.X, client.Y, client.Width, client.Height));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        // LiveIslandSurface rounds the visible clip inward to prevent native content bleeding.
        var left = Math.Clamp((int)Math.Ceiling(visible.Left) - bounds.X, 0, bounds.Width);
        var top = Math.Clamp((int)Math.Ceiling(visible.Top) - bounds.Y, 0, bounds.Height);
        var right = Math.Clamp((int)Math.Floor(visible.Right) - bounds.X, left, bounds.Width);
        var bottom = Math.Clamp((int)Math.Floor(visible.Bottom) - bounds.Y, top, bounds.Height);
        clip = new RectInt32(left, top, right - left, bottom - top);
        return clip.Width > 0 && clip.Height > 0;
    }

    private void OnLayoutUpdated(object? sender, object args) => QueueBounds();
    private void OnSlotUnloaded(object sender, RoutedEventArgs args)
    {
        HideNative();
    }
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
                catch (Exception ex) { PublishBoundsFailure(ex); }
            }
        })) _layoutQueued = false;
    }

    private RectInt32? _lastBounds;
    private RectInt32? _lastClip;
    private enum BoundsFailureStage { Capture, Measure, Hide, Position, ResizeIsland, ReorderIsland, PublishSize, Chrome }

    private void UpdateBounds()
    {
        if (IsIslandUnavailable) return;
        if (_boundsUpdating) { _boundsFollowup = true; return; }
        _boundsUpdating = true;
        var stage = BoundsFailureStage.Capture;
        var failed = false;
        try
        {
            var source = _source;
            var renderHwnd = _renderHwnd;
            bool SameSource() => !IsIslandUnavailable && ReferenceEquals(source, _source) && renderHwnd == _renderHwnd;
            var bridge = _bridge;
            if (!SameSource()) return;
            if (source is null || renderHwnd == IntPtr.Zero || bridge is null)
            {
                _lastBounds = null;
                stage = BoundsFailureStage.Chrome;
                RequestChromeRegionUpdate();
                return;
            }

            // One bridge per initialized source; inspect its HWND lifetime, not projected wrapper identity.
            bool Current()
            {
                if (!SameSource()) return false;
                var bridgeClosed = bridge.IsClosed;
                if (!SameSource()) return false;
                if (bridgeClosed || Volatile.Read(ref _bridgeClosedSeen) != 0 || !(bool)PInvoke.IsWindow(new(_islandHwnd)))
                    throw new InvalidOperationException("Embedded XAML island site bridge is closed.");
                return true;
            }
            bool CanPresent()
            {
                if (!Current()) return false;
                var suspended = _owner.IsEmbeddedPlayerRenderingSuspended;
                var hidden = suspended || !_slot.IsLoaded;
                if (!Current()) return false;
                if (!hidden) return true;
                stage = BoundsFailureStage.Hide;
                HideNative();
                return false;
            }

            stage = BoundsFailureStage.Measure;
            if (!CanPresent()) return;
            var hasBounds = TryGetBounds(out var bounds);
            if (!CanPresent()) return;
            if (!hasBounds || !TryGetClip(bounds, out var clip))
            {
                stage = BoundsFailureStage.Hide;
                HideNative();
                return;
            }
            if (_lastBounds?.Equals(bounds) == true && _lastClip?.Equals(clip) == true)
            {
                stage = BoundsFailureStage.Chrome;
                RequestChromeRegionUpdate();
                return;
            }

            // Match LiveIslandSurface: resize/reorder the island while its STATIC parent is
            // hidden, then position/show that parent. Island geometry is parent-local pixels.
            PInvoke.ShowWindow(new(renderHwnd), SHOW_WINDOW_CMD.SW_HIDE);
            if (!CanPresent()) return;
            if (_lastClip?.Equals(clip) != true)
            {
                var region = NativeSnapshot.CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
                if (region == IntPtr.Zero) throw new InvalidOperationException("Embedded clipping region creation failed.");
                if (NativeSnapshot.SetWindowRgn(renderHwnd, region, true) == 0)
                {
                    NativeSnapshot.DeleteObject(region);
                    throw new InvalidOperationException("Embedded clipping region attachment failed.");
                }
                _lastClip = clip; // Windows owns the successfully attached region.
                if (!CanPresent()) return;
            }
            stage = BoundsFailureStage.ResizeIsland;
            var islandBounds = new RectInt32(0, 0, bounds.Width, bounds.Height);
            bridge.MoveAndResize(islandBounds);
            if (!CanPresent()) return;
            stage = BoundsFailureStage.ReorderIsland;
            bridge.MoveInZOrderAtTop();
            if (!CanPresent()) return;
            stage = BoundsFailureStage.Position;
            bool positioned = PInvoke.SetWindowPos(new(renderHwnd), new(0), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            // Preserve last-error before any further native/property call can overwrite it.
            var positionError = positioned ? 0 : Marshal.GetLastWin32Error();
            if (!Current()) return;
            if (!positioned) throw new Win32Exception(positionError, "Positioning embedded mpv render window failed.");
            if (!CanPresent()) return;
            if (!NativeSnapshot.IsWindowVisible(renderHwnd) || !NativeSnapshot.IsWindowVisible(_islandHwnd))
                throw new InvalidOperationException("Embedded native windows did not become visible.");
            stage = BoundsFailureStage.PublishSize;
            _lastBounds = bounds;
            SizeChanged?.Invoke(this, new Size(bounds.Width, bounds.Height));
            if (!CanPresent()) return;
            stage = BoundsFailureStage.Chrome;
            RequestChromeRegionUpdate();
            if (CanPresent()) _firstPresentation?.TrySetResult();
        }
        catch (Exception ex)
        {
            failed = true;
            _firstPresentation?.TrySetException(ex);
            SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
                logger.LogError("Embedded native host layout failed. BoundsStage={BoundsStage} ExceptionType={ExceptionType} HResult={HResult}",
                    stage, ex.GetType().FullName, ex.HResult));
            throw; // A still-current native/XAML failure is not a successful layout or a retry signal.
        }
        finally
        {
            _boundsUpdating = false;
            var followup = _boundsFollowup;
            _boundsFollowup = false;
            if (followup && !failed) QueueBounds(); // Reuse the one coalesced dispatcher callback, never recurse/poll.
        }
    }

    private void PublishBoundsFailure(Exception exception)
    {
        // UpdateBounds already logged finite context, including when native callbacks retired this host.
        if (IsClosed || !_owner.IsCurrentEmbeddedHost(this)) return;
        var player = _owner.Get<AppViewModel>().EmbeddedPlayer;
        if (player is null) return;
        HideNative();
        // Hiding can reenter. Pin the originating VM as well as the host before publishing its error.
        if (!IsClosed && _owner.IsCurrentEmbeddedHost(this, player)) player.ErrorMessage = exception.Message;
    }

    private void HideNative()
    {
        var renderHwnd = _renderHwnd;
        _lastBounds = null;
        _lastClip = null;
        CancelCompactHeaderDrag();
        // As in LiveIslandSurface, hide the parent, not the bridge-owned child HWND.
        if (renderHwnd != IntPtr.Zero && renderHwnd == _renderHwnd) PInvoke.ShowWindow(new(renderHwnd), SHOW_WINDOW_CMD.SW_HIDE);
    }

    private void CancelCompactHeaderDrag()
    {
        if (!IsIslandUnavailable && _root.Children.FirstOrDefault() is PlayerOverlay overlay)
            overlay.CancelCompactHeaderDrag();
    }

    internal void UpdateRenderingOcclusion()
    {
        if (IsClosed) return;
        if (_owner.IsEmbeddedPlayerRenderingSuspended)
        {
            HideNative();
        }
        else QueueBounds();
    }

    public Task SetPresentationAsync(PlayerPresentationMode mode)
    {
        ApplyPresentation(mode);
        return Task.CompletedTask;
    }

    private void PublishPresentation()
    {
        var mode = Presentation;
        if (_publishedPresentation == mode ||
            (_publishingPresentation == mode && _publishingPresentationVersion == _presentationPublicationVersion))
        {
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
            _owner.ApplyPlayerPresentation(mode);
            PresentationChanged?.Invoke(this, EventArgs.Empty);
            // A nested different-mode publication supersedes this one.
            if (version == _presentationPublicationVersion) _publishedPresentation = mode;
        }
        finally
        {
            _publishingPresentation = previousPublishing;
            _publishingPresentationVersion = previousPublishingVersion;
        }
    }

    private void ApplyPresentation(PlayerPresentationMode mode)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        mode = PlayerPresentationModes.Normalize(mode);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!_owner.IsCurrentEmbeddedHost(this)) return;
        var requestVersion = ++_presentationRequestVersion;
        ++_presentationDepth;
        try
        {
            RequestChromeRegionUpdate();
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
                ApplyCompactPresenter(requestVersion);
            }
            else if (mode == PlayerPresentationMode.FullScreen)
            {
                _owner.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                RestorePresenter(requestVersion);
            }
            if (!IsCurrentPresentationRequest(requestVersion)) return;
            if (!transient) _layout = mode;
            _presentation = mode;
            PublishPresentation();
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
            RequestChromeRegionUpdate();
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
        => version == _presentationRequestVersion && !IsIslandUnavailable && _owner.IsCurrentEmbeddedHost(this);

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

    private void ApplyCompactPresenter(long version)
    {
        if (_compactPresenter is null)
        {
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMaximizable = false;
            presenter.IsResizable = true;
            _compactPresenter = presenter;
        }
        _owner.MinWidth = CompactMinWidth;
        if (!IsCurrentPresentationRequest(version)) return;
        _owner.MinHeight = CompactMinHeight;
        if (!IsCurrentPresentationRequest(version)) return;
        _owner.AppWindow.SetPresenter(_compactPresenter);
        if (!IsCurrentPresentationRequest(version)) return;
        _compactPresenter.Restore();
        if (!IsCurrentPresentationRequest(version)) return;
        _compactPresenter.IsAlwaysOnTop = _compactTopMost;
        if (!IsCurrentPresentationRequest(version)) return;
        var area = DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest) ?? DisplayArea.Primary;
        var work = area.WorkArea; // physical screen pixels, as are AppWindow bounds.
        var scale = HwndExtensions.GetDpiForWindow(OwnerHwnd) / 96d;
        var bounds = _compactBounds ?? new RectInt32(work.X + work.Width - (int)Math.Round(480 * scale),
            work.Y + work.Height - (int)Math.Round(270 * scale), (int)Math.Round(480 * scale), (int)Math.Round(270 * scale));
        bounds.Width = Math.Clamp(bounds.Width, Math.Min(work.Width, (int)Math.Round(CompactMinWidth * scale)), work.Width);
        bounds.Height = Math.Clamp(bounds.Height, Math.Min(work.Height, (int)Math.Round(CompactMinHeight * scale)), work.Height);
        bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
        bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
        _owner.AppWindow.MoveAndResize(bounds);
        if (IsCurrentPresentationRequest(version)) CaptureCompactState();
    }

    private void ApplyImmediateBounds(long requestVersion, long publicationVersion)
    {
        bool Current() => _queue.HasThreadAccess && _ready && !IsIslandUnavailable &&
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

    private void RestorePresenter(long? requestVersion = null)
    {
        if (_normalRestore is not { } saved) return;
        bool Current() => requestVersion is null || IsCurrentPresentationRequest(requestVersion.Value);
        _owner.AppWindow.SetPresenter(saved.Presenter);
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
        // WINDOWPLACEMENT restores the true normal rectangle AND showCmd/maximized state.
        var placement = saved.Placement;
        if (!PInvoke.SetWindowPlacement(new(OwnerHwnd), in placement))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Restoring main window placement failed.");
    }

    public ValueTask DisposeAsync()
    {
        if (IsDisposed) return ValueTask.CompletedTask;
        if (!_queue.HasThreadAccess)
            throw new InvalidOperationException("Embedded surface requires its owning UI dispatcher.");
        CancelCompactHeaderDrag();
        _closing = true;
        _firstPresentation?.TrySetException(new ObjectDisposedException(nameof(MainPlayerHost)));
        HideNative();
        _slot.LayoutUpdated -= OnLayoutUpdated;
        _slot.Unloaded -= OnSlotUnloaded;
        if (_slot.XamlRoot is { } root) root.Changed -= OnRootChanged;
        _owner.AppWindow.Changed -= OnOwnerChanged;
        _owner.Activated -= OnOwnerActivated;
        // Unsubscribe while the bridge is live, BEFORE source.Dispose can synchronously close
        // it. If it already closed, do not invoke another native event accessor on it.
        if (Volatile.Read(ref _bridgeClosedSeen) == 0)
        {
            if (_bridge is { } bridge) bridge.Closed -= OnBridgeClosed;
        }
        _source?.Dispose();
        _source = null;
        _bridge = null;
        _islandHwnd = IntPtr.Zero;
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
        ObjectDisposedException.ThrowIf(IsIslandUnavailable, this);
        _root.Children.Clear();
        if (element is not null) _root.Children.Add(element);
        _lastBounds = null;
        RequestChromeRegionUpdate();
        QueueBounds();
    }
    public void SetActiveAction(Action<bool>? action) => _activeChanged = action;
    public void SetTheme(ElementTheme theme)
    {
        if (!IsIslandUnavailable) _root.RequestedTheme = theme;
    }
    public void SetCaptionButtonVisibility(bool visible) { /* Main shell owns its caption. */ }
    public void DelayUpdateTitleBarPadding(bool delaySec = false) { /* Never attach overlay titlebar to main HWND. */ }
    public void ResetFocus()
    {
        if (!IsIslandUnavailable && _root.Children.FirstOrDefault() is Control control) control.Focus(FocusState.Programmatic);
    }
    public void ShowCursor() { if (!IsIslandUnavailable) _root.ShowCursor(); }
    public void HideCursor() { if (!IsIslandUnavailable) _root.HideCursor(); }
    public void ShowTip(string message, InfoType type)
    {
        if (!IsIslandUnavailable && _root.Children.FirstOrDefault() is PlayerOverlay overlay && overlay.ViewModel is { } owner)
            TryShowTip(owner, message, type);
    }
    internal bool TryShowTip(PlayerViewModel expectedOwner, string message, InfoType type)
    {
        if (!_queue.HasThreadAccess || !_ready || IsIslandUnavailable || !_slot.IsLoaded ||
            _owner.IsEmbeddedPlayerRenderingSuspended || !_owner.IsCurrentEmbeddedHost(this, expectedOwner) ||
            _root.Children.FirstOrDefault() is not PlayerOverlay overlay || !overlay.IsLoaded ||
            overlay.XamlRoot is null || !ReferenceEquals(overlay.ViewModel, expectedOwner)) return false;
        overlay.ShowTip(message, type);
        return true;
    }

    internal void RequestChromeRegionUpdate()
    {
        if (!_queue.HasThreadAccess || IsIslandUnavailable) return;
        // Keep callers' lifecycle notification, but publish NO native caption regions.
        // A compact drag is owned solely by its captured, physically pressed pointer.
        if (IsPresenterChanging || Presentation != PlayerPresentationMode.CompactOverlay
            || !_slot.IsLoaded || _owner.IsEmbeddedPlayerRenderingSuspended)
            CancelCompactHeaderDrag();
    }

    internal bool TryMoveCompactWindow(PlayerViewModel expectedOwner, PointInt32 position)
    {
        if (!_queue.HasThreadAccess || IsIslandUnavailable || !_ready || IsPresenterChanging
            || expectedOwner.IsPlaybackClosing || expectedOwner.IsWindowDeactivated || expectedOwner.IsPopupVisible
            || Presentation != PlayerPresentationMode.CompactOverlay || _owner.IsEmbeddedPlayerRenderingSuspended
            || !_owner.IsCurrentEmbeddedHost(this, expectedOwner)) return false;
        _owner.AppWindow.Move(position);
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
