using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Rules;

public static partial class SponsorPlaybackRules
{
    public static PlaybackExecutionPlan EvaluateExecution(PlaybackInput input, PlaybackExecutionContext context)
    {
        if (context.Stamp != input.Stamp) return RejectedExecution(input, PlaybackExecutionValidity.StaleContext);
        if (!ValidExecutionContext(input, context)) return RejectedExecution(input, PlaybackExecutionValidity.InvalidContext);

        var common = EvaluateCore(input, context);
        var plan = common.Plan;
        if (!plan.IsCurrent)
            return new(plan with { NextBoundary = null }, PlaybackExecutionValidity.StaleContext,
                null, PlaybackRateDecision.None, null, null, []);

        PlaybackRateDesire? rate = null;
        PlaybackPendingMuteDesire? pendingMute = null;
        var decision = PlaybackRateDecision.None;
        var retirements = ImmutableArray.CreateBuilder<PlaybackRetirementDesire>();
        ExecutionWake? wake = plan.NextBoundary is { } boundary ? new MediaExecutionWake(boundary.Position, boundary.DelaySeconds) : null;
        var playing = common.CanExecute && input.Actual.State == SponsorPlaybackState.Playing;
        var seek = plan.Effects.Any(IsSeek);
        var releaseMute = plan.Effects.Any(e => e.Kind == SponsorEffectKind.ReleaseMute);
        var rateRequestBlocks = input.Request is not null
            && !plan.Effects.Any(e => e.Kind is SponsorEffectKind.HoldMute or SponsorEffectKind.ReleaseMute);

        void Retire<T>(PlaybackLeaseSnapshot<T> owned, PlaybackLeaseField field, RetirementReason reason, bool external = false)
        {
            var epoch = field == PlaybackLeaseField.Rate ? input.Stamp.RateUserEpoch : input.Stamp.MuteUserEpoch;
            var disposition = owned.UserEpoch != epoch || external ? LeaseRetirementDisposition.RelinquishToUser
                : owned.Restoration?.Phase == RestorationPhase.Confirmed ? LeaseRetirementDisposition.AlreadyRestored
                : owned.Phase is PlaybackLeasePhase.Pending or PlaybackLeasePhase.SucceededUnobserved or PlaybackLeasePhase.Unknown
                    || owned.Restoration?.Phase is RestorationPhase.Pending or RestorationPhase.SucceededUnobserved or RestorationPhase.Unknown
                    ? LeaseRetirementDisposition.AwaitCompletion
                : owned.Phase == PlaybackLeasePhase.Failed || owned.Restoration?.Phase == RestorationPhase.Failed
                    ? LeaseRetirementDisposition.ReconcileFailure : LeaseRetirementDisposition.ConditionalRestore;
            retirements.Add(new(owned.Owner, owned.Token, field, owned.UserEpoch, reason, disposition));
        }

        if (context.MuteLease is { } mute)
        {
            // The controller keeps an admitted manual request in the lease, not in successful ManualMute.
            var pending = mute.Origin == PlaybackLeaseOrigin.Manual
                && mute.Phase is PlaybackLeasePhase.Pending or PlaybackLeasePhase.SucceededUnobserved;
            var retained = playing && !seek && !releaseMute && mute.UserEpoch == input.Stamp.MuteUserEpoch
                && mute.VisitRevision == input.Stamp.VisitRevision && mute.Retirement is null && mute.Restoration is null
                && input.Capabilities.CanOwnMute;
            if (pending && retained)
            {
                var candidates = common.Selected.Where(item => item.Segment.ActionType == "mute"
                    && item.Policy is CategoryPolicy.AutoSkip or CategoryPolicy.ManualSkip
                    && mute.Coverage.Contributors.Any(c => c.Key == item.Contributor.Key));
                var coverage = ConnectedAtPosition(candidates, input.Actual.Position);
                if (coverage.Length > 0)
                    pendingMute = new(mute.Owner, mute.Token, mute.UserEpoch, mute.RequestId!, Coverage(coverage), mute.Phase);
                else retained = false;
            }
            else if (pending) retained = false;
            else retained &= plan.Effects.Any(e => e.Kind == SponsorEffectKind.HoldMute);

            if (mute.Phase is PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown) retained = false;
            if (!retained) Retire(mute, PlaybackLeaseField.Mute, mute.UserEpoch != input.Stamp.MuteUserEpoch
                ? RetirementReason.UserIntent : RetirementCause(input, mute.VisitRevision, seek));
            // Retain pending desire distinctly, and never let progress replay a failed or revoked mute operation.
            if (pending || !retained)
                plan = plan with { Effects = plan.Effects.Where(e => e.Kind != SponsorEffectKind.HoldMute).ToImmutableArray() };
        }

        var lease = context.RateLease;
        if (lease is not null && (lease.UserEpoch != input.Stamp.RateUserEpoch
            || lease.VisitRevision != input.Stamp.VisitRevision || !playing || seek || rateRequestBlocks
            || input.Policy.SkipStrategy != SponsorSkipStrategy.SpeedUp || !context.CanOwnRate
            || lease.Retirement is not null || lease.Restoration is not null))
        {
            Retire(lease, PlaybackLeaseField.Rate, lease.UserEpoch != input.Stamp.RateUserEpoch ? RetirementReason.UserIntent
                : input.Policy.SkipStrategy != SponsorSkipStrategy.SpeedUp ? RetirementReason.StrategyChanged
                : RetirementCause(input, lease.VisitRevision, seek));
            decision = PlaybackRateDecision.Retiring;
        }
        else if (lease is not null && lease.Phase is PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown)
        {
            Retire(lease, PlaybackLeaseField.Rate, RetirementReason.Ineligible);
            decision = PlaybackRateDecision.AwaitingReconciliation;
        }
        else if (lease is not null && lease.Phase == PlaybackLeasePhase.Confirmed
            && (input.Actual.Rate < lease.Target - .12 || input.Actual.Rate > lease.Target + .12))
        {
            // Only attributed epochs or a CONFIRMED lease's deviation revoke ownership. Pending target echoes do not.
            Retire(lease, PlaybackLeaseField.Rate, RetirementReason.ExternalChange, external: true);
            decision = PlaybackRateDecision.Retiring;
        }
        else if (input.Request is not null && (lease is null || rateRequestBlocks)) decision = PlaybackRateDecision.ExplicitRequest;
        else if (playing && input.Policy.SkipStrategy == SponsorSkipStrategy.SpeedUp)
        {
            if (!context.CanOwnRate) decision = PlaybackRateDecision.Unavailable;
            else
            {
                var candidates = ImmutableArray.CreateBuilder<Selected>();
                foreach (var item in common.AutomaticSkips)
                {
                    if (context.RateClaims.Any(claim => claim.Token != lease?.Token
                        && claim.Coverage.Contributors.Any(c => c.Key == item.Contributor.Key)))
                    {
                        if (Active(item, input.Actual.Position)) decision = PlaybackRateDecision.AlreadyClaimed;
                        continue;
                    }
                    var cooldown = context.RateCooldowns.Where(c => c.Key == item.Contributor.Key
                        && input.Actual.Position >= c.Start - .3 && input.Actual.Position < c.End + .5
                        && context.MonotonicSeconds < c.CancelledAtMonotonicSeconds + 5).ToArray();
                    if (cooldown.Length > 0)
                    {
                        decision = PlaybackRateDecision.Cooldown;
                        foreach (var c in cooldown)
                        {
                            var due = c.CancelledAtMonotonicSeconds + 5;
                            wake = Earlier(wake, new MonotonicExecutionWake(due, due - context.MonotonicSeconds));
                        }
                        continue;
                    }
                    candidates.Add(item);
                }

                // Filter INDIVIDUAL segments before connected union; never launder short segments through a long neighbor.
                var speed = ConnectedAtPosition(candidates.Where(item => item.End - item.Start >= .5
                    && (lease is not null || !Active(item, input.Actual.Position) || input.Actual.Position < item.End - .05)), input.Actual.Position);
                if (speed.Length > 0)
                {
                    var baseline = lease?.Baseline ?? input.Actual.Rate;
                    var configured = double.IsFinite(context.Speed.ConfiguredRate) ? Math.Clamp(context.Speed.ConfiguredRate, 1.1, 16) : 2;
                    var target = lease?.Target ?? Math.Min(16, Math.Abs(baseline - configured) <= .05
                        ? baseline + configured : Math.Max(baseline, configured));
                    if (target <= baseline) decision = PlaybackRateDecision.NoBenefit;
                    else if (lease is not null && (lease.Coverage.End < speed.Min(item => item.Start)
                        || lease.Coverage.Start > speed.Max(item => item.End)))
                    {
                        Retire(lease, PlaybackLeaseField.Rate, RetirementReason.CoverageEnded);
                        decision = PlaybackRateDecision.Retiring;
                    }
                    else
                    {
                        var coverage = Coverage(speed);
                        if (lease is not null) coverage = ExtendCoverage(lease.Coverage, coverage, input.Actual.Position);
                        if (coverage.Contributors.Length > MaximumRecords || !IsConnectedCoverage(coverage))
                        {
                            if (lease is not null) Retire(lease, PlaybackLeaseField.Rate, RetirementReason.InvalidSnapshot);
                            decision = PlaybackRateDecision.Retiring;
                        }
                        else
                        {
                            rate = new(input.Stamp.Owner, lease?.Token, input.Stamp.RateUserEpoch, baseline, target, coverage);
                            decision = PlaybackRateDecision.Desired;
                            var delay = (coverage.End - input.Actual.Position) / input.Actual.Rate;
                            wake = Earlier(wake, new MediaExecutionWake(coverage.End, delay));
                        }
                    }
                }
                else if (lease is not null)
                {
                    Retire(lease, PlaybackLeaseField.Rate, RetirementReason.CoverageEnded);
                    decision = PlaybackRateDecision.Retiring;
                }
                else
                {
                    var shortOrEnd = ConnectedAtPosition(candidates.Where(item => Active(item, input.Actual.Position)
                        && (item.End - item.Start < .5 || input.Actual.Position >= item.End - .05)), input.Actual.Position);
                    if (shortOrEnd.Length > 0)
                    {
                        decision = PlaybackRateDecision.ShortOrNearEnd;
                        if (context.Speed.Fallback == SpeedUpFallback.SeekShortOrNearEnd && input.Capabilities.CanSeekExactly)
                            plan = plan with { Effects = [new(SponsorEffectKind.SeekInterval, input.Actual.Position,
                                shortOrEnd.Max(item => item.End), shortOrEnd.Select(item => item.Contributor).ToImmutableArray())] };
                    }
                    else if (decision == PlaybackRateDecision.None && common.Selected.Any(item => Active(item, input.Actual.Position)
                        && item.Policy == CategoryPolicy.AutoSkip && item.Segment.ActionType == "skip" && !Source2AutomaticEligible(item, context)))
                        decision = PlaybackRateDecision.Source2Unavailable;
                }
            }
        }

        if (plan.Effects.Any(IsSeek) && context.MuteLease is { } seekMute
            && !retirements.Any(r => r.Field == PlaybackLeaseField.Mute))
        {
            Retire(seekMute, PlaybackLeaseField.Mute, RetirementReason.UserSeek);
            pendingMute = null;
        }
        if (!playing || plan.Effects.Any(IsSeek)) wake = null;
        return new(plan with { NextBoundary = null }, PlaybackExecutionValidity.Current, rate, decision, wake,
            pendingMute, retirements.ToImmutable());
    }

