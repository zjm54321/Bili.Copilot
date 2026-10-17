using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Application;

// Captured on the actual player dispatcher. MediaVersion identifies the loaded danmaku pool;
// Owner is read again at use time and is never inferred from that unrelated version counter.
internal sealed record SponsorDanmakuSource(MediaIdentity Media, long MediaVersion, PlaybackOwner? Owner);
internal readonly record struct SponsorDanmakuInterval(double Start, double End);

internal sealed partial class SponsorApplicationCoordinator
{
    private SponsorFilterFacts? PublishedFilterFacts(PlayerState state)
    {
        if (state.Result?.Owner is not { } owner) return null;
        var local = settings.FactsFor(owner.Media);
        // Same real inputs as CurrentFilterFactsAsync, captured within the existing APP publication.
        return new(owner.Media, revision, state.ReadStatus, state.Raw, state.Aligned,
            local.Hidden.Union(local.Downvoted).Union(local.ExternalSourceExcluded));
    }
}

internal static class SponsorDanmakuCategoryProjection
{
    internal static ImmutableArray<SponsorDanmakuInterval> Build(SponsorApplicationView view,
        Guid attachment, SponsorDanmakuSource? source)
    {
        if (source?.Owner is not { } owner || owner.Media != source.Media || view.Closed
            || view.StoreVersion is null || view.Persistence is not (PersistenceStatus.Success or PersistenceStatus.Missing)
            || view.AuxiliaryPersistence is not (null or PersistenceStatus.Success)
            || !view.Auxiliary.Filters.DanmakuEnabled || view.Auxiliary.Filters.DanmakuCategories.IsDefaultOrEmpty)
            return [];
        var player = view.Players.FirstOrDefault(p => p.Attachment == attachment);
        if (player is not { Attached: true, CleanupPending: false, DeliveryStatus: ApplicationStatus.Success }
            || player.Controller?.Owner != owner || player.FilterFacts is not { } facts
            || facts.Media != source.Media || facts.Revision != view.Revision
            || facts.Segments.IsDefault || facts.Segments.Length > 4096)
            return [];

        var options = view.DanmakuFilter;
        var ranges = new List<SponsorDanmakuInterval>();
        foreach (var segment in facts.Segments)
        {
            if (segment is null || !SponsorRules.CanExecuteInterval(segment)) continue;
            // Reuse the existing full eligibility rule, including wire Hidden, local exclusions,
            // category validation and actual Source2 alignment. No independent category classifier.
            var decision = SponsorFilterProjection.Decide(SponsorContentSurface.Danmaku, source.Media,
                view.Revision, facts with { Segments = [segment] }, options, segment.Segment[0]);
            if (decision.Decision == SponsorFilterDecision.HideByExplicitPreference)
                ranges.Add(new(segment.Segment[0], segment.Segment[1]));
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = ImmutableArray.CreateBuilder<SponsorDanmakuInterval>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || merged[^1].End < range.Start) merged.Add(range);
            else merged[^1] = new(merged[^1].Start, Math.Max(merged[^1].End, range.End));
        }
        return merged.ToImmutable();
    }

    internal static bool Contains(ImmutableArray<SponsorDanmakuInterval> ranges, double mediaTime)
    {
        if (!double.IsFinite(mediaTime) || mediaTime < 0 || ranges.IsDefaultOrEmpty) return false;
        var low = 0;
        var high = ranges.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var range = ranges[middle];
            if (mediaTime < range.Start) high = middle - 1;
            else if (mediaTime >= range.End) low = middle + 1;
            else return true; // The existing rule is [start, end), not a VOD seek or wall clock.
        }
        return false;
    }
}
