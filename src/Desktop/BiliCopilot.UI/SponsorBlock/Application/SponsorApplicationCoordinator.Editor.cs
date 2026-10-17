using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    internal Task<ApplicationResult> SaveDraftsAsync() => OnOwner(async () =>
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        var saved = await SaveAsync(settings); Publish();
        return new(saved == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure, Persistence: saved);
    });

    internal Task<ApplicationResult> EditAsync(PlayerTicket ticket, EditorCommand command) => OnOwner(async () =>
    {
        if (!Enum.IsDefined(command.Action)) return Result(ApplicationStatus.Invalid);
        if (command.Action is EditorAction.EndPreview or EditorAction.KeepCurrentPreview
            && players.TryGetValue(ticket.Attachment, out var cleanup))
        {
            // Old UI may end its exact current panel context. A stale ticket cannot select a newer session.
            var token = command.PreviewToken ?? (Current(ticket, out _) ? cleanup.PreviewPlayback?.Token : null);
            if (token is not null) return await EndPreviewCoreAsync(cleanup, token,
                command.Action == EditorAction.KeepCurrentPreview ? SponsorPreviewEndMode.KeepCurrent : SponsorPreviewEndMode.Restore);
            if (PreviewUnresolved(cleanup)) return Result(ApplicationStatus.Stale, "ExplicitPreviewCleanupTokenRequired");
            if (command.Action == EditorAction.KeepCurrentPreview) return Result(ApplicationStatus.Unavailable, "PreviewCleanupTokenRequired");
        }
        if (!Current(ticket, out var state) || ticket.DraftRevision != draftRevision) return Result(ApplicationStatus.Stale);
        if (command.Action is EditorAction.Split or EditorAction.Merge)
            return Result(ApplicationStatus.Unavailable, "Q4HasNoAtomicSplitMerge");
        if (editor is not null && editor != ticket.Attachment) return Result(ApplicationStatus.Busy, "AnotherPlayerOwnsEditor");
        var selected = command.Draft is null ? null : queue.Entries.FirstOrDefault(e => e.LocalDraftId == command.Draft.LocalDraftId
            && e.Draft.Revision == command.Draft.Revision && e.Draft.MediaKey == ticket.Stamp.Owner.Media.Combined);
        if (command.Draft is not null && selected is null) return Result(ApplicationStatus.Stale, "DraftRevisionChanged");
        if (command.Action is EditorAction.Edit or EditorAction.Delete or EditorAction.PreparePreview or EditorAction.StartPreviewPlayback && selected is null)
            return Result(ApplicationStatus.Invalid, "DraftSelectionRequired");
        if (command.Action is EditorAction.FrameForward or EditorAction.FrameBackward or EditorAction.StartPreviewPlayback)
            return await EditorTransportCoreAsync(state, ticket, command, selected);
        if (PreviewUnresolved(state)) return Result(ApplicationStatus.Busy, "PreviewCleanupTokenStillOwned");

        SponsorControllerResult? observed;
        try { observed = await state.Attachment.ObserveAsync(ticket.Stamp); }
        catch { return Result(ApplicationStatus.ControllerFailure); }
        if (observed?.Stamp is not { } stamp || observed.Actual is not { } actual
            || state.Attachment.Latest?.Stamp != stamp) return Result(ApplicationStatus.Stale);
        if (actual.Duration is not double duration || !double.IsFinite(duration) || duration <= 0
            || !double.IsFinite(actual.Position) || actual.Position < 0 || actual.Position > duration)
            return Result(ApplicationStatus.Unavailable, "ActualPositionDurationRequired");
        if (editor is null) queue.SwitchActiveMedia(null, 0);
        editor = ticket.Attachment; editorStamp = stamp;
        queue.SwitchActiveMedia(stamp.Owner.Media, stamp.Owner.Generation);
        state.Editing = true; state.Preview = null; state.PreparedDraft = null;
        var media = stamp.Owner.Media;
        QueueOutcome outcome;
        switch (command.Action)
        {
            case EditorAction.StartCapture:
                outcome = queue.StartCapture(media, stamp.Owner.Generation, actual.Position, duration, command.Category, command.ActionType); break;
            case EditorAction.FinishCapture:
                outcome = queue.FinishCapture(media, stamp.Owner.Generation, actual.Position); break;
            case EditorAction.CancelCapture: outcome = queue.CancelCapture(); break;
            case EditorAction.Point: outcome = queue.CreatePoint(media, actual.Position, duration); break;
            case EditorAction.FullLabel: outcome = queue.CreateFullLabel(media, duration, command.Category); break;
            case EditorAction.Edit: outcome = queue.Edit(selected!.LocalDraftId, command.Category, command.ActionType, command.Start, command.End); break;
            case EditorAction.Delete: outcome = queue.Delete(selected!.LocalDraftId); break;
            case EditorAction.Undo: outcome = queue.Undo(media); break;
            case EditorAction.PreparePreview:
                state.Preview = queue.Preview(selected!.LocalDraftId, media, stamp.Owner.Generation);
                state.PreparedDraft = state.Preview is null ? null : new(selected.LocalDraftId, selected.Draft.Revision);
                outcome = new(state.Preview is not null, state.Preview is null ? "PreviewNotAvailableForDraft" : null); break;
            case EditorAction.EndPreview: ClearEditor(); outcome = new(true); break;
            default: return Result(ApplicationStatus.Invalid);
        }
        if (!outcome.Success) { await PushAsync(state); Publish(); return new(ApplicationStatus.Invalid, outcome.Error, Queue: outcome); }
        // N3 consumes this explicit owner fact, retires temporary effects and suppresses automation.
        // Preparing preview does not fabricate a native seek or claim playback of the interval.
        await PushAsync(state);
        if (command.Action is EditorAction.PreparePreview or EditorAction.EndPreview)
        { Publish(); return new(ApplicationStatus.Success, Queue: outcome); }
        ++draftRevision; dirty = true;
        var saved = await SaveAsync(settings); Publish();
        return new(saved == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure,
            Persistence: saved, Queue: outcome);
    });

    private async Task<ApplicationResult> EditorTransportCoreAsync(PlayerState state, PlayerTicket ticket,
        EditorCommand command, DraftEntry? selected)
    {
        if (!state.Attachment.HasEditorPort) return RecordEditorReply(state,
            new(SponsorEditorOutcome.Unavailable, null, Reason: "EditorPlayerPortUnavailable"));
        var start = command.Action == EditorAction.StartPreviewPlayback;
        if (start && PreviewUnresolved(state)) return Result(ApplicationStatus.Busy, "PreviewCleanupTokenStillOwned");
        var prepared = start ? queue.Preview(selected!.LocalDraftId, ticket.Stamp.Owner.Media, ticket.Stamp.Owner.Generation) : null;
        if (start && (prepared is null || state.Preview != prepared || !state.Editing || editor != ticket.Attachment
            || state.PreparedDraft != command.Draft))
            return Result(ApplicationStatus.Stale, "PrepareExactCurrentQ4PreviewFirst");
        SponsorControllerResult? observed;
        try { observed = await state.Attachment.ObserveAsync(ticket.Stamp); }
        catch { return RecordEditorReply(state, new(SponsorEditorOutcome.NeedsReconciliation, null, Reason: "EditorObservationFailed")); }
        if (observed?.Stamp is not { } observedStamp || state.Attachment.Latest?.Stamp != observedStamp)
            return Result(ApplicationStatus.Stale);
        if (observed.Actual is not { } actual || (start
            ? !actual.CanSeekExactly || actual.State is not (SponsorPlaybackState.Playing or SponsorPlaybackState.Paused)
            : !actual.CanStepFrames || actual.State != SponsorPlaybackState.Paused))
            return RecordEditorReply(state, new(SponsorEditorOutcome.Unavailable, observed, Reason: "ActualEditorCapabilityUnavailable"));
        AdoptEditorSnapshot(state, observed);
        if (editor is null) queue.SwitchActiveMedia(null, 0);
        editor = ticket.Attachment; editorStamp = observedStamp;
        queue.SwitchActiveMedia(observedStamp.Owner.Media, observedStamp.Owner.Generation);
        state.Editing = true;
        // Keep a live preview's input unchanged for frame takeover. Native typed Seek interrupts it;
        // clearing preparation here would instead initiate an unwanted restore before the frame.
        if (!await PushAsync(state)) { Publish(); return Result(state.DeliveryStatus, "EditorIsolationNotAccepted"); }
        SponsorControllerResult? fresh;
        try { fresh = await state.Attachment.ReadSnapshotAsync(); }
        catch { return RecordEditorReply(state, new(SponsorEditorOutcome.NeedsReconciliation, null, Reason: "EditorSnapshotFailed")); }
        if (Stopping || state.Attachment.IsClosed || editor != ticket.Attachment || !state.Editing
            || ticket.DraftRevision != draftRevision || fresh?.Stamp is not { } stamp
            || stamp.Owner != observedStamp.Owner || stamp.PolicyRevision != policyRevision
            || state.Attachment.Latest?.Stamp != stamp)
        { Publish(); return Result(ApplicationStatus.Stale, "EditorContextChangedAfterPush"); }
        if (start && (selected is null || !queue.Entries.Any(e => e.LocalDraftId == selected.LocalDraftId && e.Draft.Revision == selected.Draft.Revision)
            || queue.Preview(selected.LocalDraftId, stamp.Owner.Media, stamp.Owner.Generation) != prepared || state.Preview != prepared))
        { Publish(); return Result(ApplicationStatus.Stale, "DraftPreviewChangedAfterPush"); }
        SponsorEditorReply reply;
        try
        {
            // Fresh stamp is acquired AFTER preparation/isolation Push, never from the original UI ticket.
            state.EditorWork = start ? state.Attachment.StartPreviewAsync(stamp, prepared!)
                : state.Attachment.FrameAsync(stamp, command.Action == EditorAction.FrameForward ? FrameDirection.Forward : FrameDirection.Backward);
            Publish();
            reply = await state.EditorWork;
        }
        catch { reply = new(SponsorEditorOutcome.NeedsReconciliation, state.Attachment.Latest, Reason: "EditorPlayerTaskFailed"); }
        var result = RecordEditorReply(state, reply, publish: false);
        var latest = state.Attachment.Latest;
        var ownFrame = !start && reply.Outcome == SponsorEditorOutcome.Acknowledged
            && reply.Command is { Kind: NativeOperationKind.StepFrame, Phase: SponsorOperationPhase.SucceededUnobserved };
        var ownPreview = start && reply.Snapshot?.PreviewPlayback is { } preview
            && preview.Token.Owner == stamp.Owner && preview.Interval == prepared
            && preview.Phase is not (SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted);
        if ((ownFrame || ownPreview) && reply.Snapshot?.Stamp is { } returned && latest?.Stamp is { } current
            && SameEditorContext(returned, current) && current.Owner == stamp.Owner && current.PolicyRevision == policyRevision)
        {
            // Do not switch Q4 active media: that would cancel the user's in-progress capture.
            editorStamp = current;
            AdoptEditorSnapshot(state, latest);
        }
        else if (latest?.Stamp is not { } currentStamp || editorStamp is null || !SameEditorContext(editorStamp, currentStamp))
        {
            ClearEditor(); await PushAsync(state);
        }
        Publish(); return result; // Ack is native task success only; no draft revision/Applied/stat mutation.
    }

    private static void AdoptEditorSnapshot(PlayerState state, SponsorControllerResult snapshot)
    {
        // Foreign owners must pass Receive's owner reset (raw rows/channel/read epoch) first.
        if (snapshot.Owner == state.Result?.Owner && snapshot.Sequence > (state.Result?.Sequence ?? -1)) state.Result = snapshot;
        TrackPreview(state, snapshot);
    }

    private ApplicationResult RecordEditorReply(PlayerState state, SponsorEditorReply reply, bool publish = true)
    {
        if (reply.Snapshot is { } snapshot) AdoptEditorSnapshot(state, snapshot);
        var safe = reply with
        {
            Snapshot = UiController(reply.Snapshot), Reason = reply.Reason is null ? null : "Editor" + reply.Outcome,
            Command = reply.Command is null ? null : reply.Command with { Failure = reply.Command.Failure is null ? null : "EditorCommandFailure" },
        };
        state.LastEditorReply = safe;
        var status = reply.Outcome switch
        {
            SponsorEditorOutcome.Accepted => ApplicationStatus.Pending,
            SponsorEditorOutcome.Acknowledged => ApplicationStatus.Acknowledged,
            SponsorEditorOutcome.Released => ApplicationStatus.Released,
            SponsorEditorOutcome.Stale => ApplicationStatus.Stale,
            SponsorEditorOutcome.Busy => ApplicationStatus.Busy,
            SponsorEditorOutcome.NeedsReconciliation => ApplicationStatus.NeedsReconciliation,
            _ => ApplicationStatus.Unavailable,
        };
        if (publish) Publish();
        return new(status, safe.Reason) { EditorReply = safe };
    }

    internal Task<ApplicationResult> EndPreviewPlaybackAsync(Guid attachment, SponsorPreviewToken token,
        SponsorPreviewEndMode mode) => OnOwner(async () =>
    {
        if (!players.TryGetValue(attachment, out var state)) return Result(ApplicationStatus.Closed);
        return await EndPreviewCoreAsync(state, token, mode);
    });

    private async Task<ApplicationResult> EndPreviewCoreAsync(PlayerState state, SponsorPreviewToken token,
        SponsorPreviewEndMode mode, bool automatic = false)
    {
        if (!Enum.IsDefined(mode)) return Result(ApplicationStatus.Invalid);
        if (state.Attachment.Latest is { } latest) TrackPreview(state, latest);
        if (state.PreviewPlayback is not { } preview || preview.Token != token) return Result(ApplicationStatus.Stale, "PreviewCleanupTokenChanged");
        if (state.ReleasedPreviewToken == token)
            return state.CleanupResult ?? Result(ApplicationStatus.Released, "PreviewOwnershipAlreadyReleased");
        if (automatic && state.EndRequestedToken == token && state.CleanupResult is { } pending
            && preview.Phase is not (SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted))
            return pending; // A repeated detach/close is not permission to retry unresolved native cleanup.
        state.EndRequestedToken = token;
        SponsorEditorReply reply;
        try
        {
            state.CleanupWork = state.Attachment.EndPreviewAsync(token, mode);
            Publish();
            reply = await state.CleanupWork; // Actual task retained, even when failure/pending keeps the attachment alive.
        }
        catch { reply = new(SponsorEditorOutcome.NeedsReconciliation, state.Attachment.Latest, Reason: "PreviewCleanupTaskFailed"); }
        if (reply.Outcome == SponsorEditorOutcome.Released)
        {
            state.ReleasedPreviewToken = token;
            if (editor == state.Attachment.Id) { ClearEditor(); await PushAsync(state); }
        }
        var result = RecordEditorReply(state, reply, publish: false);
        state.CleanupResult = result;
        Publish();
        return result;
    }

    internal Task<ApplicationResult> RequestPlaybackAsync(PlayerTicket ticket, PlaybackUiCommand command) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
        if (!Enum.IsDefined(command.Action)) return Result(ApplicationStatus.Invalid);
        if (command.Target is { } key && (key.Media != ticket.Stamp.Owner.Media
            || !state.Raw.Any(s => s.Uuid == key.Uuid && s.Source == key.Source))) return Result(ApplicationStatus.Stale);
        var applied = state.Attachment.Latest?.Applied;
        if (command.Action == PlaybackUiAction.ReleaseMute)
        {
            var lease = state.Attachment.Latest?.MuteLease;
            if (lease is null || lease.Owner != ticket.Stamp.Owner || lease.UserEpoch != ticket.Stamp.MuteUserEpoch)
                return Result(ApplicationStatus.Stale, "CurrentOwnedMuteLeaseRequired");
            if (state.Attachment.HasEditorPort)
            {
                SponsorEditorReply reply;
                try
                {
                    state.EditorWork = state.Attachment.ReleaseMuteAsync(ticket.Stamp, lease.Token);
                    Publish(); reply = await state.EditorWork;
                }
                catch { reply = new(SponsorEditorOutcome.NeedsReconciliation, state.Attachment.Latest, Reason: "MuteReleaseTaskFailed"); }
                return RecordEditorReply(state, reply);
            }
            // Legacy Undo is legitimate only for its exact still-current confirmed HoldMute receipt.
            if (!LegacyMuteReleaseAvailable(state.Attachment.Latest))
                return RecordEditorReply(state, new(SponsorEditorOutcome.Unavailable, state.Attachment.Latest, Reason: "LeaseReleasePortRequired"));
        }
        var kind = command.Action switch
        {
            PlaybackUiAction.Skip => SponsorRequestKind.Skip,
            PlaybackUiAction.SkipNow => SponsorRequestKind.SkipNow, // UI must label exactly "Skip now".
            PlaybackUiAction.ReSkip => SponsorRequestKind.ReSkip,
            PlaybackUiAction.JumpToPoint => SponsorRequestKind.JumpToPoint,
            _ => SponsorRequestKind.Undo,
        };
        var request = new PlaybackRequest(Guid.NewGuid().ToString("N"), ticket.Stamp, kind, command.Target,
            kind == SponsorRequestKind.Undo ? applied : null);
        try
        {
            var admitted = await state.Attachment.RequestAsync(request);
            return Result(!admitted ? ApplicationStatus.Stale : command.Action == PlaybackUiAction.ReleaseMute
                ? ApplicationStatus.Pending : ApplicationStatus.Success,
                command.Action == PlaybackUiAction.ReleaseMute ? "LegacyReleaseIntentOnly" : null);
        }
        catch { return Result(ApplicationStatus.ControllerFailure); }
        // Success means intent admitted, not applied. Only subsequent N3 receipts establish effects.
    });

    internal Task<ApplicationResult> ReconcileFieldAsync(PlayerTicket ticket, string leaseToken, PlaybackLeaseField field) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state) || !Enum.IsDefined(field)) return Result(ApplicationStatus.Stale);
        try { return Result(await state.Attachment.ReconcileFieldAsync(ticket.Stamp, new(ticket.Stamp.Owner, leaseToken, field))
            ? ApplicationStatus.Success : ApplicationStatus.Unavailable, "ExplicitReadbackOnly"); }
        catch { return Result(ApplicationStatus.ControllerFailure); }
    });
    internal Task<ApplicationResult> ReconcileSeekAsync(PlayerTicket ticket, string operationToken) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
        try { return Result(await state.Attachment.ReconcileSeekAsync(ticket.Stamp, new(ticket.Stamp.Owner, operationToken))
            ? ApplicationStatus.Success : ApplicationStatus.Unavailable, "ReconciledIsNotConfirmed"); }
        catch { return Result(ApplicationStatus.ControllerFailure); }
    });

    internal Task<ApplicationResult> RetryDraftAsync(PlayerTicket ticket, DraftSelection selected, bool acknowledgeDuplicateRisk) => OnOwner(async () =>
    {
        if (!Current(ticket, out _) || ticket.DraftRevision != draftRevision
            || !queue.Entries.Any(e => e.LocalDraftId == selected.LocalDraftId && e.Draft.Revision == selected.Revision))
            return Result(ApplicationStatus.Stale);
        var media = ticket.Stamp.Owner.Media;
        var outcome = queue.AdmitRetry(selected.LocalDraftId, media,
            Models.ManualWriteIntent.Capture(media, Models.ManualOperation.Submit), acknowledgeDuplicateRisk);
        if (!outcome.Success) return new(ApplicationStatus.Invalid, outcome.Error, Queue: outcome);
        ++draftRevision; dirty = true; var saved = await SaveAsync(settings); Publish();
        return new(saved == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure, Persistence: saved, Queue: outcome);
    });

    internal Task<PersistenceResult<byte[]>> ExportBackupAsync() => OnOwner(async () =>
    {
        if (Stopping || requests.Count != 0 || editor is not null) return new PersistenceResult<byte[]>(PersistenceStatus.Busy);
        if (dirty && await SaveAsync(settings) != PersistenceStatus.Success)
        { Publish(); return new PersistenceResult<byte[]>(persistence!.Value); }
        if (HasUnsavedJournal) { Publish(); return new PersistenceResult<byte[]>(PersistenceStatus.UnresolvedMutations); }
        if (statisticsDirty && await PersistStatisticsCoreAsync() != PersistenceStatus.Success)
        { Publish(); return new PersistenceResult<byte[]>(persistence!.Value); }
        var backup = await store.ExportBackupAsync(); Publish(); return backup;
    });

    internal Task<ApplicationResult> ReplaceBackupAsync(ReadOnlyMemory<byte> backup, BackupReplaceIntent intent)
    {
        // Bound/copy UI-owned bytes before asynchronous admission.
        if (backup.Length > 4 * 1024 * 1024) return Task.FromResult(new ApplicationResult(ApplicationStatus.Invalid, "BackupTooLarge"));
        var copy = backup.ToArray();
        return OnOwner(async () =>
        {
            if (Stopping || players.Count != 0 || requests.Count != 0 || editor is not null || dirty || statisticsDirty || HasUnsavedJournal || version is null)
                return Result(ApplicationStatus.Busy, "DetachDrainAndSaveBeforeReplace");
            if (queue.Entries.Any(e => e.State is DraftState.Unknown or DraftState.SubmissionInFlight or DraftState.AcceptedUnconfirmed))
                return Result(ApplicationStatus.Unavailable, "UnresolvedDrafts");
            var replaced = await store.ReplaceBackupAsync(copy, version, intent); persistence = replaced.Status;
            if (replaced.Success)
            {
                queue = replaced.Value!.Drafts.Restore(); settings = replaced.Value.Settings; RestoreAuxiliary(replaced.Value);
                ++draftRevision; ++policyRevision; RetireClient();
            }
            Publish(); return new(replaced.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure, Persistence: replaced.Status);
        });
    }

    internal Task<IdentityView> LoadIdentityViewAsync() => OnOwner(async () =>
    {
        if (Stopping) return new IdentityView(PersistenceStatus.Busy);
        var identity = await store.LoadIdentityAsync(); return new(identity.Status, identity.Value?.PublicId);
    });
    internal Task<IdentityView> CreateIdentityAsync(SponsorIdentityCreateIntent intent) => OnOwner(async () =>
    {
        if (Stopping) return new IdentityView(PersistenceStatus.Busy);
        var identity = await store.CreateIdentityAsync(intent); return new(identity.Status, identity.Value?.PublicId);
    });
}
