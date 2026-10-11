// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 综合动态分区详情视图模型.
/// </summary>
public sealed partial class ComprehensiveMomentSectionDetailViewModel : LayoutPageViewModelBase, IMomentSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMomentSectionDetailViewModel"/> class.
    /// </summary>
    public ComprehensiveMomentSectionDetailViewModel(
        IMomentDiscoveryService service)
    {
        _service = service;
        _logger = this.Get<ILogger<VideoMomentSectionDetailViewModel>>();
    }

    /// <inheritdoc/>
    protected override string GetPageKey()
        => "ComprehensiveMoment";

    [RelayCommand]
    private Task InitializeAsync()
    {
        if (!_operationTask.IsCompleted)
        {
            // Join the owner's full initialize/refresh operation without holding a gate or becoming its prerequisite.
#pragma warning disable VSTHRD003
            return _operationTask;
#pragma warning restore VSTHRD003
        }

        if (Upers.Count > 0)
        {
            return SelectedUper?.InitializeCommand.ExecuteAsync(null) ?? Task.CompletedTask;
        }

        return StartOperation(refresh: false);
    }

    [RelayCommand]
    private Task RefreshAsync()
        => !_refreshTask.IsCompleted ? _refreshTask : StartOperation(refresh: true);

    private Task StartOperation(bool refresh)
    {
        var previousOperation = _operationTask;
        var selected = SelectedUper;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationTask = completion.Task;
        if (refresh)
        {
            _refreshTask = completion.Task;
        }

        _ = CompleteOperationAsync(previousOperation, completion, refresh, selected);
        return completion.Task;
    }

    private async Task CompleteOperationAsync(
        Task previousOperation,
        TaskCompletionSource completion,
        bool refresh,
        MomentUperSectionViewModel? selected)
    {
        try
        {
            try
            {
                // Captured before successor publication; this gate-free successor is not the predecessor's prerequisite.
#pragma warning disable VSTHRD003
                await previousOperation;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // A failed initialization/refresh must not prevent the next attempt.
            }

            if (refresh && selected is { IsTotal: false })
            {
                // Capture the owner at invocation; a later selection must not redirect it.
                await selected.RefreshCommand.ExecuteAsync(null);
            }
            else
            {
                await RebuildAsync();
            }

            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    [RelayCommand]
    private async Task SelectUperAsync(MomentUperSectionViewModel? item)
    {
        // Ignore selection notifications from a replaced list, including equal profiles.
        if (item is null || _isRebuilding || !Upers.Any(uper => ReferenceEquals(uper, item)))
        {
            return;
        }

        SelectedUper = item;
        await item.InitializeCommand.ExecuteAsync(null);
        // No selection/list writes after awaiting an item from this generation.
    }

    private async Task RebuildAsync()
    {
        _isRebuilding = true;
        IsLoading = true;
        try
        {
            // Retire every old section before awaiting any of them. In particular, an
            // old total page must finish before reset, and cannot enqueue another page.
            var pendingLoads = Upers.Select(uper => uper.RetireAsync()).ToArray();
            try
            {
                await Task.WhenAll(pendingLoads);
            }
            catch
            {
                // Failures belong to old callers; rebuilding is still a valid retry.
            }

            SelectedUper = default;
            Upers.Clear();
            var total = new MomentUperSectionViewModel(default, RefreshCommand);
            await LoadUpersAsync(total);
            SelectedUper = total;
            await total.InitializeCommand.ExecuteAsync(null);
            IsEmpty = total.IsEmpty;
            // Notify only after selection exposes the seeded total to its view.
            total.NotifyListUpdated();
        }
        finally
        {
            _isRebuilding = false;
            IsLoading = false;
        }

        Initialized?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadUpersAsync(MomentUperSectionViewModel total)
    {
        try
        {
            var response = await _service.GetComprehensiveMomentsAsync();
            // Seed synchronously on the captured UI context before exposing this total.
            total.InjectFirstPageData(response);
            Upers.Add(total);
            var upers = response.Users;
            if (upers?.Count > 0)
            {
                foreach (var item in upers)
                {
                    Upers.Add(new MomentUperSectionViewModel(item));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试获取综合动态时发生错误.");
            // Keep owner refresh reachable on failure, but do not fall back to a total
            // Initialize request that would load items without rebuilding the UP list.
            total.IsEmpty = total.Items.Count == 0;
            if (!Upers.Any(uper => ReferenceEquals(uper, total)))
            {
                Upers.Add(total);
            }
        }
    }
}
