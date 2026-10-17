using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal sealed partial class SponsorPlaybackController
{
    // Read-only affordance after the existing VM drain task (including physical drain + Seal).
    // Never starts cleanup, changes admission, clears a failure or manufactures a Read record.
    internal SponsorNativeRetirementReceipt? CaptureNativeRetirement(PlaybackOwner expected, Task actualDrain)
    {
        VerifyAccess();
        return actualDrain.IsCompletedSuccessfully && NativeRetirementIsClean(expected)
            ? new(this, expected, _generation, _sequence, actualDrain) : null;
    }

    internal bool IsCurrentNativeRetirement(SponsorNativeRetirementReceipt receipt)
    {
        VerifyAccess();
        return ReferenceEquals(receipt.Producer, this) && receipt.DrainTask.IsCompletedSuccessfully
            && receipt.Generation == _generation && receipt.Sequence <= _sequence
            && NativeRetirementIsClean(receipt.Owner);
    }

    private bool NativeRetirementIsClean(PlaybackOwner expected) => _owner == expected
        && expected.Generation == _generation && _generation > 0 && _issuer is not null
        && _sealed && _retiring && !_sponsorOpen && !_running && !_observationQueued
        && _jobs.Count == 0 && _intents.Count == 0 && _request is null && _wake is null
        && (_actualCompletion is null || _actualCompletion.IsCompletedSuccessfully)
        && _failure is null && (_actual is null || _actual.NativeGeneration == _generation)
        && (_operation is null || _operation.Generation == _generation && _operation.Failure is null
            && (_operation.Phase is SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled
                || _operation.Kind == NativeOperationKind.Read && _operation.Phase == SponsorOperationPhase.SucceededUnobserved
                || _operation.Purpose == NativeAdmissionPurpose.User && _operation.Kind == NativeOperationKind.SetPaused
                    && _operation.Phase == SponsorOperationPhase.SucceededUnobserved))
        && _mute is null && _rate is null && _physical is null && _preview is null
        && _rateClaims.All(claim => claim.Phase == PlaybackLeasePhase.Confirmed)
        && _seekClaims.All(claim => claim.Phase is SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled);
}
