// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Search;
using Richasy.BiliKernel.Models;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频搜索分区详情视图模型.
/// </summary>
public sealed partial class VideoSearchSectionDetailViewModel
{
    private readonly ISearchService _service;
    private readonly ILogger<VideoSearchSectionDetailViewModel> _logger;

    private bool _canRequest;
    private int? _currentPage;
    private string _keyword;
    private bool _isPreventLoadMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAttemptLoadMore))]
    private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAttemptLoadMore))]
    private bool _isLoading;

    [ObservableProperty]
    private ComprehensiveSearchSortType _sort;

    [ObservableProperty]
    private List<ComprehensiveSearchSortType> _sorts;

    /// <summary>
    /// 列表已完成更新.
    /// </summary>
    public event EventHandler ListUpdated;

    /// <inheritdoc/>
    public SearchSectionType SectionType => SearchSectionType.Video;

    /// <summary>
    /// 条目列表.
    /// </summary>
    public ObservableCollection<VideoItemViewModel> Items { get; } = new();

    /// <summary>原加载命令的当前准入条件；仅允许尝试，不保证服务器还有结果。</summary>
    public bool CanAttemptLoadMore
        => _canRequest && !IsEmpty && !_isPreventLoadMore && !IsLoading && _currentPage is not null;
}
