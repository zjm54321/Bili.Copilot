// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.Graphics;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// 主窗口.
/// </summary>
public sealed partial class MainWindow : WindowBase, ITipWindow
{
    private const int WindowMinWidth = 640;
    private const int WindowMinHeight = 480;
    private bool _isFirstActivated = true;
    private readonly RenderingOcclusionState _playerOcclusion;
    internal bool IsEmbeddedPlayerRenderingSuspended => _playerOcclusion.IsSuspended;
    internal bool SuppressPlayerBoundsPersistence { get; set; }

#if DEBUG
    private int _geometryDiagnosticCount;
    private int _geometryResizeObservationCount;
    private long _geometryChangeSequence;
    private long _geometryPendingChangeSequence;
    private bool _geometryFirstActivatedAttempted;
    private bool _geometryFirstLoadedAttempted;
    private bool _geometryRootLoaded;
    private bool _geometryDiagnosticsStopped;
#endif

    private bool _rootChromeRetired;
    private bool _playerOcclusionClosed;
    private bool _appClosedMarked;
    private bool _handlersUnregistered;
    private bool _windowStatsSaved;
    private bool _isTerminalClosePermitted;

    internal bool IsCurrentEmbeddedHost(MainPlayerHost host, PlayerViewModel? expectedOwner = null)
    {
        var app = this.Get<AppViewModel>();
        return !_isTerminalClosePermitted && !app.IsClosed && app.EmbeddedPlayer is { } player &&
            !player.IsPlaybackClosing && ReferenceEquals(player.Window, host) &&
            (expectedOwner is null || ReferenceEquals(player, expectedOwner));
    }

    internal bool RequestEmbeddedOwnerClose(MainPlayerHost host, PlayerViewModel expectedOwner)
    {
        if (!IsCurrentEmbeddedHost(host, expectedOwner)) return false;
        // Avoid internal programmatic base Close() path; route explicit close requests directly to App.RequestExitAsync.
        _ = App.Current.RequestExitAsync();
        return true;
    }

    internal void UpdatePlayerBoundsSuppression(MainPlayerHost requester, bool suppressed)
    {
        if (this.Get<AppViewModel>().EmbeddedPlayer?.Window is MainPlayerHost current && !ReferenceEquals(current, requester)) return;
        SuppressPlayerBoundsPersistence = suppressed;
    }

    internal FrameworkElement ShowEmbeddedPlayer(PlayerViewModel viewModel) => RootLayout.ShowEmbeddedPlayer(viewModel);
    internal void ApplyPlayerPresentation(PlayerPresentationMode mode) => RootLayout.ApplyPlayerPresentation(mode);
    internal void ClearEmbeddedPlayer(PlayerViewModel expectedViewModel) => RootLayout.ClearEmbeddedPlayer(expectedViewModel);

    internal bool TryShowEmbeddedLive(object ownerKey, BiliCopilot.UI.Live.Controls.LiveView liveView)
        => RootLayout.TryShowEmbeddedLive(ownerKey, liveView);

    internal bool HideEmbeddedLive(object expectedOwner)
        => RootLayout.HideEmbeddedLive(expectedOwner);

    internal bool ClearEmbeddedLive(object expectedOwner)
        => RootLayout.ClearEmbeddedLive(expectedOwner);

    internal bool IsEmbeddedLiveShown => RootLayout.IsEmbeddedLiveShown;

    internal object? CurrentEmbeddedLiveOwner => RootLayout.CurrentEmbeddedLiveOwner;

    internal bool IsEmbeddedLiveVisible => IsEmbeddedLiveShown && !_playerOcclusion.IsSuspended;

    internal event EventHandler<object>? EmbeddedLiveBackRequested
    {
        add => RootLayout.EmbeddedLiveBackRequested += value;
        remove => RootLayout.EmbeddedLiveBackRequested -= value;
    }

    internal event Action<object, bool>? LivePresentationChanged
    {
        add => RootLayout.LivePresentationChanged += value;
        remove => RootLayout.LivePresentationChanged -= value;
    }

    /// <summary>
    /// 当主窗口渲染遮挡状态（如弹出弹窗/浮层）变化时触发，通知 Live 后端调整底层 Surface 呈现.
    /// </summary>
    internal event Action? LiveOcclusionChanged;

