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
    private bool _closingEmbedded;
    private readonly RenderingOcclusionState _playerOcclusion;
    internal bool IsEmbeddedPlayerRenderingSuspended => _playerOcclusion.IsSuspended;
    internal bool SuppressPlayerBoundsPersistence { get; set; }

    internal bool IsCurrentEmbeddedHost(MainPlayerHost host, PlayerViewModel? expectedOwner = null)
    {
        var app = this.Get<AppViewModel>();
        return !_closingEmbedded && !app.IsClosed && app.EmbeddedPlayer is { } player &&
            !player.IsPlaybackClosing && ReferenceEquals(player.Window, host) &&
            (expectedOwner is null || ReferenceEquals(player, expectedOwner));
    }

    internal bool RequestEmbeddedOwnerClose(MainPlayerHost host, PlayerViewModel expectedOwner)
    {
        if (!IsCurrentEmbeddedHost(host, expectedOwner)) return false;
        // Preserve the existing programmatic-close/embedded drain and retry path.
        Close();
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

    internal void ReportEmbeddedHostFailure(Exception exception)
    {
        this.Get<ILogger<App>>().LogError(exception, "Embedded native host layout failed.");
        if (this.Get<AppViewModel>().EmbeddedPlayer is { } vm) vm.ErrorMessage = exception.Message;
    }

    /// <summary>Scope main-root dialogs/popups that cover native video airspace.</summary>
    public IDisposable SuspendEmbeddedPlayerRendering()
        => _playerOcclusion.Suspend();

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
        _playerOcclusion = new RenderingOcclusionState(UpdateEmbeddedPlayerOcclusion);
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
        // Read the layout actually applied by Root, not a possibly stale event payload.
        var mode = RootLayout.TitleBarPresentation;
        if (mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay)
        {
            SetTitleBar(null);
            return;
        }
        SetTitleBar(RootLayout.GetActiveTitleBar());

        // The bar instance is persistent; a mode change still requires a height update.
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

        if (!_isFirstActivated)
        {
            return;
        }

        MoveAndResize();
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
    }

    private void OnMainClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var app = this.Get<AppViewModel>();
        if (_closingEmbedded) { args.Cancel = true; return; }
        if (app.HasEmbeddedWork)
        {
            args.Cancel = true;
            _ = CloseEmbeddedAndRetryAsync();
            return;
        }
        if (app.HasIndependentWorkRequiringMainWindow) { args.Cancel = true; this.Activate(); return; }
        if (!app.HasClassicWork) return;
        args.Cancel = true;
        if (app.ClassicPlayers.Count == 0)
        {
            // Keep the owner of a protocol prompt / explicit failed-close retry reachable.
            if (app.HasPendingClassicExternalCleanup)
                app.ReportClassicFailure(new InvalidOperationException("外部播放器尚未确认退出，需要明确重试清理。"));
            return;
        }
        // Classic is registered in Windows for theme/lifetime, but must remain reachable.
        this.Hide();
    }

    private async Task CloseEmbeddedAndRetryAsync()
    {
        if (_closingEmbedded) return;
        _closingEmbedded = true;
        try
        {
            await this.Get<AppViewModel>().CloseEmbeddedPlayerAsync();
            _closingEmbedded = false;
            Close();
        }
        catch (Exception ex)
        {
            this.Get<ILogger<App>>().LogError(ex, "Embedded playback shutdown failed; main window retained.");
            this.Get<AppViewModel>().ShowTipCommand.Execute((ex.Message, InfoType.Error));
        }
        finally { _closingEmbedded = false; }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        var appVM = this.Get<AppViewModel>();
        // Window.Close can raise Closed without AppWindow.Closing (programmatic close).
        if (appVM.HasEmbeddedWork || _closingEmbedded)
        {
            e.Handled = true;
            _ = CloseEmbeddedAndRetryAsync();
            return;
        }
        if (appVM.HasIndependentWorkRequiringMainWindow)
        {
            e.Handled = true;
            this.Activate();
            return;
        }
        var windows = appVM.Windows;
        var hasPlayer = appVM.Players.Any(player => !player.IsEmbedded) || appVM.HasClassicWork;
        if (hasPlayer)
        {
            e.Handled = true;
            if (appVM.HasClassicWork) this.Hide();
            else appVM.HideAllWindowsCommand.Execute(default);
            return;
        }

        RootLayout.ActiveTitleBarChanged -= OnActiveTitleBarChanged;
        _playerOcclusion.Close();
        appVM.IsClosed = true;
        foreach (var item in windows)
        {
            if (item is not MainWindow)
            {
                item.Close();
            }
        }

        foreach (var player in appVM.Players)
        {
            player.Close();
        }

        appVM.Windows.Remove(this);
        UnregisterEventHandlers();
        SaveCurrentWindowStats();
        App.Current?.Exit();
        Environment.Exit(0);
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
            ? (workArea.Width - width) / 2d
            : lastPoint.X;
        var top = isZeroPoint || !isValidPosition
            ? (workArea.Height - height) / 2d
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
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed)
        {
            e.Handled = true;
            if (RootLayout.ViewModel.IsOverlayOpen || this.Get<AppViewModel>().HasEmbeddedWork)
            {
                RootLayout.ViewModel.Back();
            }
        }
    }

    private void OnRootLayoutLoaded(object sender, RoutedEventArgs e)
    {
        var keyboardSource = InputKeyboardSource.GetForIsland(RootLayout.XamlRoot?.ContentIsland);
        keyboardSource.KeyUp += OnWindowKeyUp;
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
