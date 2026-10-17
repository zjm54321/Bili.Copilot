// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.SponsorBlock.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// Local-save recovery during quit. Reachable from any page; the settings page does not need to be open.
/// </summary>
/// <remarks>
/// <para>All state comes from the runtime's authoritative <c>SponsorRuntime.CloseRecovery</c>
/// (<see cref="SponsorCloseRecoveryState"/>): <c>Availability</c>, <c>HasLocalDurabilityWork</c>,
/// <c>HasPendingCleanup</c>, <c>LastCloseResult</c>, <c>LastRetryResult</c>, <c>ExpectedRevision</c>.
/// Nothing is derived from snapshot fields any more. "保存到本机" appears only when
/// <c>Availability == Available</c>, so an in-progress retry, a close still running, or an unknown store
/// version never offers a save.</para>
/// <para>The state is read on the app UI thread (the runtime checks this with <c>CheckAccess</c>). Updates
/// come from <c>CloseStateChanged</c>, which the runtime posts and merges on the app UI thread.</para>
/// <para>On click: read the state again, confirm Available, take its <c>ExpectedRevision</c>, and call the
/// runtime wrapper <c>RetryLocalDurabilityForCloseAsync</c>. The same Task is registered with
/// <c>TrackUiWork</c> immediately. The wrapper returns only the local result and never awaits Close/Wait or
/// the completion check, so there is no cycle. No automatic retry, discard, network replay or force exit.
/// Success here is "saved to this device", not "the app has exited".</para>
/// <para>Unbind releases only this VM's subscription. A running save keeps going and stays tracked. Its result
/// is kept by the runtime as <c>LastRetryResult</c> and shows again after a rebind.</para>
/// </remarks>
public sealed partial class SponsorCloseRecoveryViewModel : ObservableObject
{
    private SponsorRuntime? _runtime;
    private DispatcherQueue? _queue;
    private long _epoch;
    private string? _localFailure;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isError = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _canRetry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _isBusy;

    /// <summary>
    /// Associates with the app's single runtime. Must be called on the app UI thread, which is the runtime's
    /// dispatcher. Only subscribes; never Close/Wait/Load.
    /// </summary>
    /// <returns>false when called off that thread.</returns>
    internal bool Bind(SponsorRuntime runtime, DispatcherQueue queue)
    {
        if (!queue.HasThreadAccess)
        {
            return false;
        }

        Unbind();
        _runtime = runtime;
        _queue = queue;
        _epoch++;

        // Subscribe first, then read: a change in between is not lost.
        runtime.CloseStateChanged += OnCloseStateChanged;
        Refresh();
        return true;
    }

    /// <summary>Releases only this VM's subscription. Does not cancel a save that is already running.</summary>
    internal void Unbind()
    {
        _epoch++;
        if (_runtime is { } runtime)
        {
            runtime.CloseStateChanged -= OnCloseStateChanged;
        }

        _runtime = null;
        _queue = null;
        _localFailure = null;
        IsBusy = false;
        IsVisible = false;
        CanRetry = false;
        ResultText = string.Empty;
    }

    // The runtime delivers this through a dispatcher post on the app UI thread, never from inside a publish
    // call stack. If it ever arrives off-thread, re-queue it.
    private void OnCloseStateChanged()
    {
        if (_queue is not { } queue)
        {
            return;
        }

        if (queue.HasThreadAccess)
        {
            Refresh();
            return;
        }

        var epoch = _epoch;
        _ = queue.TryEnqueue(() =>
        {
            if (epoch == _epoch)
            {
                Refresh();
            }
        });
    }

    private SponsorCloseRecoveryState? ReadState()
    {
        if (_runtime is not { } runtime)
        {
            return null;
        }

        try
        {
            return runtime.CloseRecovery;
        }
        catch (InvalidOperationException)
        {
            // Not on the runtime's app UI thread: no trusted state, so show nothing.
            return null;
        }
    }

