using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.SponsorBlock.Content;

public enum TextRegexStatus { Success, MissingInput, InvalidPattern, Unsupported, LimitExceeded, TimedOut }
public sealed record TextRegexLimits
{
    public int MaximumPatternLength { get; init; } = 4096;
    public int MaximumInputLength { get; init; } = 32768;
    public int MaximumMatches { get; init; } = 256;
    // Both the per-match timeout and the whole-invocation elapsed budget. Not a wall-time guarantee.
    public int TimeoutMilliseconds { get; init; } = 25;

    public bool IsValid => MaximumPatternLength is > 0 and <= 4096
        && MaximumInputLength is > 0 and <= 32768 && MaximumMatches is > 0 and <= 256
        && TimeoutMilliseconds is > 0 and <= 100;
}

public sealed record TextRegexMatch(int Index, int Length, string Value, ImmutableArray<string?> Groups);
public sealed record TextRegexResult(TextRegexStatus Status, string Reason, bool Global, ImmutableArray<TextRegexMatch> Matches);

/// <summary>A deliberately bounded JS-compatible subset, not an arbitrary JS-to-.NET translator.
/// No cache, compilation, background work or user-pattern logging. Failure discards partial matches.</summary>
public static class BoundedTextRegex
{
    // ECMAScript whitespace, not .NET's broader Unicode whitespace class.
    private const string Space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    public static TextRegexResult Match(string? configuredPattern, string? input, TextRegexLimits limits,
        bool allowSlashFlags, bool rawGlobal = false, int minimumCaptureGroups = 0)
    {
        var invocation = Stopwatch.StartNew();
        TextRegexResult Fail(TextRegexStatus status, string reason) => new(status, reason, false, []);
        if (configuredPattern is null || input is null || limits is null)
            return Fail(TextRegexStatus.MissingInput, "MissingRegexInput");
        if (!limits.IsValid || configuredPattern.Length > limits.MaximumPatternLength
            || input.Length > limits.MaximumInputLength || minimumCaptureGroups is < 0 or > 32)
            return Fail(TextRegexStatus.LimitExceeded, "RegexLimits");

        bool BudgetExhausted() => invocation.Elapsed.TotalMilliseconds >= limits.TimeoutMilliseconds;
        TextRegexResult BudgetFailure() => Fail(TextRegexStatus.TimedOut, "RegexInvocationBudgetExceeded");
        if (BudgetExhausted()) return BudgetFailure();

        var pattern = configuredPattern;
        var flags = "";
        if (allowSlashFlags && pattern.StartsWith('/'))
        {
            var end = pattern.LastIndexOf('/');
            if (end == 0) return Fail(TextRegexStatus.InvalidPattern, "UnclosedSlashPattern");
            flags = pattern[(end + 1)..];
            pattern = pattern[1..end];
        }

        var global = rawGlobal;
        var insensitive = false;
        var multiline = false;
        var singleline = false;
        var seen = "";
        foreach (var flag in flags)
        {
            if (seen.Contains(flag)) return Fail(TextRegexStatus.InvalidPattern, "DuplicateRegexFlag");
            seen += flag;
            switch (flag)
            {
                case 'g': global = true; break;
                case 'i': insensitive = true; break;
                case 'm': multiline = true; break;
                case 's': singleline = true; break;
                default: return Fail(TextRegexStatus.Unsupported, "UnsupportedRegexFlag");
            }
        }
        if (BudgetExhausted()) return BudgetFailure();
        if (insensitive && (HasNonAsciiCase(pattern) || HasNonAsciiCase(input)))
            return Fail(TextRegexStatus.Unsupported, "UnicodeCaseFoldingNotEstablished");
        if (BudgetExhausted()) return BudgetFailure();
        if (!TryLower(pattern, insensitive, multiline, singleline, out var lowered))
            return Fail(TextRegexStatus.Unsupported, "OutsidePortableRegexSubset");
        if (BudgetExhausted()) return BudgetFailure();

        try
        {
            // Backtracking with a real timeout is intentional: the pinned default needs (?!1).
            var regex = new Regex(lowered, RegexOptions.CultureInvariant
                | (insensitive ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds));
            if (BudgetExhausted()) return BudgetFailure();
            var groupCount = regex.GetGroupNumbers().Length - 1;
            if (BudgetExhausted()) return BudgetFailure();
            if (groupCount > 32) return Fail(TextRegexStatus.LimitExceeded, "RegexCaptureCountLimit");
            if (groupCount < minimumCaptureGroups)
                return Fail(TextRegexStatus.Unsupported, "MissingRequiredCaptureGroups");
            var matches = ImmutableArray.CreateBuilder<TextRegexMatch>();
            // Inline monotonic checks only; no timer or autonomous polling. One in-flight regex
            // operation may outlive the remaining budget until its own timeout/check returns.
            if (BudgetExhausted()) return BudgetFailure();
            var match = regex.Match(input);
            if (BudgetExhausted()) return BudgetFailure();
            while (match.Success)
            {
                if (matches.Count == limits.MaximumMatches)
                    return Fail(TextRegexStatus.LimitExceeded, "RegexMatchCountLimit");
                var groups = ImmutableArray.CreateBuilder<string?>(match.Groups.Count);
                foreach (Group group in match.Groups) groups.Add(group.Success ? group.Value : null);
                matches.Add(new(match.Index, match.Length, match.Value, groups.ToImmutable()));
                if (BudgetExhausted()) return BudgetFailure();
                if (!global) break;
                if (BudgetExhausted()) return BudgetFailure();
                match = match.NextMatch();
                if (BudgetExhausted()) return BudgetFailure();
            }
            if (BudgetExhausted()) return BudgetFailure();
            var completed = matches.ToImmutable();
            if (BudgetExhausted()) return BudgetFailure();
            return new(TextRegexStatus.Success, "MatchedOrKnownNoMatch", global, completed);
        }
        catch (RegexMatchTimeoutException)
        {
            return BudgetExhausted() ? BudgetFailure() : Fail(TextRegexStatus.TimedOut, "RegexTimeout");
        }
        catch (ArgumentException)
        {
            return BudgetExhausted() ? BudgetFailure() : Fail(TextRegexStatus.InvalidPattern, "InvalidRegexPattern");
        }
    }

