using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    private bool HasUnsavedJournal => storeJournal?.UnsavedOutcomes.IsEmpty == false;

    // Adopt metadata only. Auxiliary writes MUST NOT restore the live Q4 pending-operation map.
    private void AdoptAuxiliary(SponsorLocalSnapshot snapshot)
    {
        version = snapshot.Version; auxiliary = snapshot.Auxiliary;
        statisticsDirty = !ledger.Matches(auxiliary.Statistics);
        mutations = mutations.Select(m => !m.Persisted && auxiliary.Journal.Any(e => e.Request.Id == m.Id
            && e.Phase == DurableJournalPhase.Completed && e.Outcome?.Status == m.Status)
            ? m with { Persisted = true } : m).ToImmutableArray();
    }

    private void RestoreAuxiliary(SponsorLocalSnapshot snapshot)
    {
        ledger = SponsorReceiptLedger.FromDurable(snapshot.Auxiliary.Statistics);
        AdoptAuxiliary(snapshot); auxiliaryPersistence = statisticsPersistence = PersistenceStatus.Success;
        auxiliaryCapacityReached = false;
        mutations = []; // full recovered history is exposed separately in Auxiliary.Journal.
    }

    // Runs with APP serial already held, including journal callbacks; never calls OnOwner.
    private async Task<PersistenceStatus> CommitAuxiliaryAsync(SponsorAuxiliarySnapshot candidate)
    {
        if (version is null) return SetAuxiliaryStatus(PersistenceStatus.InvalidData);
        PersistenceResult<SponsorLocalSnapshot> saved;
        try { saved = await store.UpdateAuxiliaryAsync(version, candidate); }
        catch { return SetAuxiliaryStatus(PersistenceStatus.IoFailure); }
        SetAuxiliaryStatus(saved.Status);
        if (saved.Success) AdoptAuxiliary(saved.Value!);
        return saved.Status;
    }

    private PersistenceStatus SetAuxiliaryStatus(PersistenceStatus status)
    {
        persistence = auxiliaryPersistence = status;
        if (status == PersistenceStatus.TooLarge) auxiliaryCapacityReached = true;
        else if (status == PersistenceStatus.Success) auxiliaryCapacityReached = false;
        return status;
    }

    private static ApplicationResult AuxiliaryResult(PersistenceStatus status) =>
        new(status == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure,
            Persistence: status);

    private async Task<PersistenceStatus> PersistStatisticsCoreAsync()
    {
        var status = version is null ? SetAuxiliaryStatus(PersistenceStatus.InvalidData) : statisticsDirty
            ? await CommitAuxiliaryAsync(auxiliary with { Statistics = ledger.ExportDurable() }) : PersistenceStatus.Success;
        statisticsPersistence = status;
        return status;
    }

    internal Task<ApplicationResult> SaveStatisticsAsync(long expectedRevision) => OnOwner(async () =>
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        var status = await PersistStatisticsCoreAsync(); Publish(); return AuxiliaryResult(status);
    });

    internal Task<ApplicationResult> SavePendingMutationsAsync(long expectedRevision) => OnOwner(async () =>
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        if (storeJournal is null) return Result(ApplicationStatus.Unavailable, "InjectedJournalOwnsCompletionPersistence");
        var status = await storeJournal.FlushAsync(); SetAuxiliaryStatus(status);
        Publish(); return AuxiliaryResult(status);
    });

    // Injection compatibility is retained, but an independent writer must not silently advance this
    // owner's scope. Default journal commits adopt the exact returned snapshot instead of reloading.
    private async Task<PersistenceStatus> CheckInjectedJournalVersionAsync(PersistenceStatus status)
    {
        if (status != PersistenceStatus.Success || storeJournal is not null) return SetAuxiliaryStatus(status);
        var current = await store.LoadAsync();
        if (current.Status is not (PersistenceStatus.Success or PersistenceStatus.Missing)) return SetAuxiliaryStatus(current.Status);
        return SetAuxiliaryStatus(current.Value?.Version == version ? status : PersistenceStatus.Conflict);
    }

    internal Task<ApplicationResult> AddWhitelistedUpAsync(long expectedRevision, string upUid) =>
        SetWhitelistedUpAsync(expectedRevision, upUid, true);
    internal Task<ApplicationResult> RemoveWhitelistedUpAsync(long expectedRevision, string upUid) =>
        SetWhitelistedUpAsync(expectedRevision, upUid, false);
    internal Task<ApplicationResult> SetWhitelistedUpAsync(long expectedRevision, string upUid, bool whitelisted) =>
        OnOwner(() => SetWhitelistedUpCoreAsync(expectedRevision, upUid, whitelisted));

    internal Task<ApplicationResult> SetCurrentUpWhitelistAsync(PlayerTicket ticket, long expectedRevision, bool whitelisted) =>
        OnOwner(async () =>
        {
            if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
            if (state.Channel.Verification != SponsorChannelVerification.Verified || state.VerifiedUpUid is null)
                return Result(ApplicationStatus.Unavailable, "ActualVerifiedUpUidRequired");
            return await SetWhitelistedUpCoreAsync(expectedRevision, state.VerifiedUpUid, whitelisted);
        });

    private async Task<ApplicationResult> SetWhitelistedUpCoreAsync(long expectedRevision, string upUid, bool whitelisted)
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        if (!SponsorUpUid.IsValid(upUid)) return Result(ApplicationStatus.Invalid, "CanonicalUpUidRequired");
        if (version is null) return AuxiliaryResult(SetAuxiliaryStatus(PersistenceStatus.InvalidData));
        var values = auxiliary.WhitelistedUpUids;
        var exists = values.Contains(upUid, StringComparer.Ordinal);
        if (exists == whitelisted) return Result(ApplicationStatus.Success);
        if (whitelisted && values.Length >= 4096)
        { SetAuxiliaryStatus(PersistenceStatus.CapacityReached); Publish(); return AuxiliaryResult(PersistenceStatus.CapacityReached); }
        var next = whitelisted ? values.Add(upUid) : values.Remove(upUid);
        var status = await CommitAuxiliaryAsync(auxiliary with { WhitelistedUpUids = next });
        if (status == PersistenceStatus.Success)
        {
            ++policyRevision;
            foreach (var state in players.Values)
                if (state.VerifiedUpUid is { } uid && state.Channel.Verification == SponsorChannelVerification.Verified)
                    state.Channel = new(SponsorChannelVerification.Verified, next.Contains(uid, StringComparer.Ordinal));
            await PushAllAsync();
        }
        Publish(); return AuxiliaryResult(status);
    }

    /// <summary>Metadata adapter handshake. Only pass the actual UP UID for this exact owner; Verified
    /// is an explicit trusted adapter assertion, never inferred by this method from a supplied string.</summary>
    internal Task<ApplicationResult> SetVerifiedUpUidAsync(PlayerTicket ticket, string? upUid,
        SponsorChannelVerification verification) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
        if (!Enum.IsDefined(verification) || verification == SponsorChannelVerification.Verified && !SponsorUpUid.IsValid(upUid)
            || verification != SponsorChannelVerification.Verified && upUid is not null)
            return Result(ApplicationStatus.Invalid, "ActualVerifiedUpUidRequired");
        state.VerifiedUpUid = upUid;
        state.Channel = new(verification, upUid is null ? null : auxiliary.WhitelistedUpUids.Contains(upUid, StringComparer.Ordinal));
        await PushAsync(state); Publish(); return Result(ApplicationStatus.Success);
    });

    internal Task<ApplicationResult> SaveFilterPreferencesAsync(long expectedRevision, DurableFilterPreferences preferences) =>
        OnOwner(async () =>
        {
            if (Stopping) return Result(ApplicationStatus.Closed);
            if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
            // Store validates bounded known categories independently for the two off-by-default surfaces.
            var status = await CommitAuxiliaryAsync(auxiliary with { Filters = preferences });
            Publish(); return AuxiliaryResult(status);
        });

    internal Task<ApplicationResult> ImportStatisticsAsync(long expectedRevision, SponsorStatisticsArchive archive) =>
        OnOwner(() => ImportStatisticsCoreAsync(expectedRevision, archive));

    private async Task<ApplicationResult> ImportStatisticsCoreAsync(long expectedRevision, SponsorStatisticsArchive archive)
    {
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        if (Stopping || players.Count != 0 || requests.Count != 0 || editor is not null || dirty || statisticsDirty || HasUnsavedJournal)
            return Result(ApplicationStatus.Busy, "QuiescedStatisticsImportRequired");
        var candidate = new SponsorReceiptLedger();
        if (!candidate.Import(archive)) return Result(ApplicationStatus.Invalid);
        // Import is an explicit monotonic aggregate+watermark update, never a destructive reset.
        // Store HistoryConflict rejects missing/decreased dedupe facts and sticky-capacity resets.
        var status = await CommitAuxiliaryAsync(auxiliary with { Statistics = candidate.ExportDurable() });
        statisticsPersistence = status;
        if (status == PersistenceStatus.Success) { ledger = candidate; statisticsDirty = false; }
        Publish(); return AuxiliaryResult(status);
    }
}
