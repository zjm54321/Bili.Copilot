using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Api;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

// All UI contracts are assembly-internal: XAML view models adapt them, not the native backend.
internal enum ApplicationStatus { Success, Disabled, Stale, Busy, Closed, Unavailable, Invalid, PersistenceFailure, ControllerFailure, Pending, Acknowledged, Released, NeedsReconciliation }
internal sealed record ApplicationResult(ApplicationStatus Status, string? Reason = null,
    PersistenceStatus? Persistence = null, WriteResult? Write = null, QueueOutcome? Queue = null)
{
    // Command is the native mutation record, not Snapshot.Operation (which may already be Read).
    internal SponsorEditorReply? EditorReply { get; init; }
}
internal sealed record PlayerTicket(Guid Attachment, PlaybackStamp Stamp, long DraftRevision);
internal sealed record DraftSelection(string LocalDraftId, string Revision);
internal enum EditorAction { StartCapture, FinishCapture, CancelCapture, Point, FullLabel, Edit, Delete, Undo, PreparePreview, EndPreview, Split, Merge, FrameForward, FrameBackward, StartPreviewPlayback, KeepCurrentPreview }
internal sealed record EditorCommand(EditorAction Action, DraftSelection? Draft = null,
    string Category = "sponsor", string ActionType = "skip", double? Start = null, double? End = null)
{
    internal SponsorPreviewToken? PreviewToken { get; init; }
}
internal enum PlaybackUiAction { Skip, SkipNow, Undo, ReSkip, JumpToPoint, ReleaseMute }
internal sealed record PlaybackUiCommand(PlaybackUiAction Action, PlaybackSegmentKey? Target = null);
internal sealed record SponsorPlayerView(Guid Attachment, SponsorControllerResult? Controller,
    ReadStatus ReadStatus, ImmutableArray<SponsorSegment> RawSegments, PlaybackChannel Channel,
    bool? KnownMusic, bool Editing, PreviewInterval? Preview, bool Attached,
    ApplicationStatus DeliveryStatus = ApplicationStatus.Success)
{
    // Supplied only by an exact-owner metadata adapter; never inferred from a title/BVID.
    internal string? VerifiedUpUid { get; init; }
    internal SponsorFilterFacts? FilterFacts { get; init; }
    internal bool EditorPortAvailable { get; init; }
    internal bool EditorCommandPending { get; init; }
    internal SponsorPreviewStatus? PreviewPlayback { get; init; }
    internal SponsorEditorReply? LastEditorReply { get; init; }
    internal bool CleanupPending { get; init; }
    internal ImmutableArray<SponsorAvailability> EditorCapabilities { get; init; } = [];
}
internal sealed record SponsorApplicationView(long Revision, long DraftRevision, SponsorLocalSettings Settings,
    ImmutableArray<DraftEntry> Drafts, PersistenceStatus? Persistence, bool Dirty, bool Closed,
    ImmutableArray<SponsorPlayerView> Players, SponsorStatistics Statistics,
    ImmutableArray<MutationCompletion> Mutations)
{
    internal string? StoreVersion { get; init; }
    internal SponsorAuxiliarySnapshot Auxiliary { get; init; } = SponsorAuxiliarySnapshot.Empty;
    internal bool StatisticsDirty { get; init; }
    internal PersistenceStatus? StatisticsPersistence { get; init; }
    internal PersistenceStatus? AuxiliaryPersistence { get; init; }
    internal bool JournalCapacityReached { get; init; }
    internal bool AuxiliaryCapacityReached { get; init; }
    internal ImmutableArray<DurableJournalEntry> UnsavedMutationOutcomes { get; init; } = [];
    // Closed stops admission; it does not mean retained player cleanup has completed.
    internal bool PlayerCleanupPending { get; init; }
    internal SponsorFilterOptions ContentFilter => new(Auxiliary.Filters.ContentEnabled,
        Auxiliary.Filters.ContentCategories.ToImmutableHashSet(StringComparer.Ordinal));
    internal SponsorFilterOptions DanmakuFilter => new(Auxiliary.Filters.DanmakuEnabled,
        Auxiliary.Filters.DanmakuCategories.ToImmutableHashSet(StringComparer.Ordinal));
}
internal sealed record IdentityView(PersistenceStatus Status, string? PublicId = null);
internal sealed record RelatedView(MediaIdentity Original, MediaIdentity Target, ReadResult<PortVideo> Port,
    ReadResult<ImmutableArray<SponsorSegment>> Segments, string MappingState = "PendingExplicitMapping");
internal sealed record RelatedReadResult(ApplicationStatus Status, RelatedView? View = null);
internal sealed record PortMutationResult(ApplicationResult Mutation, RelatedReadResult? TargetRefresh = null);

/// <summary>Post must preserve FIFO and run with this dispatcher's SynchronizationContext. It must
/// throw if shutdown rejects admission; silently dropping callbacks would strand ownership.</summary>
internal interface ISponsorDispatcher
{
    bool HasAccess { get; }
    void Post(Action action);
}

internal static class SponsorDispatch
{
    internal static Task<T> RunAsync<T>(this ISponsorDispatcher dispatcher, Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Run()
        {
            try
            {
                if (!dispatcher.HasAccess || System.Threading.SynchronizationContext.Current is null)
                    throw new InvalidOperationException("SponsorDispatcherContextRequired");
                completion.TrySetResult(await action());
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        try { if (dispatcher.HasAccess) Run(); else dispatcher.Post(Run); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
}

/// <summary>Composition supplies the fixed S7 client (tests supply its internal fake HTTP handler).
/// A new immutable consent epoch gets its own client. No Bili/default HTTP client may be supplied.</summary>
internal interface ISponsorClientFactory
{
    SponsorClient Create(SponsorNetworkConsent consent);
}
internal sealed class SponsorClientFactory : ISponsorClientFactory
{
    public SponsorClient Create(SponsorNetworkConsent consent) => new(consent);
}

internal sealed record MutationIntent(PlayerTicket Ticket, ManualOperation Operation,
    PlaybackSegmentKey? Target = null, SponsorVote Vote = SponsorVote.Up, string? Text = null,
    PortVideo? Port = null);
internal sealed record MutationRecord(string Id, Guid Attachment, PlaybackStamp Stamp,
    long DraftRevision, ManualOperation Operation, PlaybackSegmentKey? Target, SponsorVote Vote,
    string? Text, PortVideo? Port, string Service, double? Duration, SponsorSegment? OriginalSegment, string? PublicUserId);
internal sealed record MutationCompletion(string Id, MediaIdentity Media, ManualOperation Operation,
    WriteStatus Status, bool Persisted, string? Reason = null);

/// <summary>Optional extension REQUIRED for non-Q4 writes. BeforeSend must durably commit an
/// immutable, secret-free record before returning Success. Complete must retain Unknown and accepted
/// outcomes without replay. Startup must expose unresolved records, never reconstruct send intents.
/// The default store-backed implementation is owner-bound. An optional injected implementation must
/// not re-enter coordinator methods or independently mutate its store scope; drift fails closed.</summary>
internal interface ISponsorMutationJournal
{
    Task<PersistenceStatus> BeforeSendAsync(MutationRecord record);
    Task<PersistenceStatus> CompleteAsync(MutationRecord record, WriteResult result);
}
