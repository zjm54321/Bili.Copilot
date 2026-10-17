// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态点赞者条目视图模型.
/// </summary>
public sealed partial class MomentUperSectionViewModel
{
    private readonly IMomentDiscoveryService _service;
    private readonly ILogger<MomentUperSectionViewModel> _logger;
    private readonly AccountViewModel _account;
    private readonly IAsyncRelayCommand? _ownerRefreshCommand;
    private long _accountGeneration;
    private CancellationTokenSource? _requestCancellation;
    private Task _loadTask = Task.CompletedTask;
    private bool _isRefreshing;
    private bool _isRetired;
    private bool _isSeeded;

    [ObservableProperty]
    private bool _isAppending;

    [ObservableProperty]
    private bool _hasAppendError;

    [ObservableProperty]
    private bool _isPrefetchExhausted;
    private bool _preventLoadMore;
    private string? _offset;
    private string? _baseline;

    [ObservableProperty]
    private bool _hasUnread;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 列表已完成更新.
    /// </summary>
    public event EventHandler ListUpdated;

    /// <summary>
    /// 是否为全部动态区块.
    /// </summary>
    public bool IsTotal { get; init; }

    public IAsyncRelayCommand? RefreshFeedCommand => IsTotal ? _ownerRefreshCommand : RefreshCommand;

    /// <summary>
    /// 头像.
    /// </summary>
    public Uri? Avatar { get; init; }

    /// <summary>
    /// 名称.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 条目列表.
    /// </summary>
    public ObservableCollection<MomentItemViewModel> Items { get; } = new();

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MomentUperSectionViewModel model && base.Equals(obj) && IsTotal == model.IsTotal && Name == model.Name;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), IsTotal, Name);
}
