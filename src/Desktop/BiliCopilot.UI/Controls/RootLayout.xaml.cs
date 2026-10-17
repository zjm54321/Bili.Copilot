// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Live;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Pages.Overlay;
#if DEBUG
using BiliCopilot.UI.Toolkits;
#endif
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// 根布局，用于包裹整个应用程序的布局.
/// </summary>
public sealed partial class RootLayout : RootLayoutBase
{
    /// <summary>数字徽标显示上限：数值更大时仅显示该紧凑值，完整数值由 tooltip/自动化文本承载.</summary>
    private const int CompactBadgeValueCap = 99;

    /// <summary>与 App.xaml 的 <c>WindowNarrowMinWidth</c> 对齐的兜底值.</summary>
    private const double FallbackNarrowMinWidth = 850d;

    private const string NarrowMinWidthResourceKey = "WindowNarrowMinWidth";

    private static readonly string MessagePageKey = typeof(MessagePage).FullName ?? string.Empty;

    private readonly AppViewModel _appViewModel;
    private readonly AccountViewModel _account;
    private readonly double _narrowMinWidth;
    private readonly string _primaryTitle;
    private readonly DispatcherQueue _dispatcher;
    private readonly RootChromeRegionAuthority _chromeRegions;
    private NonClientRegionSink? _chromeSink;
    private XamlRoot? _chromeXamlRoot;
    private bool _chromeCompact;
    private double _chromeCompactThreshold;
    private bool _chromeWindowActive = true;
    private PlayerViewModel? _embeddedViewModel;
    private PropertyChangedEventHandler? _navigationChanged;
    private PropertyChangedEventHandler? _accountChanged;
    private EventHandler? _accountBoundaryChanged;
    private NavigationViewModel? _navigationOwner;
    private readonly HashSet<AppNavigationViewItem> _loadedNavigationItems = new();
    private readonly Dictionary<AppNavigationViewItem, NavigationItemState> _navigationItems = new();
    private long _scopeEpoch;
    private bool _stateLoaded;
    private bool _navigationInitialized;
    private bool _isNarrow;
    private object? _notificationScope;
    private Task? _notificationInitialization;
    private object? _notificationInitializationIdentity;
    private NavigationViewModel? _initialNavigationOwner;
    private bool _notificationNavigationSucceeded;
    private bool _notificationClosed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RootLayout"/> class.
    /// </summary>
    public RootLayout()
    {
        InitializeComponent();
        _appViewModel = this.Get<AppViewModel>();
        _account = this.Get<AccountViewModel>();
        _primaryTitle = ChromeTitle.Text ?? string.Empty;
        _narrowMinWidth = TryGetNarrowMinWidth();
        _dispatcher = DispatcherQueue;
        _chromeRegions = new RootChromeRegionAuthority(
            publish => _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => publish()),
            MeasureChromeRegions,
            ResolveChromeSink);
        ActualThemeChanged += OnChromeThemeChanged;
        LayoutGrid.SizeChanged += OnLayoutGridSizeChanged;
        Loaded += OnRootLoaded;
        Unloaded += OnRootUnloaded;
    }

    /// <summary>
    /// 获取当前实际应用的标题栏呈现模式（无嵌入式会话时归为 <see cref="PlayerPresentationMode.Normal"/>）.
    /// </summary>
    public PlayerPresentationMode TitleBarPresentation { get; private set; }

    /// <summary>
    /// 在标题栏内容/可见性/布局状态应用完成后发布当前呈现模式.
    /// </summary>
    public event EventHandler<PlayerPresentationMode>? ActiveTitleBarChanged;

    /// <summary>
    /// 主窗口激活状态：失去焦点时标题与返回按钮变淡（与原共享标题栏一致），不写入任何原生区域.
    /// </summary>
    /// <param name="isActive">窗口是否处于激活状态.</param>
    internal void SetChromeWindowActivation(bool isActive)
    {
        _chromeWindowActive = isActive;
        VisualStateManager.GoToState(this, isActive ? nameof(ChromeWindowActive) : nameof(ChromeWindowInactive), false);
    }

    /// <summary>
    /// 主窗口关闭：主标题栏区域的唯一写入者退役，之后任何排队回调都不再写入（包括清除）.
    /// </summary>
    internal void RetireRootChrome()
    {
        _notificationClosed = true;
        RetireNotificationScope();
        _chromeRegions.Retire();
    }

    public void TryFocusSearchBox()
        => SearchBox.Focus(FocusState.Programmatic);

    /// <summary>
    /// 绑定嵌入式播放器视图，并返回供宿主定位原生子窗口的视频占位元素.
    /// </summary>
    /// <param name="viewModel">当前 New 播放器视图模型.</param>
    /// <returns>视频占位元素（<c>VideoSlot</c>）.</returns>
    public FrameworkElement ShowEmbeddedPlayer(PlayerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // Attach also resumes an explicitly released/temporarily unloaded view for the same VM.
        _embeddedViewModel = viewModel;
        var slot = EmbeddedPlayerHost.Attach(viewModel);

        EmbeddedPlayerHost.Visibility = Visibility.Visible;
        NavigationContentHost.Visibility = Visibility.Collapsed;
        ApplyPlayerPresentation(viewModel.Presentation);
        return slot;
    }

    /// <summary>Forward the main window's existing rendering-occlusion lease factory.</summary>
    internal void SetEmbeddedPlayerRenderingSuspensionFactory(Func<IDisposable> suspendRendering)
        => EmbeddedPlayerHost.SetRenderingSuspensionFactory(suspendRendering);

    /// <summary>
    /// 应用播放器呈现模式：仅调整播放层内部布局、外壳与唯一标题栏的外观，不接触会话或原生宿主.
    /// </summary>
    /// <param name="mode">目标呈现模式.</param>
    public void ApplyPlayerPresentation(PlayerPresentationMode mode)
    {
        if (_embeddedViewModel is null)
        {
            // 无活动嵌入式会话时保持常规外壳状态（例如宿主释放晚于清理）.
            ApplyPresentation(PlayerPresentationMode.Normal);
            return;
        }

        EmbeddedPlayerHost.SetPresentationMode(mode);
        ApplyPresentation(mode);
    }

    /// <summary>
    /// 身份护栏：仅当传入视图模型仍是当前嵌入式会话时移除播放层并恢复原导航内容，不执行额外后退.
    /// </summary>
    /// <param name="expectedViewModel">调用方期望被清理的视图模型.</param>
    public void ClearEmbeddedPlayer(PlayerViewModel expectedViewModel)
    {
        if (!ReferenceEquals(_embeddedViewModel, expectedViewModel))
        {
            return;
        }

        _embeddedViewModel = null;
        EmbeddedPlayerHost.Release();
        EmbeddedPlayerHost.Visibility = Visibility.Collapsed;
        NavigationContentHost.Visibility = Visibility.Visible;
        ApplyPresentation(PlayerPresentationMode.Normal);
    }

    private object? _embeddedLiveOwner;

    /// <summary>
    /// 当嵌入式直播展示中用户触发标题栏或鼠标返回时引发，通知直播所有者或路由层处理返回/取消.
    /// </summary>
    public event EventHandler<object>? EmbeddedLiveBackRequested;

    /// <summary>
    /// 当嵌入式直播呈现或隐藏时通知宿主与后端，用于协调底层 Surface 呈现与空域管理.
    /// </summary>
    public event Action<object, bool>? LivePresentationChanged;

    /// <summary>
    /// 获取当前嵌入式直播呈现是否处于视觉可见状态（注意：由 UI 视觉呈现决定，而非后台 HasLiveWork）.
    /// </summary>
    public bool IsEmbeddedLiveShown => EmbeddedLiveHost.Visibility == Visibility.Visible;

    /// <summary>
    /// 获取当前持有嵌入式直播展示权的宿主引用.
    /// </summary>
    public object? CurrentEmbeddedLiveOwner => _embeddedLiveOwner;

    /// <summary>
    /// 尝试绑定并展示嵌入式直播视图，折叠常规导航浏览区.
    /// 严格身份防护：若已有不同所有者未释放，或同一所有者传入不同视图，均拒绝并返回 false.
    /// </summary>
    /// <param name="ownerKey">直播所有者标识引用.</param>
    /// <param name="liveView">直播视图元素.</param>
    /// <returns>若成功接纳并展示返回 true；若存在未释放的所有者或视图不匹配返回 false.</returns>
    public bool TryShowEmbeddedLive(object ownerKey, BiliCopilot.UI.Live.Controls.LiveView liveView)
    {
        ArgumentNullException.ThrowIfNull(ownerKey);
        ArgumentNullException.ThrowIfNull(liveView);

        if (_embeddedLiveOwner is not null && !ReferenceEquals(_embeddedLiveOwner, ownerKey))
        {
            return false;
        }

        if (ReferenceEquals(_embeddedLiveOwner, ownerKey) && EmbeddedLiveHost.Child is not null && !ReferenceEquals(EmbeddedLiveHost.Child, liveView))
        {
            return false;
        }

        if (!ReferenceEquals(_embeddedLiveOwner, ownerKey) && ownerKey is ILivePresentation presentation)
            presentation.PropertyChanged += OnLivePresentationPropertyChanged;
        _embeddedLiveOwner = ownerKey;
        if (!ReferenceEquals(EmbeddedLiveHost.Child, liveView))
        {
            EmbeddedLiveHost.Child = liveView;
        }

        EmbeddedLiveHost.Visibility = Visibility.Visible;
        NavigationContentHost.Visibility = Visibility.Collapsed;
        ApplyLivePresentation();
        LivePresentationChanged?.Invoke(ownerKey, true);
        return true;
    }

    /// <summary>
    /// 隐藏嵌入式直播呈现，恢复常规导航浏览，但保留宿主 owner 引用.
    /// 仅当 expectedOwner 匹配当前所有者时执行.
    /// </summary>
    /// <param name="expectedOwner">期望被隐藏的宿主引用.</param>
    /// <returns>若匹配并执行隐藏返回 true；否则返回 false.</returns>
    public bool HideEmbeddedLive(object expectedOwner)
    {
        if (!ReferenceEquals(_embeddedLiveOwner, expectedOwner))
        {
            return false;
        }

        LivePresentationChanged?.Invoke(expectedOwner, false);
        if (!ReferenceEquals(_embeddedLiveOwner, expectedOwner))
        {
            return false;
        }

        EmbeddedLiveHost.Visibility = Visibility.Collapsed;
        NavigationContentHost.Visibility = Visibility.Visible;
        ApplyPresentation(_embeddedViewModel?.Presentation ?? PlayerPresentationMode.Normal);
        return true;
    }

    /// <summary>
    /// 释放并清理嵌入式直播呈现，清空视图引用并恢复导航浏览区.
    /// 仅当 expectedOwner 匹配时执行，绝不触发主窗口关闭.
    /// </summary>
    /// <param name="expectedOwner">期望被释放的宿主引用.</param>
    /// <returns>若匹配并执行清理返回 true；否则返回 false.</returns>
    public bool ClearEmbeddedLive(object expectedOwner)
    {
        if (!ReferenceEquals(_embeddedLiveOwner, expectedOwner))
        {
            return false;
        }

        LivePresentationChanged?.Invoke(expectedOwner, false);
        if (!ReferenceEquals(_embeddedLiveOwner, expectedOwner))
        {
            return false;
        }

        if (_embeddedLiveOwner is ILivePresentation presentation)
            presentation.PropertyChanged -= OnLivePresentationPropertyChanged;
        _embeddedLiveOwner = null;
        EmbeddedLiveHost.Visibility = Visibility.Collapsed;
        EmbeddedLiveHost.Child = null;
        NavigationContentHost.Visibility = Visibility.Visible;
        ApplyPresentation(_embeddedViewModel?.Presentation ?? PlayerPresentationMode.Normal);
        return true;
    }

    private void OnLivePresentationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _embeddedLiveOwner) && IsEmbeddedLiveShown
            && args.PropertyName is nameof(ILivePresentation.IsFullScreen) or nameof(ILivePresentation.IsCompactOverlay))
            ApplyLivePresentation();
    }

    private void ApplyLivePresentation()
    {
        var presentation = _embeddedLiveOwner as ILivePresentation;
        ApplyPresentation(presentation?.IsFullScreen == true ? PlayerPresentationMode.FullScreen
            : presentation?.IsCompactOverlay == true ? PlayerPresentationMode.CompactOverlay
            : PlayerPresentationMode.Normal);
    }

    internal void RequestLiveBack(object owner)
    {
        if (ReferenceEquals(_embeddedLiveOwner, owner) && EmbeddedLiveHost.Visibility == Visibility.Visible)
        {
            if (HideEmbeddedLive(owner)) EmbeddedLiveBackRequested?.Invoke(this, owner);
        }
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        if (_navigationInitialized) return;
        _navigationInitialized = true;
        ViewModel.Initialize(NavView, MainFrame, OverlayFrame);

        var selectedItem = ViewModel.MenuItems.FirstOrDefault(p => p.IsSelected);
        var navigation = ViewModel;
        _initialNavigationOwner = navigation;
        var initialization = _notificationInitializationIdentity = new object();
        _notificationNavigationSucceeded = false;
        EnsureNotificationScope();
        // Even with every menu item hidden, observe the existing real navigation barrier.
        _notificationInitialization = CompleteInitialNavigationAsync(navigation, selectedItem, initialization);
    }

    private object EnsureNotificationScope()
    {
        if (_notificationScope is { } current) return current;
        var owner = _notificationScope = new object();
        var navigation = ViewModel;
        App.Current.BeginNotificationRoot(owner, () => !_notificationClosed && IsLoaded
            && ReferenceEquals(_notificationScope, owner) && ReferenceEquals(ViewModel, navigation)
            && XamlRoot is { } root && _appViewModel.Windows.OfType<BiliCopilot.UI.Forms.MainWindow>()
                .Any(window => ReferenceEquals(window.Content?.XamlRoot, root)));
        return owner;
    }

    private async Task CompleteInitialNavigationAsync(NavigationViewModel navigation,
        AppNavigationItemViewModel? selectedItem, object initialization)
    {
        try
        {
            Action? selectAndNavigate = null;
            if (selectedItem is not null)
            {
                selectAndNavigate = () =>
                {
                    NavView.SelectedItem = selectedItem;
                    selectedItem.NavigateCommand.Execute(default);
                };
            }
            await navigation.NavigateInitialAsync(selectAndNavigate);
        }
        catch (Exception)
        {
            // The task is observed here even if the selected command failed before queuing.
            // Existing navigation observation retains close errors; never manufacture readiness.
            this.Get<ILogger<App>>().LogWarning("通知初始导航未完成。");
            if (ReferenceEquals(_notificationInitializationIdentity, initialization)
                && ReferenceEquals(ViewModel, navigation)) RetireNotificationScope();
            return;
        }
        if (_notificationClosed || !ReferenceEquals(_notificationInitializationIdentity, initialization)
            || !ReferenceEquals(_initialNavigationOwner, navigation)) return;
        // Record this producer independently of the live load/view binding. Publication checks it below.
        // No per-load owner was captured by this worker, so success can survive temporary unload.
        _notificationNavigationSucceeded = true;
        PublishNotificationNavigation();
    }

    private void PublishNotificationNavigation()
    {
        if (_notificationClosed || !IsLoaded || !_notificationNavigationSucceeded
            || !ReferenceEquals(ViewModel, _initialNavigationOwner) || _notificationScope is not { } owner) return;
        App.Current.CompleteNotificationNavigation(owner); // Only the current load's lease can publish.
    }

    private void RetireNotificationScope()
    {
        if (_notificationScope is not { } owner) return;
        _notificationScope = null;
        App.Current.RetireNotificationRoot(owner);
    }

    // The shared base owns the one-time navigation setup only. These subscriptions
    // must survive repeated Root loads, independently of its one-shot hooks.
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (!_notificationClosed)
        {
            EnsureNotificationScope();
            PublishNotificationNavigation();
        }
        if (_stateLoaded) return;
        _stateLoaded = true;
        var scope = ++_scopeEpoch;
        _navigationOwner = ViewModel;
        var navigation = _navigationOwner;
        _navigationChanged = (source, args) =>
        {
            if (ReferenceEquals(source, navigation) && args.PropertyName is nameof(NavigationViewModel.IsOverlayOpen) or nameof(NavigationViewModel.IsTopNavBarShown))
                QueueScopeUpdate(scope, () =>
                {
                    if (ReferenceEquals(navigation, ViewModel)) ApplyTitleBarChrome();
                });
        };
        _accountChanged = (source, args) =>
        {
            if (ReferenceEquals(source, _account) && args.PropertyName == nameof(AccountViewModel.UnreadCount)) QueueScopeUpdate(scope, UpdateMessageBadges);
        };
        _accountBoundaryChanged = (source, args) =>
        {
            if (ReferenceEquals(source, _account)) QueueScopeUpdate(scope, UpdateMessageBadges);
        };
        navigation.PropertyChanged += _navigationChanged;
        _account.PropertyChanged += _accountChanged;
        _account.AccountChanged += _accountBoundaryChanged;
        foreach (var item in _loadedNavigationItems.ToArray())
            if (item.IsLoaded) TrackNavigationItem(item);
        _isNarrow = LayoutGrid.ActualWidth > 0 && LayoutGrid.ActualWidth < _narrowMinWidth;
        AttachChromeXamlRoot();
        SetChromeWindowActivation(_chromeWindowActive);
        ApplyTitleBarChrome();
        UpdateChromeRegionOwnership();
        QueueScopeUpdate(scope, UpdateMessageBadges);
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        RetireNotificationScope();
        if (!_stateLoaded) return;
        _stateLoaded = false;
        _scopeEpoch++;
        // A temporary unload stops publishing (queued callbacks become stale) without touching the native rects;
        // the next load re-measures and replaces them.
        _chromeRegions.Deactivate(clearNative: false);
        DetachChromeXamlRoot();
        if (_navigationOwner is not null) _navigationOwner.PropertyChanged -= _navigationChanged;
        _account.PropertyChanged -= _accountChanged;
        _account.AccountChanged -= _accountBoundaryChanged;
        _navigationOwner = null;
        _navigationChanged = _accountChanged = null;
        _accountBoundaryChanged = null;
        foreach (var item in _navigationItems.Keys.ToArray()) UntrackNavigationItem(item);
    }

    private bool IsCurrentScope(long scope) => _stateLoaded && IsLoaded && scope == _scopeEpoch;

    private void QueueScopeUpdate(long scope, Action update)
        => _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (IsCurrentScope(scope)) update();
        });

    /// <summary>
    /// 全屏与小窗：隐藏主标题栏整行，播放层铺满客户区并使用自身的纯视觉标题区.
    /// 旧的全窗口（FullWindow）已不再面向用户，按常规呈现处理；枚举值保留给旧数据归一化.
    /// </summary>
    private static bool IsPlayerChromeMode(PlayerPresentationMode mode)
        => mode is PlayerPresentationMode.CompactOverlay or PlayerPresentationMode.FullScreen;

    private static PlayerPresentationMode NormalizeTitleBarMode(PlayerPresentationMode mode)
        => IsPlayerChromeMode(mode) ? mode : PlayerPresentationMode.Normal;

    private static double TryGetNarrowMinWidth()
    {
        if (Application.Current is { } app
            && app.Resources.TryGetValue(NarrowMinWidthResourceKey, out var value)
            && value is double width
            && width > 0)
        {
            return width;
        }

        return FallbackNarrowMinWidth;
    }

    /// <summary>
    /// 应用呈现：先落外壳布局，再落唯一标题栏外观，最后记录并发布当前模式.
    /// </summary>
    private void ApplyPresentation(PlayerPresentationMode mode)
    {
        var titleBarMode = NormalizeTitleBarMode(mode);
        var modeChanged = TitleBarPresentation != titleBarMode;
        TitleBarPresentation = titleBarMode;
        if (modeChanged) _chromeRegions.Deactivate();
        ApplyShellLayout(titleBarMode);
        ApplyTitleBarChrome();
        ActiveTitleBarChanged?.Invoke(this, titleBarMode);
    }

    private void ApplyShellLayout(PlayerPresentationMode mode)
    {
        // 全屏/小窗：标题行高度归零且折叠，视频槽填满整个客户区（不留 32px 标题条）.
        var playerChrome = IsPlayerChromeMode(mode);
        // Ownership changes before the row collapses/expands, so layout callbacks raised by the change already
        // see the new generation (an inactive chrome never writes; reactivation re-measures).
        UpdateChromeRegionOwnership();
        MainTitleBar.Visibility = playerChrome ? Visibility.Collapsed : Visibility.Visible;
        LayoutGrid.RowDefinitions[0].Height = playerChrome ? new GridLength(0) : GridLength.Auto;
        NavView.IsPaneVisible = !playerChrome;
    }

    /// <summary>
    /// 唯一持久主标题栏只保留 Store 主外观（图标/标题/顶部导航/搜索/账户）；
    /// 全屏/小窗时该行整体折叠，并由 <see cref="RootChromeRegionAuthority"/> 收回其原生区域.
    /// </summary>
    private void ApplyTitleBarChrome()
    {
        HeaderHost.Visibility = ViewModel.IsTopNavBarShown ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        FooterHost.Visibility = Visibility.Visible;
        UpdateChromeBackButtonVisibility();
        ApplyChromeTitle();
        _chromeRegions.Request();
    }

    private void UpdateChromeBackButtonVisibility()
    {
        ChromeBackButton.Visibility = ViewModel.IsOverlayOpen || _embeddedViewModel is not null || (_embeddedLiveOwner is not null && EmbeddedLiveHost.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _chromeRegions.Request();
    }

    /// <summary>
    /// Root chrome owns only the main window's Normal regions; the compact island owns its own input window.
    /// </summary>
    private void UpdateChromeRegionOwnership()
    {
        if (_stateLoaded && IsLoaded && TitleBarPresentation == PlayerPresentationMode.Normal)
        {
            _chromeRegions.Activate();
            UpdateChromeInsets();
            ApplyCaptionButtonColors();
        }
        else if (_stateLoaded)
        {
            _chromeRegions.Deactivate();
        }
    }

    private void ApplyChromeTitle()
    {
        // Narrow windows drop the title text (as before); a squeezed search box also borrows its space.
        ChromeTitle.Visibility = !_isNarrow && !_chromeCompact && !string.IsNullOrEmpty(_primaryTitle)
            ? Visibility.Visible : Visibility.Collapsed;
        ChromeSearchArea.HorizontalAlignment = _chromeCompact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ChromeSearchArea.Margin = _chromeCompact ? new Thickness(16, 0, 16, 0) : new Thickness(0);
    }

    private void OnLayoutGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_stateLoaded) return;
        var narrow = e.NewSize.Width < _narrowMinWidth;
        if (narrow == _isNarrow)
        {
            return;
        }

        _isNarrow = narrow;
        ApplyChromeTitle();
        _chromeRegions.Request();
    }

    private void OnChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateChromeInsets();
        UpdateChromeDisplayMode();
        _chromeRegions.Request();
    }

    private void OnChromePartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ChromeSearchArea)) UpdateChromeDisplayMode();
        _chromeRegions.Request();
    }

    private void UpdateChromeDisplayMode()
    {
        if (!_stateLoaded || MainTitleBar.Visibility != Visibility.Visible || MainTitleBar.ActualWidth <= 0) return;
        var required = SearchBox.MinWidth + SearchBox.Margin.Left + SearchBox.Margin.Right;
        var (compact, threshold) = RootChromeCompactState.Next(_chromeCompact, _chromeCompactThreshold,
            MainTitleBar.ActualWidth, ChromeSearchColumn.ActualWidth, required);
        _chromeCompactThreshold = threshold;
        if (compact == _chromeCompact) return;
        _chromeCompact = compact;
        ApplyChromeTitle();
    }

    private void OnChromeThemeChanged(FrameworkElement sender, object args) => ApplyCaptionButtonColors();

    private void AttachChromeXamlRoot()
    {
        if (ReferenceEquals(_chromeXamlRoot, XamlRoot)) return;
        DetachChromeXamlRoot();
        _chromeXamlRoot = XamlRoot;
        if (_chromeXamlRoot is not null) _chromeXamlRoot.Changed += OnChromeXamlRootChanged;
    }

    private void DetachChromeXamlRoot()
    {
        if (_chromeXamlRoot is not null) _chromeXamlRoot.Changed -= OnChromeXamlRootChanged;
        _chromeXamlRoot = null;
    }

    private void OnChromeXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _chromeXamlRoot)) return;
        // DPI moves change both the caption-button insets and every physical rect.
        UpdateChromeInsets();
        _chromeRegions.Request();
    }

    private AppWindow? ResolveChromeWindow()
        => XamlRoot?.ContentIslandEnvironment is { } environment ? AppWindow.GetFromWindowId(environment.AppWindowId) : null;

    private IRootChromeRegionSink? ResolveChromeSink()
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } environment) return null;
        var id = environment.AppWindowId;
        if (_chromeSink is null || _chromeSink.WindowId.Value != id.Value) _chromeSink = new NonClientRegionSink(id);
        return _chromeSink;
    }

    /// <summary>Keep content clear of the system caption buttons (same inset rule as the replaced bar).</summary>
    private void UpdateChromeInsets()
    {
        if (!_chromeRegions.IsActive || XamlRoot is not { } root || !(root.RasterizationScale > 0)
            || ResolveChromeWindow() is not { } window) return;
        var scale = root.RasterizationScale;
        var left = window.TitleBar.LeftInset / scale;
        var right = window.TitleBar.RightInset / scale;
        if (ChromeLeftInsetColumn.Width.Value != left) ChromeLeftInsetColumn.Width = new GridLength(left);
        if (ChromeRightInsetColumn.Width.Value != right) ChromeRightInsetColumn.Width = new GridLength(right);
    }

    /// <summary>Caption-button colors follow the theme while the root chrome owns the title row.</summary>
    private void ApplyCaptionButtonColors()
    {
        if (!_chromeRegions.IsActive || ResolveChromeWindow() is not { } window) return;
        var bar = window.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        switch (ActualTheme)
        {
            case ElementTheme.Light:
                bar.ButtonHoverBackgroundColor = Colors.LightGray;
                bar.ButtonForegroundColor = Colors.Black;
                bar.ButtonHoverForegroundColor = Colors.Black;
                bar.ButtonPressedForegroundColor = Colors.Black;
                bar.ButtonInactiveForegroundColor = Colors.Gray;
                break;
            case ElementTheme.Dark:
                bar.ButtonHoverBackgroundColor = Colors.Black;
                bar.ButtonForegroundColor = Colors.White;
                bar.ButtonHoverForegroundColor = Colors.White;
                bar.ButtonPressedForegroundColor = Colors.White;
                bar.ButtonInactiveForegroundColor = Colors.LightGray;
                break;
            default:
                bar.ButtonHoverBackgroundColor = null;
                bar.ButtonForegroundColor = null;
                bar.ButtonHoverForegroundColor = null;
                bar.ButtonPressedForegroundColor = null;
                bar.ButtonInactiveForegroundColor = null;
                break;
        }
    }

    /// <summary>
    /// Caption = the whole visible bar; passthrough = every interactive part (back, top navigation, search,
    /// footer). Coordinates are relative to the window content root, i.e. the client area.
    /// </summary>
    private RootChromeRegions? MeasureChromeRegions()
    {
        if (!_stateLoaded || !IsLoaded || XamlRoot is not { } root) return null;
        var scale = root.RasterizationScale;

        if (MainTitleBar.Visibility != Visibility.Visible) return null;
        if (PhysicalBounds(MainTitleBar, scale) is not { } caption) return null;
        var passthrough = new List<RootChromeRect>(4);
        foreach (var part in new FrameworkElement[] { ChromeBackButton, HeaderHost, SearchBox, FooterHost })
        {
            if (PhysicalBounds(part, scale) is { } rect) passthrough.Add(rect);
        }

        return new RootChromeRegions([caption], passthrough.ToArray());
    }

    private static RootChromeRect? PhysicalBounds(FrameworkElement element, double scale)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return RootChromeGeometry.ToPhysical(origin.X, origin.Y, element.ActualWidth, element.ActualHeight, scale);
    }

    /// <summary>Keep the shared unread-dot source for every non-message navigation item.</summary>
    public static bool ShowNonMessageUnread(string? pageKey, bool showUnread)
        => pageKey != MessagePageKey && showUnread;

    private void OnNavigationItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AppNavigationViewItem item) return;
        _loadedNavigationItems.Add(item);
        if (_stateLoaded) TrackNavigationItem(item);
    }

    private void OnNavigationItemUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AppNavigationViewItem item) return;
        _loadedNavigationItems.Remove(item);
        UntrackNavigationItem(item);
    }

    private void TrackNavigationItem(AppNavigationViewItem item)
    {
        if (_navigationItems.ContainsKey(item)) return;
        var scope = _scopeEpoch;
        var state = new NavigationItemState();
        _navigationItems.Add(item, state);
        state.TagToken = item.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, (sender, property) =>
        {
            if (!IsCurrentItem(item, state, scope)) return;
            BindNavigationItemOwner(item, state, scope);
            // Run after template x:Bind has applied ShowUnread to the recycled item.
            QueueScopeUpdate(scope, () => UpdateMessageBadge(item, state, scope));
        });
        BindNavigationItemOwner(item, state, scope);
        QueueScopeUpdate(scope, () => UpdateMessageBadge(item, state, scope));
    }

    private bool IsCurrentItem(AppNavigationViewItem item, NavigationItemState state, long scope)
        => IsCurrentScope(scope) && item.IsLoaded && _navigationItems.TryGetValue(item, out var current) && ReferenceEquals(current, state);

    private void BindNavigationItemOwner(AppNavigationViewItem item, NavigationItemState state, long scope)
    {
        var owner = item.Tag as AppNavigationItemViewModel;
        if (ReferenceEquals(owner, state.Owner)) return;
        if (state.Owner is not null) state.Owner.PropertyChanged -= state.OwnerChanged;
        ClearOwnedBadge(item, state);
        state.Owner = owner;
        state.OwnerChanged = null;
        if (owner is null) return;
        state.OwnerChanged = (sender, args) =>
        {
            if (ReferenceEquals(sender, owner) && args.PropertyName == nameof(AppNavigationItemViewModel.PageKey))
                QueueScopeUpdate(scope, () =>
                {
                    if (ReferenceEquals(state.Owner, owner)) UpdateMessageBadge(item, state, scope);
                });
        };
        owner.PropertyChanged += state.OwnerChanged;
    }

    private void UntrackNavigationItem(AppNavigationViewItem item)
    {
        if (!_navigationItems.Remove(item, out var state)) return;
        item.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, state.TagToken);
        if (state.Owner is not null) state.Owner.PropertyChanged -= state.OwnerChanged;
        ClearOwnedBadge(item, state);
    }

    private static void ClearOwnedBadge(AppNavigationViewItem item, NavigationItemState state)
    {
        if (state.Badge is not { } badge) return;
        if (ReferenceEquals(item.InfoBadge, badge)) item.InfoBadge = null;
        ToolTipService.SetToolTip(badge, null);
        AutomationProperties.SetName(badge, string.Empty);
        state.Badge = null;
    }

    private void UpdateMessageBadges()
    {
        foreach (var (item, state) in _navigationItems.ToArray()) UpdateMessageBadge(item, state, _scopeEpoch);
    }

    /// <summary>Revalidate every recycled container; never clear somebody else's badge or item tooltip.</summary>
    private void UpdateMessageBadge(AppNavigationViewItem item, NavigationItemState state, long scope)
    {
        if (!IsCurrentItem(item, state, scope)) return;
        if (item.Tag is not AppNavigationItemViewModel owner || !ReferenceEquals(owner, state.Owner)
            || owner.PageKey != MessagePageKey || _account.UnreadCount <= 0)
        {
            ClearOwnedBadge(item, state);
            return;
        }

        var count = _account.UnreadCount;
        var badge = state.Badge ??= new InfoBadge();
        if (!ReferenceEquals(item.InfoBadge, badge)) item.InfoBadge = badge;
        badge.Value = Math.Min(count, CompactBadgeValueCap);
        var fullValue = count.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetName(badge, fullValue);
        ToolTipService.SetToolTip(badge, fullValue);
    }

    private sealed class NavigationItemState
    {
        internal long TagToken;
        internal AppNavigationItemViewModel? Owner;
        internal PropertyChangedEventHandler? OwnerChanged;
        internal InfoBadge? Badge;
    }

    private void OnNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        _ = this;
        var item = args.InvokedItemContainer as AppNavigationViewItem;
        var context = item?.Tag as AppNavigationItemViewModel;
        context?.NavigateCommand.Execute(default);
    }

    private async void OnBackRequested(object sender, RoutedEventArgs e)
    {
        if (!_stateLoaded || !IsLoaded) return;
        var owner = _embeddedViewModel;
        // 主标题栏只在常规呈现下可见；若旧状态仍停在非常规呈现，返回只恢复默认呈现，不关闭播放.
        if (owner is not null && owner.Presentation != PlayerPresentationMode.Normal)
        {
            var command = owner.BackToDefaultModeCommand;
            if (!command.CanExecute(default)) return;
            var scope = _scopeEpoch;
            try { await command.ExecuteAsync(default); }
            catch (Exception ex)
            {
                this.Get<ILogger<RootLayout>>().LogError(ex, "返回默认播放呈现失败");
                if (IsCurrentScope(scope) && ReferenceEquals(owner, _embeddedViewModel))
                    _appViewModel.ShowTipCommand.Execute((ex.Message, InfoType.Error));
            }
            return;
        }

        if (_embeddedLiveOwner is { } liveOwner && EmbeddedLiveHost.Visibility == Visibility.Visible)
        {
            if (HideEmbeddedLive(liveOwner)) EmbeddedLiveBackRequested?.Invoke(this, liveOwner);
            return;
        }

        ViewModel.Back();
    }

    private void OnUpdateActionButtonClick(TeachingTip sender, object args)
        => _appViewModel.ShowUpdateCommand.Execute(default);

    private void OnUpdateCloseButtonClick(TeachingTip sender, object args)
        => _appViewModel.HideUpdateCommand.Execute(default);

    private void OnFavoriteButtonClick(object sender, EventArgs e)
        => ViewModel.NavigateToOver(typeof(FavoritesPage), default);

    private void OnViewLaterButtonClick(object sender, EventArgs e)
        => ViewModel.NavigateToOver(typeof(ViewLaterPage), default);

    private void OnHistoryButtonClick(object sender, EventArgs e)
        => ViewModel.NavigateToOver(typeof(HistoryPage), default);