    private static bool IsSeek(PlaybackEffect effect) => effect.Kind is SponsorEffectKind.SeekInterval
        or SponsorEffectKind.SeekPoint or SponsorEffectKind.SeekUndo;

    private static bool Source2AutomaticEligible(Selected item, PlaybackExecutionContext context) => item.Segment.Source != 2
        || context.Source2 is { } facts && facts.AlignedEligibleKeys.Contains(item.Contributor.Key);

    private static PlaybackCoverage Coverage(ImmutableArray<Selected> selected) => new(selected.Min(item => item.Start),
        selected.Max(item => item.End), selected.Select(item => item.Contributor).ToImmutableArray());

    private static PlaybackCoverage ExtendCoverage(PlaybackCoverage previous, PlaybackCoverage next, double position)
    {
        // Keep past provenance, but reselect all present/future coverage after normalization on EVERY snapshot.
        // A newly demoted/hidden neighbor cannot keep an old future endpoint alive.
        var contributors = previous.Contributors.Where(c => c.Segment.Segment[1] <= position).Concat(next.Contributors)
            .DistinctBy(c => c.Key).OrderBy(c => c.Segment.Segment[0]).ThenBy(c => c.Key.Uuid, StringComparer.Ordinal)
            .ThenBy(c => c.Key.Source ?? -1).ToImmutableArray();
        return new(contributors.Min(c => c.Segment.Segment[0]), next.End, contributors);
    }

