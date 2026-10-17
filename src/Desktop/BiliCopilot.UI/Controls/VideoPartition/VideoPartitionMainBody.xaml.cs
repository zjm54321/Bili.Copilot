// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;

namespace BiliCopilot.UI.Controls.VideoPartition;

/// <summary>
/// 视频分区主体.
/// </summary>
public sealed partial class VideoPartitionMainBody : VideoPartitionDetailControlBase
{
    public VideoCategorySource CategorySource { get; }
    private VideoPartitionDetailViewModel? _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartitionMainBody"/> class.
    /// </summary>
    public VideoPartitionMainBody()
    {
        CategorySource = new VideoCategorySource(this, () => ViewModel?.Videos, () => ViewModel);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => BindModel(ViewModel);
    private void OnUnloaded(object sender, RoutedEventArgs args) => BindModel(null);

    /// <inheritdoc/>
    protected override void OnViewModelChanged(VideoPartitionDetailViewModel? oldValue, VideoPartitionDetailViewModel? newValue)
    {
        CategorySource?.Rebind();
        BindModel(newValue);
    }
    private void BindModel(VideoPartitionDetailViewModel? model)
    {
        if (_observed is not null) _observed.VideoListUpdated -= OnVideoListUpdatedAsync;
        _observed = IsLoaded ? model : null;
        if (_observed is not null) _observed.VideoListUpdated += OnVideoListUpdatedAsync;
        if (View is null) return;
        if (model is null) View.ClearValue(IncrementVerticalScrollControl.LoadMoreCommandProperty);
        else View.LoadMoreCommand = model.LoadVideosCommand;
    }

    private async void OnVideoListUpdatedAsync(object? sender, EventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observed)) return;
        CategorySource.RawCommitted();
        await View.DelayCheckItemsAsync();
    }
}
