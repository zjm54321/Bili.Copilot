using System.Text.Json.Serialization;
using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.DanmakuAid;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

/// <summary>Optional, versioned complete option pair inside the unreleased schema-2 auxiliary object.
/// Null on SponsorAuxiliarySnapshot means absent, not a persisted default object. No runtime state.</summary>
public sealed record SponsorContentPreferences(
    [property: JsonRequired] int Version,
    [property: JsonRequired] PersistedContentOptions Content,
    [property: JsonRequired] PersistedDanmakuAidOptions DanmakuAid)
{
    // Single default authority: the frozen pure options, including corrected DynamicEnabled=true.
    public static SponsorContentPreferences Default { get; } = FromOptions(new(), new());

    /// <summary>Does not mutate the snapshot or persist an absent section.</summary>
    public static SponsorContentPreferences GetEffective(SponsorAuxiliarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ContentPreferences ?? Default;
    }

    public static SponsorContentPreferences FromOptions(ContentPromotionOptions content, DanmakuAidOptions danmakuAid) =>
        new(1, PersistedContentOptions.FromOptions(content), PersistedDanmakuAidOptions.FromOptions(danmakuAid));
}

/// <summary>Only serializable option values. Pure TextRegexLimits.IsValid is not persisted.</summary>
public sealed record PersistedTextRegexLimits(
    [property: JsonRequired] int MaximumPatternLength,
    [property: JsonRequired] int MaximumInputLength,
    [property: JsonRequired] int MaximumMatches,
    [property: JsonRequired] int TimeoutMilliseconds)
{
    public static PersistedTextRegexLimits FromOptions(TextRegexLimits options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.MaximumPatternLength, options.MaximumInputLength, options.MaximumMatches, options.TimeoutMilliseconds);
    }

    public TextRegexLimits ToOptions() => new()
    {
        MaximumPatternLength = MaximumPatternLength,
        MaximumInputLength = MaximumInputLength,
        MaximumMatches = MaximumMatches,
        TimeoutMilliseconds = TimeoutMilliseconds,
    };
}

public sealed record PersistedContentOptions(
    [property: JsonRequired] bool FeatureEnabled,
    [property: JsonRequired] bool DynamicEnabled,
    [property: JsonRequired] bool DynamicInAuthorSpaceEnabled,
    [property: JsonRequired] bool CommentEnabled,
    [property: JsonRequired] bool BlockReplies,
    [property: JsonRequired] bool IgnoreWhitelist,
    [property: JsonRequired] ContentCategoryAction GoodsAction,
    [property: JsonRequired] ContentCategoryAction ForwardedGoodsAction,
    [property: JsonRequired] ContentCategoryAction SuspicionAction,
    [property: JsonRequired] int SuspicionHideThreshold,
    [property: JsonRequired] string DynamicRegex,
    [property: JsonRequired] PersistedTextRegexLimits RegexLimits)
{
    public static PersistedContentOptions FromOptions(ContentPromotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.FeatureEnabled, options.DynamicEnabled, options.DynamicInAuthorSpaceEnabled,
            options.CommentEnabled, options.BlockReplies, options.IgnoreWhitelist, options.GoodsAction,
            options.ForwardedGoodsAction, options.SuspicionAction, options.SuspicionHideThreshold,
            options.DynamicRegex, PersistedTextRegexLimits.FromOptions(options.RegexLimits));
    }

    public ContentPromotionOptions ToOptions() => new()
    {
        FeatureEnabled = FeatureEnabled,
        DynamicEnabled = DynamicEnabled,
        DynamicInAuthorSpaceEnabled = DynamicInAuthorSpaceEnabled,
        CommentEnabled = CommentEnabled,
        BlockReplies = BlockReplies,
        IgnoreWhitelist = IgnoreWhitelist,
        GoodsAction = GoodsAction,
        ForwardedGoodsAction = ForwardedGoodsAction,
        SuspicionAction = SuspicionAction,
        SuspicionHideThreshold = SuspicionHideThreshold,
        DynamicRegex = DynamicRegex,
        RegexLimits = RegexLimits.ToOptions(),
    };
}

public sealed record PersistedDanmakuAidOptions(
    [property: JsonRequired] bool EnableDanmakuSkip,
    [property: JsonRequired] bool EnableAuto,
    [property: JsonRequired] bool EnableClickableTimestamp,
    [property: JsonRequired] bool EnableSubmissionMenu,
    [property: JsonRequired] bool CheckNearbySegments,
    [property: JsonRequired] bool DisableSkipping,
    [property: JsonRequired] string OffsetPattern,
    [property: JsonRequired] PersistedTextRegexLimits RegexLimits)
{
    public static PersistedDanmakuAidOptions FromOptions(DanmakuAidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.EnableDanmakuSkip, options.EnableAuto, options.EnableClickableTimestamp,
            options.EnableSubmissionMenu, options.CheckNearbySegments, options.DisableSkipping,
            options.OffsetPattern, PersistedTextRegexLimits.FromOptions(options.RegexLimits));
    }

    public DanmakuAidOptions ToOptions() => new()
    {
        EnableDanmakuSkip = EnableDanmakuSkip,
        EnableAuto = EnableAuto,
        EnableClickableTimestamp = EnableClickableTimestamp,
        EnableSubmissionMenu = EnableSubmissionMenu,
        CheckNearbySegments = CheckNearbySegments,
        DisableSkipping = DisableSkipping,
        OffsetPattern = OffsetPattern,
        RegexLimits = RegexLimits.ToOptions(),
    };
}
