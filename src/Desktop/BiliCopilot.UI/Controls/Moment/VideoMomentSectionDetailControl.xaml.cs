// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 视频动态详情控件.
/// </summary>
public sealed partial class VideoMomentSectionDetailControl : VideoMomentSectionDetailControlBase
{
    private VideoMomentSectionDetailViewModel? _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentSectionDetailControl"/> class.
    /// </summary>
    public VideoMomentSectionDetailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => BindModel(ViewModel);
    private void OnUnloaded(object sender, RoutedEventArgs args) => BindModel(null);

    /// <inheritdoc/>
    protected override void OnViewModelChanged(VideoMomentSectionDetailViewModel? oldValue, VideoMomentSectionDetailViewModel? newValue)
        => BindModel(newValue);

    private void BindModel(VideoMomentSectionDetailViewModel? model)
    {
        if (_observed is not null) _observed.ListUpdated -= OnListUpdatedAsync;
        _observed = IsLoaded ? model : null;
        if (_observed is not null) _observed.ListUpdated += OnListUpdatedAsync;
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observed)) return;
        await View.DelayCheckItemsAsync();
    }
}

/// <summary>
/// 视频动态详情控件基类.
/// </summary>
public abstract class VideoMomentSectionDetailControlBase : LayoutUserControlBase<VideoMomentSectionDetailViewModel>
{
}
