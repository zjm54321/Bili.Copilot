using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal enum SponsorContentSurface { VideoCard, Danmaku, PrivateMessage }
internal enum SponsorFilterDecision { PassThrough, HideByExplicitPreference }
internal sealed record SponsorFilterOptions(bool Enabled = false, ImmutableHashSet<string>? Categories = null);
internal sealed record SponsorFilterFacts(MediaIdentity Media, long Revision, ReadStatus Status,
    ImmutableArray<SponsorSegment> Segments, ImmutableHashSet<PlaybackSegmentKey>? AlignedEligibleSource2 = null,
    ImmutableHashSet<PlaybackSegmentKey>? LocalExcluded = null);
internal sealed record SponsorReadOnlyScope(ImmutableArray<MediaIdentity> Media, bool FullLabelsOnly);
internal sealed record SponsorFilterResult(SponsorFilterDecision Decision, ImmutableArray<PlaybackSegmentKey> Evidence);

/// <summary>Pure bounded projection only. Caller retains original page counts/cursors, and recycling
/// must supply the current exact identity + revision. No private-message filtering, text inference,
/// hidden fetch loop or per-item API call is performed here.</summary>
internal static class SponsorFilterProjection
{
    internal static SponsorReadOnlyScope PlanScope(ImmutableArray<MediaIdentity> visibleMedia, bool networkOptIn)
    {
        if (!networkOptIn || visibleMedia.IsDefault || visibleMedia.Length > 128) return new([], true);
        return new(visibleMedia.Where(m => m is not null).Distinct().Take(16).ToImmutableArray(), true);
    }
    internal static SponsorFilterResult Decide(SponsorContentSurface surface, MediaIdentity currentMedia, long currentRevision,
        SponsorFilterFacts? facts, SponsorFilterOptions options, double? danmakuMediaTime = null)
    {
        static SponsorFilterResult Pass() => new(SponsorFilterDecision.PassThrough, []);
        if (!Enum.IsDefined(surface) || surface == SponsorContentSurface.PrivateMessage || !options.Enabled
            || options.Categories is null || options.Categories.Count > SponsorRules.Categories.Length
            || options.Categories.Any(c => !SponsorRules.Categories.Any(k => k.Id == c))
            || facts is null || facts.Media != currentMedia || facts.Revision != currentRevision
            || facts.Status is not (ReadStatus.Success or ReadStatus.Empty200 or ReadStatus.Empty404)
            || facts.Segments.IsDefault || facts.Segments.Length > 4096) return Pass();
        if (surface == SponsorContentSurface.Danmaku && (danmakuMediaTime is not double t || !double.IsFinite(t) || t < 0)) return Pass();
        var matching = facts.Segments.Where(s => s.Cid == currentMedia.Cid && options.Categories.Contains(s.Category)
            && SponsorRules.ValidateShape(s) is null && SponsorRules.IsKnownSource(s.Source) && s.Hidden is null or 0)
            .Where(s => s.Source != 2 || facts.AlignedEligibleSource2?.Contains(new(currentMedia, s.Uuid, s.Source)) == true)
            .Where(s => facts.LocalExcluded?.Contains(new(currentMedia, s.Uuid, s.Source)) != true)
            .Where(s => surface == SponsorContentSurface.VideoCard ? s.ActionType == "full"
                : SponsorRules.CanExecuteInterval(s) && s.Segment[0] <= danmakuMediaTime && danmakuMediaTime < s.Segment[1])
            .Select(s => new PlaybackSegmentKey(currentMedia, s.Uuid, s.Source)).Distinct().ToImmutableArray();
        return matching.IsEmpty ? Pass() : new(SponsorFilterDecision.HideByExplicitPreference, matching);
    }
}
