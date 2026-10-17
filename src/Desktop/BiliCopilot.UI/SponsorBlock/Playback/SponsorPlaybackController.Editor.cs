using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal sealed partial class SponsorPlaybackController
{
    // Transport ownership is independent of the frozen ticket's mute/rate epochs.
    private long _positionIntentId;
    private long _pauseIntentId;
    private long _previewId;
    private PreviewSession? _preview;
    private SponsorPreviewStatus? _terminalPreviewStatus;
    private SponsorEditorReply? _terminalPreviewReply;

    internal sealed record EditorFrameReservation(UserIntentTicket Ticket, FrameDirection Direction,
        long PositionIntentId, long PauseIntentId, long InputRevision);
    internal sealed record EditorPreviewReservation(UserIntentTicket Ticket, SponsorPreviewToken Token);
    internal sealed record EditorMuteReservation(UserIntentTicket Ticket, PlaybackOwner Owner,
        string LeaseToken, long LeaseEpoch);

    private sealed class EditorAdmissionException : InvalidOperationException
    {
        private const string DefaultMessage = "Editor operation is unavailable.";

        public EditorAdmissionException() : base(DefaultMessage) { }
        public EditorAdmissionException(string? message) : base(message ?? DefaultMessage) { }
        public EditorAdmissionException(string? message, Exception? innerException)
            : base(message ?? DefaultMessage, innerException) { }
        public EditorAdmissionException(SponsorEditorOutcome outcome, string message) : base(message)
        {
            Outcome = outcome;
        }

        internal SponsorEditorOutcome Outcome { get; } = SponsorEditorOutcome.Unavailable;
    }

    private enum PreviewStage
    {
        Reserved, NeedEntryPause, AwaitEntryPause, NeedEntrySeek, AwaitEntry,
        NeedEntryResume, AwaitPlaying, Running, NeedEndPause, AwaitEnd, Inspection,
        NeedReturnPause, AwaitReturnPause, NeedReturnSeek, AwaitReturn,
        NeedReturnResume, AwaitReturnedState, Recovery
    }

    private sealed class PreviewSession(EditorPreviewReservation reservation, PreviewInterval interval,
        long policyRevision, bool editing)
    {
        internal EditorPreviewReservation Reservation { get; } = reservation;
        internal SponsorPreviewStatus Status { get; set; } = new(reservation.Token, interval,
            SponsorPreviewPhase.Starting, null, null);
        internal long PolicyRevision { get; } = policyRevision;
        internal bool Editing { get; } = editing;
        internal PreviewStage Stage { get; set; } = PreviewStage.Reserved;
        internal bool RestoreRequested { get; set; }
        internal bool RollbackStarted { get; set; }
        internal bool Revoked { get; set; }
        internal SponsorPreviewPhase TerminalPhase { get; set; } = SponsorPreviewPhase.Interrupted;
        internal double? ProbeDue { get; set; }
        internal SponsorOperation? Command { get; set; }
    }

    private SponsorEditorReply EditorReply(SponsorEditorOutcome outcome, SponsorOperation? command = null, string? reason = null)
        => new(outcome, Snapshot, command, reason);

    private bool EditorIsolated => _input is { } input && (input.IgnorePlaybackForEditing || input.Preview is not null);

    private void RequireEditorStamp(PlaybackStamp expected, bool isolated)
    {
        VerifyAccess();
        if (!CanUseBinding || !_sponsorOpen || _owner is null)
            throw new EditorAdmissionException(SponsorEditorOutcome.Unavailable, "Editor native binding is unavailable.");
        if (expected != CurrentStamp)
            throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Editor stamp is no longer current.");
        if (isolated && !EditorIsolated)
            throw new EditorAdmissionException(SponsorEditorOutcome.Unavailable, "Current editor isolation is required.");
    }

    private static bool ValidPreview(PreviewInterval preview, PlaybackOwner owner, NativePlaybackSnapshot? actual)
        => preview.MediaKey == owner.Media.Combined && preview.NativeMediaGeneration == owner.Generation
            && !string.IsNullOrWhiteSpace(preview.Revision) && double.IsFinite(preview.Start) && preview.Start >= 0
            && double.IsFinite(preview.End) && preview.Start < preview.End
            && actual?.Duration is { } duration && double.IsFinite(duration) && duration > 0 && preview.End <= duration;

    // These reservation methods run on the player dispatcher BEFORE the VM's physical drain/first await.
    internal SponsorEditorReply? ReserveEditorFrame(PlaybackStamp expected, FrameDirection direction,
        out EditorFrameReservation? reservation)
    {
        reservation = null;
        try
        {
            RequireEditorStamp(expected, isolated: true);
            if (!Enum.IsDefined(direction)) return EditorReply(SponsorEditorOutcome.Unavailable, reason: "Unknown frame direction.");
            var revision = _input!.Revision;
            var ticket = NoteUserIntent(PlayerIntentKind.Seek);
            reservation = new(ticket, direction, _positionIntentId, _pauseIntentId, revision);
            return null;
        }
        catch (EditorAdmissionException ex) { return EditorReply(ex.Outcome, reason: ex.Message); }
    }

    internal SponsorEditorReply? ReserveEditorPreview(PlaybackStamp expected, PreviewInterval interval,
        out EditorPreviewReservation? reservation)
    {
        reservation = null;
        try
        {
            RequireEditorStamp(expected, isolated: true);
            if (_preview is not null) return EditorReply(SponsorEditorOutcome.Busy, reason: "A preview still owns cleanup.");
            var input = _input!;
            if (interval != input.Preview || !ValidPreview(interval, expected.Owner, _actual))
                return EditorReply(SponsorEditorOutcome.Stale, reason: "The exact prepared preview and current duration are required.");
            if (_actual?.CanSeekExactly != true)
                return EditorReply(SponsorEditorOutcome.Unavailable, reason: "Exact preview seek capability is unavailable.");
            var ticket = NoteUserIntent(PlayerIntentKind.Seek);
            _pauseIntentId = ticket.IntentId; // One real seek ticket reserves BOTH preview transport fields.
            reservation = new(ticket, new(expected.Owner, ++_previewId));
            _preview = new(reservation, interval, input.Policy.Revision, input.IgnorePlaybackForEditing);
            CheckPreviewContext(_input!);
            _plan = null;
            _rateSample = null;
            Publish();
            return null;
        }
        catch (EditorAdmissionException ex) { return EditorReply(ex.Outcome, reason: ex.Message); }
    }

    internal SponsorEditorReply? ReserveEditorMuteRelease(PlaybackStamp expected, string leaseToken,
        out EditorMuteReservation? reservation)
    {
        reservation = null;
        try
        {
            RequireEditorStamp(expected, isolated: false);
            if (_mute is not { } lease || lease.Token != leaseToken || lease.Owner != expected.Owner
                || lease.UserEpoch != expected.MuteUserEpoch)
                return EditorReply(SponsorEditorOutcome.Stale, reason: "The exact current mute lease is required.");
            if (lease.Phase == PlaybackLeasePhase.Pending || lease.Restoration?.Phase == RestorationPhase.Pending)
                return EditorReply(SponsorEditorOutcome.Busy, reason: "Mute work is still pending.");
            try { RequireRestorable(lease.Phase, lease.Restoration); }
            catch (InvalidOperationException ex) { return EditorReply(SponsorEditorOutcome.NeedsReconciliation, reason: ex.Message); }
            var ticket = NoteUserIntent(PlayerIntentKind.Mute);
            reservation = new(ticket, lease.Owner, lease.Token, lease.UserEpoch);
            return null;
        }
        catch (EditorAdmissionException ex) { return EditorReply(ex.Outcome, reason: ex.Message); }
    }

    internal SponsorEditorReply FailEditorPreparation(UserIntentTicket ticket, SponsorPreviewToken? token, string reason)
    {
        VerifyAccess();
        if (token is not null && _preview is { } preview && preview.Status.Token == token)
            FailPreview(preview, reason);
        _intents.Remove(ticket.IntentId);
        RequestObservation();
        Publish();
        return EditorReply(SponsorEditorOutcome.NeedsReconciliation, reason: reason);
    }

    private void CheckEditorFrame(EditorFrameReservation reservation)
    {
        try { CheckTicket(reservation.Ticket); }
        catch (InvalidOperationException ex) { throw new EditorAdmissionException(SponsorEditorOutcome.Stale, ex.Message); }
        if (_positionIntentId != reservation.PositionIntentId || _pauseIntentId != reservation.PauseIntentId
            || !_sponsorOpen || !EditorIsolated || _input?.Revision != reservation.InputRevision)
            throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Editor frame lost transport or prepared context ownership.");
    }

    internal Task<SponsorEditorReply> ExecuteEditorFrameAsync(EditorFrameReservation reservation) => Enqueue(async () =>
    {
        var outcome = SponsorEditorOutcome.Acknowledged;
        SponsorOperation? command = null;
        string? reason = null;
        try
        {
            CheckEditorFrame(reservation);
            await RestoreBothAsync();
            CheckEditorFrame(reservation);
            var actual = await ReadAsync(NativeAdmissionPurpose.User);
            CheckEditorFrame(reservation);
            if (!actual.CanStepFrames || actual.State != SponsorPlaybackState.Paused)
                throw new EditorAdmissionException(SponsorEditorOutcome.Unavailable, "Actual paused-video frame capability is unavailable.");
            var beforeCommand = _operationId;
            try
            {
                await NativeAsync(NativeOperationKind.StepFrame, NativeAdmissionPurpose.User,
                    permit => _backend!.StepFrameAsync(permit, reservation.Direction));
            }
            finally
            {
                if (_operation is { Kind: NativeOperationKind.StepFrame } operation && operation.Id > beforeCommand)
                    command = operation; // Capture MUTATION before the subsequent read replaces Operation.
            }
            if (reservation.Ticket.NativeGeneration == _generation)
            {
                // The actual frame task succeeded: retire the old visit exactly once even if
                // subsequent readback fails or a newer transport intent makes the reply stale.
                AdvanceVisit();
                _visitRebasePending = true; // Not a displayed-frame/position receipt.
            }
            await ReadAsync(NativeAdmissionPurpose.Cleanup);
            CheckEditorFrame(reservation);
        }
        catch (EditorAdmissionException ex) { outcome = ex.Outcome; reason = ex.Message; }
        catch (Exception ex) { outcome = SponsorEditorOutcome.NeedsReconciliation; reason = ex.Message; _failure = reason; }
        finally { _intents.Remove(reservation.Ticket.IntentId); }
        RequestObservation();
        Publish();
        return EditorReply(outcome, command, reason);
    });

    private PlaybackLeaseSnapshot<bool>? CheckEditorMute(EditorMuteReservation reservation)
    {
        try { CheckTicket(reservation.Ticket); }
        catch (InvalidOperationException ex) { throw new EditorAdmissionException(SponsorEditorOutcome.Stale, ex.Message); }
        if (_owner != reservation.Owner || _muteEpoch != reservation.Ticket.MuteUserEpoch)
            throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Mute release lost its owner or user epoch.");
        if (_mute is not { } lease || lease.Token != reservation.LeaseToken) return null;
        if (lease.Owner != reservation.Owner || lease.UserEpoch != reservation.LeaseEpoch)
            throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Mute lease identity changed.");
        return lease;
    }

    internal Task<SponsorEditorReply> ExecuteEditorMuteReleaseAsync(EditorMuteReservation reservation) => Enqueue(async () =>
    {
        var outcome = SponsorEditorOutcome.Released;
        SponsorOperation? command = null;
        string? reason = null;
        PlaybackLeaseRestoration? restoration = null;
        try
        {
            if (CheckEditorMute(reservation) is { } lease)
            {
                RequireRestorable(lease.Phase, lease.Restoration);
                _mute = lease with { Retirement = lease.Retirement ?? new(RetirementReason.UserIntent, _clock()) };
                var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
                lease = CheckEditorMute(reservation) ?? throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Mute lease changed during release read.");
                if (actual.Muted is null) throw new InvalidOperationException("Mute release ownership read is unknown.");
                if (_muteWrittenEpoch <= reservation.LeaseEpoch && actual.Muted == lease.Target && actual.Muted != lease.Baseline)
                {
                    restoration = new(RestorationPhase.Pending, "editor-mute-release-" + (_operationId + 1));
                    _mute = lease with { Restoration = restoration };
                    var beforeCommand = _operationId;
                    try
                    {
                        await NativeAsync(NativeOperationKind.WriteMute, NativeAdmissionPurpose.Cleanup,
                            permit => _backend!.WriteMuteAsync(permit, lease.Baseline));
                    }
                    finally
                    {
                        if (_operation is { Kind: NativeOperationKind.WriteMute } operation && operation.Id > beforeCommand)
                            command = operation;
                    }
                    _mute = _mute! with { Restoration = restoration with { Phase = RestorationPhase.SucceededUnobserved } };
                    CheckEditorMute(reservation); // A newer intent can stop unstarted follow-up, not cancel the issued task.
                    actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
                    CheckEditorMute(reservation);
                    if (actual.Muted != lease.Baseline) throw new InvalidOperationException("Mute release baseline is not observed.");
                }
                // Already baseline (including true), external change or a later written epoch: no setter.
                _mute = null;
                ClearManualMute();
            }
        }
        catch (EditorAdmissionException ex) { outcome = ex.Outcome; reason = ex.Message; }
        catch (Exception ex)
        {
            outcome = SponsorEditorOutcome.NeedsReconciliation;
            reason = _failure = ex.Message;
            if (_mute is { } lease && lease.Owner == reservation.Owner && lease.Token == reservation.LeaseToken)
                _mute = lease with
                {
                    Retirement = lease.Retirement ?? new(RetirementReason.UserIntent, _clock()),
                    Restoration = new(restoration is null || ex is OperationCanceledException ? RestorationPhase.Unknown : RestorationPhase.Failed,
                        restoration?.OperationId ?? "editor-mute-read-" + _operationId),
                };
        }
        finally { _intents.Remove(reservation.Ticket.IntentId); }
        RequestObservation();
        Publish();
        return EditorReply(outcome, command, reason);
    });

    private void RecordTransportIntent(UserIntentTicket ticket)
    {
        if (ticket.Kind is PlayerIntentKind.Seek or PlayerIntentKind.OpaqueControl) _positionIntentId = ticket.IntentId;
        if (ticket.Kind is PlayerIntentKind.Pause or PlayerIntentKind.OpaqueControl) _pauseIntentId = ticket.IntentId;
        if (ticket.Kind is PlayerIntentKind.Seek or PlayerIntentKind.Pause or PlayerIntentKind.OpaqueControl)
            RevokePreview("A newer user transport intent interrupted preview.");
    }

    private bool OwnsPreview(PreviewSession session) => ReferenceEquals(_preview, session) && !session.Revoked
        && CanUseBinding && session.Status.Token.Owner == _owner
        && _positionIntentId == session.Reservation.Ticket.IntentId && _pauseIntentId == session.Reservation.Ticket.IntentId;

    private bool WakeOwnerCurrent(SponsorPreviewToken? token) => CanUseBinding && (token is null
        ? _sponsorOpen && _preview is null
        : _preview is { } preview && preview.Status.Token == token && OwnsPreview(preview));

    private void RevokePreview(string reason)
    {
        VerifyAccess();
        if (_preview is not { } preview || preview.Revoked) return;
        preview.Revoked = true; // Synchronous: no unstarted pause/seek/resume can pass its guard.
        preview.TerminalPhase = SponsorPreviewPhase.Interrupted;
        preview.Status = preview.Status with { Phase = SponsorPreviewPhase.Interrupted, Reason = reason };
        CancelWake();
    }

    private void CheckPreviewContext(SponsorCoordinatorInput input)
    {
        if (_preview is { Revoked: false } preview && (input.Policy.Revision != preview.PolicyRevision
            || input.Preview != preview.Status.Interval || input.IgnorePlaybackForEditing != preview.Editing))
            RequestPreviewRestore(preview, "Policy or prepared editor context changed.");
    }

    private void RequestPreviewRestore(PreviewSession session, string reason)
    {
        if (!ReferenceEquals(_preview, session) || session.Revoked || session.RollbackStarted) return;
        session.RestoreRequested = true;
        session.Status = session.Status with { Phase = SponsorPreviewPhase.Restoring, Reason = reason };
        CancelWake();
    }

    private void SetPreviewStage(PreviewSession session, PreviewStage stage, SponsorPreviewPhase phase)
    {
        session.Stage = stage; // Claim the next operation/wait BEFORE any backend factory.
        session.ProbeDue = null;
        session.Status = session.Status with { Phase = phase, Reason = null };
        CancelWake();
    }

    private void CheckPreviewWrite(PreviewSession session, PreviewStage claimedStage)
    {
        if (!OwnsPreview(session) || session.Stage != claimedStage
            || session.RestoreRequested && !session.RollbackStarted)
            throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Preview transport write lost its reservation.");
    }

    private async Task<NativePlaybackSnapshot> PreviewCommandAsync(PreviewSession session, PreviewStage claimedStage,
        NativeOperationKind kind, double value)
    {
        CheckPreviewWrite(session, claimedStage);
        var beforeCommand = _operationId;
        try
        {
            await NativeAsync(kind, NativeAdmissionPurpose.User, permit => kind == NativeOperationKind.SeekExact
                ? _backend!.SeekExactAsync(permit, value) : _backend!.SetPausedAsync(permit, value != 0));
        }
        finally
        {
            if (_operation is { } operation && operation.Id > beforeCommand && operation.Kind == kind)
                session.Command = operation;
        }
        // The actual task has drained. Recheck before any ordinary follow-up read/transport dispatch.
        if (!OwnsPreview(session)) throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Preview was interrupted during native work.");
        return await ReadAsync(NativeAdmissionPurpose.User);
    }

    private void WaitForPreviewObservation(PreviewSession session, string reason)
    {
        session.ProbeDue ??= _clock() + 2;
        if (_clock() >= session.ProbeDue.Value)
        {
            FailPreview(session, reason + " Diagnostic readback did not establish the required state.");
            return;
        }
        // One deadline per waiting stage, never extended by ordinary progress/readback events.
        ArmWake(new MonotonicExecutionWake(session.ProbeDue.Value, session.ProbeDue.Value - _clock()), session.Status.Token);
    }

    private void FailPreview(PreviewSession session, string reason)
    {
        if (!ReferenceEquals(_preview, session)) return;
        session.Stage = PreviewStage.Recovery;
        session.ProbeDue = null;
        session.Status = session.Status with { Phase = SponsorPreviewPhase.NeedsReconciliation, Reason = reason };
        _failure = reason;
        CancelWake();
    }

    private void FinishPreview(PreviewSession session, SponsorPreviewPhase phase, string reason)
    {
        if (!ReferenceEquals(_preview, session)) return;
        _preview = null;
        _intents.Remove(session.Reservation.Ticket.IntentId);
        _terminalPreviewStatus = session.Status with { Phase = phase, Reason = reason };
        CancelWake();
        RequestObservation(); // Never fall through into the preceding ordinary plan.
        _terminalPreviewReply = EditorReply(SponsorEditorOutcome.Released, session.Command, reason);
        Publish(); // A reentrant new reservation cannot replace the already captured terminal reply.
    }

    private async Task ReleaseRevokedPreviewAsync(PreviewSession session, NativePlaybackSnapshot? actual = null)
    {
        // Read-only relinquishment also works on the retiring issuer. No cleanup seek/autoplay.
        actual ??= await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (actual.NativeGeneration != session.Status.Token.Owner.Generation)
            throw new InvalidOperationException("Preview relinquishment read belongs to another binding.");
        FinishPreview(session, session.TerminalPhase, session.Status.Reason ?? "Preview transport ownership relinquished.");
    }

    private SponsorEditorReply PreviewReply(PreviewSession session)
    {
        if (_terminalPreviewStatus?.Token == session.Status.Token && _terminalPreviewReply is not null)
            return _terminalPreviewReply;
        return EditorReply(session.Status.Phase == SponsorPreviewPhase.NeedsReconciliation
            ? SponsorEditorOutcome.NeedsReconciliation : SponsorEditorOutcome.Accepted, session.Command, session.Status.Reason);
    }

    internal Task<SponsorEditorReply> ExecuteEditorPreviewAsync(EditorPreviewReservation reservation) => Enqueue(async () =>
    {
        if (_preview is not { } session || session.Reservation != reservation)
            return _terminalPreviewStatus?.Token == reservation.Token && _terminalPreviewReply is not null
                ? _terminalPreviewReply : EditorReply(SponsorEditorOutcome.Stale, reason: "Preview reservation is no longer active.");
        try
        {
            if (session.Revoked) await ReleaseRevokedPreviewAsync(session);
            else
            {
                CheckTicket(reservation.Ticket);
                if (!OwnsPreview(session) || session.Stage != PreviewStage.Reserved)
                    throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Preview start reservation is no longer current.");
                if (session.RestoreRequested)
                    throw new InvalidOperationException("Prepared context ended before preview captured a baseline; KeepCurrent is required.");
                await RestoreBothAsync();
                if (!OwnsPreview(session)) throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Preview lost ownership during field drain.");
                if (session.RestoreRequested) throw new InvalidOperationException("Prepared context ended before baseline capture; KeepCurrent is required.");
                var actual = await ReadAsync(NativeAdmissionPurpose.User);
                if (!OwnsPreview(session)) throw new EditorAdmissionException(SponsorEditorOutcome.Stale, "Preview lost ownership during baseline read.");
                if (session.RestoreRequested) throw new InvalidOperationException("Prepared context changed during baseline read; KeepCurrent is required.");
                if (_physical is not null || _seekClaims.Any(c => c.Phase is not (SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled)))
                    throw new InvalidOperationException("Physical or unresolved Sponsor work still owns cleanup.");
                if (!ValidPreview(session.Status.Interval, reservation.Token.Owner, actual)
                    || !actual.CanSeekExactly
                    || actual.State is not (SponsorPlaybackState.Playing or SponsorPlaybackState.Paused)
                    || actual.Duration is not { } duration || actual.Position > duration)
                    throw new InvalidOperationException("Preview needs a fresh valid interval and actual Playing/Paused baseline.");
                session.Status = session.Status with { SavedPosition = actual.Position, SavedPaused = actual.State == SponsorPlaybackState.Paused };
                SetPreviewStage(session, actual.State == SponsorPlaybackState.Paused ? PreviewStage.NeedEntrySeek : PreviewStage.NeedEntryPause,
                    SponsorPreviewPhase.Starting);
                await AdvancePreviewAsync(session, actual);
            }
        }
        catch (Exception ex)
        {
            if (session.Revoked)
            {
                try { await ReleaseRevokedPreviewAsync(session); }
                catch (Exception cleanup) { FailPreview(session, cleanup.Message); }
            }
            else FailPreview(session, ex.Message);
        }
        finally { _intents.Remove(reservation.Ticket.IntentId); }
        RequestObservation();
        Publish();
        return PreviewReply(session);
    });

    internal Task<SponsorEditorReply> EndEditorPreviewAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode)
    {
        VerifyAccess();
        if (!Enum.IsDefined(mode)) return Task.FromResult(EditorReply(SponsorEditorOutcome.Unavailable, reason: "Unknown preview end mode."));
        if (_terminalPreviewStatus?.Token == token && _terminalPreviewReply is not null) return Task.FromResult(_terminalPreviewReply);
        if (_preview is not { } session || session.Status.Token != token)
            return Task.FromResult(EditorReply(SponsorEditorOutcome.Stale, reason: "Preview cleanup token is not current."));
        if (mode == SponsorPreviewEndMode.KeepCurrent)
        {
            session.Revoked = true;
            session.TerminalPhase = SponsorPreviewPhase.Ended;
            session.Status = session.Status with { Phase = SponsorPreviewPhase.Restoring,
                Reason = "KeepCurrent requested; no return/restored-position claim." };
            CancelWake();
        }
        else RequestPreviewRestore(session, "Explicit preview End requested.");
        Publish();
        return Enqueue(async () =>
        {
            if (!ReferenceEquals(_preview, session)) return PreviewReply(session);
            try
            {
                var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
                if (session.Revoked) await ReleaseRevokedPreviewAsync(session, actual);
                else await AdvancePreviewAsync(session, actual);
            }
            catch (Exception ex) { FailPreview(session, ex.Message); }
            Publish();
            return PreviewReply(session);
        });
    }

    private async Task<bool> ObservePreviewAsync(NativePlaybackSnapshot actual)
    {
        if (_preview is not { } session) return false;
        try
        {
            if (!OwnsPreview(session))
            {
                RevokePreview("Preview transport ownership ended.");
                await ReleaseRevokedPreviewAsync(session, actual);
            }
            else if (_mute is not null || _rate is not null)
            {
                // Isolation revokes ordinary Sponsor fields even while VM preparation/recovery waits.
                // Preserve independent cleanup/failure ownership and never dispatch from the pre-restore read.
                await RestoreBothAsync();
                RequestObservation();
            }
            else if (session.Stage != PreviewStage.Reserved || session.RestoreRequested)
                await AdvancePreviewAsync(session, actual);
        }
        catch (Exception ex) { FailPreview(session, ex.Message); }
        return true; // Even terminalization gets a fresh ordinary evaluation, never old-plan fallthrough.
    }

    private async Task DrainPreviewForBoundaryAsync()
    {
        if (_preview is not { } session) return;
        if (_retiring || !OwnsPreview(session)) RevokePreview("Native boundary relinquishes preview transport.");
        var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (session.Revoked) await ReleaseRevokedPreviewAsync(session, actual);
        else
        {
            RequestPreviewRestore(session, "Preview cleanup requested by player drain.");
            await AdvancePreviewAsync(session, actual);
        }
        if (ReferenceEquals(_preview, session))
            throw new InvalidOperationException("Preview cleanup is awaiting observed return or explicit reconciliation.");
    }

    // Lane-internal continuation only. Never enqueue and await another controller job here.
    private async Task AdvancePreviewAsync(PreviewSession session, NativePlaybackSnapshot actual)
    {
        while (ReferenceEquals(_preview, session))
        {
            if (!OwnsPreview(session))
            {
                RevokePreview("Preview interrupted before its next transport operation.");
                await ReleaseRevokedPreviewAsync(session, actual);
                return;
            }
            if (session.RestoreRequested && !session.RollbackStarted)
            {
                if (session.Status.SavedPosition is null || session.Status.SavedPaused is null)
                {
                    FailPreview(session, "No captured baseline is available for return; KeepCurrent is required.");
                    return;
                }
                session.RollbackStarted = true; // One distinct rollback, not a replay of failed forward work.
                SetPreviewStage(session, actual.State == SponsorPlaybackState.Paused ? PreviewStage.NeedReturnSeek : PreviewStage.NeedReturnPause,
                    SponsorPreviewPhase.Restoring);
            }
            if (!Enum.IsDefined(actual.State) || actual.State is SponsorPlaybackState.Unknown or SponsorPlaybackState.Stopped
                || actual.Duration is not { } duration || !double.IsFinite(duration) || duration <= 0 || actual.Position > duration)
            {
                FailPreview(session, "Preview observation is unusable or playback stopped; explicit recovery is required.");
                return;
            }
            if (session.Stage is PreviewStage.NeedEntryPause or PreviewStage.AwaitEntryPause
                or PreviewStage.NeedEntrySeek or PreviewStage.AwaitEntry or PreviewStage.NeedEntryResume
                or PreviewStage.AwaitPlaying or PreviewStage.Running)
            {
                // Revalidate the prepared range against THIS read after each forward await.
                // Return/endpoint cleanup is deliberately outside this guard: an invalid old
                // interval must not prevent otherwise valid saved-position restoration.
                if (!ValidPreview(session.Status.Interval, session.Status.Token.Owner, actual))
                {
                    FailPreview(session, "Prepared preview range is invalid for the current actual duration.");
                    return;
                }
            }
            switch (session.Stage)
            {
                case PreviewStage.Reserved:
                case PreviewStage.Recovery:
                case PreviewStage.Inspection:
                    CancelWake();
                    return;
                case PreviewStage.NeedEntryPause:
                    SetPreviewStage(session, PreviewStage.AwaitEntryPause, SponsorPreviewPhase.Starting);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitEntryPause, NativeOperationKind.SetPaused, 1);
                    continue;
                case PreviewStage.AwaitEntryPause:
                    if (actual.State != SponsorPlaybackState.Paused) { WaitForPreviewObservation(session, "Entry pause is unobserved."); return; }
                    SetPreviewStage(session, PreviewStage.NeedEntrySeek, SponsorPreviewPhase.Starting);
                    continue;
                case PreviewStage.NeedEntrySeek:
                    if (!actual.CanSeekExactly) { FailPreview(session, "Exact entry seek capability is unavailable."); return; }
                    SetPreviewStage(session, PreviewStage.AwaitEntry, SponsorPreviewPhase.AwaitingEntry);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitEntry, NativeOperationKind.SeekExact, session.Status.Interval.Start);
                    continue;
                case PreviewStage.AwaitEntry:
                    if (actual.State != SponsorPlaybackState.Paused || actual.Position < session.Status.Interval.Start || actual.Position >= session.Status.Interval.End)
                    { WaitForPreviewObservation(session, "Paused in-interval entry is unobserved."); return; }
                    SetPreviewStage(session, PreviewStage.NeedEntryResume, SponsorPreviewPhase.AwaitingEntry);
                    continue;
                case PreviewStage.NeedEntryResume:
                    SetPreviewStage(session, PreviewStage.AwaitPlaying, SponsorPreviewPhase.AwaitingEntry);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitPlaying, NativeOperationKind.SetPaused, 0);
                    continue;
                case PreviewStage.AwaitPlaying:
                    if (actual.State != SponsorPlaybackState.Playing) { WaitForPreviewObservation(session, "Preview resume is unobserved."); return; }
                    SetPreviewStage(session, PreviewStage.Running, SponsorPreviewPhase.Playing);
                    continue;
                case PreviewStage.Running:
                    if (actual.State is SponsorPlaybackState.Waiting or SponsorPlaybackState.Seeking) { CancelWake(); return; }
                    if (actual.State != SponsorPlaybackState.Playing || actual.Position < session.Status.Interval.Start)
                    { FailPreview(session, "Preview playing ownership is not established by the current observation."); return; }
                    if (actual.Position >= session.Status.Interval.End)
                    {
                        SetPreviewStage(session, PreviewStage.NeedEndPause, SponsorPreviewPhase.Playing);
                        continue;
                    }
                    ArmWake(new MediaExecutionWake(session.Status.Interval.End,
                        (session.Status.Interval.End - actual.Position) / actual.Rate), session.Status.Token);
                    return;
                case PreviewStage.NeedEndPause:
                    SetPreviewStage(session, PreviewStage.AwaitEnd, SponsorPreviewPhase.Playing);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitEnd, NativeOperationKind.SetPaused, 1);
                    continue;
                case PreviewStage.AwaitEnd:
                    if (actual.State != SponsorPlaybackState.Paused) { WaitForPreviewObservation(session, "Endpoint pause is unobserved."); return; }
                    SetPreviewStage(session, PreviewStage.Inspection, SponsorPreviewPhase.AtEnd);
                    return; // Retain overshoot and inspection ownership; no automatic return/autoplay.
                case PreviewStage.NeedReturnPause:
                    SetPreviewStage(session, PreviewStage.AwaitReturnPause, SponsorPreviewPhase.Restoring);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitReturnPause, NativeOperationKind.SetPaused, 1);
                    continue;
                case PreviewStage.AwaitReturnPause:
                    if (actual.State != SponsorPlaybackState.Paused) { WaitForPreviewObservation(session, "Return pause is unobserved."); return; }
                    SetPreviewStage(session, PreviewStage.NeedReturnSeek, SponsorPreviewPhase.Restoring);
                    continue;
                case PreviewStage.NeedReturnSeek:
                    if (!actual.CanSeekExactly || session.Status.SavedPosition!.Value > duration)
                    { FailPreview(session, "Exact return capability or saved position is unavailable for the current duration."); return; }
                    SetPreviewStage(session, PreviewStage.AwaitReturn, SponsorPreviewPhase.AwaitingReturn);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitReturn, NativeOperationKind.SeekExact, session.Status.SavedPosition!.Value);
                    continue;
                case PreviewStage.AwaitReturn:
                    if (actual.State != SponsorPlaybackState.Paused || Math.Abs(actual.Position - session.Status.SavedPosition!.Value) > .05)
                    { WaitForPreviewObservation(session, "Coarse paused return readback is unobserved."); return; }
                    if (session.Status.SavedPaused == true)
                    { FinishPreview(session, SponsorPreviewPhase.Ended, "Paused baseline restored by coarse readback."); return; }
                    SetPreviewStage(session, PreviewStage.NeedReturnResume, SponsorPreviewPhase.Restoring);
                    continue;
                case PreviewStage.NeedReturnResume:
                    SetPreviewStage(session, PreviewStage.AwaitReturnedState, SponsorPreviewPhase.Restoring);
                    actual = await PreviewCommandAsync(session, PreviewStage.AwaitReturnedState, NativeOperationKind.SetPaused, 0);
                    continue;
                case PreviewStage.AwaitReturnedState:
                    if (actual.State != SponsorPlaybackState.Playing) { WaitForPreviewObservation(session, "Saved playing state is unobserved."); return; }
                    FinishPreview(session, SponsorPreviewPhase.Ended, "Saved playing state restored after coarse position readback.");
                    return;
                default:
                    throw new InvalidOperationException("Unknown preview stage.");
            }
        }
    }
}
