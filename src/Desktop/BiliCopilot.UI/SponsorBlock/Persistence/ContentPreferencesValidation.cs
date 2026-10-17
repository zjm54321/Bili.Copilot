using System.Text.Json;
using BiliCopilot.UI.SponsorBlock.Content;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

/// <summary>Storage shape/range checks only: never constructs or executes a regex. The explicit future
/// UI action owns pure-validator feedback about regex syntax/semantics before committing user edits.</summary>
internal static class ContentPreferencesValidation
{
    internal static bool HasShape(JsonElement value) => PersistenceJson.HasFields(value, "Version", "Content", "DanmakuAid")
        && PersistenceJson.HasFields(value.GetProperty("Content"), "FeatureEnabled", "DynamicEnabled", "DynamicInAuthorSpaceEnabled",
            "CommentEnabled", "BlockReplies", "IgnoreWhitelist", "GoodsAction", "ForwardedGoodsAction", "SuspicionAction",
            "SuspicionHideThreshold", "DynamicRegex", "RegexLimits")
        && LimitsShape(value.GetProperty("Content").GetProperty("RegexLimits"))
        && PersistenceJson.HasFields(value.GetProperty("DanmakuAid"), "EnableDanmakuSkip", "EnableAuto", "EnableClickableTimestamp",
            "EnableSubmissionMenu", "CheckNearbySegments", "DisableSkipping", "OffsetPattern", "RegexLimits")
        && LimitsShape(value.GetProperty("DanmakuAid").GetProperty("RegexLimits"));

    private static bool LimitsShape(JsonElement value) => PersistenceJson.HasFields(value,
        "MaximumPatternLength", "MaximumInputLength", "MaximumMatches", "TimeoutMilliseconds");

    internal static bool IsValid(SponsorContentPreferences? value) => value is not null && value.Version == 1
        && ValidContent(value.Content) && ValidDanmaku(value.DanmakuAid);

    private static bool ValidContent(PersistedContentOptions? value) => value is not null && ValidLimits(value.RegexLimits)
        && ValidAction(value.GoodsAction) && ValidAction(value.ForwardedGoodsAction) && ValidAction(value.SuspicionAction)
        && value.SuspicionHideThreshold >= 0 && BoundedPattern(value.DynamicRegex, value.RegexLimits.MaximumPatternLength)
        && KnownDynamicFlags(value.DynamicRegex);

    private static bool ValidDanmaku(PersistedDanmakuAidOptions? value) => value is not null && ValidLimits(value.RegexLimits)
        && BoundedPattern(value.OffsetPattern, value.RegexLimits.MaximumPatternLength);

    private static bool ValidAction(ContentCategoryAction action) => action is
        ContentCategoryAction.Disabled or ContentCategoryAction.Mark or ContentCategoryAction.Hide;

    private static bool ValidLimits(PersistedTextRegexLimits? value) => value is not null
        && value.MaximumPatternLength is >= 1 and <= 4096 && value.MaximumInputLength is >= 1 and <= 32768
        && value.MaximumMatches is >= 1 and <= 256 && value.TimeoutMilliseconds is >= 1 and <= 100;

    private static bool BoundedPattern(string? value, int maximumLength) => value is not null
        && value.Length <= 4096 && value.Length <= maximumLength;

    // Dynamic accepts either raw text or /pattern/flags. Check the wrapper/known flags without
    // interpreting its regex body. OffsetPattern has no slash/flags interpretation at all.
    private static bool KnownDynamicFlags(string value)
    {
        if (!value.StartsWith('/')) return true;
        var closingSlash = value.LastIndexOf('/');
        if (closingSlash == 0) return false;
        var seen = 0;
        for (var i = closingSlash + 1; i < value.Length; i++)
        {
            var flag = value[i] switch { 'g' => 1, 'i' => 2, 'm' => 4, 's' => 8, _ => 0 };
            if (flag == 0 || (seen & flag) != 0) return false;
            seen |= flag;
        }
        return true;
    }
}