    internal void ReportEmbeddedHostFailure(Exception exception)
    {
        this.Get<ILogger<App>>().LogError(exception, "Embedded native host layout failed.");
        if (this.Get<AppViewModel>().EmbeddedPlayer is { } vm) vm.ErrorMessage = exception.Message;
    }

    /// <summary>Scope main-root dialogs/popups that cover native video airspace.</summary>
    public IDisposable SuspendEmbeddedPlayerRendering()
        => _playerOcclusion.Suspend();

    private void OnRenderingOcclusionChanged()
    {
        UpdateEmbeddedPlayerOcclusion();
        LiveOcclusionChanged?.Invoke();
    }

    private void UpdateEmbeddedPlayerOcclusion()
    {
        if (this.Get<AppViewModel>().EmbeddedPlayer?.Window is MainPlayerHost host)
            host.UpdateRenderingOcclusion();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        _playerOcclusion = new RenderingOcclusionState(OnRenderingOcclusionChanged);
        InitializeComponent();
        RootLayout.SetEmbeddedPlayerRenderingSuspensionFactory(SuspendEmbeddedPlayerRendering);
        RootLayout.ActiveTitleBarChanged += OnActiveTitleBarChanged;
        ApplyActiveTitleBar();
        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);
        this.SetIcon("Assets/logo.ico");
        MinWidth = WindowMinWidth;
        MinHeight = WindowMinHeight;
        this.Get<AppViewModel>().Windows.Add(this);
        Activated += OnActivated;
        Closed += OnClosed;
        AppWindow.Closing += OnMainClosing;
    }

    private void OnActiveTitleBarChanged(object? sender, PlayerPresentationMode _)
    {
        if (ReferenceEquals(sender, RootLayout)) ApplyActiveTitleBar();
    }

    private void ApplyActiveTitleBar()
    {
        // Native caption/passthrough regions are owned solely by RootLayout's root chrome authority; this window
        // never calls SetTitleBar for the main row, so no second framework writer is ever attached to it.
        // Read the layout actually applied by Root, not a possibly stale event payload.
        var mode = RootLayout.TitleBarPresentation;
        if (mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay) return;

        // The 48px root chrome uses tall caption buttons; a mode change back to Normal restores the height.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
    }

    /// <inheritdoc/>
    public async Task ShowTipAsync(string text, InfoType type = InfoType.Error)
    {
        var player = this.Get<AppViewModel>().EmbeddedPlayer;
        if (player?.Window is MainPlayerHost host && host.TryShowTip(player, text, type)) return;
        using var rendering = SuspendEmbeddedPlayerRendering();
        var popup = new TipPopup() { Text = text };
        TipContainer.Visibility = Visibility.Visible;
        TipContainer.Children.Add(popup);
        try { await popup.ShowAsync(type); }
        finally
        {
            TipContainer.Children.Remove(popup);
            if (TipContainer.Children.Count == 0) TipContainer.Visibility = Visibility.Collapsed;
        }
    }

    private static PointInt32 GetSavedWindowPosition()
    {
        var left = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionLeft, 0);
        var top = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionTop, 0);
        return new PointInt32(left, top);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Get<AppViewModel>().ActivatedWindow = this;
        }

        RootLayout.SetChromeWindowActivation(args.WindowActivationState != WindowActivationState.Deactivated);

        if (!_isFirstActivated)
        {
            return;
        }

        MoveAndResize();
#if DEBUG
        RecordGeometrySnapshot("FirstActivated", "Window.Activated");
#endif
        this.Get<ILogger<App>>().LogInformation($"App version: {this.Get<IAppToolkit>().GetPackageVersion()}");
        var localTheme = SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default);
        this.Get<AppViewModel>().ChangeThemeCommand.Execute(localTheme);
        AppWindow.Changed += OnWindowChanged;
        _isFirstActivated = false;
    }

    internal void UnregisterEventHandlers()
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        AppWindow.Changed -= OnWindowChanged;
        AppWindow.Closing -= OnMainClosing;
#if DEBUG
        _geometryDiagnosticsStopped = true;
        DetachGeometryObservations();
