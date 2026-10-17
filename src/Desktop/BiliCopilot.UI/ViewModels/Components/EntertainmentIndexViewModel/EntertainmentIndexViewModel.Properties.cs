// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 娱乐索引视图模型.
/// </summary>
public sealed partial class EntertainmentIndexViewModel
{
    private readonly IEntertainmentDiscoveryService _service;
    private readonly ILogger<EntertainmentIndexViewModel> _logger;

    // B 站索引接口页码从 1 开始；page=0 返回空 data，SDK 会抛“无法获取到有效的剧集列表”.
    private int _pageNumer = 1;
    private bool _hasMoreSeasons = true;

    [ObservableProperty]
    private List<IndexFilterViewModel>? _filters;

    [ObservableProperty]
    private bool _isFilterLoading;

    [ObservableProperty]
    private bool _isSeasonLoading;

    /// <summary>
    /// 条目已更新.
    /// </summary>
    public event EventHandler ItemsUpdated;

    /// <inheritdoc/>
    public PgcSectionType SectionType { get; }

    /// <summary>
    /// 剧集列表.
    /// </summary>
    public ObservableCollection<SeasonItemViewModel> Items { get; } = new();
}
