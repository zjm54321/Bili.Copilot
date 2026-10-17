using System.Collections.Immutable;
using System.Text.Json;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Storage;

public sealed partial class DraftQueue
{
    public bool TryExport(out byte[]? json, out string? error)
    {
        json = JsonSerializer.SerializeToUtf8Bytes(new QueueDocument(1, entries), StorageJsonContext.Default.QueueDocument);
        error = json.Length > MaximumJsonBytes ? "ExportTooLarge" : null;
        if (error is not null) json = null;
        return error is null;
    }
    /// <summary>Atomic additive import. Never replaces existing IDs/media or resumes pending operations.
    /// Unknown categories/actions/sources stay visible Invalid; accepted/unknown history remains non-ready.</summary>
    public QueueOutcome Import(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumJsonBytes) return Fail("ImportSizeLimit");
        QueueDocument? document;
        try
        {
            using var raw = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            if (!UniqueProperties(raw.RootElement)) return Fail("DuplicateJsonProperty");
            document = JsonSerializer.Deserialize(utf8.Span, StorageJsonContext.Default.QueueDocument);
        }
        catch (JsonException) { return Fail("MalformedOrUnsupportedJson"); }
        if (document is null || document.SchemaVersion != 1 || document.Drafts.IsDefault) return Fail("UnsupportedSchema");
        if (document.Drafts.Length + entries.Length > MaximumDrafts) return Fail("QueueCapacity");
        var ids = entries.Select(e => e.LocalDraftId).ToHashSet(StringComparer.Ordinal);
        var restored = ImmutableArray.CreateBuilder<DraftEntry>();
        foreach (var entry in document.Drafts)
        {
            if (entry is null || !Text(entry.LocalDraftId, 256) || !ids.Add(entry.LocalDraftId)
                || !Enum.IsDefined(entry.State) || !ValidStored(entry.Draft, entry.Duration)
                || entry.OrdinaryRange.IsDefault || !ValidRange(entry.OrdinaryRange, entry.Duration)
                || (entry.ServerUuid is not null && !Text(entry.ServerUuid, 256))) return Fail("InvalidOrDuplicateDraft");
            if (entry.LastSubmission is not null && (entry.LastSubmission.LocalDraftId != entry.LocalDraftId
                || !ValidStored(entry.LastSubmission.Draft, entry.Duration)
                || entry.LastSubmission.Draft.MediaKey != entry.Draft.MediaKey)) return Fail("InvalidSubmissionHistory");
            if (entry.State is DraftState.Confirmed or DraftState.AcceptedUnconfirmed or DraftState.Unknown or DraftState.SubmissionInFlight
                && entry.LastSubmission is null) return Fail("MissingSubmissionHistory");
            if (entry.State == DraftState.Confirmed && entry.ServerUuid is null) return Fail("MissingServerUuid");
            MediaIdentity.TryParse(entry.Draft.MediaKey, out var media);
            var valid = SponsorRules.ValidateDraft(entry.Draft, media!, entry.Duration) is null;
            var incomplete = SponsorRules.Allows(entry.Draft.Segment.Category, entry.Draft.Segment.ActionType)
                && entry.Draft.Segment.ActionType is "skip" or "mute" && entry.Draft.Segment.Segment.Length == 1;
            var state = entry.State == DraftState.SubmissionInFlight ? DraftState.Unknown : entry.State;
            if (entry.LastSubmission is not null && state is DraftState.Ready or DraftState.Rejected) state = DraftState.Unknown;
            if (state is DraftState.Ready or DraftState.Incomplete or DraftState.Invalid or DraftState.Rejected)
                state = valid ? state == DraftState.Rejected ? DraftState.Rejected : DraftState.Ready : incomplete ? DraftState.Incomplete : DraftState.Invalid;
            restored.Add(entry with { State = state });
        }
        var combined = entries.AddRange(restored);
        if (combined.Select(e => e.Draft.MediaKey).Distinct(StringComparer.Ordinal).Count() > MaximumMedia) return Fail("MediaCapacity");
        entries = combined; undo.Clear(); ActiveCapture = null; ActivePreview = null; return new(true);
    }
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max
        && !value.Any(char.IsControl);
    private static bool ValidRange(ImmutableArray<double> range, double duration) => range.Length <= 2
        && range.All(t => Time(t, duration)) && (range.Length != 2 || range[1] >= range[0]);
    private static bool ValidStored(SegmentDraft? draft, double duration)
    {
        if (draft is null || draft.Version != 1 || !MediaIdentity.TryParse(draft.MediaKey, out var media)
            || !Text(draft.Revision, 256) || !double.IsFinite(duration) || duration <= 0) return false;
        var s = draft.Segment;
        return s is not null && s.Cid == media!.Cid && Text(s.Uuid, 256) && Text(s.Category, 128) && Text(s.ActionType, 128)
            && !s.Segment.IsDefault && s.Segment.Length is 1 or 2 && ValidRange(s.Segment, duration)
            && (s.Locked is null || double.IsFinite(s.Locked.Value))
            && (s.VideoDuration is null || double.IsFinite(s.VideoDuration.Value) && s.VideoDuration > 0);
    }
    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!seen.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
