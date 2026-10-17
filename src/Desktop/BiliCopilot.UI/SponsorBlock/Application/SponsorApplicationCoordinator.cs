using System.Collections.Concurrent;
using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Api;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

/// <summary>One explicitly composed app owner; no globals, player timer, implicit identity or IO on construction.
/// Queue mutation + archive capture + commit are serialized on the APP dispatcher. Network work releases
/// that lane; original completion returns to it, even after detach/close, without replay.</summary>
internal sealed partial class SponsorApplicationCoordinator
{
    private sealed class PlayerState(SponsorPlayerAttachment attachment)
    {
        internal readonly SponsorPlayerAttachment Attachment = attachment;
        internal SponsorControllerResult? Result;
        internal ImmutableArray<SponsorSegment> Raw = [];
        internal ReadStatus ReadStatus = ReadStatus.Disabled;
        internal PlaybackChannel Channel = new();
        internal string? VerifiedUpUid;
        internal bool? KnownMusic;
        internal ImmutableHashSet<PlaybackSegmentKey>? Aligned;
        internal bool Editing;
        internal PreviewInterval? Preview;
        internal DraftSelection? PreparedDraft;
        internal SponsorPreviewStatus? PreviewPlayback;
        internal SponsorPreviewToken? ReleasedPreviewToken;
        internal SponsorPreviewToken? EndRequestedToken;
        internal SponsorEditorReply? LastEditorReply;
        internal Task<SponsorEditorReply>? CleanupWork;
        internal Task<SponsorEditorReply>? EditorWork;
        internal ApplicationResult? CleanupResult;
        internal long ReceivedSequence = -1;
        internal long InputRevision;
        internal long ReadEpoch;
        internal ApplicationStatus DeliveryStatus;
    }
    private sealed class ClientEpoch(SponsorClient client)
    {
        internal readonly SponsorClient Client = client;
        internal int Users;
        internal bool Retired;
    }
    private readonly SponsorPersistenceStore store;
    private readonly ISponsorClientFactory clients;
    private readonly ISponsorDispatcher dispatcher;
    private readonly ISponsorMutationJournal journal;
    private readonly SponsorStoreMutationJournal? storeJournal;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Dictionary<Guid, PlayerState> players = [];
    private readonly ConcurrentDictionary<Guid, SponsorPlayerAttachment> admissions = new();
    private readonly HashSet<Task> requests = [];
    private SponsorReceiptLedger ledger = new();
    private SponsorAuxiliarySnapshot auxiliary = SponsorAuxiliarySnapshot.Empty;
    private PersistenceStatus? auxiliaryPersistence, statisticsPersistence;
    private bool statisticsDirty, auxiliaryCapacityReached;
    private DraftQueue queue = new();
    private SponsorLocalSettings settings = new();
    private ClientEpoch? client;
    private string? version;
    private long policyRevision, revision, draftRevision;
    private PersistenceStatus? persistence;
    private bool dirty, closing;
    private int queued;
    private int closeRequested;
    private bool Stopping => closing || Volatile.Read(ref closeRequested) != 0;
    private Guid? editor;
    private PlaybackStamp? editorStamp;
    private ImmutableArray<MutationCompletion> mutations = [];
    private SponsorApplicationView view = new(0, 0, new(), [], null, false, false, [], new(0, 0, 0, 0), []);
    internal SponsorApplicationView Snapshot => Volatile.Read(ref view);
    // Raised on APP dispatcher. Other-window view models must marshal this immutable record themselves.
    internal event Action<SponsorApplicationView>? Updated;

    internal SponsorApplicationCoordinator(SponsorPersistenceStore store, ISponsorClientFactory clients,
        ISponsorDispatcher dispatcher, ISponsorMutationJournal? journal = null)
    {
        this.store = store; this.clients = clients; this.dispatcher = dispatcher;
        if (journal is null)
            storeJournal = new(() => auxiliary, CommitAuxiliaryAsync, () => dispatcher.HasAccess && serial.CurrentCount == 0);
        this.journal = journal ?? storeJournal!;
    }

