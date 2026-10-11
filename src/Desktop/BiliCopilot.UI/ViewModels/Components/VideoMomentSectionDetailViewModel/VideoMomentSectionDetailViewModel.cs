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
    }

    [RelayCommand]
    private Task InitializeAsync()
    {
        if (!_loadTask.IsCompleted)
        {
            // Join the prepublished load/refresh owner, including ID saving; this caller holds no gate and is not its prerequisite.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        if (Items.Count > 0 || IsEmpty || _preventLoadMore)
        {
            return Task.CompletedTask;
        }

        return StartLoad(refresh: false, saveIds: true);
    }

    [RelayCommand]
    private Task RefreshAsync()
        => !_refreshTask.IsCompleted ? _refreshTask : StartLoad(refresh: true, saveIds: true);

    [RelayCommand]
    private Task LoadItemsAsync()
    {
        if (!_loadTask.IsCompleted)
        {
            // Paging joins the full prepublished refresh instead of overtaking it; no gate or owner dependency is introduced.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        return IsEmpty || _preventLoadMore
            ? Task.CompletedTask
            : StartLoad(refresh: false, saveIds: false);
    }

    private Task StartLoad(bool refresh, bool saveIds)
    {
        var previousLoad = _loadTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // UI-thread commands reserve the slot before notifications can re-enter.
        // Pagination/initialization join a pending refresh instead of overtaking it.
        _loadTask = completion.Task;
        if (refresh)
        {
            _refreshTask = completion.Task;
        }

        _ = CompleteLoadAsync(previousLoad, completion, refresh, saveIds);
        return completion.Task;
    }

    private async Task CompleteLoadAsync(Task previousLoad, TaskCompletionSource completion, bool refresh, bool saveIds)
    {
        try
        {
            try
            {
                // The predecessor was captured before publishing this slot; hold no gate and drain it before reset/new-page work.
#pragma warning disable VSTHRD003
                await previousLoad;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // Prior callers observe their failure; refreshing must remain retryable.
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
            if (saveIds)
            {
                SaveLastTenMomentIds();
            }

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
            var view = await _service.GetVideoMomentsAsync(_offset, _baseline);
            if (view is not null)
            {
                _offset = view.Offset;
                _baseline = view.UpdateBaseline;
                _preventLoadMore = view.HasMoreMoments != true;
                foreach (var item in view.Moments.Select(p => new MomentItemViewModel(p, Models.Constants.MomentCardStyle.Video, ShowComment)))
                {
                    Items.Add(item);
                }

                ListUpdated?.Invoke(this, EventArgs.Empty);
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

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);

    private void SaveLastTenMomentIds()
    {
        var momentIds = Items.Take(10).Select(p => p.Data.Id).ToList();
        var lastTenMomentIds = string.Join(',', momentIds);
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastTenMomentIds, lastTenMomentIds);
    }
}
