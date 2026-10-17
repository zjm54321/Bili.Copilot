// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;

namespace BiliCopilot.UI.Controls.Popular;

/// <summary>
/// 流行视频页面主体.
/// </summary>
public sealed partial class PopularMainBody : PopularPageControlBase
{
    public VideoCategorySource CategorySource { get; }
    private PopularPageViewModel? _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularMainBody"/> class.
    /// </summary>
    public PopularMainBody()
    {
        CategorySource = new VideoCategorySource(this, () => ViewModel?.Videos, () => ViewModel);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    /// <summary>
    /// x:Bind helper: append failure + retry, only for paged feeds.
    /// </summary>
    public static Visibility PagedAppendErrorVisibility(IPopularSectionItemViewModel? section, bool hasAppendError, bool isAppending)
        => IsPagedSection(section) ? IncrementVerticalScrollControl.AppendErrorFooterVisibility(hasAppendError, isAppending) : Visibility.Collapsed;

    /// <summary>
    /// x:Bind helper: quiet end line, only for paged feeds and never with an empty list.
    /// </summary>
    public static Visibility PagedExhaustedVisibility(IPopularSectionItemViewModel? section, bool isExhausted, bool hasAppendError, bool isAppending, int itemCount)
        => IsPagedSection(section) ? IncrementVerticalScrollControl.ExhaustedFooterVisibility(isExhausted, hasAppendError, isAppending, itemCount) : Visibility.Collapsed;

    private static bool IsPagedSection(IPopularSectionItemViewModel? section)
        => section is PopularSectionItemViewModel { Type: PopularSectionType.Recommend or PopularSectionType.Hot };

    private void OnLoaded(object sender, RoutedEventArgs args) => BindModel(ViewModel);
    private void OnUnloaded(object sender, RoutedEventArgs args) => BindModel(null);
    protected override void OnViewModelChanged(PopularPageViewModel? oldValue, PopularPageViewModel? newValue)
    {
        CategorySource?.Rebind();
        BindModel(newValue);
    }
    private void BindModel(PopularPageViewModel? model)
    {
        if (_observed is not null) _observed.VideoListUpdated -= OnVideoListUpdatedAsync;
        _observed = IsLoaded ? model : null;
        if (_observed is not null) _observed.VideoListUpdated += OnVideoListUpdatedAsync;
        if (View is null) return;
        if (model is null) View.ClearValue(IncrementVerticalScrollControl.LoadMoreCommandProperty);
        else View.LoadMoreCommand = model.LoadVideosCommand;
        View.RefreshCommand = model?.RefreshVideoCommand;
    }

    private async void OnVideoListUpdatedAsync(object? sender, EventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observed)) return;
        CategorySource.RawCommitted();
        await View.DelayCheckItemsAsync();
    }
}