    private void Refresh()
    {
        if (ReadState() is not { } state)
        {
            IsVisible = false;
            CanRetry = false;
            IsBusy = false;
            return;
        }

        var closeFailed = state.LastCloseResult is { } close && close.Status != ApplicationStatus.Success;
        var retried = state.LastRetryResult is not null;
        var availability = state.Availability;

        IsBusy = availability == SponsorCloseRecoveryAvailability.RetryPending;
        CanRetry = availability == SponsorCloseRecoveryAvailability.Available;
        ResultText = _localFailure
            ?? (state.LastRetryResult is { } retry ? Describe(retry) : string.Empty);

        // A normal quit (close running, no failure yet) shows nothing. The bar appears only after a real failed
        // close result, or once a recovery attempt exists.
        IsVisible = availability switch
        {
            SponsorCloseRecoveryAvailability.NotClosing => false,
            SponsorCloseRecoveryAvailability.Available or SponsorCloseRecoveryAvailability.RetryPending => true,
            _ => closeFailed || retried,
        };
        if (!IsVisible)
        {
            return;
        }

        IsError = state.HasLocalDurabilityWork
            || (availability == SponsorCloseRecoveryAvailability.Unavailable && closeFailed && !state.HasPendingCleanup);

        switch (availability)
        {
            case SponsorCloseRecoveryAvailability.Available:
                Title = "空降助手还有内容没有保存到本机";
                Message = "退出暂停在这里。可以再保存一次到本机；不会联网，也不会丢弃任何内容。"
                    + CleanupNote(state);
                break;
            case SponsorCloseRecoveryAvailability.RetryPending:
                Title = "正在保存到本机…";
                Message = "保存完成前不能再次保存。" + CleanupNote(state);
                break;
            case SponsorCloseRecoveryAvailability.ClosePending:
                Title = "空降助手正在继续退出检查";
                Message = state.HasLocalDurabilityWork
                    ? "还有内容没有保存到本机；检查结束后如果仍未保存，会在这里提示。"
                    : "本机内容不需要再保存。" + CleanupNote(state);
                break;
            case SponsorCloseRecoveryAvailability.Complete:
                Title = "空降助手已完成退出整理";
                Message = "本机内容已处理完毕。";
                break;
            default: // Unavailable
                if (state.HasLocalDurabilityWork)
                {
                    Title = "空降助手还有内容没有保存到本机";
                    Message = "现在无法从这里保存（例如本机数据版本无法确认），内容仍保留在内存中，不会被丢弃。"
                        + CleanupNote(state);
                }
                else if (state.HasPendingCleanup)
                {
                    Title = "正在等待播放器归还状态";
                    Message = "本机内容不需要再保存。播放器归还状态后才能完成退出，这一步不需要也不能通过保存解决。";
                }
                else
                {
                    Title = "空降助手没有完成退出整理";
                    Message = state.LastCloseResult is { } result ? SponsorUiText.Result(result) : "原因未知。";
                }

                break;
        }
    }

    private static string CleanupNote(SponsorCloseRecoveryState state)
        => state.HasPendingCleanup ? "播放器仍有状态需要归还。" : string.Empty;

    private bool CanRunRetry() => CanRetry && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunRetry))]
    private Task RetryAsync()
    {
        if (_runtime is not { } runtime || ReadState() is not { Availability: SponsorCloseRecoveryAvailability.Available } state)
        {
            Refresh();
            return Task.CompletedTask;
        }

        // Revision is read at click time. If it is stale, the result says so and nothing is sent again.
        _localFailure = null;
        var task = runtime.RetryLocalDurabilityForCloseAsync(state.ExpectedRevision);
        _ = runtime.TrackUiWork(task);
        return ObserveAsync(task, _epoch);
    }

    private async Task ObserveAsync(Task<ApplicationResult> task, long epoch)
    {
        IsBusy = true;
        CanRetry = false;
        try
        {
            // T5-09: The supplied close/retry result is independently owned; this UI observer is not part of the task it awaits.
            #pragma warning disable VSTHRD003
            _ = await task;
            #pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // The runtime did not return a result (e.g. dispatcher shutting down). Saying so here is the only
            // visible record; no retry.
            if (epoch == _epoch)
            {
                _localFailure = "保存没有完成，内容仍保留在内存中。";
            }
        }

        if (epoch == _epoch)
        {
            // The authoritative result (LastRetryResult) and availability come from the runtime.
            Refresh();
        }
    }

    // Copy follows the N4 close-durability result table and the runtime wrapper reasons. Raw reasons, paths
    // and exceptions are never shown.
    private static string Describe(ApplicationResult result)
    {
        var detail = result.Persistence is { } status && status != PersistenceStatus.Success
            ? "（" + SponsorUiText.Persistence(status) + "）"
            : string.Empty;
        return (result.Status, result.Reason) switch
        {
            (ApplicationStatus.Success, "LocalDurabilityRecoveredForClose") => "已保存到本机。空降助手会继续完成退出检查。",
            (ApplicationStatus.Success, "NoPendingLocalDurability") => "本机已是最新，没有需要保存的内容。",
            (ApplicationStatus.Success, _) => "已保存到本机。",
            (ApplicationStatus.Stale, _) => "内容刚刚有变化，这次没有保存。请确认后再点一次。",
            (ApplicationStatus.Busy, _) => "还有操作在收尾，这次没有保存。稍后再点一次。",
            (ApplicationStatus.Unavailable, "CloseLocalRecoveryUnavailable") => "现在不能从这里保存。",
            (ApplicationStatus.Unavailable, "CloseDurabilityRecoveryRequiresClosing") => "空降助手没有在退出，这里不需要保存。",
            (ApplicationStatus.Unavailable, "InjectedJournalOwnsCompletionPersistence") => "操作记录由其他组件负责保存，这里无法保存。",
            (ApplicationStatus.PersistenceFailure, "KnownStoreVersionRequired") => "无法确认本机数据的版本，为了安全没有写入" + detail + "。",
            (ApplicationStatus.PersistenceFailure, "LocalCloseDraftsNotDurable") => "草稿和设置没有保存成功" + detail + "，内容仍保留。",
            (ApplicationStatus.PersistenceFailure, "LocalCloseStatisticsNotDurable") => "本机统计没有保存成功" + detail + "，内容仍保留。",
            (ApplicationStatus.PersistenceFailure, "LocalCloseJournalNotDurable") => "操作记录没有保存成功" + detail + "，内容仍保留。",
            (ApplicationStatus.PersistenceFailure, _) => "仍有内容没有保存成功" + detail + "，内容仍保留。",
            _ => SponsorUiText.Result(result),
        };
    }
}
