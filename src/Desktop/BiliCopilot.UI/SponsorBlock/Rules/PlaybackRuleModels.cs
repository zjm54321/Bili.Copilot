using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public enum SponsorPlaybackState { Unknown, Playing, Paused, Waiting, Seeking, Stopped }
public enum SponsorMediaKind { Unknown, Vod, Live }
public enum SponsorChannelVerification { Unknown, Fetching, Verified }
public enum SponsorSkipStrategy { Seek, SpeedUp }
public enum SponsorEffectKind { SeekInterval, SeekPoint, SeekUndo, HoldMute, ReleaseMute }
public enum SponsorRequestKind { Skip, JumpToPoint, ReSkip, Undo, SkipNow }
public enum SponsorNoticeKind { ManualAction, AutomaticCandidate, Upcoming, PointNavigation, FullLabel, ReviewProtected }
public enum PlaybackNormalizationReason
{
    InvalidMinimumDuration, MusicStatusUnknown, MusicSkipOverride, FullVideoManualDemotion,
    BelowConfiguredMinimum, MuteDisabled, LocallyHidden, LocallyDownvoted, ExternalSourceExcluded,
    UnresolvedWireHiddenFlag, DraftIsolation, SpeedUpUnsupported,
}

public sealed record PlaybackOwner(MediaIdentity Media, long Generation);

/// <summary>Observation changes invalidate plans, not the visit's admitted-operation ledger.</summary>
public sealed record PlaybackStamp(PlaybackOwner Owner, long PolicyRevision, long ObservationRevision,
    long VisitRevision, long MuteUserEpoch, long RateUserEpoch);

public sealed record PlaybackObservation(double Position, double? Duration, double Rate,
    SponsorPlaybackState State, SponsorMediaKind MediaKind);

public sealed record PlaybackCapabilities(bool CanSeekExactly = false, bool CanOwnMute = false);
public sealed record PlaybackChannel(SponsorChannelVerification Verification = SponsorChannelVerification.Unknown,
    bool? IsWhitelisted = null);
public sealed record PlaybackSegmentSet(PlaybackOwner Owner, ImmutableArray<SponsorSegment> Segments);

/// <summary>Only the sealed table supplies category defaults. Normalization is opt-in and per segment/action.</summary>
public sealed record PlaybackPolicy(long Revision)
{
    public bool Enabled { get; init; }
    public bool DisableSkipping { get; init; }
    public bool IncludeMute { get; init; }
    public bool IncludeFull { get; init; }
    public double MinDuration { get; init; }
    public bool AutoSkipOnMusicVideos { get; init; }
    public bool ManualSkipOnFullVideo { get; init; }
    public bool WaitForChannelVerification { get; init; }
    public bool SkipOnSeekToSegment { get; init; }
    public SponsorSkipStrategy SkipStrategy { get; init; } = SponsorSkipStrategy.Seek;
    public bool EnableAutomaticDanmaku { get; init; }
    public double? UpcomingMediaSeconds { get; init; }
    public ImmutableDictionary<string, CategoryPolicy> Categories { get; init; } =
        ImmutableDictionary<string, CategoryPolicy>.Empty;
}

/// <summary>Source is part of identity: local, server, YouTube and danmaku UUIDs are never conflated.</summary>
public sealed record PlaybackSegmentKey(MediaIdentity Media, string Uuid, int? Source);
public sealed record PlaybackContributor(PlaybackSegmentKey Key, SponsorSegment Segment);

/// <summary>Same-action overlapping auto work is barred until the owner explicitly releases this review.</summary>
public sealed record PlaybackProtection(string Id, string Action, double Start, double End,
    ImmutableArray<PlaybackSegmentKey> Contributors);

/// <summary>Admitted seeks, including pending/unknown completion, prevent replay on delayed position publication.</summary>
public sealed record PlaybackClaim(ImmutableArray<PlaybackSegmentKey> Contributors);
public sealed record PlaybackMuteIntent(long MuteUserEpoch, ImmutableArray<PlaybackSegmentKey> Contributors);

