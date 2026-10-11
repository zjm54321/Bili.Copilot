// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 稍后再看视图模型.
/// </summary>
public sealed partial class ViewLaterPageViewModel
{
    private readonly IViewLaterService _service;
    private readonly ILogger<ViewLaterPageViewModel> _logger;

    // Commands and collection notifications run on the UI thread.
    private Task _loadTask = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private int _pageNumber;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private int _totalCount;

    /// <summary>
    /// 视频列表已完成更新.
    /// </summary>
    public event EventHandler ListUpdated;

    /// <summary>
    /// 视频列表.
    /// </summary>
    public ObservableCollection<VideoItemViewModel> Videos { get; } = new();
}
