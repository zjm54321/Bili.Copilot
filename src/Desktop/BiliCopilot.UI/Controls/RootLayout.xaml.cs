// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
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
    private readonly IconElement? _primaryIconElement;
    private readonly DispatcherQueue _dispatcher;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="RootLayout"/> class.
    /// </summary>
    public RootLayout()
    {
        InitializeComponent();
        _appViewModel = this.Get<AppViewModel>();
        _account = this.Get<AccountViewModel>();
        _primaryTitle = MainTitleBar.Title ?? string.Empty;
        _primaryIconElement = MainTitleBar.IconElement;
        _narrowMinWidth = TryGetNarrowMinWidth();
        _dispatcher = DispatcherQueue;
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
    /// 获取主标题栏.
    /// </summary>
    /// <returns><see cref="AppTitleBar"/>.</returns>
    public AppTitleBar GetMainTitleBar() => MainTitleBar;

    /// <summary>
    /// 获取当前可拖动的活动标题栏；全屏与小窗由播放层自带的纯视觉标题区承载，此时为 <see langword="null"/>.
    /// </summary>
    /// <returns>常规呈现下的唯一持久 <see cref="AppTitleBar"/>，或全屏/小窗时的 <see langword="null"/>.</returns>
    public AppTitleBar? GetActiveTitleBar()
        => IsPlayerChromeMode(TitleBarPresentation) ? null : MainTitleBar;

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

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        if (_navigationInitialized) return;
        _navigationInitialized = true;
        ViewModel.Initialize(NavView, MainFrame, OverlayFrame);

        var selectedItem = ViewModel.MenuItems.FirstOrDefault(p => p.IsSelected);
        if (selectedItem is not null)
        {
            NavView.SelectedItem = selectedItem;
            selectedItem.NavigateCommand.Execute(default);
        }
    }

    // The shared base owns the one-time navigation setup only. These subscriptions
    // must survive repeated Root loads, independently of its one-shot hooks.
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
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
        ApplyTitleBarChrome();
        QueueScopeUpdate(scope, UpdateMessageBadges);
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_stateLoaded) return;
        _stateLoaded = false;
        _scopeEpoch++;
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
        ApplyShellLayout(titleBarMode);
        ApplyTitleBarChrome();
        TitleBarPresentation = titleBarMode;
        ActiveTitleBarChanged?.Invoke(this, titleBarMode);
    }

    private void ApplyShellLayout(PlayerPresentationMode mode)
    {
        // 全屏/小窗：标题行高度归零且折叠，视频槽填满整个客户区（不留 32px 标题条）.
        var playerChrome = IsPlayerChromeMode(mode);
        MainTitleBar.Visibility = playerChrome ? Visibility.Collapsed : Visibility.Visible;
        LayoutGrid.RowDefinitions[0].Height = playerChrome ? new GridLength(0) : GridLength.Auto;
        NavView.IsPaneVisible = !playerChrome;
    }

    /// <summary>
    /// 唯一持久 <see cref="AppTitleBar"/> 只保留 Store 主外观（图标/顶部导航/搜索/账户）；
    /// 全屏/小窗时该行整体折叠，不再切换为次标题栏，避免与播放层标题区同时写入原生标题栏区域.
    /// </summary>
    private void ApplyTitleBarChrome()
    {
        if (_primaryIconElement is { } icon)
        {
            MainTitleBar.IconElement = icon;
        }
        else
        {
            MainTitleBar.ClearValue(AppTitleBar.IconElementProperty);
        }

        HeaderHost.Visibility = ViewModel.IsTopNavBarShown ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        FooterHost.Visibility = Visibility.Visible;
        MainTitleBar.IsBackButtonVisible = ViewModel.IsOverlayOpen || _embeddedViewModel is not null;
        MainTitleBar.IsBackEnabled = true;
        MainTitleBar.BorderThickness = new Thickness(0);
        MainTitleBar.Title = _isNarrow ? string.Empty : _primaryTitle;
        MainTitleBar.Subtitle = string.Empty;
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
        MainTitleBar.Title = _isNarrow ? string.Empty : _primaryTitle;
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

    private async void OnBackRequested(object sender, EventArgs e)
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
