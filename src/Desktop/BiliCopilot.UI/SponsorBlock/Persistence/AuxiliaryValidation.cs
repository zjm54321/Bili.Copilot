using System.Text.Json;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

internal static class AuxiliaryValidation
{
    internal const int MaximumJournal = 512, MaximumWhitelist = 4096, MaximumWatermarks = 4096;
    internal const int MaximumBytes = 2 * 1024 * 1024;

    internal static PersistenceStatus Validate(SponsorAuxiliarySnapshot? value)
    {
        if (value is null || value.WhitelistedUpUids.IsDefault || value.WhitelistedUpUids.Length > MaximumWhitelist
            || value.WhitelistedUpUids.Any(uid => !SponsorUpUid.IsValid(uid))
            || value.WhitelistedUpUids.Distinct(StringComparer.Ordinal).Count() != value.WhitelistedUpUids.Length
            || !ValidFilters(value.Filters) || !ValidStatistics(value.Statistics)
            || value.ContentPreferences is not null && !ContentPreferencesValidation.IsValid(value.ContentPreferences)
            || value.Journal.IsDefault || value.Journal.Length > MaximumJournal || value.Journal.Any(e => !ValidEntry(e))
            || value.Journal.Select(e => e.Request.Id).Distinct(StringComparer.Ordinal).Count() != value.Journal.Length)
            return PersistenceStatus.InvalidData;
        return JsonSerializer.SerializeToUtf8Bytes(value, PersistenceJsonContext.Default.SponsorAuxiliarySnapshot).Length <= MaximumBytes
            ? PersistenceStatus.Success : PersistenceStatus.TooLarge;
    }
    internal static bool ValidFilters(DurableFilterPreferences? value)
    {
        if (value is null) return false;
        bool Categories(System.Collections.Immutable.ImmutableArray<string> categories) => !categories.IsDefault
            && categories.Length <= SponsorRules.Categories.Length && categories.Distinct(StringComparer.Ordinal).Count() == categories.Length
            && categories.All(c => SponsorRules.Categories.Any(known => known.Id == c));
        return Categories(value.ContentCategories) && Categories(value.DanmakuCategories);
    }
    internal static bool ValidStatistics(DurableStatistics? value) => value is not null && value.ConfirmedSeekCount >= 0
        && Number(value.ConfirmedSeekMediaSeconds) && Number(value.ObservedRateMediaSeconds) && Number(value.ObservedRateEstimatedSavedSeconds)
        && !value.Watermarks.IsDefault && value.Watermarks.Length <= MaximumWatermarks
        && value.Watermarks.All(w => w is not null && w.Attachment != Guid.Empty && Media(w.MediaKey)
            && w.NativeGeneration >= 0 && w.AppliedId >= 0 && w.RateReceiptId >= 0)
        && value.Watermarks.Select(w => (w.Attachment, w.MediaKey, w.NativeGeneration)).Distinct().Count() == value.Watermarks.Length;
    private static bool Number(double value) => double.IsFinite(value) && value >= 0;
    private static bool Media(string? key) => MediaIdentity.TryParse(key, out var media) && media!.Cid.Any(c => c != '0');
    private static bool Text(string? text, int limit) => SponsorLocalSettings.ValidText(text, limit);
    private static bool Hex(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidPort(PortVideo? port, MediaIdentity media) => port is not null && port.BvId == media.BvId
        && port.Cid == media.Cid && Text(port.Uuid, 256) && Text(port.YoutubeId, 128)
        && (port.Votes is null || double.IsFinite(port.Votes.Value));

    private static bool ValidEntry(DurableJournalEntry? entry)
    {
        if (entry?.Request is not { } r || r.Stamp is not { } s || !Media(s.MediaKey)
            || !Guid.TryParseExact(r.Id, "N", out _) || r.Attachment == Guid.Empty || r.DraftRevision < 0
            || s.NativeGeneration < 0 || s.PolicyRevision < 0 || s.ObservationRevision < 0 || s.VisitRevision < 0
            || s.MuteUserEpoch < 0 || s.RateUserEpoch < 0 || !Enum.IsDefined(r.Operation) || r.Operation == ManualOperation.Submit
            || !Enum.IsDefined(r.Vote) || r.Service != "https://www.bsbsb.top/"
            || r.Text is not null && !Text(r.Text, 128) || r.Duration is not null && (!Number(r.Duration.Value) || r.Duration <= 0)
            || r.PublicUserId is not null && !Hex(r.PublicUserId) || !Enum.IsDefined(entry.Phase)) return false;
        if (!MediaIdentity.TryParse(s.MediaKey, out var media)) return false;
        if (r.Operation is not (ManualOperation.ReportViewed or ManualOperation.UpdatePort) && !Hex(r.PublicUserId)) return false;
        if (r.Target is { } key && (key.MediaKey != s.MediaKey || !Text(key.Uuid, 256) || !SponsorRules.IsKnownSource(key.Source))) return false;
        if (r.Operation is ManualOperation.Vote or ManualOperation.ChangeCategory or ManualOperation.ReportViewed && r.Target is null) return false;
        if (r.OriginalSegment is { } original && (r.Target is null || original.Cid != media!.Cid || original.Uuid != r.Target.Uuid
            || original.Source != r.Target.Source || original.Segment.IsDefault || original.Segment.Length != 2
            || original.Segment.Any(t => !Number(t)) || !Text(original.Category, 64) || !Text(original.ActionType, 32)
            || original.Locked is not null && !double.IsFinite(original.Locked.Value)
            || original.VideoDuration is not null && !Number(original.VideoDuration.Value))) return false;
        if (r.Port is not null && !ValidPort(r.Port, media!)) return false;
        if (r.Operation is ManualOperation.VotePort or ManualOperation.UpdatePort && r.Port is null) return false;
        if (r.Operation == ManualOperation.BindPort && (r.Duration is null || r.Text is null)) return false;
        if (entry.Phase == DurableJournalPhase.Prepared) return entry.Outcome is null;
        var outcome = entry.Outcome;
        return outcome is not null && Enum.IsDefined(outcome.Status)
            && (outcome.HttpStatus is null || outcome.HttpStatus is >= 100 and <= 599)
            && (outcome.Reason is null || Text(outcome.Reason, 64) && outcome.Reason.All(char.IsAsciiLetterOrDigit))
            && !outcome.ItemsConfirmed && !outcome.BatchAccepted // submission acknowledgement remains exclusively Q4.
            && (outcome.ConfirmedPort is null || ValidPort(outcome.ConfirmedPort, media!));
    }

    internal static bool Unresolved(DurableJournalEntry entry) => entry.Phase == DurableJournalPhase.Prepared
        || entry.Outcome?.Status == WriteStatus.Unknown;

    // Normal writes cannot erase/reinterpret journal or receipt facts. Counter/watermark resets require
    // explicit quiesced backup replacement, which independently refuses unresolved mutations/drafts.
    internal static bool PreservesHistory(SponsorAuxiliarySnapshot before, SponsorAuxiliarySnapshot after)
    {
        var oldStatistics = before.Statistics;
        var nextStatistics = after.Statistics;
        if (nextStatistics.ConfirmedSeekCount < oldStatistics.ConfirmedSeekCount
            || nextStatistics.ConfirmedSeekMediaSeconds < oldStatistics.ConfirmedSeekMediaSeconds
            || nextStatistics.ObservedRateMediaSeconds < oldStatistics.ObservedRateMediaSeconds
            || nextStatistics.ObservedRateEstimatedSavedSeconds < oldStatistics.ObservedRateEstimatedSavedSeconds
            || oldStatistics.CapacityReached && !nextStatistics.CapacityReached) return false;
        foreach (var old in oldStatistics.Watermarks)
        {
            var next = nextStatistics.Watermarks.FirstOrDefault(w => w.Attachment == old.Attachment
                && w.MediaKey == old.MediaKey && w.NativeGeneration == old.NativeGeneration);
            if (next is null || next.AppliedId < old.AppliedId || next.RateReceiptId < old.RateReceiptId) return false;
        }
        foreach (var old in before.Journal)
        {
            var next = after.Journal.FirstOrDefault(e => e.Request.Id == old.Request.Id);
            if (next is null || !SameRequest(old.Request, next.Request)) return false;
            if (old.Phase == DurableJournalPhase.Completed && (next.Phase != old.Phase || next.Outcome != old.Outcome)) return false;
        }
        return true;
    }
    internal static bool SameRequest(DurableMutationRecord a, DurableMutationRecord b) =>
        JsonSerializer.SerializeToUtf8Bytes(a, PersistenceJsonContext.Default.DurableMutationRecord).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b, PersistenceJsonContext.Default.DurableMutationRecord));
}