#endif
    }

    private void OnMainClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isTerminalClosePermitted)
        {
            return;
        }

        // Cancel caption close button / Alt+F4 to keep the main window & dispatcher VISIBLE during cleanup.
        args.Cancel = true;
        _ = App.Current.RequestExitAsync();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        if (_isTerminalClosePermitted)
        {
            PerformFinalTeardown();
            return;
        }

        // Programmatic Window.Close() without prior authorization:
        // WinUI 3 cannot resurrect an already-destroyed HWND if native destruction began;
        // log warning and route to App.Current.RequestExitAsync().
        this.Get<ILogger<App>>().LogWarning("MainWindow.Closed triggered without terminal exit permission; requesting app exit.");
        _ = App.Current.RequestExitAsync();
    }

    /// <summary>
    /// Finalization hook called by App owner once all App-level playback drains and resource releases have succeeded.
    /// Saves window stats, retires chrome, occlusion and handlers, then allows clean native window close.
    /// Does not reclose already-drained player owners.
    /// </summary>
    internal void FinalizeAndClose()
    {
        PerformFinalTeardown();
        if (!_windowStatsSaved || !_rootChromeRetired || !_playerOcclusionClosed
            || !_appClosedMarked || !_handlersUnregistered)
            throw new InvalidOperationException("Main window final teardown is incomplete.");
        _isTerminalClosePermitted = true;
        CloseTerminalNative();
    }

    private void PerformFinalTeardown()
    {
        // A save failure must reach the terminal owner before any irreversible retirement begins.
        if (!_windowStatsSaved)
        {
            SaveCurrentWindowStats();
            _windowStatsSaved = true;
        }

        if (!_rootChromeRetired)
        {
            try
            {
                RootLayout.ActiveTitleBarChanged -= OnActiveTitleBarChanged;
                RootLayout.RetireRootChrome();
                _rootChromeRetired = true;
            }
            catch
            {
                SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<App>>(), logger =>
                    logger.LogWarning("Root chrome retirement failed during final teardown."));
            }
        }

        if (!_playerOcclusionClosed)
        {
            try
            {
                _playerOcclusion.Close();
                _playerOcclusionClosed = true;
            }
            catch
            {
                SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<App>>(), logger =>
                    logger.LogWarning("Player occlusion close failed during final teardown."));
            }
        }

        if (!_appClosedMarked)
        {
            try
            {
                var appVM = this.Get<AppViewModel>();
                appVM.IsClosed = true;
                appVM.Windows.Remove(this);
                _appClosedMarked = true;
            }
            catch
            {
                SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<App>>(), logger =>
                    logger.LogWarning("AppViewModel close marking failed during final teardown."));
            }
        }

        // Keep the native Closing guard installed while any earlier required step remains failed.
        if (!_handlersUnregistered && _rootChromeRetired && _playerOcclusionClosed && _appClosedMarked)
        {
            try
            {
                UnregisterEventHandlers();
                _handlersUnregistered = true;
            }
            catch
            {
                SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<App>>(), logger =>
                    logger.LogWarning("Event handlers unregister failed during final teardown."));
            }
        }
    }

    private void CloseTerminalNative()
    {
        Close();
    }

    private void OnWindowKeyUp(InputKeyboardSource sender, KeyEventArgs args)
    {
        if (args.VirtualKey == Windows.System.VirtualKey.F && AppToolkit.IsOnlyCtrlPressed())
        {
            RootLayout.TryFocusSearchBox();
        }
    }

    private RectInt32 GetRenderRect(DisplayArea area)
    {
        var workArea = area.WorkArea;
        var scaleFactor = HwndExtensions.GetDpiForWindow(this.GetWindowHandle()) / 96d;
        var previousWidth = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowWidth, 1280d);
        var previousHeight = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowHeight, 760d);
        var width = Convert.ToInt32(previousWidth * scaleFactor);
        var height = Convert.ToInt32(previousHeight * scaleFactor);

        // 如果宽高比为 16 : 9，则计算出窗口实际宽度。
        double maxPageWidth;
        if (area.OuterBounds.Width * 9 == area.OuterBounds.Height * 16)
        {
            var maxWindowWidth = area.OuterBounds.Width / scaleFactor;
            maxPageWidth = maxWindowWidth - 74;
        }
        else
        {
            // 取当前高度的 16 : 9 比例宽度。
            var maxWindowWidth = area.OuterBounds.Height * 16 / 9 / scaleFactor;
            maxPageWidth = maxWindowWidth - 74;
        }

        App.Current.Resources["MaxPageWidth"] = maxPageWidth;

        // Ensure the window is not larger than the work area.
        if (height > workArea.Height - 20)
        {
            height = workArea.Height - 20;
        }

        var lastPoint = GetSavedWindowPosition();
        var isZeroPoint = lastPoint.X == 0 && lastPoint.Y == 0;
        var isValidPosition = lastPoint.X >= workArea.X && lastPoint.Y >= workArea.Y && lastPoint.X + width <= workArea.X + workArea.Width && lastPoint.Y + height <= workArea.Y + workArea.Height;
        var left = isZeroPoint || !isValidPosition
            ? workArea.X + (workArea.Width - width) / 2d
            : lastPoint.X;
        var top = isZeroPoint || !isValidPosition
            ? workArea.Y + (workArea.Height - height) / 2d
            : lastPoint.Y;
        return new RectInt32(Convert.ToInt32(left), Convert.ToInt32(top), width, height);
    }

    private void MoveAndResize()
    {
        var lastPoint = GetSavedWindowPosition();
        var displayArea = DisplayArea.GetFromPoint(lastPoint, DisplayAreaFallback.Primary)
            ?? DisplayArea.Primary;
        var rect = GetRenderRect(displayArea);
        AppWindow.MoveAndResize(rect);
        var isMaximized = SettingsToolkit.ReadLocalSetting(SettingNames.IsMainWindowMaximized, false);
        if (isMaximized)
        {
            (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
        }
    }

    private void SaveCurrentWindowStats()
    {
        if (SuppressPlayerBoundsPersistence || AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) return;
        var left = AppWindow.Position.X;
        var top = AppWindow.Position.Y;
        var isMaximized = PInvoke.IsZoomed(new HWND(this.GetWindowHandle()));
        SettingsToolkit.WriteLocalSetting(SettingNames.IsMainWindowMaximized, (bool)isMaximized);

        if (!isMaximized)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowPositionLeft, left);
            SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowPositionTop, top);

            if (Height >= WindowMinHeight && Width >= WindowMinWidth)
            {
                SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowHeight, Height);
                SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowWidth, Width);
            }
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            SaveCurrentWindowStats();
#if DEBUG
            if (args.DidSizeChange) ObserveGeometrySizeChange();
