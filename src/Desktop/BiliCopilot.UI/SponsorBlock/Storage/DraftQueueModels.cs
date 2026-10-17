using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Storage;

public enum DraftState { Ready, Incomplete, Invalid, SubmissionInFlight, Confirmed, AcceptedUnconfirmed, Rejected, Unknown }
public sealed record DraftEntry(string LocalDraftId, SegmentDraft Draft, double Duration, DraftState State,
    ImmutableArray<double> OrdinaryRange, string? ServerUuid = null, SubmittedDraft? LastSubmission = null);
public sealed record SubmittedDraft(string LocalDraftId, SegmentDraft Draft);
public sealed record SubmissionSnapshot(string OperationId, string MediaKey, double Duration, ImmutableArray<SubmittedDraft> Items)
{
    public ImmutableArray<SegmentDraft> Payload => Items.Select(item => item.Draft).ToImmutableArray();
}
public sealed record CaptureSnapshot(string LocalDraftId, string MediaKey, long NativeMediaGeneration, double Duration);
public sealed record PreviewInterval(string MediaKey, string Revision, long NativeMediaGeneration, double Start, double End);
public sealed record QueueOutcome(bool Success, string? Error = null, DraftEntry? Entry = null, SubmissionSnapshot? Submission = null);
public sealed record QueueDocument(int SchemaVersion, ImmutableArray<DraftEntry> Drafts);
