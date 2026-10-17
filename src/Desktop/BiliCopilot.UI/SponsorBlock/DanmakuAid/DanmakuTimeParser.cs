using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Content;

namespace BiliCopilot.UI.SponsorBlock.DanmakuAid;

public static class DanmakuTimeParser
{
    // Grammar literal from the pinned src/utils/danmakusUtils.ts:35-63, not a DOM port.
    private const string AbsolutePattern = @"(?:(\d{1,2})\s*(?:(小时|h|H)|(:|：|；|;|\.|-|—))\s*)?(?:(\d{1,2})\s*(分钟|分|:|：|；|;|\.|-|—|m|M)\s*)?(?:(\d{1,2}|整)\s*(秒|s|S)?)";

    /// <summary>Absolute first. An absolute target outside duration is rejected, never replaced by an
    /// unrelated relative match. All values are seconds on this exact selected media's timeline.</summary>
    public static DanmakuTimeResult Parse(string? rawContent, double? actualTimeSeconds, double? durationSeconds,
        string? offsetPattern, TextRegexLimits limits)
    {
        DanmakuTimeResult Unknown(string reason, TextRegexStatus? regex = null) => new(DanmakuTimeStatus.Unknown, null, null, reason, regex);
        if (rawContent is null || limits is null || !limits.IsValid)
            return Unknown("MissingContentOrInvalidLimits");
        if (rawContent.Length > limits.MaximumInputLength) return Unknown("DanmakuTextLimit", TextRegexStatus.LimitExceeded);
        if (!Bounded(actualTimeSeconds, durationSeconds)) return Unknown("UnknownOrInvalidTimeAndDuration");
        if (!TryConvertChinese(rawContent, out var text))
            return new(DanmakuTimeStatus.Unsupported, null, null, "UnsupportedChineseNumeralOrOverflow", null);
        if (text.Length > limits.MaximumInputLength) return Unknown("NormalizedTextLimit", TextRegexStatus.LimitExceeded);

        var absolute = BoundedTextRegex.Match(AbsolutePattern, text, limits, allowSlashFlags: false, rawGlobal: true);
        if (absolute.Status != TextRegexStatus.Success) return RegexFailure(absolute);
        foreach (var match in absolute.Matches)
        {
            var groups = match.Groups;
            var hours = groups[1];
            var minutes = groups[4];
            var seconds = groups[6];
            if (seconds is null || groups[7] is null && minutes is null && hours is null) continue;
            var hourValue = Number(hours);
            var minuteValue = Number(minutes);
            var secondValue = seconds == "整" ? 0 : Number(seconds);
            // A two-component separator form is minutes:seconds; an explicit h/小时 remains hours.
            if (hours is not null && groups[2] is null && minutes is null && groups[5] is null)
            {
                minuteValue = hourValue;
                hourValue = 0;
            }
            var target = hourValue * 3600d + minuteValue * 60d + secondValue;
            return InDuration(target, durationSeconds!.Value)
                ? new(DanmakuTimeStatus.Absolute, target, null, "AbsoluteTime", absolute.Status)
                : Unknown("AbsoluteTargetOutsideDuration", absolute.Status);
        }

        var relative = BoundedTextRegex.Match(offsetPattern, text, limits, allowSlashFlags: false, rawGlobal: true, minimumCaptureGroups: 3);
        if (relative.Status != TextRegexStatus.Success) return RegexFailure(relative);
        foreach (var match in relative.Matches)
        {
            var groups = match.Groups;
            if (string.IsNullOrEmpty(groups[2]) || string.IsNullOrEmpty(groups[1]) && string.IsNullOrEmpty(groups[3])) continue;
            // Do not silently mimic JS parseInt's partial-prefix acceptance for custom captures.
            if (!TryAsciiNumber(groups[2]!, out var count))
                return new(DanmakuTimeStatus.Unsupported, null, null, "OffsetCaptureMustBeAsciiUnsignedInteger", relative.Status);
            var offset = count * 5d;
            if (offset == 0) return new(DanmakuTimeStatus.NoMatch, null, null, "ZeroOffsetNotATarget", relative.Status);
            var target = actualTimeSeconds!.Value + offset;
            return InDuration(target, durationSeconds!.Value)
                ? new(DanmakuTimeStatus.Relative, target, offset, "RelativeTime", relative.Status)
                : Unknown("RelativeTargetOutsideDuration", relative.Status);
        }
        return new(DanmakuTimeStatus.NoMatch, null, null, "NoTimeExpression", relative.Status);
    }

    private static DanmakuTimeResult RegexFailure(TextRegexResult result) => new(
        result.Status == TextRegexStatus.Unsupported ? DanmakuTimeStatus.Unsupported : DanmakuTimeStatus.Unknown,
        null, null, result.Reason, result.Status);

    private static int Number(string? value) => value is null ? 0 : int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool TryAsciiNumber(string text, out ulong number)
    {
        number = 0;
        if (text.Length == 0 || text.Length > 20) return false;
        foreach (var c in text) if (c is < '0' or > '9') return false;
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    internal static bool Bounded(double? time, double? duration) => time.HasValue && duration.HasValue
        && double.IsFinite(time.Value) && double.IsFinite(duration.Value)
        && duration.Value >= 0 && time.Value >= 0 && time.Value <= duration.Value;

    private static bool InDuration(double time, double duration) => double.IsFinite(time) && time >= 0 && time <= duration;

    private static int Digit(char c) => c switch
    {
        '零' => 0, '一' or '壹' => 1, '二' or '两' or '贰' => 2, '三' or '叁' => 3,
        '四' or '肆' => 4, '五' or '伍' => 5, '六' or '陆' => 6,
        '七' or '柒' => 7, '八' or '捌' => 8, '九' or '玖' => 9, _ => -1,
    };

    private static bool IsNumeral(char c) => Digit(c) >= 0 || c is '十' or '拾' or '百' or '千' or '万';

    private static bool TryConvertChinese(string input, out string text)
    {
        text = "";
        var output = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length;)
        {
            if (!IsNumeral(input[i])) { output.Append(input[i++]); continue; }
            long value = 0;
            while (i < input.Length && IsNumeral(input[i]))
            {
                var c = input[i++];
                // Deliberately no 百/千/万 grammar and no source-style erasure of unsupported tokens.
                if (c is '百' or '千' or '万') return false;
                value = c is '十' or '拾' ? (value == 0 ? 1 : value) * 10 : value + Digit(c);
                if (value > int.MaxValue) return false;
            }
            output.Append(value.ToString(CultureInfo.InvariantCulture));
        }
        text = output.ToString();
        return true;
    }
}
