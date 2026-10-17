// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频分区详情视图模型.
/// </summary>
public sealed partial class VideoPartitionDetailViewModel
{
    private readonly ILogger<VideoPartitionDetailViewModel> _logger;
    private readonly IVideoDiscoveryService _service;
    private readonly Dictionary<string, List<VideoItemViewModel>> _childPartitionVideoCache = new();
    private readonly Dictionary<string, (long Offset, int PageNumber)> _childPartitionOffsetCache = new();

    private List<VideoItemViewModel> _recommendVideoCache;
    private long _recommendOffset;

    [ObservableProperty]
    private List<PartitionVideoSortType> _sortTypes;

    [ObservableProperty]
    private List<PartitionViewModel> _children;

    [ObservableProperty]
    private PartitionVideoSortType _selectedSortType;

    [ObservableProperty]
    private bool _isRecommend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAttemptLoadMore))]
    private bool _isVideoLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAttemptLoadMore))]
    private PartitionViewModel _currentPartition;

    /// <summary>
    /// 初始化完成.
    /// </summary>
    public event EventHandler Initialized;

    /// <summary>
    /// 视频列表已完成更新.
    /// </summary>
    public event EventHandler VideoListUpdated;

    /// <summary>
    /// 显示的视频列表.
    /// </summary>
    public ObservableCollection<VideoItemViewModel> Videos { get; } = new();

    /// <summary>已选择分区且未加载时可尝试；现有 VM 未记录结束或错误停止状态，不代表服务器还有内容。</summary>
    public bool CanAttemptLoadMore => CurrentPartition is not null && !IsVideoLoading;
}
