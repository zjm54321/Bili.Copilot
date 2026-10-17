using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed record SponsorStatistics(long ConfirmedSeekCount, double ConfirmedSeekMediaSeconds,
    double ObservedRateMediaSeconds, double ObservedRateEstimatedSavedSeconds)
{
    internal bool CapacityReached { get; init; }
}
internal sealed record ReceiptWatermark(Guid Attachment, PlaybackOwner Owner, long AppliedId, long RateReceiptId);
internal sealed record SponsorStatisticsArchive(int Schema, SponsorStatistics Totals, ImmutableArray<ReceiptWatermark> Watermarks)
{
    internal bool IsDurable { get; init; }
    internal string? StoreVersion { get; init; }
}

/// <summary>App-owner only. Counts confirmed displacement and observed sample coverage, NOT planned
/// endpoints, delivered frames or server statistics. Rate saving is explicitly a baseline-rate estimate.
/// Coordinator persists aggregates and dedupe watermarks together. Failed commits keep this bounded
/// observed ledger dirty; they do not advance the published durable auxiliary snapshot/version.</summary>
internal sealed class SponsorReceiptLedger
{
    private readonly Dictionary<(Guid, PlaybackOwner), ReceiptWatermark> watermarks = [];
    private bool touched;
    internal SponsorStatistics Totals { get; private set; } = new(0, 0, 0, 0);
    internal bool CapacityReached { get; private set; }
    // A port's snapshot at subscription can contain old receipts (including after detach/restart).
    // Retain its real IDs as a baseline, without manufacturing credits from snapshot replay.
    internal bool SeedBaseline(Guid attachment, SponsorControllerResult result)
    {
        if (CapacityReached || attachment == Guid.Empty || result.Owner is not { } owner || owner.Generation < 0) return false;
        var appliedId = result.Applied?.Owner == owner ? Math.Max(0, result.AppliedId) : 0;
        var rateId = result.RateReceipt?.Owner == owner ? Math.Max(0, result.RateReceiptId) : 0;
        if (appliedId == 0 && rateId == 0) return false;
        var known = watermarks.TryGetValue((attachment, owner), out var old);
        old ??= new(attachment, owner, 0, 0);
        appliedId = Math.Max(appliedId, old.AppliedId); rateId = Math.Max(rateId, old.RateReceiptId);
        if (appliedId == old.AppliedId && rateId == old.RateReceiptId) return false;
        if (!known && watermarks.Count >= 4096) return MarkCapacity();
        touched = true;
        watermarks[(attachment, owner)] = old with { AppliedId = appliedId, RateReceiptId = rateId };
        return true;
    }
    internal bool Accept(Guid attachment, SponsorControllerResult result)
    {
        if (CapacityReached || result.Owner is not { } owner || attachment == Guid.Empty
            || owner.Generation < 0 || result.AppliedId < 0 || result.RateReceiptId < 0) return false;
        var key = (attachment, owner);
        var known = watermarks.TryGetValue(key, out var old);
        old ??= new(attachment, owner, 0, 0);
        var totals = Totals;
        var appliedId = old.AppliedId;
        var rateId = old.RateReceiptId;
        if (result.AppliedId > old.AppliedId && result.Applied is { WasPreview: false, Kind: SponsorEffectKind.SeekInterval } applied
            && applied.Owner == owner && double.IsFinite(applied.From) && double.IsFinite(applied.To)
            && applied.From >= 0 && applied.To > applied.From && !applied.Contributors.IsDefaultOrEmpty
            && applied.Contributors.All(c => c.Key.Media == owner.Media))
        {
            if (totals.ConfirmedSeekCount == long.MaxValue) return MarkCapacity();
            totals = totals with { ConfirmedSeekCount = totals.ConfirmedSeekCount + 1,
                ConfirmedSeekMediaSeconds = totals.ConfirmedSeekMediaSeconds + applied.To - applied.From };
            appliedId = result.AppliedId;
        }
        if (result.RateReceiptId > old.RateReceiptId && result.RateReceipt is { } receipt && receipt.Owner == owner
            && double.IsFinite(receipt.Baseline) && receipt.Baseline > 0 && double.IsFinite(receipt.Target)
            && receipt.Target > receipt.Baseline && !receipt.ObservedCoverage.IsDefaultOrEmpty)
        {
            foreach (var coverage in receipt.ObservedCoverage)
            {
                var media = coverage.MediaEnd - coverage.MediaStart;
                var elapsed = coverage.MonotonicEnd - coverage.MonotonicStart;
                if (!double.IsFinite(media) || media <= 0 || !double.IsFinite(elapsed) || elapsed <= 0 || elapsed > 2
                    || coverage.Contributors.IsDefaultOrEmpty || coverage.Contributors.Any(c => c.Key.Media != owner.Media)) continue;
                totals = totals with { ObservedRateMediaSeconds = totals.ObservedRateMediaSeconds + media,
                    ObservedRateEstimatedSavedSeconds = totals.ObservedRateEstimatedSavedSeconds + Math.Max(0, media / receipt.Baseline - elapsed) };
                rateId = result.RateReceiptId;
            }
        }
        if (appliedId == old.AppliedId && rateId == old.RateReceiptId) return false;
        // No allocation for notices/plans/empty snapshots. Never evict a receipt key to count again.
        if (!Valid(totals) || !known && watermarks.Count >= 4096) return MarkCapacity();
        touched = true; Totals = totals;
        watermarks[key] = old with { AppliedId = appliedId, RateReceiptId = rateId };
        return true;
    }
    private bool MarkCapacity()
    {
        if (CapacityReached) return false;
        touched = CapacityReached = true; Totals = Totals with { CapacityReached = true }; return true;
    }
    internal SponsorStatisticsArchive Export() => new(1, Totals, watermarks.Values.ToImmutableArray());
    internal bool Import(SponsorStatisticsArchive archive)
    {
        if (touched || archive is null || archive.Schema != 1 || !Valid(archive.Totals) || archive.Watermarks.IsDefault
            || archive.Watermarks.Length > 4096 || archive.Watermarks.Any(w => w is null || w.Attachment == Guid.Empty
                || w.Owner is null || w.Owner.Media is null || !MediaIdentity.TryParse(w.Owner.Media.Combined, out _)
                || !w.Owner.Media.Cid.Any(c => c != '0') || w.Owner.Generation < 0 || w.AppliedId < 0 || w.RateReceiptId < 0)
            || archive.Watermarks.Select(w => (w.Attachment, w.Owner)).Distinct().Count() != archive.Watermarks.Length) return false;
        foreach (var watermark in archive.Watermarks) watermarks.Add((watermark.Attachment, watermark.Owner), watermark);
        Totals = archive.Totals; CapacityReached = Totals.CapacityReached; touched = true; return true;
    }
    internal DurableStatistics ExportDurable() => new(Totals.ConfirmedSeekCount, Totals.ConfirmedSeekMediaSeconds,
        Totals.ObservedRateMediaSeconds, Totals.ObservedRateEstimatedSavedSeconds, CapacityReached,
        watermarks.Values.Select(w => new DurableReceiptWatermark(w.Attachment, w.Owner.Media.Combined,
            w.Owner.Generation, w.AppliedId, w.RateReceiptId)).ToImmutableArray());