    private static bool HasNonAsciiCase(string value)
    {
        foreach (var rune in value.EnumerateRunes())
            if (rune.Value > 127 && Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter) return true;
        return false;
    }

    private static bool TryLower(string pattern, bool insensitive, bool multiline, bool singleline, out string lowered)
    {
        lowered = "";
        var result = new StringBuilder();
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (++i == pattern.Length) return false;
                var escaped = pattern[i];
                var characters = escaped switch { 'd' or 'D' => "0-9", 'w' or 'W' => "A-Za-z0-9_", 's' or 'S' => Space, _ => null };
                if (characters is not null)
                {
                    var inverse = char.IsUpper(escaped);
                    if (inClass && inverse) return false;
                    result.Append(inClass ? characters : "[" + (inverse ? "^" : "") + characters + "]");
                }
                else if (escaped is 'x' or 'u')
                {
                    var count = escaped == 'x' ? 2 : 4;
                    if (i + count >= pattern.Length) return false;
                    var hex = pattern.Substring(i + 1, count);
                    if (!ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                        || char.IsSurrogate((char)code) || insensitive && HasNonAsciiCase(((char)code).ToString())) return false;
                    result.Append('\\').Append(escaped).Append(hex);
                    i += count;
                }
                else if (escaped == 'b' && inClass) result.Append(@"\u0008");
                else if (escaped is 'b' or 'B')
                {
                    if (inClass) return false;
                    result.Append(escaped == 'b'
                        ? @"(?:(?<![A-Za-z0-9_])(?=[A-Za-z0-9_])|(?<=[A-Za-z0-9_])(?![A-Za-z0-9_]))"
                        : @"(?:(?<=[A-Za-z0-9_])(?=[A-Za-z0-9_])|(?<![A-Za-z0-9_])(?![A-Za-z0-9_]))");
                }
                else if (escaped is 'n' or 'r' or 't' or 'f' or 'v') result.Append('\\').Append(escaped);
                else if (escaped is '\\' or '/' or '.' or '*' or '+' or '?' or '|' or '{' or '}' or '(' or ')' or '[' or ']' or '^' or '$' or '-')
                    result.Append(escaped == '-' && inClass ? @"\-" : Regex.Escape(escaped.ToString()));
                else return false; // Backrefs, properties, named refs, octal and identity escapes are not portable here.
            }
            else if (c == '[')
            {
                if (inClass || i + 1 < pattern.Length && pattern[i + 1] == ']'
                    || i + 2 < pattern.Length && pattern[i + 1] == '^' && pattern[i + 2] == ']') return false;
                inClass = true;
                result.Append(c);
            }
            else if (c == ']')
            {
                if (!inClass) return false;
                inClass = false;
                result.Append(c);
            }
            else if (!inClass && c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
            {
                if (i + 2 >= pattern.Length || pattern[i + 2] is not (':' or '=' or '!')) return false;
                result.Append(c);
            }
            else if (!inClass && c == '.') result.Append(singleline ? @"[\s\S]" : @"[^\r\n\u2028\u2029]");
            else if (!inClass && c == '^') result.Append(multiline ? @"(?:\A|(?<=[\r\n\u2028\u2029]))" : @"\A");
            else if (!inClass && c == '$') result.Append(multiline ? @"(?=[\r\n\u2028\u2029]|\z)" : @"\z");
            else result.Append(c);
        }
        if (inClass) return false;
        lowered = result.ToString();
        return true;
    }
}
