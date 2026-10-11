// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
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
        IsTotal = data is null;
        Avatar = data?.User?.Avatar.Uri;
        Name = data?.User?.Name ?? ResourceToolkit.GetLocalizedString(StringNames.AllDynamic);
        HasUnread = data?.IsUnread ?? false;
    }

    internal void InjectFirstPageData(MomentView momentView)
    {
        // The owner seeds a new, unpublished section on the UI thread. Do not queue
        // collection writes: its Initialize/Refresh task must include publication.
        _preventLoadMore = momentView.HasMoreMoments != true;
        _offset = momentView.Offset;
        _baseline = momentView.UpdateBaseline;
        Items.Clear();
        foreach (var item in momentView.Moments)
        {
            Items.Add(new MomentItemViewModel(item, MomentCardStyle.Comprehensive, ShowComment));
        }

        IsEmpty = Items.Count == 0;
    }

    internal void NotifyListUpdated()
        => ListUpdated?.Invoke(this, EventArgs.Empty);

    internal Task RetireAsync()
    {
        // Stop accepting work before the owner drains this generation and replaces it.
        _isRetired = true;
        // Return this retired generation's real drain task; retirement holds no gate and is not a prerequisite of that load.
#pragma warning disable VSTHRD003
        return _loadTask;
#pragma warning restore VSTHRD003
    }

    [RelayCommand]
    private Task InitializeAsync()
    {
        if (_isRetired || !_loadTask.IsCompleted)
        {
            // Join existing work without reopening a retired generation; this caller holds no gate and is not its prerequisite.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        if (Items.Count > 0 || _preventLoadMore)
        {
            return Task.CompletedTask;
        }

        return LoadItemsAsync();
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (IsTotal)
        {
            throw new Exception("请在外层视图模型中刷新综合动态，以便更新 UP 列表");
        }

        if (_isRetired)
        {
            // A retired section only exposes its existing drain, never a new refresh; no gate or load dependency is introduced.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        return !_refreshTask.IsCompleted ? _refreshTask : StartLoad(refresh: true);
    }

    [RelayCommand]
    private Task LoadItemsAsync()
    {
        if (_isRetired || !_loadTask.IsCompleted)
        {
            // Join the full current load/refresh or retired drain; paging holds no gate and cannot become its prerequisite.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        return IsEmpty || _preventLoadMore ? Task.CompletedTask : StartLoad(refresh: false);
    }

    private Task StartLoad(bool refresh)
    {
        var previousLoad = _loadTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadTask = completion.Task;
        if (refresh)
        {
            _refreshTask = completion.Task;
        }

        _ = CompleteLoadAsync(previousLoad, completion, refresh);
        return completion.Task;
    }

    private async Task CompleteLoadAsync(Task previousLoad, TaskCompletionSource completion, bool refresh)
    {
        try
        {
            try
            {
                // Drain only the captured predecessor, never our own published slot.
                // Capture precedes successor publication; this gate-free successor cannot be the predecessor's prerequisite.
#pragma warning disable VSTHRD003
                await previousLoad;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // A prior failure must not poison subsequent refreshes.
            }

            if (refresh)
            {
                _offset = default;
                _baseline = default;
                IsEmpty = false;
                _preventLoadMore = false;
                Items.Clear();
            }

            await LoadItemsCoreAsync();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task LoadItemsCoreAsync()
    {
        try
        {
            IsLoading = true;
            if (IsTotal)
            {
                await LoadComprehensiveItemsAsync();
            }
            else
            {
                await LoadUperItemsAsync();
                if (HasUnread)
                {
                    HasUnread = false;
                }
            }
        }
        catch (Exception ex)
        {
            _preventLoadMore = true;
            _logger.LogError(ex, "加载动态列表失败.");
        }
        finally
        {
            IsEmpty = Items.Count == 0;
            IsLoading = false;
        }
    }

    private async Task LoadComprehensiveItemsAsync()
    {
        var view = await _service.GetComprehensiveMomentsAsync(_offset, _baseline);
        if (view is not null)
        {
            _offset = view.Offset;
            _baseline = view.UpdateBaseline;
            _preventLoadMore = view.HasMoreMoments != true;
            foreach (var item in view.Moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                Items.Add(item);
            }

            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadUperItemsAsync()
    {
        var (moments, offset, hasMore) = await _service.GetUserMomentsAsync(Data.User, _offset);
        _offset = offset;
        _preventLoadMore = !hasMore || string.IsNullOrEmpty(offset);
        if (moments is not null)
        {
            foreach (var item in moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                Items.Add(item);
            }

            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);
}