    internal bool Matches(DurableStatistics value) => Totals.ConfirmedSeekCount == value.ConfirmedSeekCount
        && Totals.ConfirmedSeekMediaSeconds == value.ConfirmedSeekMediaSeconds
        && Totals.ObservedRateMediaSeconds == value.ObservedRateMediaSeconds
        && Totals.ObservedRateEstimatedSavedSeconds == value.ObservedRateEstimatedSavedSeconds
        && CapacityReached == value.CapacityReached && watermarks.Count == value.Watermarks.Length
        && value.Watermarks.All(w => watermarks.Values.Any(old => old.Attachment == w.Attachment
            && old.Owner.Media.Combined == w.MediaKey && old.Owner.Generation == w.NativeGeneration
            && old.AppliedId == w.AppliedId && old.RateReceiptId == w.RateReceiptId));

    internal static SponsorReceiptLedger FromDurable(DurableStatistics value)
    {
        var marks = ImmutableArray.CreateBuilder<ReceiptWatermark>();
        foreach (var w in value.Watermarks)
        {
            if (!MediaIdentity.TryParse(w.MediaKey, out var media)) throw new InvalidOperationException("InvalidDurableStatistics");
            marks.Add(new(w.Attachment, new(media!, w.NativeGeneration), w.AppliedId, w.RateReceiptId));
        }
        var ledger = new SponsorReceiptLedger();
        if (!ledger.Import(new(1, new(value.ConfirmedSeekCount, value.ConfirmedSeekMediaSeconds,
            value.ObservedRateMediaSeconds, value.ObservedRateEstimatedSavedSeconds) { CapacityReached = value.CapacityReached }, marks.ToImmutable())))
            throw new InvalidOperationException("InvalidDurableStatistics");
        return ledger;
    }
    private static bool Valid(SponsorStatistics value) => value is not null && value.ConfirmedSeekCount >= 0
        && double.IsFinite(value.ConfirmedSeekMediaSeconds) && value.ConfirmedSeekMediaSeconds >= 0
        && double.IsFinite(value.ObservedRateMediaSeconds) && value.ObservedRateMediaSeconds >= 0
        && double.IsFinite(value.ObservedRateEstimatedSavedSeconds) && value.ObservedRateEstimatedSavedSeconds >= 0;
}
