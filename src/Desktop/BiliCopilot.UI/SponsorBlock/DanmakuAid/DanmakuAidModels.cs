using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.DanmakuAid;

public enum DanmakuSurface { Unknown, VodDanmaku, LiveChat, PrivateMessage }
public enum DanmakuTimeStatus { Absolute, Relative, NoMatch, Unknown, Unsupported }
public enum DanmakuAidStatus { Available, Disabled, NoMatch, Unknown, Unsupported, Rejected }

public sealed record DanmakuMediaStamp(MediaIdentity Media, long Generation);

/// <summary>Actual rendered VOD danmaku occurrence. AppearanceId is supplied by the lifecycle owner;
/// it is not a fabricated Sponsor UUID. Appearance time and current virtual time are distinct facts.</summary>
public sealed record DanmakuAppearance(
    DanmakuSurface Surface,
    DanmakuMediaStamp MediaStamp,
    string CommentId,
    string Revision,
    string AppearanceId,
    string? RawContent,
    double? AppearanceTimeSeconds,
    bool? IsCentered);

public sealed record DanmakuCurrentContext(
    DanmakuMediaStamp MediaStamp,
    string CommentId,
    string Revision,
    string AppearanceId,
    string? RawContent,
    bool? IsVisible,
    bool? IsPaused,
    double? VirtualTimeSeconds,
    double? DurationSeconds,
    string? SegmentSnapshotRevision);

/// <summary>Caller evaluates the existing nearby predicate for exactly this media and proposed start.
/// Missing/unknown evidence is not a known-clear result. No interval predicate is invented here.</summary>
public sealed record DanmakuNearbyEvidence(DanmakuMediaStamp MediaStamp, string SegmentSnapshotRevision,
    double ProposedStartSeconds, bool? HasNearbySegment);
public sealed record DanmakuAidInput(DanmakuAppearance Appearance, DanmakuCurrentContext Current, DanmakuNearbyEvidence? Nearby);

public sealed record DanmakuAidOptions
{
    public bool EnableDanmakuSkip { get; init; }
    public bool EnableAuto { get; init; }
    public bool EnableClickableTimestamp { get; init; }
    public bool EnableSubmissionMenu { get; init; }
    public bool CheckNearbySegments { get; init; } = true;
    public bool DisableSkipping { get; init; }
    // Raw regex, NOT /pattern/flags. Captures 1/2/3 = direction/number/suffix.
    public string OffsetPattern { get; init; } = DanmakuAidDefaults.OffsetPattern;
    public TextRegexLimits RegexLimits { get; init; } = new();
}

public static class DanmakuAidDefaults
{
    public const string OffsetPattern = @"(?:^|(右|右滑|按|右下|右向|右方向|→|⇒|⇢|⇨|⮕|🡆|🠺|🠾|🢒|👉))(\d+)(下|次)?";
}

public sealed record DanmakuTimeResult(
    DanmakuTimeStatus Status,
    double? TargetSeconds,
    double? OffsetSeconds,
    string Reason,
    TextRegexStatus? RegexStatus);

public sealed record DanmakuAidProvenance(DanmakuAppearance Appearance, double VirtualTimeSeconds, double DurationSeconds);
public sealed record DanmakuTimestampHint(DanmakuAidProvenance Provenance, double TargetSeconds);

/// <summary>A local candidate, never an executable receipt. N1 and the current playback owner must
/// admit it again. No UUID or server segment is synthesized, and ForceAuto is not final eligibility.</summary>
public sealed record DanmakuSkipProposal(
    DanmakuAidProvenance Provenance,
    double StartSeconds,
    double TargetSeconds,
    bool ForceAuto)
{
    public string Category => "sponsor";
    public string Action => "skip";
    public int Source => 3;
    public bool RequiresFinalEligibility => true;
}

/// <summary>Local intent available only after the owner admits the proposal. Never auto-submit or
/// mutate a draft queue here; the UI/editor owner handles any subsequent explicit user action.</summary>
public sealed record DanmakuSubmissionMenuIntent(DanmakuSkipProposal Proposal)
{
    public bool LocalOnly => true;
    public bool AutoSubmit => false;
}

public sealed record DanmakuAidDecision(
    DanmakuAidStatus Status,
    string Reason,
    DanmakuTimeResult? ParsedTime,
    DanmakuTimestampHint? ClickableHint,
    DanmakuSkipProposal? SkipProposal,
    DanmakuSubmissionMenuIntent? SubmissionMenuIntent,
    double? ProposedStartForNearbyCheck);
