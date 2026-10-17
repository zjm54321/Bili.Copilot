using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Api;

public sealed partial class SponsorClient
{
    private static WriteResult NotSent(string reason) => new(WriteStatus.NotSent, Reason: reason);
    private static bool HasSecret(string? privateId) => !string.IsNullOrWhiteSpace(privateId) && privateId.Length >= 30 && privateId.Length <= 1024;
    private static bool HasUuid(string? uuid) => !string.IsNullOrWhiteSpace(uuid) && uuid.Length <= 256;
    private WriteResult? Authorize(ManualWriteIntent? intent, MediaIdentity media, ManualOperation operation, CancellationToken token)
    {
        if (!consent.NetworkEnabled) return NotSent("NetworkDisabled");
        if (token.IsCancellationRequested) return NotSent("CancelledBeforeDispatch");
        if (intent is null || !intent.Consume(media, operation)) return NotSent("MissingMismatchedOrUsedManualIntent");
        return null;
    }

    // Bytes are captured before dispatch. No arbitrary HTTP/command surface and no automatic mutation replay.
    private async Task<WriteResult> WriteAsync(string path, byte[]? body, bool requires200,
        Func<WireResponse, WriteResult>? confirm, CancellationToken token)
    {
        if (token.IsCancellationRequested) return NotSent("CancelledBeforeDispatch");
        if (body?.Length > SponsorTransport.ResponseByteLimit) return NotSent("PayloadTooLarge");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Service, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var dispatched = false;
        var wire = await transport.ExchangeAsync(request, token, () => dispatched = true, readBody: confirm is not null).ConfigureAwait(false);
        if (wire.Status is ReadStatus.Cancelled or ReadStatus.TransportFailure)
            return dispatched ? new(WriteStatus.Unknown, wire.HttpStatus, "DispatchOutcomeUnknown", BatchAccepted: requires200 && wire.HttpStatus == 200)
                : NotSent("CancelledBeforeDispatch");
        if (wire.Status == ReadStatus.HttpFailure)
        {
            if (wire.HttpStatus >= 500) return new(WriteStatus.Unknown, wire.HttpStatus, "ServiceOutcomeUnknown");
            return new(WriteStatus.Rejected, wire.HttpStatus, wire.HttpStatus == 405 ? "Duplicate" : "HttpRejected");
        }
        if (wire.Status == ReadStatus.SchemaFailure) return new(WriteStatus.Unknown, wire.HttpStatus, "ResponseTooLarge",
            BatchAccepted: requires200 && wire.HttpStatus == 200);
        if (requires200 && wire.HttpStatus != 200) return new(WriteStatus.Unknown, wire.HttpStatus, "UnexpectedSubmissionStatus");
        return confirm is null ? new(WriteStatus.Accepted, wire.HttpStatus) : confirm(wire);
    }

    private static byte[] Body<T>(T payload, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(payload, type);

    public Task<WriteResult> SubmitAsync(MediaIdentity media, ImmutableArray<SegmentDraft> drafts, double duration,
        string privateId, ManualWriteIntent? intent, CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.Submit, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || drafts.IsDefaultOrEmpty || drafts.Length > 100 || !double.IsFinite(duration) || duration <= 0
            || drafts.Any(d => d is null || SponsorRules.ValidateDraft(d, media, duration) is not null)
            || drafts.Select(d => d.Segment.Uuid).Distinct(StringComparer.Ordinal).Count() != drafts.Length)
            return Task.FromResult(NotSent("InvalidSubmission"));
        var segments = drafts.Select(d => d.Segment).ToImmutableArray();
        var bytes = Body(new SubmitPayload(media.BvId, media.Cid, privateId, segments, duration, UserAgent), SponsorJsonContext.Default.SubmitPayload);
        return WriteAsync("api/skipSegments", bytes, true, w => ConfirmSubmission(w, segments), cancellationToken);
    }

    private static WriteResult ConfirmSubmission(WireResponse wire, ImmutableArray<SponsorSegment> submitted)
    {
        // A 200 acknowledges a batch, not safe draft removal. Exact payload correspondence + unique UUIDs are mandatory.
        if (!Schema(wire.Bytes!, SponsorJsonContext.Default.SponsorSegmentArray, a => a.All(ValidRaw)))
            return new(WriteStatus.Unknown, wire.HttpStatus, "UnconfirmedBatch", BatchAccepted: true);
        var ack = Parse(wire.Bytes!, SponsorJsonContext.Default.SponsorSegmentArray)!;
        var confirmed = ack.Length == submitted.Length && ack.All(s => HasUuid(s.Uuid))
            && ack.Select(s => s.Uuid).Distinct(StringComparer.Ordinal).Count() == ack.Length;
        for (var i = 0; confirmed && i < ack.Length; i++)
        {
            var a = ack[i]; var s = submitted[i];
            confirmed = a.Cid == s.Cid && a.Category == s.Category && a.ActionType == s.ActionType && a.Segment.SequenceEqual(s.Segment)
                && (!submitted.Any(other => other.Uuid == a.Uuid) || a.Uuid == s.Uuid);
        }
        return confirmed ? new(WriteStatus.Accepted, wire.HttpStatus, BatchAccepted: true, ItemsConfirmed: true, ConfirmedItems: ack.ToImmutableArray())
            : new(WriteStatus.Unknown, wire.HttpStatus, "UnconfirmedBatch", BatchAccepted: true);
    }

