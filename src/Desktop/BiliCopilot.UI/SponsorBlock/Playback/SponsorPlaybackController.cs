using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Playback;

// N4 supplies values, never a native binding, a task or a fabricated observation/interaction.
internal sealed record SponsorCoordinatorInput(PlaybackOwner Owner, long Revision,
    ImmutableArray<SponsorSegment> Segments, PlaybackPolicy Policy, PlaybackChannel Channel)
{
    internal PlaybackSpeedPolicy Speed { get; init; } = new();
    internal bool? KnownMusic { get; init; }
    internal ImmutableHashSet<PlaybackSegmentKey> LocalHidden { get; init; } = [];
    internal ImmutableHashSet<PlaybackSegmentKey> LocalDownvoted { get; init; } = [];
    internal ImmutableHashSet<PlaybackSegmentKey> ExternalSourceExcluded { get; init; } = [];
    internal ImmutableHashSet<PlaybackSegmentKey> DraftSegments { get; init; } = [];
    internal ImmutableHashSet<PlaybackSegmentKey>? AlignedEligibleSource2 { get; init; }
    internal bool IgnorePlaybackForEditing { get; init; }
    internal PreviewInterval? Preview { get; init; }
}

internal enum SponsorOperationPhase { Pending, SucceededUnobserved, Confirmed, Failed, Unknown, Reconciled }
internal sealed record SponsorOperation(long Id, long Generation, NativeOperationKind? Kind,
    NativeAdmissionPurpose Purpose, PlaybackStamp? Stamp, SponsorOperationPhase Phase, string? Failure = null);
internal sealed record SponsorControllerResult(long Sequence, PlaybackOwner? Owner, PlaybackStamp? Stamp,
    NativePlaybackSnapshot? Actual, PlaybackExecutionPlan? Plan, SponsorOperation? Operation,
    PlaybackLeaseSnapshot<bool>? MuteLease, PlaybackLeaseSnapshot<double>? RateLease,
    ImmutableArray<PlaybackRateClaim> RateClaims, PlaybackAppliedEffect? Applied,
    PlaybackRateReceipt? RateReceipt, long AppliedId, long RateReceiptId,
    ImmutableArray<SponsorSeekStatus> SeekClaims, string? Failure)
{
    internal SponsorPreviewStatus? PreviewPlayback { get; init; }
}
internal sealed record SponsorSeekStatus(string Token, PlaybackEffect Effect, PlaybackStamp Stamp, SponsorOperationPhase Phase);
internal sealed record PhysicalRateHandoff(long NativeGeneration, long Id, double Baseline);
internal sealed record SponsorReconciliation(PlaybackOwner Owner, string LeaseToken, PlaybackLeaseField Field);
internal sealed record SponsorSeekReconciliation(PlaybackOwner Owner, string OperationToken);

/// <summary>
/// One dispatcher actor per VM, one serial native lane across all presentations. The injected post
/// must execute on a dispatcher with a SynchronizationContext (the application's player dispatcher).
/// No caller cancellation token is passed to native work, cleanup or its completion owner.
/// </summary>
internal sealed partial class SponsorPlaybackController
{
    private readonly Action<Action> _post;
    private readonly Func<bool> _hasAccess;
    private readonly Func<double> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Queue<Func<Task>> _jobs = new();
    private readonly Dictionary<long, UserIntentTicket> _intents = [];
    private bool _running;
    private bool _observationQueued;
    private bool _sponsorOpen;
    private bool _retiring;
    private bool _sealed = true;
    private ISponsorPlaybackBackend? _backend;
    private NativeAdmission.Issuer? _issuer;
    private Func<bool>? _bindingCurrent;
    private long _generation;
    private long _operationId;
    private long _intentId;
    private long _observationRevision;
    private long _visit;
    private long _muteEpoch;
    private long _rateEpoch;
    private long _muteWrittenEpoch;
    private long _rateWrittenEpoch;
    private long _sequence;
    private long _wakeVersion;
    private CancellationTokenSource? _wake;
    private PhysicalRateHandoff? _physical;
    private PlaybackOwner? _owner;
    private SponsorCoordinatorInput? _input;
    private PlaybackInteraction? _interaction;
    private PlaybackRequest? _request;
    private long _requestRevision;
    private NativePlaybackSnapshot? _actual;
    private PlaybackExecutionPlan? _plan;
    private SponsorOperation? _operation;
    private Task? _actualCompletion;
    private string? _failure;
    private enum ObservedWorkFailureKind { Observation, Lane, Wake, WakeCallback, Subscriber }
    private sealed record ObservedWorkFailure(long NativeGeneration, ObservedWorkFailureKind Kind);
    private ObservedWorkFailure? _currentObservedWorkFailure;
    private ObservedWorkFailure? _retiredObservedWorkFailure;
    private PlaybackAppliedEffect? _applied;
    private PlaybackRateReceipt? _rateReceipt;
    private long _receiptId;
    private long _appliedId;
    private long _rateReceiptId;

