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
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private Application? _diagnosticApplication;
    private string _islandOperation = "NotCreated";
    private string _lastIslandState = "not-captured";
    private IntPtr _diagnosticOwnerHwnd;
    private Microsoft.UI.Content.DesktopChildSiteBridge? _bridge;
    private TaskCompletionSource? _firstPresentation;
    private int _bridgeClosedSeen;
    private bool _firstShowLogged;
    private string? _videoOutput = "not-created";
    private string? _gpuContext = "not-created";
    private string _occlusionReason = "NotPositioned";
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
    // TEMP Debug17 ready forensics: per-host attempt sequence, no global registry.
    private int _readyDiagnosticSequence;

#if DEBUG
    private DesktopWindowXamlSource? _bridgeProbeSource;
    private Microsoft.UI.Content.DesktopChildSiteBridge? _bridgeProbeBridge;
    private ClosableNotifierHandler? _bridgeProbeClosedHandler;
    private IntPtr? _bridgeProbeOwnerHwnd, _bridgeProbeRenderHwnd, _bridgeProbeIslandHwnd;
    private int _bridgeProbeHost, _bridgeProbeReadyAttempt, _bridgeProbeClosedSeen;
    private long _bridgeProbePositionSequence;
    private bool _bridgeProbeAttachAttempted, _bridgeProbeReturnLogged;
    private volatile bool _bridgeProbeInPositionCall;
    private volatile BridgeOwnReleasePhase _bridgeProbeOwnRelease;
    private bool? _bridgeProbePositioned;
    private int? _bridgeProbePositionError;
    private BridgeProbeSnapshot? _bridgeProbeBeforePosition;

    private enum BridgeProbePoint { Attached, Unavailable, BeforePosition, PositionReturn, Closed, BoundsFailure }
    private enum BridgeOwnReleasePhase { None, SourceDispose, DestroyRender }
    private readonly record struct BridgeProbeSnapshot(int Host, int ReadyAttempt, long PositionSequence,
        BridgeProbePoint Point, bool InPositionCall, bool? OwnerThread, bool Closing, bool SourceCurrent,
        BridgeOwnReleasePhase OwnReleasePhase, bool? OwnerAlive, bool? RenderAlive, bool? BridgeAlive,
        bool? Positioned, int? PositionError);
#endif

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
            ObjectDisposedException.ThrowIf(IsIslandUnavailable, this);
            var hasBounds = InspectBounds(ReadyDiagnosticTrigger.PostAwait, out var bounds);
            if (!hasBounds) throw new InvalidOperationException("Embedded video slot became unavailable.");
            var creationBoundsSnapshot = diagnosticBounds;
            _diagnosticOwnerHwnd = OwnerHwnd;
            // Exactly the LiveIslandSurface topology: mpv's wid and the island's parent are
            // the SAME hidden STATIC child. A separate opaque parent would cover the video.
            _renderHwnd = CreateRenderWindow(new RectInt32(0, 0, 1, 1));
            _diagnosticApplication = Application.Current;
            _diagnosticApplication.UnhandledException += OnIslandUnhandledException;
            _root.Loaded += OnIslandLoaded;
            _islandOperation = "Initialize";
            _source = new DesktopWindowXamlSource();
            _source.Initialize(Win32Interop.GetWindowIdFromWindow(_renderHwnd));
            var bridge = _source.SiteBridge
                ?? throw new InvalidOperationException("Embedded XAML island has no site bridge.");
            _bridge = bridge;
            _islandHwnd = Win32Interop.GetWindowFromWindowId(bridge.WindowId);
            bridge.Closed += OnBridgeClosed;
#if DEBUG
            TryAttachBridgeProbe(_source, bridge, diagnosticSequence);
