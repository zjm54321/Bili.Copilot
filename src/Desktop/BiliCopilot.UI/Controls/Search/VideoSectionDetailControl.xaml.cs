// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Search;

/// <summary>
/// 视频搜索分区详情.
/// </summary>
public sealed partial class VideoSectionDetailControl : VideoSectionDetailControlBase
{
    public VideoCategorySource CategorySource { get; }
    private VideoSearchSectionDetailViewModel? _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoSectionDetailControl"/> class.
    /// </summary>
    public VideoSectionDetailControl()
    {
        CategorySource = new VideoCategorySource(this, () => ViewModel?.Items, () => ViewModel);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => BindModel(ViewModel);
    private void OnUnloaded(object sender, RoutedEventArgs args) => BindModel(null);
    protected override void OnViewModelChanged(VideoSearchSectionDetailViewModel? oldValue, VideoSearchSectionDetailViewModel? newValue)
    {
        CategorySource?.Rebind();
        BindModel(newValue);
    }
    private void BindModel(VideoSearchSectionDetailViewModel? model)
    {
        if (_observed is not null) _observed.ListUpdated -= OnListUpdatedAsync;
        _observed = IsLoaded ? model : null;
        if (_observed is not null) _observed.ListUpdated += OnListUpdatedAsync;
        if (View is null) return;
        if (model is null) View.ClearValue(IncrementVerticalScrollControl.LoadMoreCommandProperty);
        else View.LoadMoreCommand = model.LoadItemsCommand;
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observed)) return;
        CategorySource.RawCommitted();
        await View.DelayCheckItemsAsync();
    }
}

/// <summary>
/// 视频搜索分区详情基类.
/// </summary>
public abstract class VideoSectionDetailControlBase : LayoutUserControlBase<VideoSearchSectionDetailViewModel>
{
}