/// <summary>
/// The bridge keeps one visit across ordinary progress/policy updates. A real seek/new entry advances it.
/// SeekSuppressed persists for the visit; do not clear it on the next position notification.
/// Reviews survive visit/policy changes in the same media owner, until explicit release.
/// Set MuteSuppressed at admitted user mute intent; keep it through ordinary progress in that visit.
/// Latch ManualMute after explicit HoldMute succeeds; clear on expiry/new visit/disable or user override.
/// </summary>
public sealed record PlaybackInteraction(PlaybackOwner Owner, long VisitRevision)
{
    public bool SeekSuppressed { get; init; }
    public bool MuteSuppressed { get; init; }
    public PlaybackMuteIntent? ManualMute { get; init; }
    public ImmutableArray<PlaybackProtection> Reviews { get; init; } = [];
    public ImmutableArray<PlaybackClaim> AdmittedSeeks { get; init; } = [];
    public ImmutableArray<string> AdmittedRequestIds { get; init; } = [];
}

/// <summary>Only the native owner may create this after observing successful execution, never from a proposal.</summary>
public sealed record PlaybackAppliedEffect(PlaybackOwner Owner, SponsorEffectKind Kind, double From, double To,
    ImmutableArray<PlaybackContributor> Contributors, bool WasPreview = false);

/// <summary>Snapshot of an explicit local playback action. This is not a network-write intent.</summary>
public sealed record PlaybackRequest(string Id, PlaybackStamp Stamp, SponsorRequestKind Kind,
    PlaybackSegmentKey? Target = null, PlaybackAppliedEffect? Applied = null);

public sealed record PlaybackInput(PlaybackStamp Stamp, PlaybackObservation Actual, PlaybackSegmentSet Data,
    PlaybackPolicy Policy, PlaybackCapabilities Capabilities, PlaybackChannel Channel, PlaybackInteraction Interaction)
{
    public ImmutableHashSet<PlaybackSegmentKey> LocalHidden { get; init; } = ImmutableHashSet<PlaybackSegmentKey>.Empty;
    // Facts belong to this owner/stamp's snapshot; advance ObservationRevision when any fact changes.
    // Null is unknown, never inferred from text/title. Eligible music_offtopic records can supply positive evidence.
    public bool? KnownMusic { get; init; }
    public ImmutableHashSet<PlaybackSegmentKey> LocalDownvoted { get; init; } = ImmutableHashSet<PlaybackSegmentKey>.Empty;
    // Explicit caller fact only. Source=2 does NOT imply exclusion or an inferred alignment/offset.
    public ImmutableHashSet<PlaybackSegmentKey> ExternalSourceExcluded { get; init; } = ImmutableHashSet<PlaybackSegmentKey>.Empty;
    // Draft is explicit, not inferred from Source=1. Exempting duration never makes a draft actionable.
    public ImmutableHashSet<PlaybackSegmentKey> DraftSegments { get; init; } = ImmutableHashSet<PlaybackSegmentKey>.Empty;
    public bool IgnorePlaybackForEditing { get; init; }
    public PreviewInterval? Preview { get; init; }
    public PlaybackRequest? Request { get; init; }
}

/// <summary>
/// Declarative only. HoldMute is desired lease coverage, not a repeated setter call.
/// Absence of HoldMute on a current plan revokes that desire; restoration is conditional, bridge-owned.
/// Review/release proposals take effect only on confirmed native success. Never count/report proposals.
/// </summary>
public sealed record PlaybackEffect(SponsorEffectKind Kind, double From, double To,
    ImmutableArray<PlaybackContributor> Contributors, string? RequestId = null,
    PlaybackProtection? ReviewAfterApply = null, ImmutableArray<string> ReleaseReviewsAfterApply = default);

public sealed record PlaybackMarker(PlaybackContributor Contributor, CategoryPolicy Policy);
public sealed record PlaybackNotice(SponsorNoticeKind Kind, ImmutableArray<PlaybackContributor> Contributors,
    bool CanAct, string? Reason = null);
public sealed record PlaybackDiagnostic(string Reason, PlaybackContributor? Contributor = null)
{
    public PlaybackNormalizationReason? NormalizationReason { get; init; }
}

/// <summary>One media-time boundary; delay is advisory. Re-read actual time/state and the stamp at wakeup.</summary>
public sealed record PlaybackBoundary(double Position, double DelaySeconds);
/// <summary>IsCurrent only compares snapshot ownership/revisions, not native liveness. Invalid current input revokes mute desire.</summary>
public sealed record PlaybackPlan(PlaybackStamp Stamp, bool IsCurrent, ImmutableArray<PlaybackEffect> Effects,
    ImmutableArray<PlaybackNotice> Notices, ImmutableArray<PlaybackMarker> Markers,
    ImmutableArray<PlaybackDiagnostic> Diagnostics, PlaybackBoundary? NextBoundary, PlaybackChannel Channel);