#endif
            _islandOperation = "AttachContent";
            _source.Content = _root; // Fresh per-host root; never part of the owner's XAML tree.
            if (IsIslandUnavailable) throw new InvalidOperationException("Embedded XAML island site bridge is closed.");
            _islandOperation = "Idle";
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
            LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Ready, creationBoundsSnapshot);
        }
        catch (OperationCanceledException ex)
        {
            LogReadySnapshot(diagnosticSequence, ReadyDiagnosticPhase.Cancelled, deliveredFailure ?? diagnosticBounds, ex);
            throw;
        }
        catch (Exception ex)
        {
            SafeFailureContext.TryWrite(FailureStage.EmbeddedHostReady, ex, () => _owner.Get<ILogger<App>>());
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

    internal void LogWindowSnapshot(string phase, string? videoOutput = null, string? gpuContext = null, int? nativeError = null, int? hresult = null, string? stack = null)
    {
        // Diagnostics never read media URLs, headers or cookies, and cannot replace a playback failure.
        try
        {
            _videoOutput = videoOutput ?? _videoOutput;
            _gpuContext = gpuContext ?? _gpuContext;
            var timestamp = Stopwatch.GetTimestamp();
            var owner = DescribeWindow(_diagnosticOwnerHwnd);
            var render = DescribeWindow(_renderHwnd);
            var island = DescribeWindow(_islandHwnd);
            var mpv = DescribeMpvWindows(_renderHwnd);
            SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
                logger.LogInformation(
                    "Embedded WindowSnapshot Phase={Phase} Tick={Tick} Frequency={Frequency} Occlusion={Occlusion} Closing={Closing} Owner={Owner} Render={Render} Island={Island} Mpv={Mpv} Vo={Vo} GpuContext={GpuContext} NativeError={NativeError} HResult={HResult} ManagedStack={ManagedStack}",
                    phase, timestamp, Stopwatch.Frequency, _occlusionReason, _closing, owner, render, island, mpv,
                    _videoOutput, _gpuContext, nativeError, hresult, stack));
        }
        catch (Exception) { Debug.WriteLine("Embedded window snapshot unavailable."); }
    }

    private void OnBridgeClosed()
    {
        var nativeError = Marshal.GetLastPInvokeError();
        if (Interlocked.Exchange(ref _bridgeClosedSeen, 1) != 0) return;
        LogWindowSnapshot("BridgeClosed", nativeError: nativeError, stack: Environment.StackTrace);
        // Both 2.11 dumps crashed in source.Content from this callback. No WinRT/XAML
        // projection may be read here, even behind a managed try/catch.
        LogIslandState("BridgeClosed");
        if (IsClosed) return;
        _firstPresentation?.TrySetException(new InvalidOperationException("Embedded XAML island site bridge is closed."));
        _queue.TryEnqueue(() => PublishBoundsFailure(new InvalidOperationException("Embedded XAML island site bridge is closed.")));
    }

    private void OnIslandLoaded(object sender, RoutedEventArgs args) => LogIslandState("ContentLoaded");

    private void OnIslandUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        // Observe XAML layout/Loaded failures without consuming them or changing the app's policy.
        LogIslandState("ApplicationUnhandledException");
        SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            logger.LogError(args.Exception,
                "Embedded XAML exception Host={Host} Operation={Operation} XamlMessage={XamlMessage}",
                RuntimeHelpers.GetHashCode(this), _islandOperation, args.Message));
    }

    private void LogIslandState(string phase, RectInt32? rect = null)
    {
        // Content can access freed native state during Closed/Dispose (not a catchable COM
        // failure). Capture live facts BEFORE native calls; terminal logs use ONLY this string.
        if (!IsIslandUnavailable)
        {
            string Inspect(Func<object?> read)
            {
                if (IsIslandUnavailable) return "closed:not-read";
                try { return read()?.ToString() ?? "null"; }
                catch (Exception ex) { return $"unavailable:{ex.GetType().Name}/0x{ex.HResult:X8}"; }
            }

            static string ClientRect(IntPtr hwnd)
                => PInvoke.GetClientRect(new(hwnd), out var client)
                    ? $"({client.X},{client.Y},{client.Width},{client.Height})" : "unavailable";

            _lastIslandState = $"OwnerClient={ClientRect(_diagnosticOwnerHwnd)} ParentClient={ClientRect(_renderHwnd)} " +
                $"OwnerDpi={HwndExtensions.GetDpiForWindow(_diagnosticOwnerHwnd)} ParentDpi={HwndExtensions.GetDpiForWindow(_renderHwnd)} " +
                $"Source={(_source is null ? "null" : "retained")} SourceContentMatches={Inspect(() => ReferenceEquals(_source?.Content, _root))} " +
                $"RootParent={Inspect(() => _root.Parent?.GetType().FullName)} RootXamlRoot={Inspect(() => _root.XamlRoot is { } root ? RuntimeHelpers.GetHashCode(root) : null)} " +
                $"ContentIsland={Inspect(() => _root.XamlRoot?.ContentIsland)} RootLoaded={Inspect(() => _root.IsLoaded)} " +
                $"RootSize={Inspect(() => new Size(_root.ActualWidth, _root.ActualHeight))} " +
                $"IslandScale={Inspect(() => _root.XamlRoot?.RasterizationScale)} SlotScale={Inspect(() => _slot.XamlRoot?.RasterizationScale)}";
        }

        SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            logger.LogInformation(
                "Embedded XamlState Phase={Phase} Host={Host} Operation={Operation} OwnerThread={OwnerThread} Rect={Rect} Closed={Closed} Frozen={Frozen} State={State}",
                phase, RuntimeHelpers.GetHashCode(this), _islandOperation, _queue.HasThreadAccess,
                rect is { } value ? $"({value.X},{value.Y},{value.Width},{value.Height})" : "null",
                Volatile.Read(ref _bridgeClosedSeen) != 0, IsIslandUnavailable, _lastIslandState));
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "0";
        var chain = new List<string>();
        for (var current = hwnd; current != IntPtr.Zero; current = NativeSnapshot.GetParent(current))
        {
            var thread = NativeSnapshot.GetWindowThreadProcessId(current, out var process);
            chain.Add($"0x{current:X}/tid={thread}/pid={process}/visible={NativeSnapshot.IsWindowVisible(current)}");
        }

        return string.Join(" -> ", chain);
    }

    private static unsafe string DescribeMpvWindows(IntPtr renderHwnd)
    {
        if (renderHwnd == IntPtr.Zero) return "none";
        var windows = new List<string>();
        WNDENUMPROC callback = (hwnd, _) =>
        {
            Span<char> className = stackalloc char[64];
            var length = PInvoke.GetClassName(hwnd, className);
            if (className[..length].SequenceEqual("mpv")) windows.Add(DescribeWindow(hwnd));
            return true;
        };
        try { PInvoke.EnumChildWindows(new(renderHwnd), callback, default); }
        finally { GC.KeepAlive(callback); }
        return windows.Count == 0 ? "none" : string.Join("; ", windows);
    }

    private static partial class NativeSnapshot
    {
        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial IntPtr GetParent(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

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

#if DEBUG
    private void TryAttachBridgeProbe(DesktopWindowXamlSource source, Microsoft.UI.Content.DesktopChildSiteBridge bridge, int readyAttempt)
    {
        if (_bridgeProbeAttachAttempted) return;
        _bridgeProbeAttachAttempted = true;
        var point = BridgeProbePoint.Unavailable;
        try
        {
            _bridgeProbeHost = RuntimeHelpers.GetHashCode(this);
            _bridgeProbeReadyAttempt = readyAttempt;
            _bridgeProbeSource = source;
            _bridgeProbeBridge = bridge;
            _bridgeProbeClosedHandler = OnProbeBridgeClosed; // ClosableNotifierHandler has no sender, args, or reason.
            _bridgeProbeRenderHwnd = _renderHwnd;
            try { _bridgeProbeOwnerHwnd = OwnerHwnd; } catch { }
            try { _bridgeProbeIslandHwnd = Win32Interop.GetWindowFromWindowId(bridge.WindowId); } catch { }
            bridge.Closed += _bridgeProbeClosedHandler;
            point = BridgeProbePoint.Attached;
        }
        catch { } // Observation is optional, never a Ready failure.
        LogBridgeProbe(CaptureBridgeProbe(point));
    }

    private static bool? ProbeWindowAlive(IntPtr? hwnd)
    {
        if (hwnd is not { } handle || handle == IntPtr.Zero) return null;
        try { return (bool)PInvoke.IsWindow(new(handle)); }
        catch { return null; }
    }

    private BridgeProbeSnapshot? CaptureBridgeProbe(BridgeProbePoint point)
    {
        try
        {
            bool? ownerThread = null;
            try { ownerThread = _queue.HasThreadAccess; } catch { }
            var ownerHwnd = _bridgeProbeOwnerHwnd;
            var renderHwnd = _bridgeProbeRenderHwnd;
            var islandHwnd = _bridgeProbeIslandHwnd;
            var snapshot = new BridgeProbeSnapshot(_bridgeProbeHost, _bridgeProbeReadyAttempt,
                System.Threading.Interlocked.Read(ref _bridgeProbePositionSequence), point, _bridgeProbeInPositionCall,
                ownerThread, _closing, _bridgeProbeSource is not null && ReferenceEquals(_bridgeProbeSource, _source),
                _bridgeProbeOwnRelease, null, null, null, _bridgeProbePositioned, _bridgeProbePositionError);
            // Only captured HWNDs are queried. No source/bridge/window-property reads in the Closed callback.
            return snapshot with
            {
                OwnerAlive = ProbeWindowAlive(ownerHwnd),
                RenderAlive = ProbeWindowAlive(renderHwnd),
                BridgeAlive = ProbeWindowAlive(islandHwnd),
            };
        }
        catch { return null; }
    }

    private void LogBridgeProbe(BridgeProbeSnapshot? snapshot)
        => SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            logger.LogInformation("TEMP HostBridge Probe={Probe}", snapshot));

    private void OnProbeBridgeClosed()
    {
        if (System.Threading.Interlocked.Exchange(ref _bridgeProbeClosedSeen, 1) != 0) return;
        var snapshot = CaptureBridgeProbe(BridgeProbePoint.Closed);
        LogBridgeProbe(snapshot); // First callback is sampled synchronously, never queued/re-measured.
    }

    private void ProbeBeforePosition()
    {
        System.Threading.Interlocked.Increment(ref _bridgeProbePositionSequence);
        _bridgeProbePositioned = null;
        _bridgeProbePositionError = null;
        _bridgeProbeBeforePosition = null;
        _bridgeProbeBeforePosition = CaptureBridgeProbe(BridgeProbePoint.BeforePosition);
    }

    private void ProbePositionReturned(bool positioned, int positionError)
    {
        _bridgeProbePositioned = positioned;
        _bridgeProbePositionError = positionError;
        var returned = CaptureBridgeProbe(BridgeProbePoint.PositionReturn);
        if (_bridgeProbeReturnLogged || (positioned && returned?.BridgeAlive != false
            && System.Threading.Volatile.Read(ref _bridgeProbeClosedSeen) == 0)) return;
        _bridgeProbeReturnLogged = true;
        var before = _bridgeProbeBeforePosition;
        SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            logger.LogInformation("TEMP HostBridge Position Before={Before} Return={Return}", before, returned));
    }

    private void DetachBridgeProbe()
    {
        try
        {
            if (Volatile.Read(ref _bridgeClosedSeen) == 0 && _bridgeProbeBridge is { } bridge && _bridgeProbeClosedHandler is { } handler)
                bridge.Closed -= handler;
        }
        catch { } // An already-closed bridge cannot replace the primary release failure.
        finally
        {
            _bridgeProbeBridge = null;
            _bridgeProbeSource = null;
            _bridgeProbeClosedHandler = null;
        }
        // Never Close/Dispose the retained diagnostic bridge; the source owns it.
    }
