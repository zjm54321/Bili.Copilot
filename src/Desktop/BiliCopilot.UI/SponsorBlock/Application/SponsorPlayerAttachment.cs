using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

/// <summary>Exactly the frozen N3 VM seam. Every member, including event add/remove and Snapshot,
/// is invoked on the supplied PLAYER dispatcher. Never share the app dispatcher by assumption.</summary>
internal interface ISponsorPlayerPort
{
    SponsorControllerResult? SponsorSnapshot { get; }
    event Action<SponsorControllerResult>? SponsorUpdated;
    bool UpdateSponsorInput(SponsorCoordinatorInput input);
    bool RequestSponsorAction(PlaybackRequest request);
    Task<SponsorControllerResult?> ObserveSponsorAsync();
    Task<bool> ReconcileSponsorFieldAsync(SponsorReconciliation request);
    Task<bool> ReconcileSponsorSeekAsync(SponsorSeekReconciliation request);
}

internal sealed class SponsorPlayerAttachment
{
    private readonly ISponsorDispatcher dispatcher;
    private readonly ISponsorPlayerPort port;
    private readonly Action<SponsorPlayerAttachment, SponsorControllerResult> deliver;
    private int closed;
    private bool detached;
    private SponsorControllerResult? latest;
    private SponsorControllerResult? initialSnapshot;
    private long deliveredSequence = -1;
    internal Guid Id { get; } = Guid.NewGuid();
    internal bool IsClosed => Volatile.Read(ref closed) != 0;
    internal bool HasEditorPort => port is ISponsorEditorPlayerPort;
    // The record and every nested fact are immutable. Only publication is cross-dispatcher.
    internal SponsorControllerResult? Latest => Volatile.Read(ref latest);
    // Pre-attachment retained receipts are baseline/history, not new terminal deliveries.
    internal SponsorControllerResult? InitialSnapshot => Volatile.Read(ref initialSnapshot);

    internal SponsorPlayerAttachment(ISponsorDispatcher dispatcher, ISponsorPlayerPort port,
        Action<SponsorPlayerAttachment, SponsorControllerResult> deliver)
    { this.dispatcher = dispatcher; this.port = port; this.deliver = deliver; }

    internal Task<bool> AttachAsync() => dispatcher.RunAsync(() =>
    {
        if (IsClosed) return Task.FromResult(false);
        port.SponsorUpdated += Updated;
        if (port.SponsorSnapshot is { } snapshot)
        { Volatile.Write(ref initialSnapshot, snapshot); Updated(snapshot); }
        return Task.FromResult(true);
    });

    private void Updated(SponsorControllerResult value)
    {
        // Closing start admission must not discard cleanup observations or its subscription.
        if (detached || value.Sequence <= deliveredSequence) return;
        deliveredSequence = value.Sequence;
        // A direct PLAYER read may already hold newer facts at this publication's sequence.
        // Deliver its real receipts once, but never replace that authoritative cache with an older event.
        if (value.Sequence > (latest?.Sequence ?? -1)) Volatile.Write(ref latest, value);
        deliver(this, value);
    }

    private SponsorControllerResult? ReadCurrentSnapshot()
    {
        var current = detached ? null : port.SponsorSnapshot; // PLAYER dispatcher only, no await.
        // Update/intent can change CurrentStamp without Publish or a sequence increment.
        // Refresh even equal sequence; reading is NOT publication/receipt delivery.
        if (current is not null)
        {
            if (current.Sequence < (latest?.Sequence ?? -1)) return null;
            Volatile.Write(ref latest, current);
        }
        return current;
    }

    internal Task<bool> ApplyAsync(SponsorCoordinatorInput input) => dispatcher.RunAsync(() =>
        Task.FromResult(!IsClosed && port.SponsorSnapshot?.Owner == input.Owner && port.UpdateSponsorInput(input)));

    internal Task<bool> RequestAsync(PlaybackRequest request) => dispatcher.RunAsync(() =>
        Task.FromResult(!IsClosed && port.SponsorSnapshot?.Stamp == request.Stamp && port.RequestSponsorAction(request)));

    internal Task<SponsorControllerResult?> ObserveAsync(PlaybackStamp expected) => dispatcher.RunAsync(async () =>
    {
        if (IsClosed || port.SponsorSnapshot?.Stamp != expected) return null;
        var observed = await port.ObserveSponsorAsync();
        if (IsClosed || observed?.Stamp is not { } stamp || !SameVisit(expected, stamp)
            || port.SponsorSnapshot?.Stamp != stamp) return null;
        return observed;
    });

    internal static bool SameVisit(PlaybackStamp a, PlaybackStamp b) => a.Owner == b.Owner
        && a.PolicyRevision == b.PolicyRevision && a.VisitRevision == b.VisitRevision
        && a.MuteUserEpoch == b.MuteUserEpoch && a.RateUserEpoch == b.RateUserEpoch;

    internal Task<SponsorControllerResult?> ReadSnapshotAsync() => dispatcher.RunAsync(() =>
    {
        return Task.FromResult(ReadCurrentSnapshot());
    });

    internal Task<SponsorEditorReply> FrameAsync(PlaybackStamp expected, FrameDirection direction) =>
        EditorAsync(expected, editor => editor.RequestSponsorFrameAsync(expected, direction));
    internal Task<SponsorEditorReply> StartPreviewAsync(PlaybackStamp expected, PreviewInterval preview) =>
        EditorAsync(expected, editor => editor.RequestSponsorPreviewAsync(expected, preview));
    internal Task<SponsorEditorReply> ReleaseMuteAsync(PlaybackStamp expected, string leaseToken) =>
        EditorAsync(expected, editor => editor.RequestSponsorMuteReleaseAsync(expected, leaseToken));

