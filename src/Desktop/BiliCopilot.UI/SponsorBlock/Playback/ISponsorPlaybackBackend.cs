using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal enum FrameDirection { Forward, Backward }
internal enum NativeOperationKind { Read, SeekExact, WriteMute, WriteRate, SetPaused, StepFrame }
internal enum NativeAdmissionPurpose { Observation, Sponsor, User, Cleanup }
internal enum NativeBindingPhase { Open, Retiring, Closed }
internal enum PlayerIntentKind { Seek, Pause, Mute, PersistentRate, PhysicalRate, OpaqueControl }
internal sealed record UserIntentTicket(long IntentId, long NativeGeneration, PlayerIntentKind Kind,
    long MuteUserEpoch, long RateUserEpoch);

/// <summary>Actual reads only. Failed mute read is null. Flags describe this binding, never inferred from Sponsor identity.</summary>
internal sealed record NativePlaybackSnapshot(long NativeGeneration, double Position, double? Duration, double Rate,
    SponsorPlaybackState State, bool? Muted, bool CanSeekExactly, bool CanWriteMute, bool CanWriteRate, bool CanStepFrames);

internal interface ISponsorPlaybackBackend
{
    Task<NativePlaybackSnapshot> ReadAsync(NativeAdmission operation);
    Task SeekExactAsync(NativeAdmission operation, double seconds);
    Task WriteMuteAsync(NativeAdmission operation, bool muted);
    Task WriteRateAsync(NativeAdmission operation, double rate);
    Task SetPausedAsync(NativeAdmission operation, bool paused);
    Task StepFrameAsync(NativeAdmission operation, FrameDirection direction);
}

/// <summary>
/// Opaque, one-operation permit, NOT a handle, SafeHandle, cancellation token, task tracker or disposal barrier.
/// Controller alone creates/retains Issuer for a captured Client/Player binding; backend receives that SAME issuer.
/// Before calling a backend factory the controller publishes operation/claims/lease and a tracked completion slot.
/// Each backend method calls issuer.ValidateAndConsume immediately before touching its captured native binding.
/// Publish/track synchronous throws and actual SDK task lifetime too; cancelling a waiter never completes that task.
/// Internal issuance is an assembly ownership convention, not a hostile-code security boundary.
/// </summary>
internal sealed class NativeAdmission
{
    private readonly Issuer issuer;
    private bool consumed;
    private NativeAdmission(Issuer issuer, long operationId, NativeOperationKind kind, NativeAdmissionPurpose purpose,
        PlaybackStamp? sponsorStamp)
    {
        this.issuer = issuer;
        OperationId = operationId;
        Kind = kind;
        Purpose = purpose;
        SponsorStamp = sponsorStamp;
    }
    internal long OperationId { get; }
    internal long NativeGeneration => issuer.NativeGeneration;
    internal NativeOperationKind Kind { get; }
    internal NativeAdmissionPurpose Purpose { get; }
    internal PlaybackStamp? SponsorStamp { get; }

    internal sealed class Issuer
    {
        private readonly object gate = new();
        private NativeBindingPhase phase;
        private long lastOperationId;
        internal Issuer(long nativeGeneration)
        {
            if (nativeGeneration < 0) throw new ArgumentOutOfRangeException(nameof(nativeGeneration));
            NativeGeneration = nativeGeneration;
        }
        internal long NativeGeneration { get; }

        // Dispatcher-owned issuance AFTER complete current-plan revalidation; media ID alone never creates a permit.
        internal NativeAdmission Issue(long operationId, NativeOperationKind kind, NativeAdmissionPurpose purpose,
            PlaybackStamp? sponsorStamp = null)
        {
            lock (gate)
            {
                if (!Enum.IsDefined(kind) || !Enum.IsDefined(purpose) || operationId <= lastOperationId
                    || purpose == NativeAdmissionPurpose.Sponsor && (sponsorStamp is null || sponsorStamp.Owner.Generation != NativeGeneration)
                    || purpose == NativeAdmissionPurpose.Observation && kind != NativeOperationKind.Read
                    || purpose == NativeAdmissionPurpose.Cleanup && kind is not (NativeOperationKind.Read or NativeOperationKind.WriteMute or NativeOperationKind.WriteRate)
                    || !Permitted(purpose)) throw new InvalidOperationException("Native admission is not available for this binding/operation.");
                lastOperationId = operationId;
                return new(this, operationId, kind, purpose, sponsorStamp);
            }
        }

        internal void ValidateAndConsume(NativeAdmission operation, NativeOperationKind expectedKind)
        {
            lock (gate)
            {
                if (!ReferenceEquals(operation.issuer, this) || operation.consumed || operation.Kind != expectedKind
                    || !Permitted(operation.Purpose)) throw new InvalidOperationException("Stale, foreign, consumed or mismatched native admission.");
                operation.consumed = true;
            }
        }

        // Cancels unstarted ordinary permits. Already-started tasks STILL belong to this binding and must drain.
        internal void BeginRetirement()
        {
            lock (gate) if (phase == NativeBindingPhase.Open) phase = NativeBindingPhase.Retiring;
        }

        // PRECONDITION: controller drained real tasks and all required conditional restorations succeeded/reconciled.
        // Failed restoration retains binding ownership: do not call this or Stop/Dispose to force a handoff.
        internal void SealAfterDrain()
        {
            lock (gate)
            {
                if (phase != NativeBindingPhase.Retiring) throw new InvalidOperationException("Retire the native binding before sealing it.");
                phase = NativeBindingPhase.Closed;
            }
        }

        // Ordinary lease retirement (pause/disable/user handoff) can restore on an OPEN native binding too.
        // Closing Sponsor admission does not retire the binding or disable typed user controls.
        private bool Permitted(NativeAdmissionPurpose purpose) => purpose == NativeAdmissionPurpose.Cleanup
            ? phase != NativeBindingPhase.Closed : phase == NativeBindingPhase.Open;
    }
}
