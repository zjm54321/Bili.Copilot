using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    /// <summary>Explicit local recovery only after close has stopped ordinary admission. Success means
    /// this owner's current local durability obligations are clear, NOT player/runtime close completion.
    /// Caller schedules any close recheck outside APP callbacks and its tracked UI work.</summary>
    internal Task<ApplicationResult> RetryLocalDurabilityForCloseAsync(long expectedRevision) => OnOwner(async () =>
    {
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        if (!closing) return Result(ApplicationStatus.Unavailable, "CloseDurabilityRecoveryRequiresClosing");
        // Completed physical requests remain in this set until their APP completion owner commits
        // or retains the real outcome. Never wait for that owner while holding this same serial lane.
        if (requests.Count != 0 || queue.Entries.Any(e => e.State == DraftState.SubmissionInFlight)
            || players.Values.Any(p => p.EditorWork is { IsCompleted: false } || p.CleanupWork is { IsCompleted: false }
                || p.Attachment.Latest is { } latest && latest.Sequence > p.ReceivedSequence))
            return Result(ApplicationStatus.Busy, "LocalCloseOutcomeWorkNotSettled");
        if (version is null)
        {
            persistence = PersistenceStatus.InvalidData;
            Publish();
            return new(ApplicationStatus.PersistenceFailure, "KnownStoreVersionRequired", persistence);
        }
        // An injected journal owns its own failed completions; there is no safe local flush API for it.
        if (storeJournal is null && mutations.Any(m => m.Operation != ManualOperation.Submit && !m.Persisted))
            return Result(ApplicationStatus.Unavailable, "InjectedJournalOwnsCompletionPersistence");
        if (!dirty && !statisticsDirty && !HasUnsavedJournal)
        {
            Publish();
            return Result(ApplicationStatus.Success, "NoPendingLocalDurability");
        }

        var stage = "Drafts";
        try
        {
            if (dirty)
            {
                // Capture CURRENT queue/settings through the existing expected-version envelope path.
                // Never restore/reload the queue or retry a rejected UI-only settings candidate.
                var status = await SaveAsync(settings);
                if (status != PersistenceStatus.Success) return CloseDurabilityFailure(status, stage);
            }
            stage = "Statistics";
            if (statisticsDirty)
            {
                var status = await PersistStatisticsCoreAsync();
                if (status != PersistenceStatus.Success) return CloseDurabilityFailure(status, stage);
            }
            stage = "Journal";
            if (HasUnsavedJournal)
            {
                // Flush captured completed outcomes only. No MutationIntent, client or remote read.
                var status = await storeJournal!.FlushAsync();
                SetAuxiliaryStatus(status);
                if (status != PersistenceStatus.Success) return CloseDurabilityFailure(status, stage);
            }
        }
        catch (OperationCanceledException) { return CloseDurabilityFailure(PersistenceStatus.Cancelled, stage); }
        catch { return CloseDurabilityFailure(PersistenceStatus.IoFailure, stage); }

        // Helpers alone adopt committed versions and clear proved bits. Partial successes are retained;
        // no blanket dirty reset or rollback conceals a remaining obligation.
        Publish();
        return dirty || statisticsDirty || HasUnsavedJournal
            ? new(ApplicationStatus.PersistenceFailure, "LocalChangesNotDurable", persistence)
            : Result(ApplicationStatus.Success, "LocalDurabilityRecoveredForClose");
    });

    private ApplicationResult CloseDurabilityFailure(PersistenceStatus status, string stage)
    {
        persistence = status;
        if (stage == "Statistics") statisticsPersistence = status;
        if (stage == "Statistics" || stage == "Journal") SetAuxiliaryStatus(status);
        Publish();
        return new(ApplicationStatus.PersistenceFailure, "LocalClose" + stage + "NotDurable", status);
    }
}