    private static bool IsConnectedCoverage(PlaybackCoverage coverage)
    {
        var end = coverage.Start;
        foreach (var c in coverage.Contributors.OrderBy(c => c.Segment.Segment[0]))
        {
            if (c.Segment.Segment[0] > end) return false;
            end = Math.Max(end, c.Segment.Segment[1]);
        }
        return end == coverage.End && coverage.Contributors.Min(c => c.Segment.Segment[0]) == coverage.Start;
    }

    private static ExecutionWake? Earlier(ExecutionWake? current, ExecutionWake candidate) =>
        !double.IsFinite(candidate.DelaySeconds) || candidate.DelaySeconds <= 0 ? current
        : current is null || candidate.DelaySeconds < current.DelaySeconds ? candidate : current;

    private static RetirementReason RetirementCause(PlaybackInput input, long visit, bool seek) =>
        !input.Policy.Enabled || input.Policy.DisableSkipping ? RetirementReason.Disabled
        : input.Channel.IsWhitelisted == true ? RetirementReason.Whitelisted
        : input.Preview is not null || input.IgnorePlaybackForEditing ? RetirementReason.Preview
        : seek || visit != input.Stamp.VisitRevision ? RetirementReason.UserSeek
        : input.Actual.State != SponsorPlaybackState.Playing ? RetirementReason.Paused : RetirementReason.Ineligible;

