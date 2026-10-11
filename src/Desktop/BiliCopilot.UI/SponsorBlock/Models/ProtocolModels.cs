using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BiliCopilot.UI.SponsorBlock.Models;

/// <summary>Raw server record: strings and tuple shape remain uncoerced, including unknown actions/categories/sources.</summary>
public sealed record SponsorSegment
{
    [JsonPropertyName("segment")] public required ImmutableArray<double> Segment { get; init; }
    [JsonPropertyName("cid")] public required string Cid { get; init; }
    [JsonPropertyName("UUID")] public required string Uuid { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("actionType")] public required string ActionType { get; init; }
    [JsonPropertyName("locked")] public double? Locked { get; init; }
    [JsonPropertyName("hidden")] public int? Hidden { get; init; }
    [JsonPropertyName("source")] public int? Source { get; init; }
    [JsonPropertyName("videoDuration")] public double? VideoDuration { get; init; }
}

public sealed record PortVideo
{
    [JsonPropertyName("bvID")] public required string BvId { get; init; }
    [JsonPropertyName("cid")] public required string Cid { get; init; }
    [JsonPropertyName("ytbID")] public required string YoutubeId { get; init; }
    [JsonPropertyName("UUID")] public required string Uuid { get; init; }
    [JsonPropertyName("votes")] public double? Votes { get; init; }
    [JsonPropertyName("locked")] public bool? Locked { get; init; }
}

/// <summary>Missing optional capabilities remain null, not a fabricated permission.</summary>
public sealed record SponsorUserInfo
{
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("viewCount")] public double? ViewCount { get; init; }
    [JsonPropertyName("minutesSaved")] public double? MinutesSaved { get; init; }
    [JsonPropertyName("vip")] public JsonElement? Vip { get; init; }
    [JsonPropertyName("permissions")] public JsonElement? Permissions { get; init; }
    [JsonPropertyName("segmentCount")] public double? SegmentCount { get; init; }
}

/// <summary>Neutral shareable persistence contract; deliberately cannot carry a private ID.</summary>
public sealed record SegmentDraft
{
    public int Version { get; init; } = 1;
    public required string MediaKey { get; init; }
    public required string Revision { get; init; }
    public required SponsorSegment Segment { get; init; }
}

public enum ReadStatus { Success, Empty200, Empty404, Disabled, Cancelled, Busy, HttpFailure, TransportFailure, SchemaFailure }
public sealed record ReadResult<T>(ReadStatus Status, T? Value = default, int? HttpStatus = null, string? Reason = null)
{
    public bool IsSuccess => Status is ReadStatus.Success or ReadStatus.Empty200 or ReadStatus.Empty404;
}

public enum WriteStatus { NotSent, Accepted, Rejected, Unknown }
public sealed record WriteResult(WriteStatus Status, int? HttpStatus = null, string? Reason = null,
    bool BatchAccepted = false, bool ItemsConfirmed = false, ImmutableArray<SponsorSegment> ConfirmedItems = default,
    PortVideo? ConfirmedPort = null);

public enum ManualOperation { Submit, Vote, ChangeCategory, SetUsername, BindPort, VotePort, UpdatePort, ReportViewed }
public enum SponsorVote { Down = 0, Up = 1 }

/// <summary>One-use intent captured by a future explicit UI action, bound to the original immutable part.</summary>
public sealed class ManualWriteIntent
{
    private int consumed;
    private ManualWriteIntent(MediaIdentity media, ManualOperation operation) { Media = media; Operation = operation; }
    public MediaIdentity Media { get; }
    public ManualOperation Operation { get; }
    public static ManualWriteIntent Capture(MediaIdentity media, ManualOperation operation) => new(media, operation);
    internal bool Consume(MediaIdentity media, ManualOperation operation) =>
        Media == media && Operation == operation && Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
}

internal sealed record SegmentBucket
{
    [JsonPropertyName("videoID")] public required string VideoId { get; init; }
    [JsonPropertyName("segments")] public required ImmutableArray<SponsorSegment> Segments { get; init; }
}
internal sealed record LockBucket
{
    [JsonPropertyName("videoID")] public required string VideoId { get; init; }
    [JsonPropertyName("categories")] public required ImmutableArray<string> Categories { get; init; }
}
internal sealed record SubmitPayload(
    [property: JsonPropertyName("videoID")] string VideoId,
    [property: JsonPropertyName("cid")] string Cid,
    [property: JsonPropertyName("userID")] string UserId,
    [property: JsonPropertyName("segments")] ImmutableArray<SponsorSegment> Segments,
    [property: JsonPropertyName("videoDuration")] double VideoDuration,
    [property: JsonPropertyName("userAgent")] string UserAgent);
internal sealed record PortBindPayload(
    [property: JsonPropertyName("bvID")] string BvId,
    [property: JsonPropertyName("cid")] string Cid,
    [property: JsonPropertyName("ytbID")] string YoutubeId,
    [property: JsonPropertyName("biliDuration")] double Duration,
    [property: JsonPropertyName("userID")] string UserId,
    [property: JsonPropertyName("userAgent")] string UserAgent);
internal sealed record PortVotePayload(
    [property: JsonPropertyName("UUID")] string Uuid,
    [property: JsonPropertyName("bvID")] string BvId,
    [property: JsonPropertyName("userID")] string UserId,
    [property: JsonPropertyName("type")] int Type);
internal sealed record PortUpdatePayload(
    [property: JsonPropertyName("videoID")] string VideoId,
    [property: JsonPropertyName("UUID")] string Uuid,
    [property: JsonPropertyName("cid")] string Cid);
