using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public enum CategoryPolicy { Disabled, ShowOverlay, ManualSkip, AutoSkip }
public sealed record SponsorCategory(string Id, ImmutableArray<string> AllowedActions, CategoryPolicy DefaultPolicy);

/// <summary>The single app-owned action/category and default-policy authority. No player work occurs here.</summary>
public static class SponsorRules
{
    public static ImmutableArray<SponsorCategory> Categories { get; } =
    [
        new("sponsor", ["skip", "mute", "full"], CategoryPolicy.AutoSkip),
        new("selfpromo", ["skip", "mute", "full"], CategoryPolicy.ManualSkip),
        new("exclusive_access", ["full"], CategoryPolicy.ShowOverlay),
        new("interaction", ["skip", "mute"], CategoryPolicy.ManualSkip),
        new("poi_highlight", ["poi"], CategoryPolicy.ManualSkip),
        new("intro", ["skip", "mute"], CategoryPolicy.ManualSkip),
        new("outro", ["skip", "mute"], CategoryPolicy.ManualSkip),
        new("preview", ["skip", "mute"], CategoryPolicy.ShowOverlay),
        new("padding", ["skip"], CategoryPolicy.AutoSkip),
        new("filler", ["skip", "mute"], CategoryPolicy.Disabled),
        new("music_offtopic", ["skip"], CategoryPolicy.AutoSkip),
    ];

    public static bool Allows(string category, string action) =>
        Categories.Any(c => c.Id == category && c.AllowedActions.Contains(action));

    // Absent is server; 1 local, 2 YouTube, 3 danmaku. Unknown raw values remain displayable only.
    public static bool IsKnownSource(int? source) => source is null or 1 or 2 or 3;

    public static string? ValidateShape(SponsorSegment segment, double? duration = null)
    {
        if (!Allows(segment.Category, segment.ActionType)) return "UnknownOrUnsupportedCategoryAction";
        if (segment.Segment.IsDefault || segment.Segment.Length != 2) return "IncompleteOrUnsupportedTuple";
        var a = segment.Segment[0]; var b = segment.Segment[1];
        if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < a) return "InvalidTimeRange";
        if (duration is not null && (!double.IsFinite(duration.Value) || duration <= 0 || b > duration)) return "OutsideDuration";
        return segment.ActionType switch
        {
            "poi" when a != b => "PointMustHaveEqualEndpoints",
            "full" when a != 0 || b != 0 => "FullMustBeZeroLabel",
            "skip" or "mute" when a == b => "EmptyInterval",
            _ => null,
        };
    }

    public static bool CanExecuteInterval(SponsorSegment segment) =>
        segment.ActionType is "skip" or "mute" && ValidateShape(segment) is null
        && IsKnownSource(segment.Source);

    public static string? ValidateDraft(SegmentDraft draft, MediaIdentity media, double duration)
    {
        if (draft.Version != 1 || draft.MediaKey != media.Combined || string.IsNullOrWhiteSpace(draft.Revision)
            || draft.Segment is null || draft.Segment.Cid != media.Cid || string.IsNullOrWhiteSpace(draft.Segment.Uuid)
            || draft.Segment.Uuid.Length > 256 || !IsKnownSource(draft.Segment.Source)) return "InvalidDraftIdentityRevision";
        return ValidateShape(draft.Segment, duration);
    }
}

/// <summary>Canonical immutable query snapshot; unknown categories are not sent as an executable policy.</summary>
public sealed record SegmentQuery
{
    public SegmentQuery(MediaIdentity media, ImmutableArray<string> categories, bool includeMute = false, bool includeFull = false)
    {
        if (categories.IsDefault || categories.Any(c => !SponsorRules.Categories.Any(k => k.Id == c)))
            throw new ArgumentException("Unknown category", nameof(categories));
        Media = media;
        Categories = categories.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        Actions = (includeMute, includeFull) switch
        {
            (true, true) => ["skip", "poi", "mute", "full"],
            (true, false) => ["skip", "poi", "mute"],
            (false, true) => ["skip", "poi", "full"],
            _ => ["skip", "poi"],
        };
    }
    public MediaIdentity Media { get; }
    public ImmutableArray<string> Categories { get; }
    public ImmutableArray<string> Actions { get; }
    internal string CacheIdentity => Media.Combined + "|" + string.Join(',', Categories) + "|" + string.Join(',', Actions);
    public bool Includes(SponsorSegment segment) => segment.Cid == Media.Cid
        && Categories.Contains(segment.Category) && Actions.Contains(segment.ActionType)
        && SponsorRules.ValidateShape(segment) is null && SponsorRules.IsKnownSource(segment.Source);
}
