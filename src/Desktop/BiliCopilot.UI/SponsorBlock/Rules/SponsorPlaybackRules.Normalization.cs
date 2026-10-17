using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public static partial class SponsorPlaybackRules
{
    private static bool ValidMinimum(PlaybackPolicy policy) => double.IsFinite(policy.MinDuration) && policy.MinDuration >= 0;

    private static bool ValidNormalizationFacts(PlaybackInput input)
    {
        bool Valid(ImmutableHashSet<PlaybackSegmentKey> keys) => keys is not null && keys.Count <= MaximumRecords
            && keys.All(key => key is not null && key.Media == input.Stamp.Owner.Media
                && !string.IsNullOrWhiteSpace(key.Uuid) && key.Uuid.Length <= 256 && SponsorRules.IsKnownSource(key.Source));
        return Valid(input.LocalHidden) && Valid(input.LocalDownvoted)
            && Valid(input.ExternalSourceExcluded) && Valid(input.DraftSegments);
    }

    private static void AddNormalizationDiagnostic(ImmutableArray<PlaybackDiagnostic>.Builder diagnostics,
        PlaybackNormalizationReason reason, PlaybackContributor? contributor = null)
        => diagnostics.Add(new(reason == PlaybackNormalizationReason.SpeedUpUnsupported ? "SpeedUpPolicyNotInN1" : reason.ToString(), contributor)
        { NormalizationReason = reason });

    private static bool BelowMinimum(PlaybackInput input, Selected item)
    {
        // POI [t,t] and full [0,0] never participate in interval-duration policy.
        if (item.Segment.ActionType is not ("skip" or "mute") || input.DraftSegments.Contains(item.Contributor.Key)) return false;
        var duration = item.End - item.Start;
        return double.IsFinite(duration) && duration > 0 && input.Policy.MinDuration > 0 && duration < input.Policy.MinDuration;
    }

    private static bool CanSupplyFact(PlaybackInput input, Selected item) => input.Policy.Enabled
        && item.Policy != CategoryPolicy.Disabled && ValidMinimum(input.Policy)
        && (item.Segment.ActionType != "full" || input.Policy.IncludeFull)
        && (item.Segment.ActionType != "mute" || input.Policy.IncludeMute)
        && !input.DraftSegments.Contains(item.Contributor.Key) && !BelowMinimum(input, item)
        && item.Segment.Hidden is null && !input.LocalHidden.Contains(item.Contributor.Key)
        && !input.LocalDownvoted.Contains(item.Contributor.Key) && !input.ExternalSourceExcluded.Contains(item.Contributor.Key);

    private static ImmutableArray<Selected> Normalize(PlaybackInput input, ImmutableArray<Selected> validated,
        ImmutableArray<PlaybackDiagnostic>.Builder diagnostics, bool execution = false)
    {
        // Only validated, same-owner/CID, known-category/action, non-conflicting records reach here.
        // Full labels retain their raw contributors; the set is a local derived fact, never a category-map mutation.
        var fullCategories = validated.Where(item => item.Segment.ActionType == "full" && CanSupplyFact(input, item))
            .Select(item => item.Segment.Category).ToImmutableHashSet(StringComparer.Ordinal);
        var musicEvidence = validated.Any(item => item.Segment.Category == "music_offtopic" && CanSupplyFact(input, item));
        bool? music = input.KnownMusic == true || musicEvidence ? true : input.KnownMusic;
        if (input.Policy.AutoSkipOnMusicVideos && music is null)
            AddNormalizationDiagnostic(diagnostics, PlaybackNormalizationReason.MusicStatusUnknown);

        var result = ImmutableArray.CreateBuilder<Selected>();
        foreach (var item in validated)
        {
            var policy = item.Policy; // already resolved from the sealed table + explicit category override
            void Exclude(PlaybackNormalizationReason reason)
            {
                policy = CategoryPolicy.Disabled;
                AddNormalizationDiagnostic(diagnostics, reason, item.Contributor);
            }

            // Chosen RULES-engine precedence, not the legacy scheduler's different full/music order.
            if (input.Policy.AutoSkipOnMusicVideos && music == true && item.Segment.ActionType == "skip"
                && policy != CategoryPolicy.Disabled)
            {
                policy = CategoryPolicy.AutoSkip;
                AddNormalizationDiagnostic(diagnostics, PlaybackNormalizationReason.MusicSkipOverride, item.Contributor);
            }
            if (input.Policy.ManualSkipOnFullVideo && policy == CategoryPolicy.AutoSkip
                && item.Segment.ActionType is "skip" or "mute" && fullCategories.Contains(item.Segment.Category))
            {
                policy = CategoryPolicy.ManualSkip;
                AddNormalizationDiagnostic(diagnostics, PlaybackNormalizationReason.FullVideoManualDemotion, item.Contributor);
            }
            if (BelowMinimum(input, item)) Exclude(PlaybackNormalizationReason.BelowConfiguredMinimum);
            if (item.Segment.ActionType == "mute" && !input.Policy.IncludeMute) Exclude(PlaybackNormalizationReason.MuteDisabled);
            if (input.LocalHidden.Contains(item.Contributor.Key)) Exclude(PlaybackNormalizationReason.LocallyHidden);
            if (input.LocalDownvoted.Contains(item.Contributor.Key)) Exclude(PlaybackNormalizationReason.LocallyDownvoted);
            if (input.ExternalSourceExcluded.Contains(item.Contributor.Key)) Exclude(PlaybackNormalizationReason.ExternalSourceExcluded);
            // Opaque wire values are NOT local enum states. Preserve even 0/1/2/3 verbatim and exclude conservatively.
            if (item.Segment.Hidden is not null) Exclude(PlaybackNormalizationReason.UnresolvedWireHiddenFlag);
            if (input.DraftSegments.Contains(item.Contributor.Key)) Exclude(PlaybackNormalizationReason.DraftIsolation);
            if (!execution && item.Segment.ActionType == "skip" && input.Policy.SkipStrategy == SponsorSkipStrategy.SpeedUp)
                AddNormalizationDiagnostic(diagnostics, PlaybackNormalizationReason.SpeedUpUnsupported, item.Contributor);

            // Invalid configuration is fail-closed, not repaired into a made-up duration. Drafts remain isolated.
            if (!ValidMinimum(input.Policy) || !input.Policy.Enabled || policy == CategoryPolicy.Disabled
                || item.Segment.ActionType == "full" && !input.Policy.IncludeFull) continue;
            result.Add(item with { Policy = policy });
        }
        return result.ToImmutable();
    }
}
