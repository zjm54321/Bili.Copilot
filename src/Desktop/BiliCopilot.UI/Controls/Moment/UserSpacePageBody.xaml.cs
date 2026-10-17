// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.UserSpace;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models.Media;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 用户空间页面主体.
/// </summary>
public sealed partial class UserSpacePageBody : UserSpacePageControlBase
{
    private RelayCommand? _autoLoadMoreCommand;
    private ScrollViewer? _wheelHost;
    private double _wheelTarget;
    private long _wheelTimestamp;
    private UserSpacePageViewModel? _subscribedViewModel;
    private bool _dynamicsUpdateQueued;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSpacePageBody"/> class.
    /// </summary>
    public UserSpacePageBody()
    {
        InitializeComponent();
        Loaded += OnBodyLoaded;
        Unloaded += OnBodyUnloaded;
        SizeChanged += OnBodySizeChanged;
    }

    /// <summary>
    /// 仅在当前主体已加载、可见、非搜索且就绪/初始空闲、有更多内容时自动翻页；失败/不可用状态仅通过显式重试触发.
    /// </summary>
    public ICommand AutoLoadMoreCommand => _autoLoadMoreCommand ??= new RelayCommand(() =>
    {
        if (IsLoaded && Visibility == Visibility.Visible && ViewModel is { } viewModel
            && ReferenceEquals(viewModel, _subscribedViewModel)
            && !viewModel.IsSearchMode && viewModel.HasMore && !viewModel.IsLoading
            && (viewModel.State is UserSpaceLoadState.Ready or UserSpaceLoadState.Idle))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    });

