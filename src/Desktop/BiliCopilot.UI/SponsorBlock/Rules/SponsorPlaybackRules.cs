using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Rules;

/// <summary>Pure, conservative N1 evaluator. No clock, callbacks, player objects, I/O or hidden mutable state.</summary>
public static partial class SponsorPlaybackRules
{
    public const int MaximumRecords = 4096;

    private sealed record Selected(PlaybackContributor Contributor, CategoryPolicy Policy)
    {
        public SponsorSegment Segment => Contributor.Segment;
        public double Start => Segment.Segment[0];
        public double End => Segment.Segment[1];
    }

    private sealed record CommonEvaluation(PlaybackPlan Plan, ImmutableArray<Selected> Selected,
        ImmutableArray<Selected> AutomaticSkips, bool CanExecute);

    public static PlaybackPlan Evaluate(PlaybackInput input) => EvaluateCore(input, null).Plan;

    // One normalization/admission/request/undo seam. No fictional capabilities or cloned policy inputs.
    private static CommonEvaluation EvaluateCore(PlaybackInput input, PlaybackExecutionContext? execution)
    {
        var diagnostics = ImmutableArray.CreateBuilder<PlaybackDiagnostic>();
        var notices = ImmutableArray.CreateBuilder<PlaybackNotice>();
        var markers = ImmutableArray.CreateBuilder<PlaybackMarker>();
        var effects = ImmutableArray.CreateBuilder<PlaybackEffect>();
        ImmutableArray<Selected> selected = [];
        ImmutableArray<Selected> automaticSkips = [];
        var canExecute = false;
        CommonEvaluation Finish(bool current, PlaybackBoundary? boundary = null) => new(new(input.Stamp, current,
            effects.ToImmutable(), notices.ToImmutable(), markers.ToImmutable(), diagnostics.ToImmutable(), boundary, input.Channel),
            selected, automaticSkips, canExecute);

        if (input.Data.Owner != input.Stamp.Owner || input.Policy.Revision != input.Stamp.PolicyRevision
            || input.Interaction.Owner != input.Stamp.Owner || input.Interaction.VisitRevision != input.Stamp.VisitRevision)
        {
            diagnostics.Add(new("StaleMediaPolicyOrVisit"));
            return Finish(false);
        }

        if (input.Data.Segments.IsDefault || input.Data.Segments.Length > MaximumRecords
            || input.Interaction.Reviews.IsDefault || input.Interaction.Reviews.Length > MaximumRecords
            || input.Interaction.AdmittedSeeks.IsDefault || input.Interaction.AdmittedSeeks.Length > MaximumRecords
            || input.Interaction.AdmittedRequestIds.IsDefault || input.Interaction.AdmittedRequestIds.Length > MaximumRecords
            || !ValidInteraction(input) || !ValidNormalizationFacts(input))
        {
            diagnostics.Add(new("InvalidOrUnboundedSnapshot"));
            return Finish(true);
        }

        var actual = input.Actual;
        var validDuration = actual.Duration is double duration && double.IsFinite(duration) && duration > 0;
        var validPosition = double.IsFinite(actual.Position) && actual.Position >= 0
            && (!validDuration || actual.Position <= actual.Duration!.Value);
        var validRate = double.IsFinite(actual.Rate) && actual.Rate > 0;
        var reason = !input.Policy.Enabled ? "FeatureDisabled"
            : actual.MediaKind != SponsorMediaKind.Vod ? "VodIdentityNotEstablished"
            : !validDuration ? "DurationUnknownOrInvalid"
            : !validPosition ? "PositionInvalid"
            : !validRate ? "RateInvalid"
            : input.Policy.DisableSkipping ? "SkippingDisabled"
            : input.Channel.IsWhitelisted == true ? "ChannelWhitelisted"
            : input.Policy.WaitForChannelVerification && (input.Channel.Verification != SponsorChannelVerification.Verified
                || input.Channel.IsWhitelisted is null) ? "ChannelVerificationUnknown"
            : input.IgnorePlaybackForEditing || input.Preview is not null ? "PreviewOrEditorIsolation"
            : !Enum.IsDefined(actual.State) || actual.State is SponsorPlaybackState.Unknown or SponsorPlaybackState.Seeking or SponsorPlaybackState.Stopped
                ? "PlaybackStateNotAdmitted" : null;
        if (reason is not null) diagnostics.Add(new(reason));
        if (!ValidMinimum(input.Policy))
        {
            AddNormalizationDiagnostic(diagnostics, PlaybackNormalizationReason.InvalidMinimumDuration);
            reason ??= "InvalidMinimumDuration";
        }
        if (input.Channel.Verification != SponsorChannelVerification.Verified || input.Channel.IsWhitelisted is null)
            diagnostics.Add(new("ChannelMetadataUnknown"));
        if (input.Preview is { } preview && (preview.MediaKey != input.Stamp.Owner.Media.Combined
            || preview.NativeMediaGeneration != input.Stamp.Owner.Generation || string.IsNullOrWhiteSpace(preview.Revision)
            || !ValidRange(preview.Start, preview.End) || !validDuration || preview.End > actual.Duration!.Value))
            diagnostics.Add(new("InvalidOrStalePreviewStillIsolated"));

        selected = Select(input, validDuration ? actual.Duration : null, diagnostics, execution is not null);
        foreach (var item in selected)
        {
            markers.Add(new(item.Contributor, item.Policy));
            var contributors = ImmutableArray.Create(item.Contributor);
            var actionReason = reason ?? CapabilityReason(input, item, execution);
            if (item.Segment.ActionType == "full")
                notices.Add(new(SponsorNoticeKind.FullLabel, contributors, false));
            else if (item.Segment.ActionType == "poi")
                notices.Add(new(SponsorNoticeKind.PointNavigation, contributors,
                    item.Policy != CategoryPolicy.ShowOverlay && actionReason is null,
                    item.Policy == CategoryPolicy.ShowOverlay ? "OverlayOnly" : actionReason));
            else if (validPosition && Active(item, actual.Position) && item.Policy != CategoryPolicy.ShowOverlay)
            {
                var protectedReview = Protected(input, item);
                notices.Add(new(protectedReview ? SponsorNoticeKind.ReviewProtected
                    : item.Policy == CategoryPolicy.ManualSkip ? SponsorNoticeKind.ManualAction
                    : SponsorNoticeKind.AutomaticCandidate, contributors, actionReason is null, actionReason));
            }
            else if (reason is null && IsUpcoming(input, item))
                notices.Add(new(SponsorNoticeKind.Upcoming, contributors, false));
        }

        if (reason is not null) return Finish(true);
        canExecute = true;
        // Existing rate ownership can coexist with a mute-only explicit request. Selection remains shared.
        if (execution is not null && actual.State == SponsorPlaybackState.Playing)
            automaticSkips = selected.Where(item => item.Segment.ActionType == "skip" && Automatic(input, item, execution)).ToImmutableArray();
        if (input.Request is { } request)
        {
            if (request.Stamp != input.Stamp || string.IsNullOrWhiteSpace(request.Id))
                diagnostics.Add(new("StaleOrInvalidExplicitRequest"));
            else if (input.Interaction.AdmittedRequestIds.Contains(request.Id, StringComparer.Ordinal))
                diagnostics.Add(new("ExplicitRequestAlreadyAdmitted"));
            else
            {
                var explicitEffect = EvaluateRequest(input, selected, request, diagnostics, execution is not null);
                if (explicitEffect is not null) effects.Add(explicitEffect);
            }
            // Explicit intent has priority, even when rejected. Never replace a failed click with an auto seek.
            return Finish(true, effects.Any(e => e.Kind == SponsorEffectKind.HoldMute)
                && actual.State == SponsorPlaybackState.Playing ? NextBoundary(input, selected) : null);
        }

        if (actual.State != SponsorPlaybackState.Playing)
            return Finish(true);

        var eligible = selected.Where(item => Automatic(input, item, execution)).ToArray();
        automaticSkips = eligible.Where(item => item.Segment.ActionType == "skip").ToImmutableArray();
        var skip = execution is not null && input.Policy.SkipStrategy == SponsorSkipStrategy.SpeedUp
            ? ImmutableArray<Selected>.Empty : ConnectedAtPosition(automaticSkips, actual.Position);
        if (skip.Length > 0)
            effects.Add(new(SponsorEffectKind.SeekInterval, actual.Position, skip.Max(item => item.End),
                skip.Select(item => item.Contributor).ToImmutableArray()));
        else
        {
            var muteCandidates = selected.Where(item => item.Segment.ActionType == "mute"
                && (Automatic(input, item, execution) || ManualMute(input, item)));
            var mute = ConnectedAtPosition(muteCandidates, actual.Position);
            if (mute.Length > 0)
                effects.Add(new(SponsorEffectKind.HoldMute, actual.Position, mute.Max(item => item.End),
                    mute.Select(item => item.Contributor).ToImmutableArray()));
        }

        // Successful seek publication must cause re-evaluation. Do not schedule from the proposed destination.
        return Finish(true, effects.Any(e => e.Kind == SponsorEffectKind.SeekInterval) ? null : NextBoundary(input, selected));
    }

