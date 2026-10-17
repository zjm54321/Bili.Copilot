using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

// Neutral durable values only. No Application, controller, native object, task or private identity.
public sealed record DurableMediaStamp(
    [property: JsonRequired] string MediaKey,
    [property: JsonRequired] long NativeGeneration,
    [property: JsonRequired] long PolicyRevision,
    [property: JsonRequired] long ObservationRevision,
    [property: JsonRequired] long VisitRevision,
    [property: JsonRequired] long MuteUserEpoch,
    [property: JsonRequired] long RateUserEpoch);
public sealed record DurableSegmentKey([property: JsonRequired] string MediaKey,
    [property: JsonRequired] string Uuid, [property: JsonRequired] int? Source);
public sealed record DurableMutationRecord(
    [property: JsonRequired] string Id, [property: JsonRequired] Guid Attachment,
    [property: JsonRequired] DurableMediaStamp Stamp, [property: JsonRequired] long DraftRevision,
    [property: JsonRequired] ManualOperation Operation, [property: JsonRequired] DurableSegmentKey? Target,
    [property: JsonRequired] SponsorVote Vote, [property: JsonRequired] string? Text,
    [property: JsonRequired] PortVideo? Port, [property: JsonRequired] string Service,
    [property: JsonRequired] double? Duration, [property: JsonRequired] SponsorSegment? OriginalSegment,
    [property: JsonRequired] string? PublicUserId);
public enum DurableJournalPhase { Prepared, Completed }
public sealed record DurableWriteOutcome(
    [property: JsonRequired] WriteStatus Status, [property: JsonRequired] int? HttpStatus,
    [property: JsonRequired] string? Reason, [property: JsonRequired] bool BatchAccepted,
    [property: JsonRequired] bool ItemsConfirmed, [property: JsonRequired] PortVideo? ConfirmedPort);
public sealed record DurableJournalEntry(
    [property: JsonRequired] DurableMutationRecord Request,
    [property: JsonRequired] DurableJournalPhase Phase,
    [property: JsonRequired] DurableWriteOutcome? Outcome)
{
    // True for records decoded from state/backup, including internal read-modify-write reads.
    // UI provenance only: neither false nor true authorizes dispatch/replay. Never serialized.
    [JsonIgnore] public bool RecoveredOnLoad { get; init; }
}
public sealed record DurableReceiptWatermark(
    [property: JsonRequired] Guid Attachment, [property: JsonRequired] string MediaKey,
    [property: JsonRequired] long NativeGeneration, [property: JsonRequired] long AppliedId,
    [property: JsonRequired] long RateReceiptId);
public sealed record DurableStatistics(
    [property: JsonRequired] long ConfirmedSeekCount,
    [property: JsonRequired] double ConfirmedSeekMediaSeconds,
    [property: JsonRequired] double ObservedRateMediaSeconds,
    [property: JsonRequired] double ObservedRateEstimatedSavedSeconds,
    [property: JsonRequired] bool CapacityReached,
    [property: JsonRequired] ImmutableArray<DurableReceiptWatermark> Watermarks)
{
    public static DurableStatistics Empty { get; } = new(0, 0, 0, 0, false, []);
}
public sealed record DurableFilterPreferences(
    [property: JsonRequired] bool ContentEnabled,
    [property: JsonRequired] ImmutableArray<string> ContentCategories,
    [property: JsonRequired] bool DanmakuEnabled,
    [property: JsonRequired] ImmutableArray<string> DanmakuCategories)
{
    public static DurableFilterPreferences Off { get; } = new(false, [], false, []);
}
public sealed record SponsorAuxiliarySnapshot(
    [property: JsonRequired] ImmutableArray<string> WhitelistedUpUids,
    [property: JsonRequired] DurableFilterPreferences Filters,
    [property: JsonRequired] DurableStatistics Statistics,
    [property: JsonRequired] ImmutableArray<DurableJournalEntry> Journal)
{
    // Absent remains absent across unrelated writes/export. A present JSON null is rejected by shape validation.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SponsorContentPreferences? ContentPreferences { get; init; }

    public static SponsorAuxiliarySnapshot Empty { get; } = new([], DurableFilterPreferences.Off, DurableStatistics.Empty, []);
}

/// <summary>Actual numeric Bili UP UID, not BVID, title, hash or a playback-verification flag.
/// Canonical positive unsigned decimal prevents aliases/leading-zero duplicate entries.</summary>
public static class SponsorUpUid
{
    public static bool IsValid(string? uid) => uid is not null && uid.Length is > 0 and <= 20
        && ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
        && value.ToString(CultureInfo.InvariantCulture) == uid;
}

public enum SponsorSchemaUpgradeIntent { None, UpgradePreparedSchema1 }
internal sealed record StateDocumentV2(int SchemaVersion, string ScopeId, long Revision,
    SponsorLocalSettings Settings, byte[] Drafts, SponsorAuxiliarySnapshot Auxiliary);
internal sealed record BackupDocumentV2(int SchemaVersion, string ScopeId,
    SponsorLocalSettings Settings, byte[] Drafts, SponsorAuxiliarySnapshot Auxiliary);
