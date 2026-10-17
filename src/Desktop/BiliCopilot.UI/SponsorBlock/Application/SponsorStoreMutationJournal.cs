using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;

namespace BiliCopilot.UI.SponsorBlock.Application;

/// <summary>Owner-lane only. The commit delegate uses the coordinator's authoritative version and
/// adopts its successful snapshot before returning. It MUST NOT enter OnOwner recursively. Only
/// locally captured successful preparations enter live; decoded history never grants send authority.</summary>
internal sealed class SponsorStoreMutationJournal(
    Func<SponsorAuxiliarySnapshot> snapshot,
    Func<SponsorAuxiliarySnapshot, Task<PersistenceStatus>> commit,
    Func<bool> hasOwnerAccess) : ISponsorMutationJournal
{
    private readonly Dictionary<string, MutationRecord> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableJournalEntry> pending = new(StringComparer.Ordinal);
    internal ImmutableArray<DurableJournalEntry> UnsavedOutcomes => pending.Values.ToImmutableArray();

    public async Task<PersistenceStatus> BeforeSendAsync(MutationRecord record)
    {
        VerifyOwner();
        var current = snapshot();
        if (record.Operation == ManualOperation.Submit) return PersistenceStatus.InvalidData;
        if (current.Journal.Any(e => e.Request.Id == record.Id) || live.ContainsKey(record.Id) || pending.ContainsKey(record.Id))
            return PersistenceStatus.HistoryConflict;
        if (current.Journal.Length >= 512) return PersistenceStatus.CapacityReached;
        var prepared = new DurableJournalEntry(Capture(record), DurableJournalPhase.Prepared, null);
        var saved = await commit(current with { Journal = current.Journal.Add(prepared) });
        if (saved == PersistenceStatus.Success) live.Add(record.Id, record);
        return saved;
    }

    public async Task<PersistenceStatus> CompleteAsync(MutationRecord record, WriteResult result)
    {
        VerifyOwner();
        // Reference identity is a local capture token, NOT a record reconstructed from disk.
        if (!live.TryGetValue(record.Id, out var original) || !ReferenceEquals(original, record))
            return PersistenceStatus.HistoryConflict;
        live.Remove(record.Id); // completion is captured once, even if the subsequent IO fails.
        var outcome = new DurableWriteOutcome(result.Status, result.HttpStatus, result.Reason,
            result.BatchAccepted, result.ItemsConfirmed, result.ConfirmedPort);
        pending.Add(record.Id, new(Capture(record), DurableJournalPhase.Completed, outcome));
        return await FlushAsync();
    }

    // Local persistence only (also exposed for explicit retry). Never constructs an HTTP intent or
    // changes an outcome; a later actual completion can commit other pending completions in the batch.
    internal async Task<PersistenceStatus> FlushAsync()
    {
        VerifyOwner();
        if (pending.Count == 0) return PersistenceStatus.Success;
        var current = snapshot();
        foreach (var entry in pending.Values)
        {
            var old = current.Journal.FirstOrDefault(e => e.Request.Id == entry.Request.Id);
            if (old is null || old.Phase != DurableJournalPhase.Prepared) return PersistenceStatus.HistoryConflict;
        }
        var next = current.Journal.Select(e => pending.TryGetValue(e.Request.Id, out var completed) ? completed : e).ToImmutableArray();
        var saved = await commit(current with { Journal = next });
        if (saved == PersistenceStatus.Success) pending.Clear();
        return saved;
    }

    private void VerifyOwner()
    {
        if (!hasOwnerAccess()) throw new InvalidOperationException("SponsorJournalOwnerRequired");
    }

    internal static DurableMutationRecord Capture(MutationRecord r) => new(r.Id, r.Attachment,
        new(r.Stamp.Owner.Media.Combined, r.Stamp.Owner.Generation, r.Stamp.PolicyRevision,
            r.Stamp.ObservationRevision, r.Stamp.VisitRevision, r.Stamp.MuteUserEpoch, r.Stamp.RateUserEpoch),
        r.DraftRevision, r.Operation,
        r.Target is null ? null : new(r.Target.Media.Combined, r.Target.Uuid, r.Target.Source),
        r.Vote, r.Text, r.Port, r.Service, r.Duration, r.OriginalSegment, r.PublicUserId);
}