    private static ImmutableArray<Selected> Select(PlaybackInput input, double? duration,
        ImmutableArray<PlaybackDiagnostic>.Builder diagnostics, bool execution = false)
    {
        var unique = new Dictionary<PlaybackSegmentKey, SponsorSegment>();
        var conflicts = new HashSet<PlaybackSegmentKey>();
        foreach (var segment in input.Data.Segments)
        {
            if (segment is null)
            {
                diagnostics.Add(new("NullSegment"));
                continue;
            }
            var key = new PlaybackSegmentKey(input.Stamp.Owner.Media, segment.Uuid, segment.Source);
            if (unique.TryGetValue(key, out var previous) && !SameSegment(previous, segment)) conflicts.Add(key);
            else unique.TryAdd(key, segment);
        }

        var result = ImmutableArray.CreateBuilder<Selected>();
        foreach (var pair in unique.OrderBy(p => p.Key.Uuid, StringComparer.Ordinal).ThenBy(p => p.Key.Source ?? -1))
        {
            var segment = pair.Value;
            var contributor = new PlaybackContributor(pair.Key, segment);
            var error = conflicts.Contains(pair.Key) ? "ConflictingDuplicateIdentity"
                : segment.Cid != input.Stamp.Owner.Media.Cid ? "ForeignCid"
                : string.IsNullOrWhiteSpace(segment.Uuid) || segment.Uuid.Length > 256 ? "InvalidUuid"
                : !SponsorRules.IsKnownSource(segment.Source) ? "UnknownSource"
                : SponsorRules.ValidateShape(segment, duration);
            if (error is null && segment.VideoDuration is double declared)
                error = SponsorRules.ValidateShape(segment, declared);
            if (error is not null)
            {
                diagnostics.Add(new(error, contributor));
                continue;
            }
            var category = SponsorRules.Categories.Single(c => c.Id == segment.Category);
            var policy = input.Policy.Categories.TryGetValue(category.Id, out var configured) ? configured : category.DefaultPolicy;
            if (!Enum.IsDefined(policy))
            {
                diagnostics.Add(new("UnknownPolicy", contributor));
                continue;
            }
            result.Add(new(contributor, policy));
        }
        // Normalize before projections, explicit requests, auto contributor unions and boundary choices.
        return Normalize(input, result.ToImmutable(), diagnostics, execution).OrderBy(item => item.Start).ThenBy(item => item.End)
            .ThenBy(item => item.Contributor.Key.Uuid, StringComparer.Ordinal)
            .ThenBy(item => item.Contributor.Key.Source ?? -1).ToImmutableArray();
    }

