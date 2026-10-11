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
    private PlayerViewModel? _titleOwner;
    private PropertyChangedEventHandler? _titleChanged;
    private PropertyChangedEventHandler? _navigationChanged;
    private PropertyChangedEventHandler? _accountChanged;
    private EventHandler? _accountBoundaryChanged;
    private NavigationViewModel? _navigationOwner;
    private readonly HashSet<AppNavigationViewItem> _loadedNavigationItems = new();
    private readonly Dictionary<AppNavigationViewItem, NavigationItemState> _navigationItems = new();
    private long _scopeEpoch;
    private long _titleEpoch;
    private bool _stateLoaded;
    private bool _navigationInitialized;
    private bool _isNarrow;
    private bool _isSecondaryChrome;

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
    /// 获取当前可拖动的活动标题栏；全屏无原生标题栏时为 <see langword="null"/>.
    /// </summary>
    /// <returns>同一持久 <see cref="AppTitleBar"/>（Normal/FullWindow/CompactOverlay），或全屏时的 <see langword="null"/>.</returns>
    public AppTitleBar? GetActiveTitleBar()
        => TitleBarPresentation == PlayerPresentationMode.FullScreen ? null : MainTitleBar;

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
        ObserveEmbeddedTitle(viewModel);
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
        ObserveEmbeddedTitle(null);
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
                    if (ReferenceEquals(navigation, ViewModel) && !_isSecondaryChrome) ApplyTitleBarChrome(PlayerPresentationMode.Normal);
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
        ObserveEmbeddedTitle(_embeddedViewModel);
        foreach (var item in _loadedNavigationItems.ToArray())
            if (item.IsLoaded) TrackNavigationItem(item);
        _isNarrow = LayoutGrid.ActualWidth > 0 && LayoutGrid.ActualWidth < _narrowMinWidth;
        ApplyTitleBarChrome(TitleBarPresentation);
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
        ObserveEmbeddedTitle(null);
        foreach (var item in _navigationItems.Keys.ToArray()) UntrackNavigationItem(item);
    }

    private bool IsCurrentScope(long scope) => _stateLoaded && IsLoaded && scope == _scopeEpoch;

    private void QueueScopeUpdate(long scope, Action update)
        => _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (IsCurrentScope(scope)) update();
        });

    private void ObserveEmbeddedTitle(PlayerViewModel? owner)
    {
        if (!_stateLoaded) owner = null;
        if (ReferenceEquals(_titleOwner, owner)) return;
        if (_titleOwner is not null) _titleOwner.PropertyChanged -= _titleChanged;
        _titleOwner = owner;
        _titleChanged = null;
        var titleEpoch = ++_titleEpoch;
        if (owner is null) return;
        var scope = _scopeEpoch;
        _titleChanged = (sender, args) =>
        {
            if (!ReferenceEquals(sender, owner) || args.PropertyName != nameof(PlayerViewModel.Title)) return;
            QueueScopeUpdate(scope, () =>
            {
                if (titleEpoch == _titleEpoch && ReferenceEquals(owner, _titleOwner)
                    && ReferenceEquals(owner, _embeddedViewModel) && _isSecondaryChrome)
                    MainTitleBar.Title = owner.Title ?? string.Empty;
            });
        };
        owner.PropertyChanged += _titleChanged;
    }

    private static PlayerPresentationMode NormalizeTitleBarMode(PlayerPresentationMode mode)
        => mode is PlayerPresentationMode.FullWindow or PlayerPresentationMode.CompactOverlay or PlayerPresentationMode.FullScreen
            ? mode
            : PlayerPresentationMode.Normal;

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
        ApplyShellLayout(mode);
        ApplyTitleBarChrome(titleBarMode);
        TitleBarPresentation = titleBarMode;
        ActiveTitleBarChanged?.Invoke(this, titleBarMode);
    }

    private void ApplyShellLayout(PlayerPresentationMode mode)
    {
        var hideTitleBar = mode == PlayerPresentationMode.FullScreen;
        var coversNavigation = mode is PlayerPresentationMode.FullWindow
            or PlayerPresentationMode.FullScreen
            or PlayerPresentationMode.CompactOverlay;

        MainTitleBar.Visibility = hideTitleBar ? Visibility.Collapsed : Visibility.Visible;
        LayoutGrid.RowDefinitions[0].Height = hideTitleBar ? new GridLength(0) : GridLength.Auto;
        NavView.IsPaneVisible = !coversNavigation;
    }

    /// <summary>
    /// 在唯一持久 <see cref="AppTitleBar"/> 上切换 Store 主/次外观，避免第二实例重复写原生 Passthrough.
    /// </summary>
    private void ApplyTitleBarChrome(PlayerPresentationMode titleBarMode)
    {
        var secondary = titleBarMode is PlayerPresentationMode.FullWindow or PlayerPresentationMode.CompactOverlay;
        _isSecondaryChrome = secondary;
        if (secondary)
        {
            // Store 次标题栏外观：仅返回/标题/分隔线，无导航/搜索/账户内容.
            MainTitleBar.ClearValue(AppTitleBar.IconElementProperty);
            HeaderHost.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Collapsed;
            FooterHost.Visibility = Visibility.Collapsed;
            MainTitleBar.IsBackButtonVisible = true;
            MainTitleBar.IsBackEnabled = true;
            MainTitleBar.BorderThickness = new Thickness(0, 0, 0, 1);
            MainTitleBar.Title = _embeddedViewModel?.Title ?? string.Empty;
            MainTitleBar.Subtitle = string.Empty;
        }
        else
        {
            // Store 主标题栏外观：图标/顶部导航/搜索/账户.
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
        if (!_isSecondaryChrome)
        {
            MainTitleBar.Title = _isNarrow ? string.Empty : _primaryTitle;
        }
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
        if (owner is not null && TitleBarPresentation is PlayerPresentationMode.FullWindow or PlayerPresentationMode.CompactOverlay)
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
