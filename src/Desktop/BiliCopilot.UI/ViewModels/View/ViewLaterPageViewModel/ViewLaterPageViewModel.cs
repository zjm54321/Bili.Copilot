// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 稍后再看视图模型.
/// </summary>
public sealed partial class ViewLaterPageViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ViewLaterPageViewModel"/> class.
    /// </summary>
    public ViewLaterPageViewModel(
        IViewLaterService service,
        ILogger<ViewLaterPageViewModel> logger)
    {
        _service = service;
        _logger = logger;
    }

    [RelayCommand]
    private Task InitializeAsync()
    {
        if (!_loadTask.IsCompleted)
        {
            // Join the prepublished owner through the real refresh request and finally; hold no gate and add no owner dependency.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        if (Videos.Count > 0 || IsEmpty)
        {
            return Task.CompletedTask;
        }

        return LoadVideosAsync();
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        return !_refreshTask.IsCompleted ? _refreshTask : StartLoad(refresh: true);
    }

    [RelayCommand]
    private Task LoadVideosAsync()
    {
        if (!_loadTask.IsCompleted)
        {
            // Paging joins the full pending refresh rather than passing it; this caller holds no gate and is not its prerequisite.
#pragma warning disable VSTHRD003
            return _loadTask;
#pragma warning restore VSTHRD003
        }

        if (IsEmpty || (Videos.Count >= TotalCount && Videos.Count > 0))
        {
            return Task.CompletedTask;
        }

        return StartLoad(refresh: false);
    }

    private Task StartLoad(bool refresh)
    {
        var previousLoad = _loadTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Publish before any property/collection notifications can re-enter a command.
        // A pending refresh owns the load slot, so incremental loads cannot pass it.
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
                // Drain the captured predecessor, never the slot now owned by this load.
                // Captured before successor publication; no gate is held, and old paging finishes before reset and the new first page.
#pragma warning disable VSTHRD003
                await previousLoad;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // Its callers observe the failure; a new refresh must still be able to retry.
            }

            if (refresh)
            {
                IsEmpty = false;
                _pageNumber = 0;
                TotalCount = 0;
                Videos.Clear();
            }

            await LoadVideosCoreAsync();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task LoadVideosCoreAsync()
    {
        IsLoading = true;
        try
        {
            var (videos, totalCount, nextPage) = await _service.GetViewLaterSetAsync(_pageNumber);
            if (Videos.Count == 0 && (videos is null || videos.Count == 0 || totalCount == 0))
            {
                IsEmpty = true;
            }
            else
            {
                _pageNumber = nextPage;
                TotalCount = totalCount;
                foreach (var item in videos)
                {
                    if (Videos.Any(p => p.Data.Identifier.Id == item.Identifier.Id))
                    {
                        continue;
                    }

                    Videos.Add(new VideoItemViewModel(item, Models.Constants.VideoCardStyle.ViewLater, removeAction: RemoveVideo));
                }

                ListUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载稍后再看时出现异常");
        }
        finally
        {
            IsLoading = false;
            IsEmpty = Videos.Count == 0;
        }
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        try
        {
            await _service.CleanAsync(Richasy.BiliKernel.Models.ViewLaterCleanType.All);
            await Task.Delay(500);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清除稍后再看时出错");
        }
    }

    [RelayCommand]
    private void PlayAll()
    {
        var playlist = Videos.Select(p => p.Data).ToList().ConvertAll(p => new MediaSnapshot(p));
        if (playlist.Count == 0)
        {
            return;
        }

        var snapshot = new MediaSnapshot(Videos.First().Data);
        if (playlist.Count > 1)
        {
            snapshot.Playlist = playlist;
        }

        this.Get<AppViewModel>().OpenPlayerCommand.Execute(snapshot);
    }

    private void RemoveVideo(VideoItemViewModel item)
    {
        Videos.Remove(item);
        IsEmpty = Videos.Count == 0;
    }
}