    public Task<WriteResult> VoteSegmentAsync(MediaIdentity media, string segmentUuid, SponsorVote vote, string privateId,
        ManualWriteIntent? intent, CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.Vote, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || !HasUuid(segmentUuid) || !Enum.IsDefined(vote)) return Task.FromResult(NotSent("InvalidVote"));
        var path = "api/voteOnSponsorTime?" + StringQuery("UUID", segmentUuid) + "&" + StringQuery("userID", privateId)
            + "&" + JsonQuery("type", (int)vote, SponsorJsonContext.Default.Int32);
        return WriteAsync(path, null, false, null, cancellationToken);
    }

    public Task<WriteResult> ChangeCategoryAsync(MediaIdentity media, SponsorSegment original, string newCategory, string privateId,
        ManualWriteIntent? intent, CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.ChangeCategory, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || original is null || original.Cid != media.Cid || !HasUuid(original.Uuid)
            || !SponsorRules.Allows(newCategory, original.ActionType)) return Task.FromResult(NotSent("InvalidCategoryChange"));
        var path = "api/voteOnSponsorTime?" + StringQuery("UUID", original.Uuid) + "&" + StringQuery("userID", privateId)
            + "&" + StringQuery("category", newCategory);
        return WriteAsync(path, null, false, null, cancellationToken);
    }

    public Task<WriteResult> SetUsernameAsync(MediaIdentity media, string username, string privateId, ManualWriteIntent? intent,
        CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.SetUsername, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || string.IsNullOrWhiteSpace(username) || username.Length > 128) return Task.FromResult(NotSent("InvalidUsername"));
        return WriteAsync("api/setUsername?" + StringQuery("userID", privateId) + "&" + StringQuery("username", username), null, false, null, cancellationToken);
    }

    public Task<WriteResult> BindPortAsync(MediaIdentity media, string youtubeId, double duration, string privateId,
        ManualWriteIntent? intent, CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.BindPort, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || string.IsNullOrWhiteSpace(youtubeId) || youtubeId.Length > 128 || !double.IsFinite(duration) || duration <= 0)
            return Task.FromResult(NotSent("InvalidPortBinding"));
        var bytes = Body(new PortBindPayload(media.BvId, media.Cid, youtubeId, duration, privateId, UserAgent), SponsorJsonContext.Default.PortBindPayload);
        return WriteAsync("api/portVideo", bytes, false, w =>
        {
            if (!Schema(w.Bytes!, SponsorJsonContext.Default.PortVideo, ValidPort)) return new(WriteStatus.Unknown, w.HttpStatus, "UnconfirmedPort");
            var port = Parse(w.Bytes!, SponsorJsonContext.Default.PortVideo)!;
            return port.BvId == media.BvId && port.Cid == media.Cid && port.YoutubeId == youtubeId
                ? new(WriteStatus.Accepted, w.HttpStatus, ConfirmedPort: port)
                : new(WriteStatus.Unknown, w.HttpStatus, "MismatchedPortAcknowledgement");
        }, cancellationToken);
    }

    /// <summary>The pinned caller sends combined BV+CID unchanged in the misleadingly named bvID field.</summary>
    public Task<WriteResult> VotePortAsync(MediaIdentity media, PortVideo port, SponsorVote vote, string privateId,
        ManualWriteIntent? intent, CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.VotePort, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasSecret(privateId) || port is null || !ValidPort(port) || port.BvId != media.BvId || port.Cid != media.Cid
            || !Enum.IsDefined(vote)) return Task.FromResult(NotSent("InvalidPortVote"));
        return WriteAsync("api/votePort", Body(new PortVotePayload(port.Uuid, media.Combined, privateId, (int)vote),
            SponsorJsonContext.Default.PortVotePayload), false, null, cancellationToken);
    }

    public Task<WriteResult> UpdatePortedSegmentsAsync(MediaIdentity media, PortVideo port, ManualWriteIntent? intent,
        CancellationToken cancellationToken = default)
    {
        var denied = Authorize(intent, media, ManualOperation.UpdatePort, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (port is null || !ValidPort(port) || port.BvId != media.BvId || port.Cid != media.Cid) return Task.FromResult(NotSent("InvalidPortIdentity"));
        return WriteAsync("api/updatePortedSegments", Body(new PortUpdatePayload(media.BvId, port.Uuid, media.Cid), SponsorJsonContext.Default.PortUpdatePayload),
            false, null, cancellationToken);
    }

    public Task<WriteResult> ReportViewedAsync(MediaIdentity media, string segmentUuid, ManualWriteIntent? intent,
        CancellationToken cancellationToken = default)
    {
        if (!consent.ViewedReportingEnabled) return Task.FromResult(NotSent("ViewedReportingDisabled"));
        var denied = Authorize(intent, media, ManualOperation.ReportViewed, cancellationToken);
        if (denied is not null) return Task.FromResult(denied);
        if (!HasUuid(segmentUuid)) return Task.FromResult(NotSent("InvalidSegmentUuid"));
        return WriteAsync("api/viewedVideoSponsorTime?" + StringQuery("UUID", segmentUuid), null, false, null, cancellationToken);
    }
}
