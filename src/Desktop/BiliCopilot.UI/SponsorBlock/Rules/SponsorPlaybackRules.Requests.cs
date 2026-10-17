using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public static partial class SponsorPlaybackRules
{
    private static bool ValidInteraction(PlaybackInput input) => input.Interaction.Reviews.All(review =>
        review is not null && !string.IsNullOrWhiteSpace(review.Id) && review.Action is "skip" or "mute"
        && ValidRange(review.Start, review.End) && !review.Contributors.IsDefaultOrEmpty
        && review.Contributors.Length <= MaximumRecords && review.Contributors.All(key => key is not null && key.Media == input.Stamp.Owner.Media))
        && input.Interaction.AdmittedSeeks.All(claim => claim is not null && !claim.Contributors.IsDefaultOrEmpty
            && claim.Contributors.Length <= MaximumRecords && claim.Contributors.All(key => key is not null && key.Media == input.Stamp.Owner.Media))
        && (input.Interaction.ManualMute is not { } mute || !mute.Contributors.IsDefaultOrEmpty
            && mute.Contributors.Length <= MaximumRecords && mute.Contributors.All(key => key is not null && key.Media == input.Stamp.Owner.Media));

    private static PlaybackEffect? EvaluateRequest(PlaybackInput input, ImmutableArray<Selected> selected,
        PlaybackRequest request, ImmutableArray<PlaybackDiagnostic>.Builder diagnostics, bool execution = false)
    {
        PlaybackEffect? Reject(string reason)
        {
            diagnostics.Add(new(reason));
            return null;
        }

        if (request.Kind == SponsorRequestKind.Undo)
        {
            var applied = request.Applied;
            if (applied is null || applied.Owner != input.Stamp.Owner || applied.WasPreview
                || applied.Kind is not (SponsorEffectKind.SeekInterval or SponsorEffectKind.HoldMute)
                || !ValidRange(applied.From, applied.To) || applied.To > input.Actual.Duration!.Value
                || applied.Contributors.IsDefaultOrEmpty || applied.Contributors.Length > MaximumRecords)
                return Reject("InvalidUndoReceipt");
            var action = applied.Kind == SponsorEffectKind.HoldMute ? "mute" : "skip";
            if (applied.Contributors.Any(c => c is null || c.Key is null || c.Segment is null
                || c.Key.Media != input.Stamp.Owner.Media || c.Key.Uuid != c.Segment.Uuid
                || c.Key.Source != c.Segment.Source || c.Segment.Cid != input.Stamp.Owner.Media.Cid
                || c.Segment.ActionType != action || !SponsorRules.IsKnownSource(c.Segment.Source)
                || SponsorRules.ValidateShape(c.Segment, input.Actual.Duration) is not null
                || c.Segment.Segment[0] >= applied.To || c.Segment.Segment[1] <= applied.From))
                return Reject("InvalidUndoContributors");
            var coveredThrough = applied.From;
            foreach (var contributor in applied.Contributors.OrderBy(c => c.Segment.Segment[0]))
            {
                if (contributor.Segment.Segment[0] > coveredThrough) return Reject("DisjointUndoContributors");
                coveredThrough = Math.Max(coveredThrough, contributor.Segment.Segment[1]);
            }
            if (coveredThrough < applied.To) return Reject("UndoDestinationOutsideContributors");
            if (action == "skip" && !input.Capabilities.CanSeekExactly) return Reject("ExactSeekUnavailable");
            // Release is not an unmute value. Even if capability was lost, the bridge must retire its own lease.
            var review = new PlaybackProtection(request.Id, action, applied.Contributors.Min(c => c.Segment.Segment[0]),
                applied.Contributors.Max(c => c.Segment.Segment[1]), applied.Contributors.Select(c => c.Key).Distinct().ToImmutableArray());
            return new(action == "skip" ? SponsorEffectKind.SeekUndo : SponsorEffectKind.ReleaseMute,
                input.Actual.Position, applied.From, applied.Contributors, request.Id, review, []);
        }

        if (!Enum.IsDefined(request.Kind)) return Reject("UnknownExplicitRequest");
        var target = selected.FirstOrDefault(item => item.Contributor.Key == request.Target);
        if (target is null || target.Policy == CategoryPolicy.ShowOverlay) return Reject("TargetNotActionable");
        // Speed strategy never turns a failed automatic write into a seek. Only an explicitly labelled Skip now can seek.
        var skipNow = execution && request.Kind == SponsorRequestKind.SkipNow && target.Segment.ActionType == "skip";
        if (request.Kind == SponsorRequestKind.SkipNow && !skipNow) return Reject("SkipNowRequiresExecutionSkipTarget");
        var capability = skipNow ? (input.Capabilities.CanSeekExactly ? null : "ExactSeekUnavailable") : CapabilityReason(input, target);
        if (capability is not null) return Reject(capability);
        if (request.Kind == SponsorRequestKind.JumpToPoint)
        {
            if (target.Segment.ActionType != "poi") return Reject("TargetNotPoint");
            return new(SponsorEffectKind.SeekPoint, input.Actual.Position, target.Start, [target.Contributor], request.Id,
                ReleaseReviewsAfterApply: []);
        }
        if (target.Segment.ActionType is not ("skip" or "mute") || !Active(target, input.Actual.Position))
            return Reject("TargetNotCurrentInterval");
        // An explicit card targets exactly its segment; no implicit manual-policy overlap expansion.
        var release = input.Interaction.Reviews.Where(review => review.Action == target.Segment.ActionType
            && review.Start < target.End && target.Start < review.End).Select(review => review.Id).ToImmutableArray();
        return new(target.Segment.ActionType == "skip" ? SponsorEffectKind.SeekInterval : SponsorEffectKind.HoldMute,
            input.Actual.Position, target.End, [target.Contributor], request.Id, ReleaseReviewsAfterApply: release);
    }
}