    internal SponsorPlaybackController(Action<Action> post, Func<bool> hasAccess, Func<double> clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _post = post;
        _hasAccess = hasAccess;
        _clock = clock;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    internal event Action<SponsorControllerResult>? Updated;
    internal SponsorControllerResult Snapshot => new(_sequence, _owner, CurrentStamp, _actual, _plan,
        _operation, _mute, _rate, _rateClaims, _applied, _rateReceipt, _appliedId, _rateReceiptId, _seekClaims, _failure)
        { PreviewPlayback = _preview?.Status ?? _terminalPreviewStatus };
    internal bool HasBinding => _backend is not null && !_sealed;
    internal bool CanUseBinding => HasBinding && !_retiring && _bindingCurrent?.Invoke() == true;
    internal Task? ActualCompletion => _actualCompletion;
    internal double? UserRateBaseline => _physical?.Baseline ?? _rate?.Baseline;

    internal void Bind(Func<NativeAdmission.Issuer, ISponsorPlaybackBackend> createBackend,
        Func<bool> bindingCurrent, MediaIdentity? identity)
    {
        VerifyAccess();
        if (!_sealed || _running || _jobs.Count != 0 || _mute is not null || _rate is not null || _preview is not null)
            throw new InvalidOperationException("Previous native binding has not drained and sealed.");
        var issuer = new NativeAdmission.Issuer(++_generation);
        var backend = createBackend(issuer);
        if (_currentObservedWorkFailure is { } previousFailure) _retiredObservedWorkFailure = previousFailure;
        _currentObservedWorkFailure = null;
        _issuer = issuer;
        _backend = backend;
        _bindingCurrent = bindingCurrent;
        _sealed = false;
        _retiring = false;
        _sponsorOpen = true;
        _owner = identity is null ? null : new(identity, _generation);
        _observationRevision = _visit = _muteEpoch = _rateEpoch = 0;
        _muteWrittenEpoch = _rateWrittenEpoch = 0;
        _positionIntentId = _pauseIntentId = 0;
        _interaction = _owner is null ? null : new(_owner, _visit);
        _input = _owner is null ? null : new(_owner, 0, [], new(0), new());
        _request = null;
        _visitObservation = null;
        _visitRebasePending = false;
        _actual = null;
        _plan = null;
        _operation = null;
        _actualCompletion = null;
        _applied = null;
        _rateReceipt = null;
        _appliedId = _rateReceiptId = 0;
        _failure = null;
        _rateClaims = [];
        _cooldowns = [];
        _seekClaims = [];
        _intents.Clear();
        Publish();
        RequestObservation();
    }

    internal bool Update(SponsorCoordinatorInput input)
    {
        VerifyAccess();
        if (!CanUseBinding || input.Owner != _owner || input.Revision <= (_input?.Revision ?? -1)
            || input.Policy.Revision < (_input?.Policy.Revision ?? 0)) return false;
        _input = input;
        CheckPreviewContext(input);
        ExpireRequest();
        ++_observationRevision; // facts/policy updates invalidate complete stamps, not visit claims.
        CancelWake();
        RequestObservation();
        return true;
    }

    internal bool Request(PlaybackRequest request)
    {
        VerifyAccess();
        ExpireRequest();
        if (!_sponsorOpen || !CanUseBinding || _preview is not null || request.Stamp != CurrentStamp || _request is not null
            || _interaction is null || _interaction.AdmittedRequestIds.Length >= SponsorPlaybackRules.MaximumRecords
            || _interaction.AdmittedRequestIds.Contains(request.Id)) return false;
        _request = request;
        _requestRevision = _input!.Revision;
        RequestObservation();
        return true;
    }

    // Explicit N4/user reconciliation only: no retry/write and no successful-effect receipt.
    // A failed lease is released only after an actual read establishes its original baseline.
    internal Task<bool> ReconcileRestoredFieldAsync(SponsorReconciliation request) => Enqueue(async () =>
    {
        if (request.Owner != _owner || !HasBinding) return false;
        var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (request.Field == PlaybackLeaseField.Mute && _mute is { } mute && mute.Token == request.LeaseToken
            && actual.Muted == mute.Baseline)
        {
            _mute = null;
            ClearManualMute();
            Publish();
            return true;
        }
        if (request.Field == PlaybackLeaseField.Rate && _rate is { } rate && rate.Token == request.LeaseToken
            && SameRate(actual.Rate, rate.Baseline))
        {
            _rate = null;
            _rateSample = null;
            // Failed/unknown contributor claims remain; progress/cooldown cannot replay them.
            Publish();
            return true;
        }
        return false;
    });

    internal Task<bool> ReconcileSeekAsync(SponsorSeekReconciliation request) => Enqueue(async () =>
    {
        if (request.Owner != _owner || !HasBinding) return false;
        var claim = _seekClaims.FirstOrDefault(c => c.Token == request.OperationToken);
        if (claim is null || claim.Phase == SponsorOperationPhase.Pending) return false;
        await ReadAsync(NativeAdmissionPurpose.Cleanup);
        // Explicit acknowledgement of actual quiescent state, NOT a successful seek/undo/statistic.
        _seekClaims = _seekClaims.Replace(claim, claim with { Phase = SponsorOperationPhase.Reconciled });
        Publish();
        return true;
    });

    internal void RequestObservation()
    {
        VerifyAccess();
        ++_observationRevision;
        if (_observationQueued || !CanUseBinding) return;
        _observationQueued = true;
        var generation = _generation;
        var owner = _issuer;
        Forget(Enqueue(async () =>
        {
            _observationQueued = false;
            if (generation != _generation || !CanUseBinding) return;
            await ObserveAsync();
        }), owner, ObservedWorkFailureKind.Observation);
    }

    // A bounded join for N4 capture/UI callers. It joins a reread, not a waiter-cancellable native task.
    internal Task<SponsorControllerResult> ObserveCurrentAsync()
    {
        RequestObservation();
        return Enqueue(() => Task.FromResult(Snapshot));
    }

    internal UserIntentTicket NoteUserIntent(PlayerIntentKind kind)
    {
        VerifyAccess();
        if (!Enum.IsDefined(kind) || !CanUseBinding || _intents.Count >= 256)
            throw new InvalidOperationException("Native binding is unavailable for user intent.");
        if (kind is PlayerIntentKind.Mute or PlayerIntentKind.OpaqueControl) ++_muteEpoch;
        if (kind is PlayerIntentKind.PersistentRate or PlayerIntentKind.PhysicalRate or PlayerIntentKind.OpaqueControl) ++_rateEpoch;
        var ticket = new UserIntentTicket(++_intentId, _generation, kind, _muteEpoch, _rateEpoch);
        _intents.Add(ticket.IntentId, ticket);
        RecordTransportIntent(ticket);
        if (_interaction is not null && kind is PlayerIntentKind.Mute or PlayerIntentKind.OpaqueControl)
            _interaction = _interaction with { MuteSuppressed = true, ManualMute = null };
        ++_observationRevision;
        if (_request is { } request) ConsumeRequest(request.Id);
        CancelWake();
        _rateSample = null;
        return ticket;
    }

    internal void AbandonIntent(UserIntentTicket ticket)
    {
        VerifyAccess();
        _intents.Remove(ticket.IntentId);
        RequestObservation();
    }

    internal void CloseAdmission(RetirementReason reason)
    {
        VerifyAccess();
        _sponsorOpen = false;
        if (_request is { } request) ConsumeRequest(request.Id);
        ++_observationRevision;
        CancelWake();
        RetireLeases(reason);
        if (_preview is { Revoked: false } preview) RequestPreviewRestore(preview, "Sponsor admission closed.");
    }

    internal void ReopenAdmission()
    {
        VerifyAccess();
        if (!CanUseBinding) return;
        _sponsorOpen = true;
        RequestObservation();
    }

    // Called only for actual native replacement/replay/close, never a Sponsor policy/presentation change.
    internal void BeginNativeRetirement(RetirementReason reason)
    {
        RevokePreview("Native binding is retiring.");
        CloseAdmission(reason);
        if (!HasBinding || _retiring) return;
        _retiring = true;
        _issuer!.BeginRetirement();
    }

    internal Task DrainAndRestoreAsync()
    {
        VerifyAccess();
        return Enqueue(async () =>
        {
            if (!HasBinding) return;
            var errors = new List<Exception>();
            try { await DrainPreviewForBoundaryAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await RestoreMuteAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await RestoreRateAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (_seekClaims.Any(c => c.Phase == SponsorOperationPhase.SucceededUnobserved))
            {
                try { ConfirmSeeks(await ReadAsync(NativeAdmissionPurpose.Cleanup)); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (_seekClaims.Any(c => c.Phase is not (SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled)))
                errors.Add(new InvalidOperationException("Unconfirmed seek retains its native binding."));
            if (_physical is not null) errors.Add(new InvalidOperationException("Physical rate handoff has not drained."));
            if (errors.Count != 0) throw new AggregateException("Sponsor cleanup failed; native ownership retained.", errors);
        });
    }

    internal void SealAfterDrain()
    {
        VerifyAccess();
        if (!HasBinding) return;
        if (!_retiring || _running || _jobs.Count != 0 || _actualCompletion?.IsCompleted == false
            || _mute is not null || _rate is not null || _physical is not null || _preview is not null
            || _seekClaims.Any(c => c.Phase is not (SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled)))
            throw new InvalidOperationException("Native binding cannot seal before actual completion and restoration.");
        _issuer!.SealAfterDrain();
        _sealed = true;
    }

    internal Task SeekAsync(UserIntentTicket ticket, double seconds) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.Seek);
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        await RestoreBothAsync();
        CheckTicket(ticket);
        await NativeAsync(NativeOperationKind.SeekExact, NativeAdmissionPurpose.User,
            permit => _backend!.SeekExactAsync(permit, seconds));
        AdvanceVisit();
        _visitRebasePending = true; // Acknowledgement is not an observed landing.
    });

    internal Task SetPausedAsync(UserIntentTicket ticket, bool paused) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.Pause);
        await RestoreBothAsync();
        CheckTicket(ticket);
        await NativeAsync(NativeOperationKind.SetPaused, NativeAdmissionPurpose.User,
            permit => _backend!.SetPausedAsync(permit, paused));
    });

    internal Task SetMuteAsync(UserIntentTicket ticket, bool muted) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.Mute);
        await RestoreMuteAsync();
        CheckTicket(ticket);
        await NativeAsync(NativeOperationKind.WriteMute, NativeAdmissionPurpose.User,
            permit => _backend!.WriteMuteAsync(permit, muted));
        _muteWrittenEpoch = ticket.MuteUserEpoch;
        var actual = await ReadAsync(NativeAdmissionPurpose.User);
        if (actual.Muted != muted) throw new InvalidOperationException("User mute write is not observed.");
        ConfirmOperation();
    });

    internal Task<bool> ToggleMuteAsync(UserIntentTicket ticket) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.Mute);
        await RestoreMuteAsync();
        CheckTicket(ticket);
        var before = await ReadAsync(NativeAdmissionPurpose.User);
        CheckTicket(ticket);
        if (before.Muted is not { } muted) throw new InvalidOperationException("Actual mute is unavailable for toggle.");
        await NativeAsync(NativeOperationKind.WriteMute, NativeAdmissionPurpose.User,
            permit => _backend!.WriteMuteAsync(permit, !muted));
        _muteWrittenEpoch = ticket.MuteUserEpoch;
        var actual = await ReadAsync(NativeAdmissionPurpose.User);
        if (actual.Muted != !muted) throw new InvalidOperationException("User mute toggle is not observed.");
        return !muted;
    });

    internal Task<double> SetPersistentRateAsync(UserIntentTicket ticket, double rate) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.PersistentRate);
        if (!double.IsFinite(rate) || rate < .1 || rate > 3) throw new ArgumentOutOfRangeException(nameof(rate));
        if (_physical is not null) throw new InvalidOperationException("Drain physical rate before persistent rate.");
        await RestoreRateAsync();
        CheckTicket(ticket);
        await NativeAsync(NativeOperationKind.WriteRate, NativeAdmissionPurpose.User,
            permit => _backend!.WriteRateAsync(permit, rate));
        _rateWrittenEpoch = ticket.RateUserEpoch;
        var actual = await ReadAsync(NativeAdmissionPurpose.User);
        CheckTicket(ticket);
        if (!SameRate(actual.Rate, rate)) throw new InvalidOperationException("User rate write is not observed.");
        ConfirmOperation();
        return rate; // Only this confirmed persistent intent may become PlayerSpeed.
    });

    internal Task StepFrameAsync(UserIntentTicket ticket, FrameDirection direction) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.Seek);
        await RestoreBothAsync();
        CheckTicket(ticket);
        var actual = await ReadAsync(NativeAdmissionPurpose.User);
        CheckTicket(ticket);
        if (!actual.CanStepFrames || actual.State != SponsorPlaybackState.Paused)
            throw new InvalidOperationException("Paused-video frame stepping is unavailable.");
        await NativeAsync(NativeOperationKind.StepFrame, NativeAdmissionPurpose.User,
            permit => _backend!.StepFrameAsync(permit, direction));
        AdvanceVisit();
        _visitRebasePending = true; // No frame position or delivery is inferred from acknowledgement.
    });

    // Narrow application-owned escape hatch for existing key commands. N4 never supplies this delegate.
    // It is tracked on the same lane but does not invent a seventh native permit operation.
    internal Task OpaqueControlAsync(UserIntentTicket ticket, Func<Task> command) => UserAsync(ticket, async () =>
    {
        if (ticket.Kind is not (PlayerIntentKind.OpaqueControl or PlayerIntentKind.Pause))
            throw new InvalidOperationException("Opaque command requires its matching intent.");
        await RestoreBothAsync();
        CheckTicket(ticket);
        await TrackAsync<object?>(null, NativeAdmissionPurpose.User, null, async () => { await command(); return null; });
        // An opaque key is not proof of a seek/new visit. Retain claims; reread on completion.
    });

    internal Task<PhysicalRateHandoff> AcquirePhysicalRateAsync(UserIntentTicket ticket) => UserAsync(ticket, async () =>
    {
        RequireKind(ticket, PlayerIntentKind.PhysicalRate);
        if (_physical is not null) return _physical;
        // The stored lease baseline is restored before the physical state captures it.
        await RestoreRateAsync();
        CheckTicket(ticket);
        var actual = await ReadAsync(NativeAdmissionPurpose.User);
        CheckTicket(ticket);
        _physical = new(_generation, ticket.IntentId, actual.Rate);
        return _physical;
    });

    internal Task WritePhysicalRateAsync(PhysicalRateHandoff handoff, double rate) => Enqueue(async () =>
    {
        if (!ReferenceEquals(handoff, _physical) || handoff.NativeGeneration != _generation)
            throw new InvalidOperationException("Stale physical rate handoff.");
        await NativeAsync(NativeOperationKind.WriteRate, SameRate(rate, handoff.Baseline)
                ? NativeAdmissionPurpose.Cleanup : NativeAdmissionPurpose.User,
            permit => _backend!.WriteRateAsync(permit, rate));
        var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (!SameRate(actual.Rate, rate)) throw new InvalidOperationException("Physical rate write is not observed.");
    });

    internal void ReleasePhysicalRate(PhysicalRateHandoff handoff)
    {
        VerifyAccess();
        if (!ReferenceEquals(handoff, _physical)) return;
        if (_actual is null || !SameRate(_actual.Rate, handoff.Baseline))
            throw new InvalidOperationException("Physical baseline restoration is not confirmed.");
        _physical = null;
        RequestObservation();
    }

    private Task UserAsync(UserIntentTicket ticket, Func<Task> action) => UserAsync<object?>(ticket, async () =>
    { await action(); return null; });

    private Task<T> UserAsync<T>(UserIntentTicket ticket, Func<Task<T>> action) => Enqueue(async () =>
    {
        try { CheckTicket(ticket); return await action(); }
        finally { _intents.Remove(ticket.IntentId); RequestObservation(); }
    });

    private void CheckTicket(UserIntentTicket ticket)
    {
        VerifyAccess();
        if (!CanUseBinding || ticket.NativeGeneration != _generation
            || !_intents.TryGetValue(ticket.IntentId, out var admitted) || !ReferenceEquals(admitted, ticket)
            || (ticket.Kind is PlayerIntentKind.Seek or PlayerIntentKind.OpaqueControl) && ticket.IntentId != _positionIntentId
            || (ticket.Kind is PlayerIntentKind.Pause or PlayerIntentKind.OpaqueControl) && ticket.IntentId != _pauseIntentId
            || (ticket.Kind is PlayerIntentKind.Mute or PlayerIntentKind.OpaqueControl) && ticket.MuteUserEpoch != _muteEpoch
            || (ticket.Kind is PlayerIntentKind.PersistentRate or PlayerIntentKind.PhysicalRate or PlayerIntentKind.OpaqueControl)
                && ticket.RateUserEpoch != _rateEpoch)
            throw new InvalidOperationException("User intent was superseded before native dispatch.");
    }

    private static void RequireKind(UserIntentTicket ticket, PlayerIntentKind expected)
    {
        if (ticket.Kind != expected) throw new InvalidOperationException("User mutation does not match its intent kind.");
    }

    private PlaybackStamp? CurrentStamp => _owner is null || _input is null ? null
        : new(_owner, _input.Policy.Revision, _observationRevision, _visit, _muteEpoch, _rateEpoch);

    private Task Enqueue(Func<Task> action) => Enqueue<object?>(async () => { await action(); return null; });
    private Task<T> Enqueue<T>(Func<Task<T>> action)
    {
        VerifyAccess();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception ex) { _failure = ex.Message; Publish(); completion.SetException(ex); }
        });
        if (!_running)
        {
            _running = true; // Publish before even a synchronously executing post/reentrant callback.
            var owner = _issuer;
            _post(() => Forget(RunLaneAsync(), owner, ObservedWorkFailureKind.Lane));
        }
        return completion.Task;
    }

    private async Task RunLaneAsync()
    {
        VerifyAccess();
        try { while (_jobs.TryDequeue(out var job)) { await job(); VerifyAccess(); } }
        finally { _running = false; }
    }

    private Task NativeAsync(NativeOperationKind kind, NativeAdmissionPurpose purpose,
        Func<NativeAdmission, Task> factory, PlaybackStamp? stamp = null) =>
        NativeAsync<object?>(kind, purpose, async permit => { await factory(permit); return null; }, stamp);

    private Task<T> NativeAsync<T>(NativeOperationKind kind, NativeAdmissionPurpose purpose,
        Func<NativeAdmission, Task<T>> factory, PlaybackStamp? stamp = null) =>
        TrackAsync(kind, purpose, stamp, () => factory(_issuer!.Issue(_operationId, kind, purpose, stamp)));

    private async Task<T> TrackAsync<T>(NativeOperationKind? kind, NativeAdmissionPurpose purpose,
        PlaybackStamp? stamp, Func<Task<T>> factory)
    {
        VerifyAccess();
        if (!HasBinding || _bindingCurrent?.Invoke() != true
            || purpose != NativeAdmissionPurpose.Cleanup && _retiring
            || purpose == NativeAdmissionPurpose.Sponsor && (!_sponsorOpen || _preview is not null || stamp != CurrentStamp || _intents.Count != 0))
            throw new InvalidOperationException("Native dispatch lost its complete binding/stamp.");
        var id = ++_operationId;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actualCompletion = completion.Task;
        _operation = new(id, _generation, kind, purpose, stamp, SponsorOperationPhase.Pending);
        // Claims and immutable leases are installed by the caller before entering this method.
        try
        {
            // No publication callback between complete-stamp validation and factory invocation.
            var actualTask = factory(); // synchronous throw/success is owned by the published slot too.
            var value = await actualTask;
            VerifyAccess();
            _operation = _operation! with { Phase = SponsorOperationPhase.SucceededUnobserved };
            completion.SetResult(value);
            return value;
        }
        catch (Exception ex)
        {
            VerifyAccess();
            _operation = _operation! with { Phase = ex is OperationCanceledException
                ? SponsorOperationPhase.Unknown : SponsorOperationPhase.Failed, Failure = ex.Message };
            completion.SetException(ex);
            _ = completion.Task.Exception; // This completion slot is diagnostic; caller receives the same failure.
            throw;
        }
    }

    private async Task<NativePlaybackSnapshot> ReadAsync(NativeAdmissionPurpose purpose)
    {
        var actual = await NativeAsync(NativeOperationKind.Read, purpose, permit => _backend!.ReadAsync(permit));
        if (actual.NativeGeneration != _generation || !double.IsFinite(actual.Position) || actual.Position < 0
            || !double.IsFinite(actual.Rate) || actual.Rate <= 0)
            throw new InvalidOperationException("Invalid actual native snapshot.");
        _actual = actual;
        return actual;
    }

    private void ConfirmOperation()
    {
        if (_operation is not null) _operation = _operation with { Phase = SponsorOperationPhase.Confirmed };
    }

    private void CancelWake()
    {
        ++_wakeVersion;
        _wake?.Cancel();
        _wake?.Dispose();
        _wake = null;
    }

    private void ArmWake(ExecutionWake? wake, SponsorPreviewToken? previewToken = null)
    {
        CancelWake();
        if (wake is null || !WakeOwnerCurrent(previewToken) || !double.IsFinite(wake.DelaySeconds) || wake.DelaySeconds <= 0) return;
        var source = _wake = new();
        var version = _wakeVersion;
        var generation = _generation;
        var owner = _issuer;
        Forget(WakeAsync(), owner, ObservedWorkFailureKind.Wake);
        async Task WakeAsync()
        {
            try
            {
                await _delay(TimeSpan.FromSeconds(Math.Min(wake.DelaySeconds, 86400)), source.Token);
                _post(() =>
                {
                    try
                    {
                        if (version == _wakeVersion && generation == _generation && WakeOwnerCurrent(previewToken))
                            RequestObservation(); // A wake never dispatches an old plan.
                    }
                    catch (Exception) { RecordObservedWorkFailure(owner, ObservedWorkFailureKind.WakeCallback); }
                });
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        }
    }

    private void Publish()
    {
        var owner = _issuer;
        ++_sequence;
        try { Updated?.Invoke(Snapshot); }
        catch (Exception) { RecordObservedWorkFailure(owner, ObservedWorkFailureKind.Subscriber); }
    }

    private async void Forget(Task task, NativeAdmission.Issuer? owner, ObservedWorkFailureKind kind)
    {
        // Keep observing the actual task, including wake work that runs outside the native lane.
        // Expected wake cancellation is handled by WakeAsync's source-specific filter above.
        try {
            // T6-02: Observe the already-issued native/wake work; the observer is not its producer, and real task failure remains classified.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
        }
        catch (Exception) { RecordObservedWorkFailure(owner, kind); }
    }

    private void RecordObservedWorkFailure(NativeAdmission.Issuer? owner, ObservedWorkFailureKind kind)
    {
        var failure = new ObservedWorkFailure(owner?.NativeGeneration ?? 0, kind);
        if (!_sealed && ReferenceEquals(owner, _issuer))
        {
            _currentObservedWorkFailure = failure;
            _failure ??= kind switch
            {
                ObservedWorkFailureKind.Observation => "ObservationWorkFailure",
                ObservedWorkFailureKind.Lane => "NativeLaneFailure",
                ObservedWorkFailureKind.Wake => "WakeWorkFailure",
                ObservedWorkFailureKind.WakeCallback => "WakeCallbackFailure",
                ObservedWorkFailureKind.Subscriber => "CoordinatorObserverFailure",
                _ => "ObservedWorkFailure",
            };
        }
        else if (_retiredObservedWorkFailure != failure)
        {
            // One bounded, issuer-generation-tagged retired diagnostic. Never poison the new binding.
            _retiredObservedWorkFailure = failure;
        }
        // No Publish/callbacks, exception payload retention, operation completion or ownership mutation.
    }

    private void VerifyAccess()
    {
        if (!_hasAccess()) throw new InvalidOperationException("Sponsor state requires the player dispatcher.");
    }

    private static bool SameRate(double left, double right) => Math.Abs(left - right) <= .000001;
}