    private static bool SameSegment(SponsorSegment a, SponsorSegment b) => a.Cid == b.Cid && a.Uuid == b.Uuid
        && a.Category == b.Category && a.ActionType == b.ActionType && a.Source == b.Source
        && a.Hidden == b.Hidden && Nullable.Equals(a.Locked, b.Locked) && Nullable.Equals(a.VideoDuration, b.VideoDuration)
        && (a.Segment.IsDefault || b.Segment.IsDefault ? a.Segment.IsDefault == b.Segment.IsDefault
            : a.Segment.SequenceEqual(b.Segment));

    private static bool Active(Selected item, double position) => item.Start <= position && position < item.End;
    private static bool ValidRange(double start, double end) => double.IsFinite(start) && double.IsFinite(end)
        && start >= 0 && start < end;

    private static string? CapabilityReason(PlaybackInput input, Selected item, PlaybackExecutionContext? execution = null) => item.Segment.ActionType switch
    {
        "skip" when execution is not null && input.Policy.SkipStrategy == SponsorSkipStrategy.SpeedUp => null,
        "skip" or "poi" when !input.Capabilities.CanSeekExactly => "ExactSeekUnavailable",
        "skip" when input.Policy.SkipStrategy != SponsorSkipStrategy.Seek => "SpeedUpPolicyNotInN1",
        "mute" when !input.Capabilities.CanOwnMute => "OwnedMuteUnavailable",
        _ => null,
    };