    private Task<T> OnOwner<T>(Func<Task<T>> action) => dispatcher.RunAsync(async () =>
    {
        if (Interlocked.Increment(ref queued) > 128)
        { Interlocked.Decrement(ref queued); throw new InvalidOperationException("SponsorApplicationQueueFull"); }
        try
        {
            await serial.WaitAsync();
            try { return await action(); }
            finally { serial.Release(); }
        }
        finally { Interlocked.Decrement(ref queued); }
    });
    private Task<T> OnOwner<T>(Func<T> action) => OnOwner(() => Task.FromResult(action()));
    private static ApplicationResult Result(ApplicationStatus status, string? reason = null) => new(status, reason);
    private bool Current(PlayerTicket ticket, out PlayerState state)
    {
        if (players.TryGetValue(ticket.Attachment, out state!) && !Stopping && !state.Attachment.IsClosed
            && state.Attachment.Latest?.Stamp == ticket.Stamp && state.Result?.Owner == ticket.Stamp.Owner
            && ticket.Stamp.PolicyRevision == policyRevision) return true;
        return false;
    }
    private void Publish()
    {
        var next = new SponsorApplicationView(++revision, draftRevision, settings, queue.Entries, persistence, dirty, closing,
            players.Values.Select(p => new SponsorPlayerView(p.Attachment.Id, UiController(p.Result), p.ReadStatus, p.Raw,
                 p.Channel, p.KnownMusic, p.Editing, p.Preview, !p.Attachment.IsClosed, p.DeliveryStatus)
                  {
                       VerifiedUpUid = p.VerifiedUpUid, EditorPortAvailable = p.Attachment.HasEditorPort,
                       FilterFacts = PublishedFilterFacts(p),
                      EditorCommandPending = p.EditorWork is { IsCompleted: false } || p.CleanupWork is { IsCompleted: false },
                      PreviewPlayback = UiPreview(p.PreviewPlayback), LastEditorReply = p.LastEditorReply,
                      CleanupPending = p.Attachment.IsClosed || PreviewCleanupPending(p),
                      EditorCapabilities = EditorAvailability(p),
                  }).ToImmutableArray(),
             ledger.Totals, mutations)
        {
            StoreVersion = version, Auxiliary = auxiliary, StatisticsDirty = statisticsDirty,
            StatisticsPersistence = statisticsPersistence,
            AuxiliaryPersistence = auxiliaryPersistence, JournalCapacityReached = auxiliary.Journal.Length >= 512,
            AuxiliaryCapacityReached = auxiliaryCapacityReached,
            UnsavedMutationOutcomes = storeJournal?.UnsavedOutcomes ?? [],
            PlayerCleanupPending = players.Values.Any(p => p.Attachment.IsClosed || PreviewCleanupPending(p)),
        };
        Volatile.Write(ref view, next);
        if (Updated is { } handlers)
            foreach (Action<SponsorApplicationView> handler in handlers.GetInvocationList())
                try { handler(next); } catch { /* Subscriber failures cannot roll back committed state. */ }
    }
    private static SponsorControllerResult? UiController(SponsorControllerResult? value) => value is null ? null : value with
    {
        Failure = value.Failure is null ? null : "ControllerFailure",
        Operation = value.Operation is null ? null : value.Operation with { Failure = value.Operation.Failure is null ? null : "ControllerOperationFailure" },
        PreviewPlayback = UiPreview(value.PreviewPlayback),
    };
    private static SponsorPreviewStatus? UiPreview(SponsorPreviewStatus? value) => value is null ? null : value with
    { Reason = value.Reason is null ? null : "Preview" + value.Phase };

