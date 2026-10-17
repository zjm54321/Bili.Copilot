// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Models.Moment;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态点赞者条目视图模型.
/// </summary>
public sealed partial class MomentUperSectionViewModel : ViewModelBase<MomentProfile>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentUperSectionViewModel"/> class.
    /// </summary>
    public MomentUperSectionViewModel(MomentProfile? data, IAsyncRelayCommand? ownerRefreshCommand = null)
        : base(data)
    {
        _service = this.Get<IMomentDiscoveryService>();
        _logger = this.Get<ILogger<MomentUperSectionViewModel>>();
        _ownerRefreshCommand = ownerRefreshCommand;
        _account = this.Get<AccountViewModel>();
        _accountGeneration = _account.AccountGeneration;
        IsTotal = data is null;
        Avatar = data?.User?.Avatar.Uri;
        Name = data?.User?.Name ?? ResourceToolkit.GetLocalizedString(StringNames.AllDynamic);
        HasUnread = data?.IsUnread ?? false;
    }

    internal void InjectFirstPageData(MomentView momentView)
    {
        if (_isRetired || _accountGeneration != _account.AccountGeneration) return;
        IsEmpty = false;
        _preventLoadMore = momentView.HasMoreMoments != true;
        _offset = momentView.Offset;
        _baseline = momentView.UpdateBaseline;
        IsPrefetchExhausted = _preventLoadMore;
        _isSeeded = true;
        Items.Clear();
        foreach (var item in momentView.Moments)
        {
            if (_isRetired || _accountGeneration != _account.AccountGeneration) return;
            if (Items.Any(existing => existing.Data.Id == item.Id)) continue;
            Items.Add(new MomentItemViewModel(item, MomentCardStyle.Comprehensive, ShowComment));
        }
        IsEmpty = Items.Count == 0;
        ListUpdated?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifyListUpdated() => ListUpdated?.Invoke(this, EventArgs.Empty);

    internal async Task CancelAndDrainAsync()
    {
        var cancellation = _requestCancellation;
        var loadTask = _loadTask;
        if (cancellation is not null) await cancellation.CancelAsync();
#pragma warning disable VSTHRD003
        await loadTask; // Real request completion; canceling is not a drain.
#pragma warning restore VSTHRD003
    }

    internal Task RetireAsync()
    {
        _isRetired = true;
        return CancelAndDrainAsync();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_isRetired) return;
        if (_accountGeneration == _account.AccountGeneration && (Items.Count > 0 || _preventLoadMore || _isSeeded))
        {
            return;
        }

        await LoadItemsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsTotal)
        {
            throw new InvalidOperationException("请在外层视图模型中刷新综合动态，以便更新 UP 列表");
        }

        if (_isRetired || _isRefreshing) return;
        _isRefreshing = true;
        try
        {
            await CancelAndDrainAsync();
            if (_isRetired) return;
            ResetItems();
        }
        finally { _isRefreshing = false; }
        await LoadItemsAsync();
    }

    [RelayCommand]
    private Task LoadItemsAsync()
    {
        if (_isRetired || IsLoading || _isRefreshing) return Task.CompletedTask;
        if (_accountGeneration != _account.AccountGeneration)
        {
            ResetItems();
            // Total and UP ownership must be rebuilt together by the outer model.
            if (IsTotal) return Task.CompletedTask;
        }
        if (IsEmpty || _preventLoadMore || (IsTotal && !_isSeeded)) return Task.CompletedTask;
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
        try
        {
            if (IsTotal)
            {
                await LoadComprehensiveItemsAsync(generation, token);
            }
            else
            {
                await LoadUperItemsAsync(generation, token);
                if (IsCurrentRequest(generation, token) && HasUnread)
                {
                    HasUnread = false;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(generation, token)) return;
            _preventLoadMore = true;
            HasAppendError = true;
            _logger.LogError(ex, "加载动态列表失败.");
        }
        finally
        {
            if (IsCurrentRequest(generation, token)) IsEmpty = Items.Count == 0;
            if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            cancellation.Dispose();
            IsAppending = false;
            IsLoading = false;
        }
    }

    private async Task LoadComprehensiveItemsAsync(long generation, CancellationToken token)
    {
        var view = await _service.GetComprehensiveMomentsAsync(_offset, _baseline, token);
        if (!IsCurrentRequest(generation, token)) return;
        if (view is not null)
        {
            _offset = view.Offset;
            _baseline = view.UpdateBaseline;
            _preventLoadMore = view.HasMoreMoments != true;
            IsPrefetchExhausted = _preventLoadMore;
            foreach (var item in view.Moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                if (!IsCurrentRequest(generation, token)) return;
                if (Items.Any(existing => existing.Data.Id == item.Data.Id)) continue;
                Items.Add(item);
            }

            if (IsCurrentRequest(generation, token)) ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadUperItemsAsync(long generation, CancellationToken token)
    {
        var (moments, offset, hasMore) = await _service.GetUserMomentsAsync(Data.User, _offset, token);
        if (!IsCurrentRequest(generation, token)) return;
        _offset = offset;
        _preventLoadMore = !hasMore || string.IsNullOrEmpty(offset);
        IsPrefetchExhausted = _preventLoadMore;
        if (moments is not null)
        {
            foreach (var item in moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                if (!IsCurrentRequest(generation, token)) return;
                if (Items.Any(existing => existing.Data.Id == item.Data.Id)) continue;
                Items.Add(item);
            }

            if (IsCurrentRequest(generation, token)) ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task RetryAppendAsync()
    {
        if (_isRetired || IsLoading || _isRefreshing || !HasAppendError) return;
        _preventLoadMore = false;
        IsEmpty = false;
        await LoadItemsAsync();
    }

    private bool IsCurrentRequest(long generation, CancellationToken token)
        => !_isRetired && !token.IsCancellationRequested && generation == _account.AccountGeneration;

    private void ResetItems()
    {
        _accountGeneration = _account.AccountGeneration;
        _offset = default;
        _baseline = default;
        _preventLoadMore = false;
        _isSeeded = false;
        IsEmpty = false;
        HasAppendError = false;
        IsPrefetchExhausted = false;
        Items.Clear();
    }

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);
}