    private static bool Protected(PlaybackInput input, Selected item) => input.Interaction.Reviews.Any(review =>
        review.Action == item.Segment.ActionType && review.Start < item.End && item.Start < review.End);

    private static bool Automatic(PlaybackInput input, Selected item, PlaybackExecutionContext? execution = null) => item.Policy == CategoryPolicy.AutoSkip
        && item.Segment.ActionType is "skip" or "mute" && CapabilityReason(input, item, execution) is null
        && (execution is null || Source2AutomaticEligible(item, execution))
        && (item.Segment.Source != 3 || input.Policy.EnableAutomaticDanmaku)
        && (!input.Interaction.SeekSuppressed || input.Policy.SkipOnSeekToSegment)
        && !Protected(input, item)
        && (item.Segment.ActionType != "mute" || !input.Interaction.MuteSuppressed
            && (input.Interaction.ManualMute is null || input.Interaction.ManualMute.MuteUserEpoch == input.Stamp.MuteUserEpoch))
        && (item.Segment.ActionType != "skip" || !input.Interaction.AdmittedSeeks.Any(claim =>
            claim.Contributors.Contains(item.Contributor.Key)));

    private static bool ManualMute(PlaybackInput input, Selected item) => item.Policy is CategoryPolicy.ManualSkip or CategoryPolicy.AutoSkip
        && item.Segment.ActionType == "mute" && CapabilityReason(input, item) is null
        && !input.Interaction.MuteSuppressed && !Protected(input, item)
        && input.Interaction.ManualMute is { } intent && intent.MuteUserEpoch == input.Stamp.MuteUserEpoch
        && intent.Contributors.Contains(item.Contributor.Key);

    private static ImmutableArray<Selected> ConnectedAtPosition(IEnumerable<Selected> candidates, double position)
    {
        var ordered = candidates.OrderBy(item => item.Start).ThenBy(item => item.End).ToArray();
        var result = ImmutableArray.CreateBuilder<Selected>();
        var end = position;
        foreach (var item in ordered)
        {
            if (item.End <= position) continue;
            if (item.Start > end) break;
            result.Add(item);
            end = Math.Max(end, item.End);
        }
        return result.ToImmutable();
    }

    private static bool IsUpcoming(PlaybackInput input, Selected item) => item.Policy is CategoryPolicy.ManualSkip or CategoryPolicy.AutoSkip
        && item.Segment.ActionType is "skip" or "mute" && input.Policy.UpcomingMediaSeconds is double lead
        && double.IsFinite(lead) && lead > 0 && input.Actual.Position < item.Start && item.Start - input.Actual.Position <= lead;

    private static PlaybackBoundary? NextBoundary(PlaybackInput input, ImmutableArray<Selected> selected)
    {
        var next = double.PositiveInfinity;
        foreach (var item in selected.Where(item => item.Segment.ActionType is "skip" or "mute"))
        {
            if (item.Start > input.Actual.Position) next = Math.Min(next, item.Start);
            if (item.End > input.Actual.Position) next = Math.Min(next, item.End);
            if (item.Policy != CategoryPolicy.ShowOverlay && input.Policy.UpcomingMediaSeconds is double lead
                && double.IsFinite(lead) && lead > 0 && item.Start - lead > input.Actual.Position)
                next = Math.Min(next, item.Start - lead);
        }
        var delay = (next - input.Actual.Position) / input.Actual.Rate;
        return double.IsFinite(next) && double.IsFinite(delay) && delay > 0 ? new(next, delay) : null;
    }
}