    internal Task<ApplicationResult> LoadAsync() => OnOwner(async () =>
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        // Reload cannot erase a live queue or its pending-operation map. Explicit backup is separate.
        if (players.Count != 0 || requests.Count != 0 || dirty || statisticsDirty || HasUnsavedJournal || editor is not null)
            return Result(ApplicationStatus.Busy, "QuiescedLoadRequired");
        var loaded = await store.LoadAsync();
        persistence = loaded.Status;
        if ((loaded.Status == PersistenceStatus.Success || loaded.Status == PersistenceStatus.Missing && version is null)
            && loaded.Value is { } value)
        {
            settings = value.Settings; queue = value.Drafts.Restore(); RestoreAuxiliary(value);
            ++policyRevision; ++draftRevision; RetireClient();
        }
        Publish();
        return loaded.Value is null || loaded.Status == PersistenceStatus.Missing && version != "missing"
            ? new(ApplicationStatus.PersistenceFailure, Persistence: loaded.Status) : Result(ApplicationStatus.Success);
    });

    internal async Task<Guid?> AttachAsync(ISponsorDispatcher playerDispatcher, ISponsorPlayerPort port)
    {
        var attachment = new SponsorPlayerAttachment(playerDispatcher, port, Receive);
        var admitted = await OnOwner(() =>
        {
            if (Stopping || version is null || players.Count >= 16) return false;
            players.Add(attachment.Id, new(attachment)); admissions.TryAdd(attachment.Id, attachment); Publish(); return true;
        });
        if (!admitted) return null;
        try { if (await attachment.AttachAsync()) return attachment.Id; }
        catch { /* No exception text or native path enters UI records. */ }
        await OnOwner(() => { players.Remove(attachment.Id); admissions.TryRemove(attachment.Id, out _); Publish(); return false; });
        return null;
    }

    private void Receive(SponsorPlayerAttachment attachment, SponsorControllerResult observed) => ObserveDelivery(ReceiveAsync(attachment, observed));
    private async void ObserveDelivery(Task task)
    {
        try {
            // T5-01: Delivery was independently scheduled; this observer does not produce that task or make its runner await Runtime.Close.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
        } catch { /* Dispatcher shutdown: no new work can be delivered. */ }
    }
    // Receive's captured arguments are passed explicitly; no mutable event payload is retained.
    private Task<bool> ReceiveAsync(SponsorPlayerAttachment attachment, SponsorControllerResult observed) => OnOwner(async () =>
    {
        if (!players.TryGetValue(attachment.Id, out var state) || observed.Sequence <= state.ReceivedSequence) return false;
        state.ReceivedSequence = observed.Sequence;
        var receiptChanged = ReferenceEquals(attachment.InitialSnapshot, observed)
            ? ledger.SeedBaseline(attachment.Id, observed) : ledger.Accept(attachment.Id, observed);
        if (receiptChanged) { statisticsDirty = true; await PersistStatisticsCoreAsync(); }
        // Direct editor replies rebase before releasing APP serial. Queued older events may still
        // carry real receipts, but must never undo that newer context or discard an active capture.
        if (observed.Sequence <= (state.Result?.Sequence ?? -1)) { if (receiptChanged) Publish(); return false; }
        if (attachment.Latest?.Owner != observed.Owner) { if (receiptChanged) Publish(); return false; }
        var previous = state.Result;
        var changedOwner = previous?.Owner != observed.Owner;
        state.Result = observed;
        TrackPreview(state, observed);
        if (changedOwner)
        {
            ++state.ReadEpoch; state.Raw = []; state.ReadStatus = ReadStatus.Disabled;
            state.Channel = new(); state.VerifiedUpUid = null; state.KnownMusic = null; state.Aligned = null;
        }
        var previewOwnsContext = PreviewUnresolved(state) && state.PreviewPlayback is { } activePreview && activePreview.Token.Owner == observed.Owner
            && activePreview.Phase is not (SponsorPreviewPhase.Interrupted or SponsorPreviewPhase.Ended)
            && observed.Stamp?.PolicyRevision == policyRevision;
        var invalidateEditor = editor == attachment.Id && (observed.Stamp is null || editorStamp is null
            || (!previewOwnsContext && !SameEditorContext(editorStamp, observed.Stamp)));
        if (invalidateEditor) ClearEditor();
        else if (editor == attachment.Id) editorStamp = observed.Stamp;
        if ((changedOwner || invalidateEditor) && observed.Owner is not null) await PushAsync(state);
        Publish(); return true;
    });

    private async Task<bool> PushAsync(PlayerState state)
    {
        if (state.Attachment.IsClosed || state.Result?.Owner is not { } owner
            || state.Attachment.Latest?.Owner != owner) return false;
        var facts = settings.FactsFor(owner.Media);
        var segments = state.Raw.Where(s => s.Source != 2 || settings.IncludeExternalSegments)
            .Where(s => s.Source != 3 || settings.IncludeDanmakuSegments).ToImmutableArray();
        var input = new SponsorCoordinatorInput(owner, ++state.InputRevision, segments,
            settings.ForPlayback(policyRevision), state.Channel)
        {
            Speed = settings.Speed, KnownMusic = state.KnownMusic,
            LocalHidden = facts.Hidden, LocalDownvoted = facts.Downvoted, ExternalSourceExcluded = facts.ExternalSourceExcluded,
            AlignedEligibleSource2 = state.Aligned, IgnorePlaybackForEditing = state.Editing, Preview = state.Preview,
        };
        try
        {
            var accepted = await state.Attachment.ApplyAsync(input);
            state.DeliveryStatus = accepted ? ApplicationStatus.Success : ApplicationStatus.Stale;
            return accepted;
        }
        catch { state.DeliveryStatus = ApplicationStatus.ControllerFailure; return false; }
    }
    private async Task PushAllAsync()
    { foreach (var state in players.Values) await PushAsync(state); }

    private void ClearEditor()
    {
        if (editor is { } id && players.TryGetValue(id, out var state))
        { state.Editing = false; state.Preview = null; state.PreparedDraft = null; }
        queue.SwitchActiveMedia(null, 0); editor = null; editorStamp = null;
        // PreviewPlayback/token and CleanupWork deliberately survive loss of local editor context.
    }

    private static bool SameEditorContext(PlaybackStamp a, PlaybackStamp b) => a.Owner == b.Owner
        && a.PolicyRevision == b.PolicyRevision && a.VisitRevision == b.VisitRevision;
    private static bool PreviewUnresolved(PlayerState state) => state.PreviewPlayback is { } preview
        && state.ReleasedPreviewToken != preview.Token;
    private static bool PreviewCleanupPending(PlayerState state) => PreviewUnresolved(state) && state.PreviewPlayback is { } preview
        && (state.EndRequestedToken == preview.Token || preview.Phase is
            SponsorPreviewPhase.Restoring or SponsorPreviewPhase.AwaitingReturn or SponsorPreviewPhase.NeedsReconciliation
            or SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted);
    private static void TrackPreview(PlayerState state, SponsorControllerResult snapshot)
    {
        if (snapshot.PreviewPlayback is not { } preview) return; // Absence is not a cleanup receipt.
        if (state.PreviewPlayback is { } retained && retained.Token != preview.Token && PreviewUnresolved(state)) return;
        state.PreviewPlayback = preview;
    }

    private async Task<PersistenceStatus> SaveAsync(SponsorLocalSettings candidate)
    {
        if (version is null) { persistence = PersistenceStatus.InvalidData; return PersistenceStatus.InvalidData; }
        var archive = SponsorDraftArchive.Capture(queue); // synchronous, before ANY IO; live pending map survives.
        if (!archive.Success) { persistence = archive.Status; return archive.Status; }
        var saved = await store.SaveAsync(version, candidate, archive.Value!);
        persistence = saved.Status;
        if (saved.Success) { AdoptAuxiliary(saved.Value!); dirty = false; }
        return saved.Status;
    }

    internal Task<ApplicationResult> SaveSettingsAsync(long expectedRevision, SponsorLocalSettings candidate) => OnOwner(async () =>
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        var status = await SaveAsync(candidate);
        if (status != PersistenceStatus.Success) { Publish(); return new(ApplicationStatus.PersistenceFailure, Persistence: status); }
        settings = candidate; ++policyRevision; ClearEditor(); RetireClient();
        foreach (var player in players.Values) { ++player.ReadEpoch; player.Aligned = null; }
        await PushAllAsync(); Publish(); return Result(ApplicationStatus.Success);
    });

    internal Task<ApplicationResult> SetOwnerFactsAsync(PlayerTicket ticket, PlaybackChannel channel,
        bool? knownMusic, ImmutableHashSet<PlaybackSegmentKey>? alignedEligibleSource2 = null) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
        if (!Enum.IsDefined(channel.Verification) || alignedEligibleSource2 is not null
            && (alignedEligibleSource2.Count > 4096 || alignedEligibleSource2.Any(k => k.Media != ticket.Stamp.Owner.Media
                || k.Source != 2 || !state.Raw.Any(s => s.Uuid == k.Uuid && s.Source == 2)))) return Result(ApplicationStatus.Invalid);
        // Caller assertion is deliberately explicit, exact-owner and never inferred from a port binding.
        state.VerifiedUpUid = null;
        state.Channel = channel; state.KnownMusic = knownMusic; state.Aligned = alignedEligibleSource2;
        await PushAsync(state); Publish(); return Result(ApplicationStatus.Success);
    });

    private ClientEpoch AcquireClient()
    {
        if (!settings.NetworkEnabled || Stopping || requests.Count >= 16) throw new InvalidOperationException("SponsorNetworkAdmissionClosed");
        client ??= new(clients.Create(new(true, settings.ViewedTelemetryEnabled)));
        ++client.Users; return client;
    }
    private void ReleaseClient(ClientEpoch epoch)
    { if (--epoch.Users == 0 && epoch.Retired) epoch.Client.Dispose(); }
    private void RetireClient()
    {
        if (client is null) return;
        client.Retired = true; if (client.Users == 0) client.Client.Dispose(); client = null;
    }
    private static SegmentQuery Query(MediaIdentity media) => new(media,
        SponsorRules.Categories.Select(c => c.Id).ToImmutableArray(), includeMute: true, includeFull: true);

    internal async Task<ApplicationResult> FetchAsync(PlayerTicket ticket, bool refresh = false)
    {
        // Per invocation; the owner writes refusal status before OnOwner completes.
        var admissionStatus = ApplicationStatus.Success;
        var work = await OnOwner<ReadWork?>(() =>
        {
            // Classify before creating work, on the same owner lane as all admission checks.
            if (!Current(ticket, out var state)) { admissionStatus = ApplicationStatus.Stale; return null; }
            if (!settings.NetworkEnabled) { admissionStatus = ApplicationStatus.Disabled; return null; }
            if (requests.Count >= 16) { admissionStatus = ApplicationStatus.Busy; return null; }
            var epoch = AcquireClient(); var readEpoch = ++state.ReadEpoch;
            // No waiter cancellation: returned S7 task follows the shared physical read to completion.
            var task = epoch.Client.GetSegmentsAsync(Query(ticket.Stamp.Owner.Media), refresh);
            requests.Add(task);
            return new ReadWork(epoch, task, readEpoch);
        });
        if (work is null) return Result(admissionStatus, admissionStatus == ApplicationStatus.Stale ? "ReadAdmissionStale" : null);
        ReadResult<ImmutableArray<SponsorSegment>> result;
        try { result = await work.Task.ConfigureAwait(false); }
        catch { result = new(ReadStatus.TransportFailure); }
        return await OnOwner(async () =>
        {
            requests.Remove(work.Task); ReleaseClient(work.Epoch);
            if (Stopping || !settings.NetworkEnabled || !players.TryGetValue(ticket.Attachment, out var state)
                || state.Attachment.IsClosed || state.Attachment.Latest?.Owner != ticket.Stamp.Owner
                || state.ReadEpoch != work.ReadEpoch) return Result(ApplicationStatus.Stale);
            state.ReadStatus = result.Status;
            if (result.IsSuccess) { state.Raw = result.Value; state.Aligned = null; await PushAsync(state); }
            Publish(); return Result(result.IsSuccess ? ApplicationStatus.Success : ApplicationStatus.Unavailable, result.Status.ToString());
        });
    }
    private sealed record ReadWork(ClientEpoch Epoch, Task<ReadResult<ImmutableArray<SponsorSegment>>> Task, long ReadEpoch);

    internal Task<ApplicationResult> DetachAsync(Guid id)
    {
        if (admissions.TryGetValue(id, out var attachment)) attachment.StopAdmission();
        return OnOwner(async () =>
        {
            if (!players.TryGetValue(id, out var state)) return Result(ApplicationStatus.Closed);
            state.Attachment.StopAdmission(); ++state.ReadEpoch;
            // Native End must get the owned cleanup token, even after start admission has closed.
            if (state.Attachment.Latest is { } latest) TrackPreview(state, latest);
            if (PreviewUnresolved(state))
            {
                var ended = await EndPreviewCoreAsync(state, state.PreviewPlayback!.Token, SponsorPreviewEndMode.Restore, automatic: true);
                if (ended.Status != ApplicationStatus.Released) { Publish(); return ended; }
            }
            if (editor == id) ClearEditor();
            bool drained;
            try { drained = await state.Attachment.DetachAsync(++state.InputRevision, policyRevision); }
            catch { drained = false; }
            if (drained) { players.Remove(id); admissions.TryRemove(id, out _); }
            Publish();
            return Result(drained ? ApplicationStatus.Success : ApplicationStatus.NeedsReconciliation,
                drained ? null : "PlayerCleanupOwnershipRetained");
        });
    }

    internal async Task<ApplicationResult> CloseAsync()
    {
        Interlocked.Exchange(ref closeRequested, 1);
        foreach (var attachment in admissions.Values) attachment.StopAdmission();
        var ids = await OnOwner(() =>
        {
            closing = true; RetireClient(); // Detach owns conditional End, not implicit abandonment.
            foreach (var state in players.Values) state.Attachment.StopAdmission();
            Publish(); return players.Keys.ToArray();
        });
        ApplicationResult? incomplete = null;
        foreach (var id in ids)
        {
            var detached = await DetachAsync(id); // Outside APP serial: never await a job on our own queue.
            if (detached.Status != ApplicationStatus.Success) incomplete = detached;
        }
        // Completion owners must finish persistence too; loop is a drain, never an HTTP retry.
        while (true)
        {
            var pending = await OnOwner(() => requests.ToArray());
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
            await Task.Yield();
        }
        return await OnOwner(() =>
        {
            Publish();
            if (dirty || statisticsDirty || HasUnsavedJournal)
                return new ApplicationResult(ApplicationStatus.PersistenceFailure, "LocalChangesNotDurable", persistence)
                    { EditorReply = incomplete?.EditorReply };
            return incomplete ?? Result(players.Count == 0 ? ApplicationStatus.Success : ApplicationStatus.Pending,
                players.Count == 0 ? null : "PlayerCleanupOwnershipRetained");
        });
    }
}