#endif
        }
    }

#if DEBUG
    private void StartGeometryLoadedObservation()
    {
        // Diagnostic lifecycle must not depend on resolving the application logger.
        SafeFailureContext.TryWriteFacts(() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ =>
        {
            if (_geometryDiagnosticsStopped) return;
            RootLayout.Unloaded -= OnGeometryRootUnloaded;
            RootLayout.Unloaded += OnGeometryRootUnloaded;
            _geometryRootLoaded = true;
            if (_geometryFirstLoadedAttempted) return;
            RootLayout.SizeChanged -= OnGeometryFirstSizeChanged;
            RootLayout.SizeChanged += OnGeometryFirstSizeChanged;
            RecordFirstPositiveRootGeometry("RootLayout.Loaded");
        });
    }

    private void OnGeometryFirstSizeChanged(object sender, SizeChangedEventArgs args)
        => RecordFirstPositiveRootGeometry("RootLayout.SizeChanged");

    private void RecordFirstPositiveRootGeometry(string signal)
    {
        SafeFailureContext.TryWriteFacts(() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ =>
        {
            if (_geometryDiagnosticsStopped || _geometryFirstLoadedAttempted || !RootLayout.IsLoaded
                || RootLayout.ActualWidth <= 0 || RootLayout.ActualHeight <= 0) return;
            RootLayout.SizeChanged -= OnGeometryFirstSizeChanged;
            RecordGeometrySnapshot("FirstLoaded", signal);
        });
    }

    private void ObserveGeometrySizeChange()
    {
        if (_geometryDiagnosticsStopped) return;
        ++_geometryChangeSequence;
        // Reserve the two first-phase attempts; allow at most three immediate/layout pairs after load.
        // Changed can be programmatic. Pending real changes coalesce into the same subsequent layout sample.
        if (!_geometryRootLoaded || !_geometryFirstLoadedAttempted || _geometryPendingChangeSequence != 0
            || _geometryResizeObservationCount >= 3) return;
        ++_geometryResizeObservationCount;
        var triggerSequence = _geometryChangeSequence;
        _geometryPendingChangeSequence = triggerSequence;
        RecordGeometrySnapshot("WindowChangedImmediate", "AppWindow.Changed", triggerSequence);
        if (_geometryDiagnosticsStopped || !_geometryRootLoaded || _geometryPendingChangeSequence != triggerSequence) return;
        if (!SafeFailureContext.TryWriteFacts(() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ =>
            RootLayout.LayoutUpdated += OnGeometryPostLayout))
            _geometryPendingChangeSequence = 0;
    }

    private void OnGeometryPostLayout(object? sender, object args)
    {
        var triggerSequence = _geometryPendingChangeSequence;
        _geometryPendingChangeSequence = 0;
        SafeFailureContext.TryWriteFacts(() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ =>
        {
            RootLayout.LayoutUpdated -= OnGeometryPostLayout;
            if (triggerSequence == 0 || _geometryDiagnosticsStopped) return;
            // A real LayoutUpdated callback, not a claim that native or XAML geometry is now stable.
            RecordGeometrySnapshot("WindowChangedPostLayout", "RootLayout.LayoutUpdated", triggerSequence);
        });
    }

    private void OnGeometryRootUnloaded(object sender, RoutedEventArgs args)
        => DetachGeometryObservations();

    private void DetachGeometryObservations()
    {
        _geometryRootLoaded = false;
        _geometryPendingChangeSequence = 0;
        SafeFailureContext.TryWriteFacts(() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ =>
        {
            RootLayout.SizeChanged -= OnGeometryFirstSizeChanged;
            RootLayout.LayoutUpdated -= OnGeometryPostLayout;
            RootLayout.Unloaded -= OnGeometryRootUnloaded;
        });
    }

    private void RecordGeometrySnapshot(string stage, string signal, long triggerSequence = 0)
    {
        if (_geometryDiagnosticsStopped || _geometryDiagnosticCount >= 8) return;
        if (stage == "FirstActivated")
        {
            if (_geometryFirstActivatedAttempted) return;
            _geometryFirstActivatedAttempted = true;
        }
        else if (stage == "FirstLoaded")
        {
            if (_geometryFirstLoadedAttempted) return;
            _geometryFirstLoadedAttempted = true;
        }

        var attempt = ++_geometryDiagnosticCount; // Count failed getters/logger attempts too.
        var changeSequence = _geometryChangeSequence;
        SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<App>>(), logger =>
        {
            var handle = this.GetWindowHandle();
            var hwnd = new HWND(handle);
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            var clientSize = AppWindow.ClientSize;
            var dpi = HwndExtensions.GetDpiForWindow(handle);
            var isZoomed = (bool)PInvoke.IsZoomed(hwnd);
            var clientRectSucceeded = (bool)PInvoke.GetClientRect(hwnd, out var clientRect);
            var windowRectSucceeded = (bool)GetWindowRectForDiagnostics(hwnd, out var windowRect);
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = displayArea?.WorkArea;
            var rootWidth = ReadMeasurement(() => RootLayout.ActualWidth);
            var rootHeight = ReadMeasurement(() => RootLayout.ActualHeight);
            var rootOriginY = ReadMeasurement(() => RootLayout.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y);
            var rasterizationScale = ReadMeasurement(() => RootLayout.XamlRoot?.RasterizationScale);
            var (navTop, navBottom, navHeight) = RootLayout.GetNavViewGeometry(logger);
            var (row0, row1, row2) = RootLayout.GetLayoutGridRowHeights(logger);

            logger.LogInformation(
                "MainWindowGeometry Stage={Stage} Signal={Signal} WindowHandle={WindowHandle} Attempt={Attempt} TriggerChangeSequence={TriggerChangeSequence} LatestChangeSequence={LatestChangeSequence} NativeUnits=px XamlUnits=DIP XamlOrigin=VisualRoot RasterizationScale={RasterizationScale} AppWindowPosition=({AppX},{AppY}) AppWindowSize=({AppWidth},{AppHeight}) AppWindowClientSize=({ClientWidth},{ClientHeight}) RootActualWidth={RootWidth} RootActualHeight={RootHeight} RootOriginY={RootOriginY} RootBottom={RootBottom} RootOriginSuccess={RootOriginSuccess} NavViewTop={NavTop} NavViewBottom={NavBottom} NavViewHeight={NavHeight} NavOriginSuccess={NavOriginSuccess} Row0Height={Row0} Row1Height={Row1} Row2Height={Row2} DPI={Dpi} WorkArea=({WorkX},{WorkY},{WorkWidth},{WorkHeight}) GetWindowRectSuccess={WindowRectSuccess} WindowRect=({WindowLeft},{WindowTop},{WindowRight},{WindowBottom}) GetClientRectSuccess={ClientRectSuccess} ClientRect=({ClientX},{ClientY},{ClientRectWidth},{ClientRectHeight}) IsZoomed={IsZoomed}",
                stage, signal, handle.ToInt64(), attempt, triggerSequence, changeSequence, rasterizationScale,
                pos.X, pos.Y, size.Width, size.Height, clientSize.Width, clientSize.Height,
                rootWidth, rootHeight, rootOriginY, rootOriginY + rootHeight, rootOriginY.HasValue,
                navTop, navBottom, navHeight, navTop.HasValue, row0, row1, row2,
                dpi > 0 ? (double?)dpi : null, work?.X, work?.Y, work?.Width, work?.Height,
                windowRectSucceeded, windowRectSucceeded ? (int?)windowRect.left : null,
                windowRectSucceeded ? (int?)windowRect.top : null, windowRectSucceeded ? (int?)windowRect.right : null,
                windowRectSucceeded ? (int?)windowRect.bottom : null,
                clientRectSucceeded, clientRectSucceeded ? (int?)clientRect.X : null,
                clientRectSucceeded ? (int?)clientRect.Y : null, clientRectSucceeded ? (int?)clientRect.Width : null,
                clientRectSucceeded ? (int?)clientRect.Height : null, isZoomed);

            double? ReadMeasurement(Func<double?> read)
            {
                double? value = null;
                SafeFailureContext.TryWriteFacts(() => logger, _ => value = read());
                return value;
            }
        });
    }

    // NativeMethods.txt already generates GetClientRect/IsZoomed, but not GetWindowRect.
    // Keep this read-only, known Win32 ABI local to the temporary DEBUG diagnostics.
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern BOOL GetWindowRectForDiagnostics(HWND hwnd, out RECT rect);
#endif

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed)
        {
            e.Handled = true;
            if (IsEmbeddedLiveShown && CurrentEmbeddedLiveOwner is { } liveOwner)
            {
                RootLayout.RequestLiveBack(liveOwner);
            }
            else if (RootLayout.ViewModel.IsOverlayOpen || this.Get<AppViewModel>().HasEmbeddedWork)
            {
                RootLayout.ViewModel.Back();
            }
        }
    }

    private void OnRootLayoutLoaded(object sender, RoutedEventArgs e)
    {
        var keyboardSource = InputKeyboardSource.GetForIsland(RootLayout.XamlRoot?.ContentIsland);
        keyboardSource.KeyUp += OnWindowKeyUp;
#if DEBUG
        StartGeometryLoadedObservation();
#endif
    }

    private bool IsCursorInWindow()
    {
        var isSuccess = PInvoke.GetCursorPos(out var point);
        if (!isSuccess)
        {
            return false;
        }

        if (AppWindow is null || !AppWindow.IsVisible)
        {
            return false;
        }

        var handle = this.GetWindowHandle();
        isSuccess = PInvoke.GetClientRect(new(handle), out var clientRect);
        if (!isSuccess)
        {
            return false;
        }

        var lt = new System.Drawing.Point(clientRect.X, clientRect.Y);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref lt);
        if (!isSuccess)
        {
            return false;
        }

        var rb = new System.Drawing.Point(clientRect.X + clientRect.Width, clientRect.Y + clientRect.Height);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref rb);
        if (!isSuccess)
        {
            return false;
        }

        return point.X >= lt.X && point.X <= rb.X && point.Y >= lt.Y && point.Y <= rb.Y;
    }

    private bool IsForegroundWindow()
    {
        var handle = PInvoke.GetForegroundWindow();
        return handle.Equals(new(this.GetWindowHandle()));
    }
}