    private static PlaybackExecutionPlan RejectedExecution(PlaybackInput input, PlaybackExecutionValidity validity) => new(
        new(input.Stamp, false, [], [], [], [new(validity.ToString())], null, input.Channel), validity,
        null, PlaybackRateDecision.None, null, null, []);

    private static bool ValidExecutionContext(PlaybackInput input, PlaybackExecutionContext context)
    {
        bool Key(PlaybackSegmentKey key) => key is not null && key.Media == input.Stamp.Owner.Media
            && !string.IsNullOrWhiteSpace(key.Uuid) && key.Uuid.Length <= 256 && SponsorRules.IsKnownSource(key.Source);
        bool CoverageValid(PlaybackCoverage coverage, string action)
        {
            if (!(coverage is not null && ValidRange(coverage.Start, coverage.End)
            && !coverage.Contributors.IsDefaultOrEmpty && coverage.Contributors.Length <= MaximumRecords
            && coverage.Contributors.All(c => c is not null && Key(c.Key) && c.Segment is not null
                && c.Key.Uuid == c.Segment.Uuid && c.Key.Source == c.Segment.Source && c.Segment.Cid == input.Stamp.Owner.Media.Cid
                && c.Segment.ActionType == action && SponsorRules.ValidateShape(c.Segment) is null
                && c.Segment.Segment[0] >= coverage.Start && c.Segment.Segment[1] <= coverage.End)
            && coverage.Contributors.Select(c => c.Key).Distinct().Count() == coverage.Contributors.Length)) return false;
            return IsConnectedCoverage(coverage);
        }
        bool Lease<T>(PlaybackLeaseSnapshot<T> lease, string action, long epoch) => lease.Owner == input.Stamp.Owner
            && !string.IsNullOrWhiteSpace(lease.Token) && lease.Token.Length <= 256 && lease.UserEpoch >= 0 && lease.UserEpoch <= epoch
            && lease.VisitRevision >= 0 && lease.VisitRevision <= input.Stamp.VisitRevision
            && Enum.IsDefined(lease.Phase) && Enum.IsDefined(lease.Origin) && CoverageValid(lease.Coverage, action)
            && (lease.Origin != PlaybackLeaseOrigin.Manual || !string.IsNullOrWhiteSpace(lease.RequestId))
            && (lease.Retirement is null || Enum.IsDefined(lease.Retirement.Reason)
                && double.IsFinite(lease.Retirement.MonotonicSeconds) && lease.Retirement.MonotonicSeconds >= 0
                && lease.Retirement.MonotonicSeconds <= context.MonotonicSeconds)
            && (lease.Restoration is null || lease.Retirement is not null && Enum.IsDefined(lease.Restoration.Phase)
                && !string.IsNullOrWhiteSpace(lease.Restoration.OperationId));
        return context.Stamp.Owner is { Media: not null, Generation: >= 0 }
            && context.Stamp.PolicyRevision >= 0 && context.Stamp.ObservationRevision >= 0 && context.Stamp.VisitRevision >= 0
            && context.Stamp.MuteUserEpoch >= 0 && context.Stamp.RateUserEpoch >= 0
            && double.IsFinite(context.MonotonicSeconds) && context.MonotonicSeconds >= 0
            && context.Speed is not null && Enum.IsDefined(context.Speed.Fallback)
            && (context.Source2 is null || context.Source2.Stamp == input.Stamp && context.Source2.AlignedEligibleKeys is not null
                && context.Source2.AlignedEligibleKeys.Count <= MaximumRecords
                && context.Source2.AlignedEligibleKeys.All(key => Key(key) && key.Source == 2))
            && (context.RateLease is not { } rate || Lease(rate, "skip", input.Stamp.RateUserEpoch)
                && rate.Origin == PlaybackLeaseOrigin.Automatic && double.IsFinite(rate.Baseline) && rate.Baseline > 0
                && double.IsFinite(rate.Target) && rate.Target > rate.Baseline && rate.Target <= 16)
            && (context.MuteLease is not { } mute || Lease(mute, "mute", input.Stamp.MuteUserEpoch) && mute.Target)
            && !context.RateClaims.IsDefault && context.RateClaims.Length <= MaximumRecords
            && context.RateClaims.All(claim => claim is not null && claim.Owner == input.Stamp.Owner
                && !string.IsNullOrWhiteSpace(claim.Token) && claim.Token.Length <= 256
                && Enum.IsDefined(claim.Phase) && CoverageValid(claim.Coverage, "skip")
                && (context.RateLease is not { } owned || owned.Token != claim.Token || owned.Phase == claim.Phase))
            && context.RateClaims.Select(claim => claim.Token).Distinct(StringComparer.Ordinal).Count() == context.RateClaims.Length
            && !context.RateCooldowns.IsDefault && context.RateCooldowns.Length <= MaximumRecords
            && context.RateCooldowns.All(c => c is not null && Key(c.Key) && ValidRange(c.Start, c.End)
                && double.IsFinite(c.CancelledAtMonotonicSeconds) && c.CancelledAtMonotonicSeconds >= 0
                && c.CancelledAtMonotonicSeconds <= context.MonotonicSeconds && double.IsFinite(c.CancelledAtMonotonicSeconds + 5));
    }
}