    private Task<SponsorEditorReply> EditorAsync(PlaybackStamp expected,
        Func<ISponsorEditorPlayerPort, Task<SponsorEditorReply>> command) => dispatcher.RunAsync(async () =>
    {
        if (port is not ISponsorEditorPlayerPort editor) return new(SponsorEditorOutcome.Unavailable, null, Reason: "EditorPlayerPortUnavailable");
        if (IsClosed || detached) return new(SponsorEditorOutcome.Unavailable, port.SponsorSnapshot, Reason: "EditorAdmissionClosed");
        if (port.SponsorSnapshot?.Stamp != expected) return new(SponsorEditorOutcome.Stale, port.SponsorSnapshot, Reason: "EditorStampChanged");
        var reply = await command(editor); // Join the actual VM task, not a posted/admitted boolean.
        ReadCurrentSnapshot();
        return reply;
    });

    internal Task<SponsorEditorReply> EndPreviewAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode) => dispatcher.RunAsync(async () =>
    {
        if (detached || port is not ISponsorEditorPlayerPort editor)
            return new SponsorEditorReply(SponsorEditorOutcome.Unavailable, null, Reason: "EditorCleanupPortUnavailable");
        // Cleanup deliberately ignores closed admission, current stamp, policy and draft revision.
        var reply = await editor.EndSponsorPreviewAsync(token, mode);
        ReadCurrentSnapshot();
        return reply;
    });

    internal Task<bool> ReconcileFieldAsync(PlaybackStamp expected, SponsorReconciliation request) => dispatcher.RunAsync(async () =>
        !IsClosed && port.SponsorSnapshot?.Stamp == expected && request.Owner == expected.Owner
        && await port.ReconcileSponsorFieldAsync(request));
    internal Task<bool> ReconcileSeekAsync(PlaybackStamp expected, SponsorSeekReconciliation request) => dispatcher.RunAsync(async () =>
        !IsClosed && port.SponsorSnapshot?.Stamp == expected && request.Owner == expected.Owner
        && await port.ReconcileSponsorSeekAsync(request));

    internal void StopAdmission() => Interlocked.Exchange(ref closed, 1);
    internal Task<bool> DetachAsync(long inputRevision, long policyRevision) => dispatcher.RunAsync(async () =>
    {
        StopAdmission();
        var current = ReadCurrentSnapshot();
        // A never-bound adapter has no acquired native ownership. Losing a previously seen
        // snapshot is different: keep that attachment until its cleanup is evidenced.
        if (current is null && Latest is null) { detached = true; port.SponsorUpdated -= Updated; return true; }
        if (current is null) return false;
        if (current.Owner is { } retiringOwner && port is ISponsorNativeRetirementPort retirement)
        {
            // A replacement/replay can already have drained and sealed this exact managed binding.
            // Join only its real narrow task; normal Observe cannot read a sealed issuer.
            var receipt = await retirement.JoinSponsorNativeRetirementAsync(retiringOwner);
            current = ReadCurrentSnapshot();
            if (current?.Owner != retiringOwner) return false;
            if (receipt is not null)
            {
                if (receipt.Owner != retiringOwner || receipt.Generation != retiringOwner.Generation
                    || current.Sequence < receipt.Sequence || !retirement.IsCurrentSponsorNativeRetirement(receipt)) return false;
                detached = true;
                port.SponsorUpdated -= Updated;
                return true; // Actual sealed retirement proof, not rejected Update or a fabricated fresh Read.
            }
        }
        // This is Sponsor disable, NOT native retirement. Ordinary player controls remain available.
        if (current.Owner is { } owner && !port.UpdateSponsorInput(new(owner, inputRevision, [], new(policyRevision), new()))) return false;
        var beforeRead = ReadCurrentSnapshot();
        var drained = await port.ObserveSponsorAsync();
        current = ReadCurrentSnapshot();
        var clean = drained is not null && current is not null && current.Sequence >= drained.Sequence
            && CleanupDrained(current, beforeRead);
        if (clean) { detached = true; port.SponsorUpdated -= Updated; }
        return clean;
    });

    private static bool CleanupDrained(SponsorControllerResult value, SponsorControllerResult? before) => before is not null
        && value.Owner == before.Owner
        && value.Operation is { Kind: NativeOperationKind.Read, Phase: SponsorOperationPhase.SucceededUnobserved, Failure: null } read
        && read.Id > (before.Operation?.Id ?? 0)
        && value.Actual is { } actual && !ReferenceEquals(actual, before.Actual)
        && actual.NativeGeneration == read.Generation && (value.Owner is null || value.Owner.Generation == read.Generation)
        && (before.Actual is null || before.Actual.NativeGeneration == read.Generation)
        && (before.Operation is null || before.Operation.Generation == read.Generation)
        && double.IsFinite(actual.Position) && actual.Position >= 0 && double.IsFinite(actual.Rate) && actual.Rate > 0
        // ReadAsync installs a newly returned valid snapshot only after native success/validation.
        // Historical controller Failure is sticky until Bind; it is not current failed work.
        && value.MuteLease is null && value.RateLease is null
        && value.PreviewPlayback is not { Phase: not (SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted) }
        && value.RateClaims.All(claim => claim.Phase == PlaybackLeasePhase.Confirmed)
        && value.SeekClaims.All(claim => claim.Phase is SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled);
}