    private void OnBodyLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_subscribedViewModel, ViewModel))
        {
            DetachViewModel();
            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
                _subscribedViewModel.ListUpdated += OnListUpdated;
            }
        }

        SyncSelectors();
        QueueFeaturedLayoutUpdate();
        QueueDynamicsUpdate();
    }

    private void OnBodyUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
        DynamicsRepeater.ItemsSource = null;
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.ListUpdated -= OnListUpdated;
            _subscribedViewModel = null;
        }
    }

    private async void OnListUpdated(object? sender, EventArgs e)
    {
        QueueDynamicsUpdate();
        if (ScrollViewer is not null)
        {
            await ScrollViewer.DelayCheckItemsAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UserSpacePageViewModel.SelectedTab):
                OnTabChanged();
                break;
            case nameof(UserSpacePageViewModel.SelectedSubmission):
                SyncSubmissionKind();
                break;
            case nameof(UserSpacePageViewModel.VideoOrder):
                SyncVideoOrder();
                break;
            case nameof(UserSpacePageViewModel.OpusFilter):
                SyncOpusFilter();
                break;
            case nameof(UserSpacePageViewModel.PinnedDynamicId):
                RefreshPinnedBadges();
                break;
        }
    }

    private void OnTabChanged()
    {
        // Reset the repeater while inactive; never initialize StaggeredLayout with a collapsed viewport.
        if (ViewModel?.SelectedTab != UserSpaceTab.Dynamics) DynamicsRepeater.ItemsSource = null;
        QueueDynamicsUpdate();
        if (ScrollViewer is not null)
        {
            ScrollViewer.ResetScrollPosition();
        }
    }

    private void SyncSelectors()
    {
        SyncSubmissionKind();
        SyncVideoOrder();
        SyncOpusFilter();
    }

    private void SyncSubmissionKind()
    {
        if (ViewModel is null || SubmissionSelector is null) return;
        var index = (int)ViewModel.SelectedSubmission;
        if (index >= 0 && index < SubmissionSelector.Items.Count && SubmissionSelector.SelectedItem != SubmissionSelector.Items[index])
        {
            SubmissionSelector.SelectedItem = SubmissionSelector.Items[index];
        }
    }

    private void SyncVideoOrder()
    {
        if (ViewModel is null || VideoOrderSelector is null) return;
        var index = (int)ViewModel.VideoOrder;
        if (index >= 0 && index < VideoOrderSelector.Items.Count && VideoOrderSelector.SelectedItem != VideoOrderSelector.Items[index])
        {
            VideoOrderSelector.SelectedItem = VideoOrderSelector.Items[index];
        }
    }

    private void SyncOpusFilter()
    {
        if (ViewModel is null || OpusFilterSelector is null) return;
        var index = (int)ViewModel.OpusFilter;
        if (index >= 0 && index < OpusFilterSelector.Items.Count && OpusFilterSelector.SelectedItem != OpusFilterSelector.Items[index])
        {
            OpusFilterSelector.SelectedItem = OpusFilterSelector.Items[index];
        }
    }

    private void OnSubmissionSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null || ViewModel is null) return;
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0 && index <= 2)
        {
            var kind = (UserSubmissionKind)index;
            if (kind != ViewModel.SelectedSubmission)
            {
                ViewModel.SelectSubmissionCommand.Execute(kind);
            }
        }
    }

    private void OnVideoOrderSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null || ViewModel is null) return;
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0 && index <= 2)
        {
            var order = (UserVideoOrder)index;
            if (order != ViewModel.VideoOrder)
            {
                ViewModel.SelectVideoOrderCommand.Execute(order);
            }
        }
    }

    private void OnOpusFilterSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null || ViewModel is null) return;
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0 && index <= 2)
        {
            var filter = (UserOpusFilter)index;
            if (filter != ViewModel.OpusFilter)
            {
                ViewModel.SelectOpusFilterCommand.Execute(filter);
            }
        }
    }

    private void OnVideoRowClick(object sender, RoutedEventArgs e)
    {
        // 普通投稿、置顶与代表作共用显式模板参数，不依赖内容呈现器的数据上下文.
        if (sender is FrameworkElement { Tag: UserSpaceVideoRow row } && ViewModel is not null)
        {
            ViewModel.OpenVideoCommand.Execute(row);
        }
    }

    private void OnOpusRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserSpaceOpusRow row } && ViewModel is not null)
        {
            ViewModel.OpenOpusCommand.Execute(row);
        }
    }

    private void OnAudioRowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UserSpaceAudioRow row && ViewModel is not null)
        {
            ViewModel.OpenAudioCommand.Execute(row);
        }
    }

    private void OnLoadCollectionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UserSpaceCollectionViewModel group && ViewModel is not null)
        {
            ViewModel.LoadCollectionCommand.Execute(group);
        }
    }

    /// <summary>
    /// 合集与系列：分组被 ItemsRepeater 实现（进入视口附近）时自动加载组内首批视频，
    /// 只对“空闲且尚无视频”的组触发；失败/空组不自动重试，仍可通过组内按钮显式触发 OnLoadCollectionClick.
    /// </summary>
    private void OnCollectionElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (sender.ItemsSourceView is null || args.Index < 0 || args.Index >= sender.ItemsSourceView.Count)
        {
            return;
        }

        if (sender.ItemsSourceView.GetAt(args.Index) is not UserSpaceCollectionViewModel group)
        {
            return;
        }

        // 推迟到布局之后执行，避免在测量过程中改写绑定状态.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel is { } viewModel
                && ReferenceEquals(viewModel, _subscribedViewModel)
                && !viewModel.IsSearchMode
                && viewModel.SelectedTab == UserSpaceTab.Collections
                && group.State == UserSpaceLoadState.Idle
                && group.HasMore
                && group.Videos.Count == 0
                && viewModel.Collections.Contains(group))
            {
                viewModel.LoadCollectionCommand.Execute(group);
            }
        });
    }

    private async void OnPlayCollectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: UserSpaceCollectionViewModel group } button || ViewModel is not { } viewModel
            || !viewModel.PlayCollectionCommand.CanExecute(group)) return;

        button.IsEnabled = false;
        try
        {
            await viewModel.PlayCollectionCommand.ExecuteAsync(group);
        }
        catch (Exception error)
        {
            this.Get<ILogger<UserSpacePageBody>>().LogWarning("个人主页合集播放失败 ({ErrorType})", error.GetType().Name);
            group.Error = "合集播放未完成，请重试。";
            group.State = UserSpaceLoadState.Error;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnDynamicsLoaded(object sender, RoutedEventArgs e) => QueueDynamicsUpdate();

    private void OnDynamicsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width) QueueDynamicsUpdate();
    }

    private void QueueDynamicsUpdate()
    {
        if (_dynamicsUpdateQueued) return;
        _dynamicsUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _dynamicsUpdateQueued = false;
            if (!IsLoaded || !DynamicsRepeater.IsLoaded || DynamicsRepeater.ActualWidth <= 0
                || ViewModel is not { SelectedTab: UserSpaceTab.Dynamics, IsSearchMode: false } viewModel
                || !ReferenceEquals(viewModel, _subscribedViewModel)) return;

            if (!ReferenceEquals(DynamicsRepeater.ItemsSource, viewModel.Dynamics))
                DynamicsRepeater.ItemsSource = viewModel.Dynamics;
            DynamicsRepeater.InvalidateMeasure();
            RefreshPinnedBadges();
        });
    }

    /// <summary>
    /// 横向视频轨道上的纵向滚轮：不让横向 ScrollViewer 把它换算成横向滚动，而是交给外层页面上下滚动.
    /// 处理器挂在轨道内容面板上，先于横向 ScrollViewer 收到事件；标记 Handled 后横向 ScrollViewer 不再处理.
    /// Shift+滚轮、水平滚轮（倾斜滚轮/触控板横滑产生的水平滚轮）保持默认的横向滚动.
    /// </summary>
    private void OnRailPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var point = e.GetCurrentPoint(element);
        if (point.Properties.IsHorizontalMouseWheel
            || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
        {
            return;
        }

        var outer = FindVerticalScrollHost(element);
        if (outer is null)
        {
            return;
        }

        e.Handled = true;
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || outer.ScrollableHeight <= 0)
        {
            return;
        }

        // 连续滚动时以上一次目标为基准累加，避免动画进行中以当前位置为基准导致“越滚越慢”.
        var now = Environment.TickCount64;
        var baseOffset = ReferenceEquals(outer, _wheelHost) && now - _wheelTimestamp < 250 ? _wheelTarget : outer.VerticalOffset;
        _wheelTarget = Math.Clamp(baseOffset - delta, 0, outer.ScrollableHeight);
        _wheelHost = outer;
        _wheelTimestamp = now;
        outer.ChangeView(null, _wheelTarget, null);
    }

    private static ScrollViewer? FindVerticalScrollHost(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is ScrollViewer viewer && viewer.VerticalScrollMode != ScrollMode.Disabled)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 动态：元素准备时按 IsPinnedMoment 设置“置顶”标签（模板元素被回收复用，因此每次都显式赋值）.
    /// </summary>
    private void OnDynamicElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => UpdatePinnedBadge(args.Element, sender.ItemsSourceView?.GetAt(args.Index) as MomentItemViewModel);

    private void RefreshPinnedBadges()
    {
        if (DynamicsRepeater?.ItemsSourceView is not { } source)
        {
            return;
        }

        for (var i = 0; i < source.Count; i++)
        {
            if (DynamicsRepeater.TryGetElement(i) is { } element)
            {
                UpdatePinnedBadge(element, source.GetAt(i) as MomentItemViewModel);
            }
        }
    }

    private void UpdatePinnedBadge(UIElement element, MomentItemViewModel? item)
    {
        if (element is FrameworkElement root && root.FindName("PinnedBadge") is UIElement badge)
        {
            badge.Visibility = IsPinnedMoment(item?.Data?.Id, ViewModel?.PinnedDynamicId);
        }
    }

    private void OnRailScrollLeftClick(object sender, RoutedEventArgs e) => ScrollRail(sender, -1);

    private void OnRailScrollRightClick(object sender, RoutedEventArgs e) => ScrollRail(sender, 1);

    private static void ScrollRail(object sender, int direction)
    {
        if ((sender as FrameworkElement)?.Parent is Panel panel
            && panel.Children.OfType<ScrollViewer>().FirstOrDefault() is { } rail)
        {
            var step = Math.Max(rail.ViewportWidth * 0.8, 236);
            var target = Math.Clamp(rail.HorizontalOffset + (direction * step), 0, rail.ScrollableWidth);
            rail.ChangeView(target, null, null);
        }
    }

    private void OnViewAllSubmissionsClick(object sender, RoutedEventArgs e)
    {
        // 头部 SelectorBar 监听 SelectedTab 变化自行同步选中项.
        if (ViewModel is not null && ViewModel.SelectedTab != UserSpaceTab.Submissions)
        {
            ViewModel.SelectTabCommand.Execute(UserSpaceTab.Submissions);
        }
    }

    #region Static Helpers for x:Bind

    public static Visibility IsTabVisible(UserSpaceTab current, UserSpaceTab target)
        => current == target ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsSubmissionVisible(UserSubmissionKind current, UserSubmissionKind target)
        => current == target ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsCountVisible(double? count)
        => count.HasValue && count.Value >= 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsCountVisible(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatCount(double? count)
    {
        if (!count.HasValue) return string.Empty;
        if (count.Value >= 100_000_000) return $"{Math.Round(count.Value / 100_000_000, 1)}亿";
        if (count.Value >= 10_000) return $"{Math.Round(count.Value / 10_000, 1)}万";
        return count.Value.ToString("N0");
    }

    public static Visibility IsDurationVisible(long? duration)
        => duration.HasValue && duration.Value > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatDuration(long? duration)
    {
        if (!duration.HasValue || duration.Value <= 0) return string.Empty;
        var span = TimeSpan.FromSeconds(duration.Value);
        return span.TotalHours >= 1 ? span.ToString(@"hh\:mm\:ss") : span.ToString(@"mm\:ss");
    }

    public static Visibility IsTimeVisible(DateTimeOffset? time)
        => time.HasValue ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatPublishTime(DateTimeOffset? time)
        => time.HasValue ? time.Value.LocalDateTime.ToString("yyyy-MM-dd") : string.Empty;

    public static Visibility IsReasonVisible(string? reason)
        => !string.IsNullOrWhiteSpace(reason) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsTextVisible(string? text)
        => !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsCoverVisible(Uri? cover)
        => cover is not null ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsUriValid(Uri? uri)
        => uri is not null;

    public static string FormatAudioId(string id)
        => $"au{id}";

    public static string FormatCollectionKind(UserCollectionKind kind)
        => kind == UserCollectionKind.Season ? "合集" : "系列";

    public static Visibility IsGroupIdleVisible(UserSpaceLoadState state, int videoCount)
        => state == UserSpaceLoadState.Idle && videoCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupEmptyVisible(UserSpaceLoadState state, int videoCount)
        => state == UserSpaceLoadState.Empty || (state == UserSpaceLoadState.Ready && videoCount == 0) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupLoadMoreVisible(UserSpaceLoadState state, bool hasMore, int videoCount)
        => state == UserSpaceLoadState.Ready && hasMore && videoCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatGroupError(string? error)
        => !string.IsNullOrWhiteSpace(error) ? error : "加载失败";

    public static string FormatFeaturedError(string? error)
        => !string.IsNullOrWhiteSpace(error) ? error : "置顶内容加载失败";

    public static Visibility IsStateLoading(UserSpaceLoadState state)
        => state == UserSpaceLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsStateError(UserSpaceLoadState state)
        => (state is UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsStateEmpty(UserSpaceLoadState state)
        => state == UserSpaceLoadState.Empty ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsInitialLoading(bool isLoading, UserSpaceLoadState state)
        => isLoading && state == UserSpaceLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFeaturedSectionVisible(int count, UserSpaceLoadState state)
        => (count > 0 || state is UserSpaceLoadState.Loading or UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFeaturedLoading(int count, UserSpaceLoadState state)
        => count == 0 && state == UserSpaceLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsFeaturedLoadingActive(int count, UserSpaceLoadState state)
        => count == 0 && state == UserSpaceLoadState.Loading;

    public static bool IsStateLoadingActive(UserSpaceLoadState state)
        => state == UserSpaceLoadState.Loading;

    public static Visibility HasItems(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupRailVisible(int videoCount)
        => videoCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupFirstLoadingVisible(UserSpaceLoadState state, int videoCount)
        => state == UserSpaceLoadState.Loading && videoCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupErrorVisible(UserSpaceLoadState state, int videoCount)
        => (state is UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable) && videoCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsGroupAppendErrorVisible(UserSpaceLoadState state, int videoCount)
        => (state is UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable) && videoCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatCollectionCountSuffix(long? count)
        => count.HasValue && count.Value >= 0 ? $" · {count.Value} 个视频" : string.Empty;

    public static Visibility IsPinnedMoment(string? id, string? pinnedId)
        => !string.IsNullOrEmpty(id) && string.Equals(id, pinnedId, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFeaturedKindVisible(UserSpaceFeaturedKind kind)
        => kind is UserSpaceFeaturedKind.Top or UserSpaceFeaturedKind.Masterpiece ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatFeaturedKind(UserSpaceFeaturedKind kind) => kind switch
    {
        UserSpaceFeaturedKind.Top => "置顶",
        UserSpaceFeaturedKind.Masterpiece => "代表作",
        _ => string.Empty,
    };

    public static FluentIcons.Common.Symbol GetFeaturedKindSymbol(UserSpaceFeaturedKind kind)
        => kind == UserSpaceFeaturedKind.Masterpiece ? FluentIcons.Common.Symbol.Star : FluentIcons.Common.Symbol.Pin;

    public static Visibility IsCoverCollapsed(Uri? cover)
        => cover is null ? Visibility.Visible : Visibility.Collapsed;

    // ---- 置顶/代表作大卡 ----
    private const double FeaturedCompactThreshold = 620;
    private readonly HashSet<ContentControl> _featuredCards = [];
    private bool _featuredLayoutUpdateQueued;

    /// <summary>
    /// 只监听页面宽度，合并到布局之后更新；子项尺寸变化不能反向触发模板/布局切换.
    /// </summary>
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width)
        {
            QueueFeaturedLayoutUpdate();
        }
    }

    private void QueueFeaturedLayoutUpdate()
    {
        if (_featuredLayoutUpdateQueued) return;
        _featuredLayoutUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _featuredLayoutUpdateQueued = false;
            if (!IsLoaded) return;
            foreach (var card in _featuredCards)
            {
                UpdateFeaturedCardLayout(card);
            }
        });
    }

    private void OnFeaturedCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl card)
        {
            _featuredCards.Add(card);
            UpdateFeaturedCardLayout(card);
        }
    }

    private void OnFeaturedCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl card)
        {
            _featuredCards.Remove(card);
        }
    }

    private void UpdateFeaturedCardLayout(ContentControl card)
    {
        var width = ActualWidth - ScrollViewer.ScrollPadding.Left - ScrollViewer.ScrollPadding.Right;
        if (width <= 0 || card.Content is not Grid grid) return;

        var compact = width < FeaturedCompactThreshold;
        var minWidth = compact ? 280 : 560;
        var columns = width >= (2 * minWidth) + 16 ? 2 : 1;
        // 向下取整使两列加间距不超过页面宽度；再窄也不保留超出页面的固定宽度.
        card.Width = Math.Max(1, Math.Floor((width - ((columns - 1) * 16)) / columns));
        card.MinHeight = compact ? 372 : 200;
        grid.Padding = new Thickness(compact ? 10 : 12);
        grid.ColumnSpacing = compact ? 0 : 16;
        grid.RowSpacing = compact ? 12 : 0;
        grid.RowDefinitions[0].Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        grid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        grid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        var cover = (ContentControl)grid.Children[0];
        cover.Width = compact ? double.NaN : 312;
        cover.Height = compact ? 180 : 176;
        var info = (ContentControl)grid.Children[1];
        Grid.SetColumn(info, compact ? 0 : 1);
        Grid.SetRow(info, compact ? 1 : 0);
        info.Padding = compact ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 4, 0);
    }

    public static Visibility IsFeaturedMetaVisible(UserSpaceFeaturedKind kind, string? reason)
        => kind is UserSpaceFeaturedKind.Top or UserSpaceFeaturedKind.Masterpiece || !string.IsNullOrWhiteSpace(reason)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 视频简介：读取 VideoInformation 的 Description 扩展数据（数据层写入后自动显示，未写入时为空并隐藏）.
    /// </summary>
    public static string GetVideoDescription(VideoInformation? video)
    {
        var text = video?.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description);
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }

    public static Visibility IsVideoDescriptionVisible(VideoInformation? video)
        => string.IsNullOrEmpty(GetVideoDescription(video)) ? Visibility.Collapsed : Visibility.Visible;

    private static double? GetVideoStat(VideoInformation? video, string kind)
    {
        var info = video?.CommunityInformation;
        return info is null ? null : kind switch
        {
            "danmaku" => info.DanmakuCount,
            "like" => info.LikeCount,
            "coin" => info.CoinCount,
            "comment" => info.CommentCount,
            _ => null,
        };
    }

    public static Visibility IsVideoStatVisible(VideoInformation? video, string kind)
        => IsCountVisible(GetVideoStat(video, kind));

    public static string FormatVideoStat(VideoInformation? video, string kind)
        => FormatCount(GetVideoStat(video, kind));

    #endregion
}