#if DEBUG
    internal (double? Top, double? Bottom, double? ActualHeight) GetNavViewGeometry(ILogger logger)
    {
        double? height = null;
        double? top = null;
        SafeFailureContext.TryWriteFacts(() => logger, _ => height = NavView.ActualHeight);
        SafeFailureContext.TryWriteFacts(() => logger, _ =>
            top = NavView.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y);
        return (top, top + height, height);
    }

    internal (double? Row0, double? Row1, double? Row2) GetLayoutGridRowHeights(ILogger logger)
    {
        double? row0 = null;
        double? row1 = null;
        double? row2 = null;
        SafeFailureContext.TryWriteFacts(() => logger, _ =>
            row0 = LayoutGrid.RowDefinitions.Count > 0 ? LayoutGrid.RowDefinitions[0].ActualHeight : null);
        SafeFailureContext.TryWriteFacts(() => logger, _ =>
            row1 = LayoutGrid.RowDefinitions.Count > 1 ? LayoutGrid.RowDefinitions[1].ActualHeight : null);
        SafeFailureContext.TryWriteFacts(() => logger, _ =>
            row2 = LayoutGrid.RowDefinitions.Count > 2 ? LayoutGrid.RowDefinitions[2].ActualHeight : null);
        return (row0, row1, row2);
    }
#endif
}

/// <summary>
/// 根布局基类.
/// </summary>
public abstract class RootLayoutBase : LayoutUserControlBase<NavigationViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RootLayoutBase"/> class.
    /// </summary>
    protected RootLayoutBase() => ViewModel = this.Get<NavigationViewModel>();
}
