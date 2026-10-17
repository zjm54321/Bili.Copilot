using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Storage;

/// <summary>Single-owner synchronous domain. No callbacks, player actions, automatic intent or replay.
/// Limits: 256 total retained drafts (including confirmed history), 16 media, 100 per submission.</summary>
public sealed partial class DraftQueue
{
    public const int MaximumDrafts = 256, MaximumMedia = 16, MaximumJsonBytes = 1024 * 1024;
    private ImmutableArray<DraftEntry> entries = [];
    private readonly Dictionary<string, ImmutableArray<DraftEntry>> undo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubmissionSnapshot> pending = new(StringComparer.Ordinal);
    private MediaIdentity? activeMedia;
    private long generation;
    public ImmutableArray<DraftEntry> Entries => entries;
    public CaptureSnapshot? ActiveCapture { get; private set; }
    public PreviewInterval? ActivePreview { get; private set; }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static QueueOutcome Fail(string error) => new(false, error);
    private static bool Time(double t, double duration) => double.IsFinite(duration) && duration > 0
        && double.IsFinite(t) && t >= 0 && t <= duration;
    private static double Round(double t) => Math.Round(t * 1000, MidpointRounding.AwayFromZero) / 1000;
    private int Index(string id)
    {
        for (var i = 0; i < entries.Length; i++) if (entries[i].LocalDraftId == id) return i;
        return -1;
    }
    private void Remember(string media) => undo[media] = entries.Where(e => e.Draft.MediaKey == media).ToImmutableArray();
    private QueueOutcome Replace(int index, DraftEntry entry)
    {
        Remember(entry.Draft.MediaKey);
        entries = entries.SetItem(index, entry);
        ActivePreview = null;
        return new(true, Entry: entry);
    }
    public void SwitchActiveMedia(MediaIdentity? media, long nativeMediaGeneration)
    {
        if (activeMedia != media || generation != nativeMediaGeneration)
        { ActiveCapture = null; ActivePreview = null; }
        activeMedia = media; generation = nativeMediaGeneration;
    }
    private QueueOutcome Add(MediaIdentity media, double duration, string category, string action, ImmutableArray<double> times, DraftState state)
    {
        if (entries.Length >= MaximumDrafts || (!entries.Any(e => e.Draft.MediaKey == media.Combined)
            && entries.Select(e => e.Draft.MediaKey).Distinct().Count() >= MaximumMedia)) return Fail("QueueCapacity");
        if (!SponsorRules.Allows(category, action) || !double.IsFinite(duration) || duration <= 0) return Fail("InvalidContextOrCategoryAction");
        var localId = Id();
        var draft = new SegmentDraft { MediaKey = media.Combined, Revision = Id(), Segment = new SponsorSegment
            { Cid = media.Cid, Uuid = localId, Category = category, ActionType = action, Segment = times, Source = 1 } };
        if (state == DraftState.Ready && SponsorRules.ValidateDraft(draft, media, duration) is not null) return Fail("InvalidDraft");
        var entry = new DraftEntry(localId, draft, duration, state, action is "skip" or "mute" ? times : []);
        Remember(media.Combined); entries = entries.Add(entry); return new(true, Entry: entry);
    }
    public QueueOutcome StartCapture(MediaIdentity media, long nativeMediaGeneration, double seconds, double duration,
        string category = "sponsor", string action = "skip")
    {
        if (activeMedia != media || generation != nativeMediaGeneration) return Fail("StaleMediaGeneration");
        if (ActiveCapture is not null) return Fail("CaptureAlreadyActive");
        if (!Time(seconds, duration) || !Time(Round(seconds), duration)) return Fail("InvalidCaptureTime");
        if (action is not ("skip" or "mute")) return Fail("OrdinaryCaptureRequired");
        var result = Add(media, duration, category, action, [Round(seconds)], DraftState.Incomplete);
        if (result.Success) ActiveCapture = new(result.Entry!.LocalDraftId, media.Combined, nativeMediaGeneration, duration);
        return result;
    }
    public QueueOutcome FinishCapture(MediaIdentity media, long nativeMediaGeneration, double seconds)
    {
        var capture = ActiveCapture;
        if (capture is null || activeMedia != media || generation != nativeMediaGeneration
            || capture.MediaKey != media.Combined || capture.NativeMediaGeneration != nativeMediaGeneration) return Fail("StaleCapture");
        var index = Index(capture.LocalDraftId);
        if (index < 0 || !Time(seconds, capture.Duration)) return Fail("InvalidCaptureTime");
        var start = entries[index].Draft.Segment.Segment[0];
        var result = Edit(capture.LocalDraftId, entries[index].Draft.Segment.Category,
            entries[index].Draft.Segment.ActionType, Math.Min(start, seconds), Math.Max(start, seconds));
        if (result.Success) ActiveCapture = null;
        return result;
    }
    public QueueOutcome CreatePoint(MediaIdentity media, double seconds, double duration) =>
        Time(seconds, duration) ? Add(media, duration, "poi_highlight", "poi", [Round(seconds), Round(seconds)], DraftState.Ready) : Fail("InvalidTime");
    public QueueOutcome CreateFullLabel(MediaIdentity media, double duration, string category) =>
        Add(media, duration, category, "full", [0, 0], DraftState.Ready);
    public QueueOutcome Edit(string localDraftId, string category, string action, double? start = null, double? end = null)
    {
        var index = Index(localDraftId);
        if (index < 0) return Fail("DraftNotFound");
        var old = entries[index];
        if (old.State == DraftState.Confirmed) return Fail("ConfirmedHistoryReadOnly");
        if (!SponsorRules.Allows(category, action)) return Fail("UnsupportedCategoryAction");
        ImmutableArray<double> times;
        if (action == "full") times = [0, 0];
        else if (start.HasValue && (end.HasValue || action == "poi"))
        {
            var b = action == "poi" ? start.Value : end!.Value;
            if (!Time(start.Value, old.Duration) || !Time(b, old.Duration)) return Fail("InvalidTime");
            times = [Round(start.Value), Round(b)];
        }
        else if (start.HasValue || end.HasValue) return Fail("IncompleteEdit");
        else times = action == "poi" ? [old.Draft.Segment.Segment[0], old.Draft.Segment.Segment[0]]
            : old.Draft.Segment.ActionType == "full" ? old.OrdinaryRange : old.Draft.Segment.Segment;
        var draft = old.Draft with { Revision = Id(), Segment = old.Draft.Segment with { Category = category, ActionType = action, Segment = times } };
        if (!MediaIdentity.TryParse(draft.MediaKey, out var media) || SponsorRules.ValidateDraft(draft, media!, old.Duration) is not null) return Fail("InvalidDraft");
        var risk = old.State is DraftState.SubmissionInFlight or DraftState.Unknown or DraftState.AcceptedUnconfirmed;
        return Replace(index, old with { Draft = draft, State = risk ? old.State == DraftState.AcceptedUnconfirmed
            ? DraftState.AcceptedUnconfirmed : DraftState.Unknown : DraftState.Ready,
            OrdinaryRange = action is "skip" or "mute" ? times : old.OrdinaryRange });
    }
    public QueueOutcome Delete(string localDraftId)
    {
        var index = Index(localDraftId);
        if (index < 0) return Fail("DraftNotFound");
        var old = entries[index];
        if (old.LastSubmission is not null || old.State == DraftState.Confirmed) return Fail("RemoteHistoryCannotBeLocallyWithdrawn");
        Remember(old.Draft.MediaKey); entries = entries.RemoveAt(index);
        if (ActiveCapture?.LocalDraftId == localDraftId) ActiveCapture = null;
        ActivePreview = null; return new(true);
    }
    public QueueOutcome CancelCapture() => ActiveCapture is null ? Fail("NoActiveCapture") : Delete(ActiveCapture.LocalDraftId);
    public QueueOutcome Undo(MediaIdentity media)
    {
        if (!undo.TryGetValue(media.Combined, out var previous)) return Fail("NoLocalUndo");
        var others = entries.Where(e => e.Draft.MediaKey != media.Combined).ToImmutableArray();
        if (others.Length + previous.Length > MaximumDrafts || others.Concat(previous)
            .Select(e => e.Draft.MediaKey).Distinct().Count() > MaximumMedia) return Fail("QueueCapacity");
        // Restore positions inside this media queue; every restored draft gets a fresh revision.
        var restored = previous.Select(e => e with { Draft = e.Draft with { Revision = Id() },
            State = e.State == DraftState.SubmissionInFlight ? DraftState.Unknown : e.State }).ToImmutableArray();
        var first = -1;
        for (var i = 0; i < entries.Length; i++) if (entries[i].Draft.MediaKey == media.Combined) { first = i; break; }
        entries = others.InsertRange(first < 0 ? others.Length : Math.Min(first, others.Length), restored);
        undo.Remove(media.Combined); ActiveCapture = null; ActivePreview = null; return new(true);
    }
    public PreviewInterval? Preview(string localDraftId, MediaIdentity media, long nativeMediaGeneration)
    {
        var index = Index(localDraftId);
        if (index < 0 || activeMedia != media || generation != nativeMediaGeneration) return null;
        var entry = entries[index];
        if (entry.Draft.MediaKey != media.Combined || !SponsorRules.CanExecuteInterval(entry.Draft.Segment)
            || SponsorRules.ValidateDraft(entry.Draft, media, entry.Duration) is not null) return null;
        return ActivePreview = new(media.Combined, entry.Draft.Revision, nativeMediaGeneration,
            entry.Draft.Segment.Segment[0], entry.Draft.Segment.Segment[1]);
    }
    public QueueOutcome PrepareSubmission(MediaIdentity media, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return Fail("InvalidDuration");
        var candidates = entries.Where(e => e.Draft.MediaKey == media.Combined && e.State == DraftState.Ready).ToImmutableArray();
        if (candidates.IsEmpty || candidates.Length > 100 || candidates.Any(e => e.Duration != duration
            || SponsorRules.ValidateDraft(e.Draft, media, duration) is not null)
            || candidates.Select(e => e.Draft.Segment.Uuid).Distinct(StringComparer.Ordinal).Count() != candidates.Length) return Fail("InvalidSubmissionSelection");
        if (pending.Count >= MaximumDrafts) return Fail("OperationCapacity");
        candidates = candidates.Select(e => e with { Draft = e.Draft with { Revision = Id() } }).ToImmutableArray();
        var snapshot = new SubmissionSnapshot(Id(), media.Combined, duration,
            candidates.Select(e => new SubmittedDraft(e.LocalDraftId, e.Draft)).ToImmutableArray());
        pending.Add(snapshot.OperationId, snapshot); undo.Remove(media.Combined);
        foreach (var item in snapshot.Items)
        { var i = Index(item.LocalDraftId); entries = entries.SetItem(i, entries[i] with { Draft = item.Draft, State = DraftState.SubmissionInFlight, LastSubmission = item }); }
        return new(true, Submission: snapshot);
    }
    public QueueOutcome CompleteSubmission(string operationId, MediaIdentity originalMedia, WriteResult result)
    {
        if (!pending.TryGetValue(operationId, out var snapshot)) return Fail("UnknownOrCompletedOperation");
        if (snapshot.MediaKey != originalMedia.Combined) return Fail("ForeignMedia");
        var confirmed = result.Status == WriteStatus.Accepted && result.BatchAccepted && result.ItemsConfirmed
            && !result.ConfirmedItems.IsDefault && result.ConfirmedItems.Length == snapshot.Items.Length
            && result.ConfirmedItems.All(s => s is not null && !string.IsNullOrWhiteSpace(s.Uuid) && s.Uuid.Length <= 256)
            && result.ConfirmedItems.Select(s => s.Uuid).Distinct(StringComparer.Ordinal).Count() == snapshot.Items.Length;
        for (var i = 0; confirmed && i < snapshot.Items.Length; i++)
        {
            var a = result.ConfirmedItems[i]; var s = snapshot.Items[i].Draft.Segment;
            confirmed = a.Cid == originalMedia.Cid && a.Category == s.Category && a.ActionType == s.ActionType
                && !a.Segment.IsDefault && a.Segment.SequenceEqual(s.Segment)
                && (!snapshot.Items.Any(other => other.Draft.Segment.Uuid == a.Uuid) || a.Uuid == s.Uuid);
        }
        pending.Remove(operationId); undo.Remove(snapshot.MediaKey);
        for (var i = 0; i < snapshot.Items.Length; i++)
        {
            var item = snapshot.Items[i]; var index = Index(item.LocalDraftId);
            if (index < 0 || entries[index].Draft.MediaKey != snapshot.MediaKey) continue;
            var old = entries[index];
            // An older acknowledgement never erases or confirms an edited revision.
            if (old.Draft.Revision != item.Draft.Revision) continue;
            var state = confirmed ? DraftState.Confirmed : result.BatchAccepted || result.Status == WriteStatus.Accepted
                ? DraftState.AcceptedUnconfirmed : result.Status switch
                { WriteStatus.NotSent => DraftState.Ready, WriteStatus.Rejected => DraftState.Rejected, _ => DraftState.Unknown };
            entries = entries.SetItem(index, old with { Draft = old.Draft with { Revision = Id() }, State = state,
                ServerUuid = confirmed ? result.ConfirmedItems[i].Uuid : null,
                LastSubmission = state is DraftState.Ready or DraftState.Rejected ? null : item });
        }
        return new(true);
    }
    /// <summary>Consumes a NEW explicit user token for retry admission. Caller needs a separate new Submit token.
    /// The queue never manufactures tokens; acknowledgement of duplicate risk is mandatory for ambiguous writes.</summary>
    public QueueOutcome AdmitRetry(string localDraftId, MediaIdentity media, ManualWriteIntent? newUserIntent, bool acknowledgeDuplicateRisk)
    {
        var index = Index(localDraftId);
        if (index < 0 || entries[index].Draft.MediaKey != media.Combined) return Fail("ForeignOrMissingDraft");
        var old = entries[index];
        if (pending.Values.Any(s => s.Items.Any(i => i.LocalDraftId == localDraftId))) return Fail("SubmissionStillInFlight");
        if (old.State is not (DraftState.Rejected or DraftState.Unknown or DraftState.AcceptedUnconfirmed)) return Fail("NotRetryable");
        if (old.State != DraftState.Rejected && !acknowledgeDuplicateRisk) return Fail("DuplicateRiskAcknowledgementRequired");
        if (SponsorRules.ValidateDraft(old.Draft, media, old.Duration) is not null) return Fail("InvalidDraft");
        if (newUserIntent is null || !newUserIntent.Consume(media, ManualOperation.Submit)) return Fail("NewUserIntentRequired");
        undo.Remove(media.Combined);
        entries = entries.SetItem(index, old with { Draft = old.Draft with { Revision = Id() }, State = DraftState.Ready });
        return new(true, Entry: entries[index]);
    }
}
