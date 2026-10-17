using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public enum SpeedUpFallback { None, SeekShortOrNearEnd }
public sealed record PlaybackSpeedPolicy(double ConfiguredRate = 2, SpeedUpFallback Fallback = SpeedUpFallback.None);
public enum PlaybackLeasePhase { Pending, SucceededUnobserved, Confirmed, Failed, Unknown }
public enum PlaybackLeaseOrigin { Automatic, Manual }
public enum PlaybackLeaseField { Mute, Rate }
public enum RetirementReason
{
    CoverageEnded, Ineligible, Paused, UserSeek, UserIntent, ExternalChange, Disabled,
    Whitelisted, Preview, Replacement, Replay, Closing, StrategyChanged, InvalidSnapshot,
}
public enum RestorationPhase { NotStarted, Pending, SucceededUnobserved, Confirmed, Failed, Unknown }
public sealed record PlaybackLeaseRetirement(RetirementReason Reason, double MonotonicSeconds);
public sealed record PlaybackLeaseRestoration(RestorationPhase Phase, string OperationId);

/// <summary>Proposed/owned coverage, never evidence that future contributors have played.</summary>
public sealed record PlaybackCoverage(double Start, double End, ImmutableArray<PlaybackContributor> Contributors);

/// <summary>
/// Controller snapshots one admission, not one observation. Baseline/target/token never change during a lease.
/// Manual RequestId and VisitRevision identify pending manual mute without forging Interaction.ManualMute.
/// Failed/Unknown leases and claims survive progress, cooldown and ordinary visit changes until reconciled.
/// </summary>
public sealed record PlaybackLeaseSnapshot<T>(PlaybackOwner Owner, string Token, long UserEpoch,
    long VisitRevision, T Baseline, T Target, PlaybackCoverage Coverage, PlaybackLeasePhase Phase,
    PlaybackLeaseOrigin Origin = PlaybackLeaseOrigin.Automatic, string? RequestId = null,
    PlaybackLeaseRetirement? Retirement = null, PlaybackLeaseRestoration? Restoration = null);

public sealed record PlaybackRateClaim(PlaybackOwner Owner, string Token, PlaybackCoverage Coverage,
    PlaybackLeasePhase Phase);
/// <summary>Exact media/UUID/source key; expiry is CancelledAtMonotonicSeconds + 5, not media time.</summary>
public sealed record PlaybackRateCooldown(PlaybackSegmentKey Key, double Start, double End,
    double CancelledAtMonotonicSeconds);
/// <summary>Caller-proven alignment AND eligibility in the current stamp. Never infer from source=2 alone.</summary>
public sealed record PlaybackSource2Facts(PlaybackStamp Stamp, ImmutableHashSet<PlaybackSegmentKey> AlignedEligibleKeys);

/// <summary>Values only. The caller supplies monotonic seconds from one controller time origin; no clock is read here.</summary>
public sealed record PlaybackExecutionContext(PlaybackStamp Stamp, double MonotonicSeconds)
{
    public PlaybackSpeedPolicy Speed { get; init; } = new();
    public bool CanOwnRate { get; init; }
    public PlaybackLeaseSnapshot<double>? RateLease { get; init; }
    public PlaybackLeaseSnapshot<bool>? MuteLease { get; init; }
    public ImmutableArray<PlaybackRateClaim> RateClaims { get; init; } = [];
    public ImmutableArray<PlaybackRateCooldown> RateCooldowns { get; init; } = [];
    public PlaybackSource2Facts? Source2 { get; init; }
}

public enum PlaybackExecutionValidity { Current, StaleContext, InvalidContext }
public enum PlaybackRateDecision
{
    None, Desired, Unavailable, NoBenefit, ShortOrNearEnd, Cooldown, AlreadyClaimed,
    AwaitingReconciliation, Retiring, ExplicitRequest, Source2Unavailable,
}
/// <summary>Desired coverage. Null LeaseToken means a new admission proposal, NOT permission to write on every Evaluate.</summary>
public sealed record PlaybackRateDesire(PlaybackOwner Owner, string? LeaseToken, long UserEpoch,
    double Baseline, double Target, PlaybackCoverage Coverage);
/// <summary>Only Pending/SucceededUnobserved manual leases. Retain the existing operation, never issue another setter/receipt.</summary>
public sealed record PlaybackPendingMuteDesire(PlaybackOwner Owner, string LeaseToken, long UserEpoch,
    string RequestId, PlaybackCoverage Coverage, PlaybackLeasePhase Phase);
public enum LeaseRetirementDisposition { ConditionalRestore, AwaitCompletion, ReconcileFailure, RelinquishToUser, AlreadyRestored }
/// <summary>Controller must drain, reread and check owner/token/field epoch before any conditional restore.</summary>
public sealed record PlaybackRetirementDesire(PlaybackOwner Owner, string LeaseToken, PlaybackLeaseField Field,
    long LeaseUserEpoch, RetirementReason Reason, LeaseRetirementDisposition Disposition);

// A discriminated wake: a wall expiry has no fictitious media position. Delay is advisory, always reread on wake.
public abstract record ExecutionWake(double DelaySeconds);
public sealed record MediaExecutionWake(double Position, double DelaySeconds) : ExecutionWake(DelaySeconds);
public sealed record MonotonicExecutionWake(double DueMonotonicSeconds, double DelaySeconds) : ExecutionWake(DelaySeconds);

/// <summary>ExecutionWake alone is authoritative; OrdinaryPlan.NextBoundary is cleared in this wrapper.</summary>
public sealed record PlaybackExecutionPlan(PlaybackPlan OrdinaryPlan, PlaybackExecutionValidity Validity,
    PlaybackRateDesire? RateDesire, PlaybackRateDecision RateDecision, ExecutionWake? ExecutionWake,
    PlaybackPendingMuteDesire? PendingManualMute,
    ImmutableArray<PlaybackRetirementDesire> Retirements);

/// <summary>Controller-only evidence after native success AND observed coverage; never emitted by this evaluator.</summary>
public sealed record PlaybackConfirmedRateCoverage(double MediaStart, double MediaEnd,
    double MonotonicStart, double MonotonicEnd, ImmutableArray<PlaybackContributor> Contributors);
public sealed record PlaybackRateReceipt(PlaybackOwner Owner, string LeaseToken, long UserEpoch,
    double Baseline, double Target, ImmutableArray<PlaybackConfirmedRateCoverage> ObservedCoverage);
