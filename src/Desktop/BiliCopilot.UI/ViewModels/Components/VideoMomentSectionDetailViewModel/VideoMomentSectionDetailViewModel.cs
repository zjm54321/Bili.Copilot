// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频动态分区详情视图模型.
/// </summary>
public sealed partial class VideoMomentSectionDetailViewModel : ViewModelBase, IMomentSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentSectionDetailViewModel"/> class.
    /// </summary>
    public VideoMomentSectionDetailViewModel(
        IMomentDiscoveryService service)
    {
        _service = service;
        _logger = this.Get<ILogger<VideoMomentSectionDetailViewModel>>();
        _account = this.Get<AccountViewModel>();
        _accountGeneration = _account.AccountGeneration;
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (IsLoading || _isRefreshing) return;
        if (_accountGeneration == _account.AccountGeneration && Items.Count > 0)
        {
            return;
        }

        // An empty/failed first page is not a usable cache. Retry from the first cursor.
        ResetItems();
        await LoadItemsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var cancellation = _requestCancellation;
            var loadTask = _loadTask;
            if (cancellation is not null) await cancellation.CancelAsync();
#pragma warning disable VSTHRD003
            await loadTask; // The actual physical request, not a canceled waiter.
#pragma warning restore VSTHRD003
            ResetItems();
        }
        finally { _isRefreshing = false; }
        await LoadItemsAsync();
    }

    [RelayCommand]
    private Task LoadItemsAsync()
    {
        if (IsLoading || _isRefreshing) return Task.CompletedTask;
        if (_accountGeneration != _account.AccountGeneration) ResetItems();
        // An empty page can still advertise a next cursor; only exhaustion stops paging.
        if (_preventLoadMore) return Task.CompletedTask;
        IsLoading = true;
        IsAppending = Items.Count > 0;
        HasAppendError = false;
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
        var loadTask = FetchItemsAsync(_accountGeneration, _requestCancellation);
        _loadTask = loadTask;
        return loadTask;
    }

    private async Task FetchItemsAsync(long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var first = Items.Count == 0;
        try
        {
            var view = await _service.GetVideoMomentsAsync(_offset, _baseline, token);
            if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
            if (view is not null)
            {
                _offset = view.Offset;
                _baseline = view.UpdateBaseline;
                _preventLoadMore = view.HasMoreMoments != true;
                IsPrefetchExhausted = _preventLoadMore;
                var ids = Items.Select(item => item.Data.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in view.Moments)
                {
                    if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
                    if (!ids.Add(item.Id)) continue;
                    Items.Add(new MomentItemViewModel(item, Models.Constants.MomentCardStyle.Video, ShowComment));
                }

                if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
                if (first) SaveLastTenMomentIds();
                ListUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
            _preventLoadMore = true;
            HasAppendError = true;
            _logger.LogError(ex, "加载动态列表失败.");
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _account.AccountGeneration) IsEmpty = Items.Count == 0;
            if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            cancellation.Dispose();
            IsAppending = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RetryAppendAsync()
    {
        if (IsLoading || _isRefreshing || !HasAppendError) return;
        _preventLoadMore = false;
        IsEmpty = false;
        await LoadItemsAsync();
    }

    private void ResetItems()
    {
        _accountGeneration = _account.AccountGeneration;
        _offset = default;
        _baseline = default;
        _preventLoadMore = false;
        IsEmpty = false;
        HasAppendError = false;
        IsPrefetchExhausted = false;
        Items.Clear();
    }

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);

    private void SaveLastTenMomentIds()
    {
        var momentIds = Items.Take(10).Select(p => p.Data.Id).ToList();
        var lastTenMomentIds = string.Join(',', momentIds);
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastTenMomentIds, lastTenMomentIds);
    }
}
