using BiliCopilot.UI.SponsorBlock.Application;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal enum SponsorCloseRecoveryAvailability { NotClosing, ClosePending, Available, RetryPending, Unavailable, Complete }
internal sealed record SponsorCloseRecoveryState(bool HasPendingCleanup, ApplicationResult? LastCloseResult,
    SponsorCloseRecoveryAvailability Availability, ApplicationResult? LastRetryResult, long ExpectedRevision,
    bool HasLocalDurabilityWork);

internal sealed partial class SponsorRuntime
{
    private ApplicationResult? lastCloseResult, lastLocalRetryResult;
    private Task<ApplicationResult>? localRetryTask, localRecoveryCheckTask;
    private Task? localRetryObserver, closeStateObserver, localRecoveryCheckObserver;
    private bool closeNotificationQueued;
    private CloseNotification? deliveredCloseState;
    private sealed record CloseNotification(bool Pending, ApplicationResult? Close,
        SponsorCloseRecoveryAvailability Availability, ApplicationResult? Retry, bool LocalWork);

    internal event Action? CloseStateChanged;

    internal SponsorCloseRecoveryState CloseRecovery
    {
        get
        {
            dispatcher.CheckAccess();
            var view = Coordinator.Snapshot;
            var localWork = view.Dirty || view.StatisticsDirty || !view.UnsavedMutationOutcomes.IsDefaultOrEmpty;
            var availability = !closing ? SponsorCloseRecoveryAvailability.NotClosing
                : closed.Task.IsCompleted ? SponsorCloseRecoveryAvailability.Complete
                : localRetryTask is { IsCompleted: false } ? SponsorCloseRecoveryAvailability.RetryPending
                : closeAttempt is { IsCompleted: false } || localRecoveryCheckTask is { IsCompleted: false }
                    ? SponsorCloseRecoveryAvailability.ClosePending
                : view.Closed && view.StoreVersion is not null
                    && (localWork || LastCloseResult is { Status: ApplicationStatus.PersistenceFailure, Reason: "LocalChangesNotDurable" })
                    ? SponsorCloseRecoveryAvailability.Available : SponsorCloseRecoveryAvailability.Unavailable;
            return new(HasPendingCleanup, LastCloseResult, availability, lastLocalRetryResult, view.Revision, localWork);
        }
    }

    // ExpectedRevision is read at the explicit UI click. No automatic local retry, save fallback,
    // HTTP/identity operation, reopening, replay or discard is performed by this runtime wrapper.
    internal Task<ApplicationResult> RetryLocalDurabilityForCloseAsync(long expectedRevision) => dispatcher.InvokeAsync(() =>
    {
        if (localRetryTask is { IsCompleted: false }) return localRetryTask;
        if (CloseRecovery.Availability != SponsorCloseRecoveryAvailability.Available)
            return Task.FromResult(new ApplicationResult(ApplicationStatus.Unavailable, "CloseLocalRecoveryUnavailable"));
        localRetryTask = RetryLocalDurabilityCoreAsync(expectedRevision);
        localRetryObserver = ObserveLocalRetryAsync(localRetryTask);
        QueueCloseStateChanged();
        return localRetryTask;
    });

    private async Task<ApplicationResult> RetryLocalDurabilityCoreAsync(long expectedRevision)
    {
        await Task.Yield(); // Publish the actual retry before coordinator/store callbacks.
        ApplicationResult result;
        try { result = await Coordinator.RetryLocalDurabilityForCloseAsync(expectedRevision); }
        catch { result = new(ApplicationStatus.PersistenceFailure, "CloseLocalRecoveryFailed"); }
        lastLocalRetryResult = result;
        QueueCloseStateChanged();
        return result; // In particular: no await of Close/Wait or of the completion check.
    }

    private async Task ObserveLocalRetryAsync(Task<ApplicationResult> actualRetry)
    {
        // T5-05: Local retry finishes independently of the retained close check; this observer joins that exact retry, not itself.
        #pragma warning disable VSTHRD003
        var result = await actualRetry;
        #pragma warning restore VSTHRD003
        QueueCloseStateChanged();
        if (result.Status != ApplicationStatus.Success || !closing || closed.Task.IsCompleted) return;
        // Exactly one separately owned completion check for this successful actual local retry.
        // A UI runner registered by TrackUiWork can return its local result independently.
        localRecoveryCheckTask = CheckAfterLocalRecoveryAsync();
        localRecoveryCheckObserver = NotifyAfterCloseTaskAsync(localRecoveryCheckTask);
        QueueCloseStateChanged();
    }

    private async Task<ApplicationResult> CheckAfterLocalRecoveryAsync()
    {
        await Task.Yield(); // Outside APP publication and the completed local/UI retry task.
        if (closeAttempt is { } previous) await previous;
        return await CloseAsync();
    }

    private async Task NotifyAfterCloseTaskAsync(Task actual)
    {
        try {
            // T5-06: The close task is already issued and independently retained; its state-change observer is not a close producer.
            #pragma warning disable VSTHRD003
            await actual;
            #pragma warning restore VSTHRD003
        }
        catch { LastCloseResult = new(ApplicationStatus.ControllerFailure, "SponsorCloseTaskFailed"); }
        finally { QueueCloseStateChanged(); }
    }

    private void QueueCloseStateChanged()
    {
        dispatcher.CheckAccess();
        var state = CloseRecovery;
        var next = new CloseNotification(state.HasPendingCleanup, state.LastCloseResult, state.Availability,
            state.LastRetryResult, state.HasLocalDurabilityWork);
        // Revision-only/position publications are not close-state changes. UI reads the latest
        // revision at click time, not an event payload that could already be stale.
        if (next == deliveredCloseState || closeNotificationQueued) return;
        closeNotificationQueued = true;
        try { dispatcher.Post(DeliverCloseStateChanged); }
        catch { closeNotificationQueued = false; throw; }
    }

    private void DeliverCloseStateChanged()
    {
        dispatcher.CheckAccess();
        closeNotificationQueued = false;
        var state = CloseRecovery;
        var next = new CloseNotification(state.HasPendingCleanup, state.LastCloseResult, state.Availability,
            state.LastRetryResult, state.HasLocalDurabilityWork);
        if (next == deliveredCloseState) return;
        deliveredCloseState = next;
        // No ownership lock is acquired/held here; never invoke UI subscribers inline from Publish.
        if (CloseStateChanged is { } subscribers)
            foreach (Action subscriber in subscribers.GetInvocationList())
                try { subscriber(); } catch { /* Subscriber failure cannot rewrite cleanup/retry results. */ }
    }
}
