
namespace BiliCopilot.UI.SponsorBlock.DanmakuAid;

public static class DanmakuAidPolicy
{
    public static DanmakuAidDecision Evaluate(DanmakuAidInput input, DanmakuAidOptions options)
    {
        DanmakuAidDecision Empty(DanmakuAidStatus status, string reason) => new(status, reason, null, null, null, null, null);
        if (options.DisableSkipping || !options.EnableDanmakuSkip && !options.EnableClickableTimestamp)
            return Empty(DanmakuAidStatus.Disabled, "DanmakuAidsDisabled");
        var appearance = input.Appearance;
        var current = input.Current;
        if (appearance is null || current is null) return Empty(DanmakuAidStatus.Unknown, "MissingAppearanceOrCurrentContext");
        if (appearance.Surface != DanmakuSurface.VodDanmaku)
            return Empty(DanmakuAidStatus.Unsupported, "OnlyActualVodDanmakuSupported");
        if (!ValidStamp(appearance.MediaStamp) || !ValidStamp(current.MediaStamp)
            || !ValidId(appearance.CommentId) || !ValidId(appearance.Revision) || !ValidId(appearance.AppearanceId))
            return Empty(DanmakuAidStatus.Unknown, "MissingExactIdentityOrGeneration");
        if (!SameAppearance(appearance, current)) return Empty(DanmakuAidStatus.Rejected, "StaleMediaCommentRevisionOrAppearance");
        if (current.IsVisible != true)
            return Empty(current.IsVisible is null ? DanmakuAidStatus.Unknown : DanmakuAidStatus.Rejected, "NotKnownVisible");
        if (!DanmakuTimeParser.Bounded(appearance.AppearanceTimeSeconds, current.DurationSeconds)
            || !DanmakuTimeParser.Bounded(current.VirtualTimeSeconds, current.DurationSeconds))
            return Empty(DanmakuAidStatus.Unknown, "MissingOrInvalidActualTimesAndDuration");

        var parsed = DanmakuTimeParser.Parse(appearance.RawContent, current.VirtualTimeSeconds,
            current.DurationSeconds, options.OffsetPattern, options.RegexLimits);
        if (parsed.TargetSeconds is null)
            return new(parsed.Status switch
            {
                DanmakuTimeStatus.NoMatch => DanmakuAidStatus.NoMatch,
                DanmakuTimeStatus.Unsupported => DanmakuAidStatus.Unsupported,
                _ => DanmakuAidStatus.Unknown,
            }, parsed.Reason, parsed, null, null, null, null);
        var target = parsed.TargetSeconds.Value;
        var provenance = new DanmakuAidProvenance(appearance, current.VirtualTimeSeconds!.Value, current.DurationSeconds!.Value);
        var hint = options.EnableClickableTimestamp ? new DanmakuTimestampHint(provenance, target) : null;
        var result = new DanmakuAidDecision(DanmakuAidStatus.Available, "ClickableTimestampOnly", parsed, hint, null, null, null);
        if (!options.EnableDanmakuSkip) return result;
        if (current.IsPaused != false)
            return result with { Status = hint is null ? DanmakuAidStatus.Unknown : DanmakuAidStatus.Available,
                Reason = current.IsPaused == true ? "PausedNoSkipProposal" : "PlaybackStateUnknownNoSkipProposal" };
        if (appearance.IsCentered is null)
            return result with { Status = hint is null ? DanmakuAidStatus.Unknown : DanmakuAidStatus.Available, Reason = "CenteredStateUnknownNoSkipProposal" };

        var start = provenance.VirtualTimeSeconds + (appearance.IsCentered == true ? 0.3 : 1);
        if (!double.IsFinite(start) || target < start + 5 || target > provenance.DurationSeconds)
            return result with { Status = hint is null ? DanmakuAidStatus.Rejected : DanmakuAidStatus.Available, Reason = "SkipTargetBeforeMinimumOrOutsideDuration" };
        result = result with { ProposedStartForNearbyCheck = start };
        if (options.CheckNearbySegments)
        {
            var nearby = input.Nearby;
            if (nearby is null || nearby.MediaStamp != appearance.MediaStamp || nearby.ProposedStartSeconds != start
                || string.IsNullOrWhiteSpace(current.SegmentSnapshotRevision)
                || nearby.SegmentSnapshotRevision != current.SegmentSnapshotRevision
                || nearby.HasNearbySegment is null)
                return result with { Status = hint is null ? DanmakuAidStatus.Unknown : DanmakuAidStatus.Available, Reason = "NearbyEvidenceUnknownOrStale" };
            if (nearby.HasNearbySegment == true)
                return result with { Status = hint is null ? DanmakuAidStatus.Rejected : DanmakuAidStatus.Available, Reason = "NearbySegmentSuppressesProposal" };
        }
        var proposal = new DanmakuSkipProposal(provenance, start, target, options.EnableAuto);
        return result with
        {
            Reason = "LocalProposalRequiresN1AndCurrentOwnerAdmission",
            SkipProposal = proposal,
            SubmissionMenuIntent = options.EnableSubmissionMenu ? new DanmakuSubmissionMenuIntent(proposal) : null,
        };
    }

    /// <summary>Pure recheck for a later click; the real media owner still owns seeking and permission.
    /// This checks visibility, raw text, exact media/generation and occurrence, not just a timestamp.</summary>
    public static bool IsClickableHintCurrent(DanmakuTimestampHint hint, DanmakuCurrentContext current, DanmakuAidOptions options) =>
        hint is not null && current is not null && options is not null && options.EnableClickableTimestamp
        && !options.DisableSkipping && hint.Provenance is not null
        && hint.Provenance.Appearance is not null && hint.Provenance.Appearance.Surface == DanmakuSurface.VodDanmaku
        && ValidStamp(current.MediaStamp) && ValidStamp(hint.Provenance.Appearance.MediaStamp)
        && SameAppearance(hint.Provenance.Appearance, current) && current.IsVisible == true
        && DanmakuTimeParser.Bounded(hint.TargetSeconds, current.DurationSeconds);

    private static bool SameAppearance(DanmakuAppearance appearance, DanmakuCurrentContext current) =>
        appearance.MediaStamp == current.MediaStamp && appearance.CommentId == current.CommentId
        && appearance.Revision == current.Revision && appearance.AppearanceId == current.AppearanceId
        && appearance.RawContent is not null && appearance.RawContent == current.RawContent;

    private static bool ValidStamp(DanmakuMediaStamp stamp) => stamp is not null && stamp.Media is not null && stamp.Generation >= 0;
    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}