#endif

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
        if (!double.IsFinite(scale) || scale <= 0)
        {
            snapshot.FailureReason = ReadyFailureReason.InvalidGeometry;
            return false;
        }
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
    private void OnSlotUnloaded(object sender, RoutedEventArgs args)
    {
        _occlusionReason = "SlotUnloaded";
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
                _occlusionReason = suspended ? "OcclusionLease" : hidden ? "SlotUnloaded" : "None";
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
                _occlusionReason = "SlotOrOwnerUnavailable";
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

            var firstShow = !_firstShowLogged;
            if (firstShow)
            {
                _firstShowLogged = true;
                LogWindowSnapshot("FirstShowBefore");
            }
            // Match LiveIslandSurface: resize/reorder the island while its STATIC parent is
            // hidden, then position/show that parent. Island geometry is parent-local pixels.
            // Capture before ShowWindow too: it can synchronously deliver bridge Closed.
            LogIslandState("BeforeHideHost", bounds);
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
            _islandOperation = "MoveAndResize";
            LogIslandState("BeforeMoveAndResize", islandBounds);
            bridge.MoveAndResize(islandBounds);
            if (!CanPresent()) return;
            stage = BoundsFailureStage.ReorderIsland;
            bridge.MoveInZOrderAtTop();
            if (!CanPresent()) return;
            _islandOperation = "ShowHosts";
            stage = BoundsFailureStage.Position;
            bool positioned;
            int positionError;
#if DEBUG
            ProbeBeforePosition();
            try
            {
                _bridgeProbeInPositionCall = true;
#endif
            positioned = PInvoke.SetWindowPos(new(renderHwnd), new(0), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            // Preserve last-error before any further native/property call can overwrite it.
            positionError = positioned ? 0 : Marshal.GetLastWin32Error();
#if DEBUG
            }
            finally { _bridgeProbeInPositionCall = false; }
            ProbePositionReturned(positioned, positionError);
#endif
            if (!Current()) return;
            if (!positioned) throw new Win32Exception(positionError, "Positioning embedded mpv render window failed.");
            if (!CanPresent()) return;
            if (firstShow) LogWindowSnapshot("FirstShowAfter", nativeError: positionError);
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
            LogIslandState("BoundsException");
            if (stage == BoundsFailureStage.ResizeIsland)
                SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
                    logger.LogError(ex, "Embedded XAML MoveAndResize failed. Host={Host}", RuntimeHelpers.GetHashCode(this)));
            _firstPresentation?.TrySetException(ex);
#if DEBUG
            var bridgeProbe = CaptureBridgeProbe(BridgeProbePoint.BoundsFailure);
#endif
            SafeFailureContext.TryWriteFacts(() => _owner.Get<ILogger<App>>(), logger =>
            {
                var context = SafeFailureContext.Capture(ex);
#if DEBUG
                logger.LogError("Embedded native host layout failed. BoundsStage={BoundsStage} ExceptionType={ExceptionType} HResult={HResult} CodeMethods={CodeMethods} Probe={Probe}",
                    stage, context.ExceptionType, context.HResult, context.CodeMethods, bridgeProbe);
#else
                logger.LogError("Embedded native host layout failed. BoundsStage={BoundsStage} ExceptionType={ExceptionType} HResult={HResult} CodeMethods={CodeMethods}",
                    stage, context.ExceptionType, context.HResult, context.CodeMethods);
#endif
            });
            throw; // A still-current native/XAML failure is not a successful layout or a retry signal.
        }
        finally
        {
            _islandOperation = "Idle";
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
        _occlusionReason = "BoundsFailure";
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

    /// <summary>Main-root modal/popup scope. Never pauses, reinitializes or reparents media.</summary>
    public IDisposable SuspendRendering()
        => _owner.SuspendEmbeddedPlayerRendering();

    internal void UpdateRenderingOcclusion()
    {
        if (IsClosed) return;
        if (_owner.IsEmbeddedPlayerRenderingSuspended)
        {
            _occlusionReason = "OcclusionLease";
            HideNative();
        }
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
        _owner.MinWidth = CompactMinWidth;
        if (!IsCurrentPresentationRequest(version)) return;
        _owner.MinHeight = CompactMinHeight;
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
        bounds.Width = Math.Clamp(bounds.Width, Math.Min(work.Width, (int)Math.Round(CompactMinWidth * scale)), work.Width);
        bounds.Height = Math.Clamp(bounds.Height, Math.Min(work.Height, (int)Math.Round(CompactMinHeight * scale)), work.Height);
        bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
        bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
        start = Stopwatch.GetTimestamp();
        try { _owner.AppWindow.MoveAndResize(bounds); }
        finally { if (diagnostic is not null) diagnostic.MoveResizeMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
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
        if (!_queue.HasThreadAccess)
            throw new InvalidOperationException("Embedded surface requires its owning UI dispatcher.");
        LogIslandState("BeforeDispose");
        CancelCompactHeaderDrag();
        _closing = true;
        _firstPresentation?.TrySetException(new ObjectDisposedException(nameof(MainPlayerHost)));
        HideNative();
        _slot.LayoutUpdated -= OnLayoutUpdated;
        _slot.Unloaded -= OnSlotUnloaded;
        if (_slot.XamlRoot is { } root) root.Changed -= OnRootChanged;
        _owner.AppWindow.Changed -= OnOwnerChanged;
        _owner.Activated -= OnOwnerActivated;
        _islandOperation = "DisposeSource";
        // Unsubscribe while the bridge is live, BEFORE source.Dispose can synchronously close
        // it. If it already closed, do not invoke another native event accessor on it.
        if (Volatile.Read(ref _bridgeClosedSeen) == 0)
        {
            if (_bridge is { } bridge) bridge.Closed -= OnBridgeClosed;
            _root.Loaded -= OnIslandLoaded;
        }
        if (_diagnosticApplication is { } application) application.UnhandledException -= OnIslandUnhandledException;
        _diagnosticApplication = null;
#if DEBUG
        DetachBridgeProbe();
#endif
#if DEBUG
        try
        {
            try
            {
                _bridgeProbeOwnRelease = BridgeOwnReleasePhase.SourceDispose;
#endif
        _source?.Dispose();
#if DEBUG
            }
            finally { _bridgeProbeOwnRelease = BridgeOwnReleasePhase.None; }
#endif
        _source = null;
        _bridge = null;
        _islandHwnd = IntPtr.Zero;
        if (_renderHwnd != IntPtr.Zero)
        {
#if DEBUG
            try
            {
                _bridgeProbeOwnRelease = BridgeOwnReleasePhase.DestroyRender;
#endif
            if (!PInvoke.DestroyWindow(new(_renderHwnd)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Destroying embedded mpv render window failed.");
            _renderHwnd = IntPtr.Zero;
#if DEBUG
            }
            finally { _bridgeProbeOwnRelease = BridgeOwnReleasePhase.None; }
#endif
        }
#if DEBUG
        }
        finally { DetachBridgeProbe(); }
#endif
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
