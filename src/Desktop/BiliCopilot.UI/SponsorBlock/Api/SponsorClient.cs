using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Api;

/// <summary>Typed opt-in protocol only. No cookies, arbitrary endpoints, retries, UI, or player ownership.</summary>
public sealed partial class SponsorClient : IDisposable
{
    public const string UserAgent = "BiliCopilot/2.2611.2.0";
    public static Uri Service { get; } = new("https://www.bsbsb.top/");
    private readonly SponsorNetworkConsent consent;
    private readonly SponsorTransport transport;

    public SponsorClient(SponsorNetworkConsent consent) : this(consent, new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        Credentials = null, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    }, TimeProvider.System, TimeSpan.FromSeconds(15)) { }

    // Internal fake-handler seam cannot change the production service host or inject Bili HttpClient defaults.
    internal SponsorClient(SponsorNetworkConsent consent, HttpMessageHandler handler, TimeProvider clock, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.consent = consent;
        transport = new(handler, clock, timeout);
    }

    internal static string JsonQuery<T>(string name, T value, JsonTypeInfo<T> type) =>
        Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(JsonSerializer.Serialize(value, type));
    internal static string StringQuery(string name, string value) => Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);

    private static T? Parse<T>(byte[] bytes, JsonTypeInfo<T> type) => JsonSerializer.Deserialize(bytes, type);
    private static bool Schema<T>(byte[] bytes, JsonTypeInfo<T> type, Func<T, bool> validate)
    {
        try { var value = Parse(bytes, type); return value is not null && validate(value); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return false; }
    }
    private static bool ValidRaw(SponsorSegment s) => s is not null && !s.Segment.IsDefault
        && s.Cid is not null && s.Uuid is not null && s.Category is not null && s.ActionType is not null;

    /// <summary>Raw exact-media records survive filtering for future display/vote workflows. Use query.Includes for executable selections.</summary>
    public async Task<ReadResult<ImmutableArray<SponsorSegment>>> GetSegmentsAsync(SegmentQuery query,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!consent.NetworkEnabled) return new(ReadStatus.Disabled);
        var path = "api/skipSegments/" + query.Media.VideoPrefix + "?"
            + JsonQuery("categories", query.Categories.ToArray(), SponsorJsonContext.Default.StringArray) + "&"
            + JsonQuery("actionTypes", query.Actions.ToArray(), SponsorJsonContext.Default.StringArray);
        var wire = await transport.ReadAsync(path, query.CacheIdentity, query.Actions.Length == 1 && query.Actions[0] == "full",
            refresh, b => Schema(b, SponsorJsonContext.Default.SegmentBucketArray,
                a => a.All(v => v is not null && v.VideoId is not null && !v.Segments.IsDefault && v.Segments.All(ValidRaw))), cancellationToken).ConfigureAwait(false);
        if (wire.Status == ReadStatus.Empty404) return new(ReadStatus.Empty404, [] , wire.HttpStatus);
        if (wire.Status != ReadStatus.Success) return new(wire.Status, HttpStatus: wire.HttpStatus);
        var buckets = Parse(wire.Bytes!, SponsorJsonContext.Default.SegmentBucketArray)!;
        var exact = buckets.Where(v => v.VideoId == query.Media.BvId).SelectMany(v => v.Segments)
            .Where(s => s.Cid == query.Media.Cid).ToImmutableArray();
        return new(exact.IsEmpty ? ReadStatus.Empty200 : ReadStatus.Success, exact, wire.HttpStatus);
    }

    /// <summary>Separate full-label read/cache pool, never a playback interval.</summary>
    public Task<ReadResult<ImmutableArray<SponsorSegment>>> GetFullLabelsAsync(MediaIdentity media,
        bool refresh = false, CancellationToken cancellationToken = default) => GetFullLabelsCoreAsync(media, refresh, cancellationToken);

    private async Task<ReadResult<ImmutableArray<SponsorSegment>>> GetFullLabelsCoreAsync(MediaIdentity media, bool refresh, CancellationToken token)
    {
        if (!consent.NetworkEnabled) return new(ReadStatus.Disabled);
        var categories = SponsorRules.Categories.Where(c => c.AllowedActions.Contains("full")).Select(c => c.Id).ToArray();
        var path = "api/skipSegments/" + media.VideoPrefix + "?" + JsonQuery("categories", categories, SponsorJsonContext.Default.StringArray)
            + "&" + JsonQuery("actionTypes", new[] { "full" }, SponsorJsonContext.Default.StringArray);
        var wire = await transport.ReadAsync(path, media.Combined + "|full", true, refresh,
            b => Schema(b, SponsorJsonContext.Default.SegmentBucketArray, a => a.All(v => v is not null && v.VideoId is not null
                && !v.Segments.IsDefault && v.Segments.All(ValidRaw))), token).ConfigureAwait(false);
        if (wire.Status == ReadStatus.Empty404) return new(ReadStatus.Empty404, [], wire.HttpStatus);
        if (wire.Status != ReadStatus.Success) return new(wire.Status, HttpStatus: wire.HttpStatus);
        var exact = Parse(wire.Bytes!, SponsorJsonContext.Default.SegmentBucketArray)!
            .Where(v => v.VideoId == media.BvId).SelectMany(v => v.Segments).Where(s => s.Cid == media.Cid).ToImmutableArray();
        return new(exact.IsEmpty ? ReadStatus.Empty200 : ReadStatus.Success, exact, wire.HttpStatus);
    }

    public async Task<ReadResult<ImmutableArray<string>>> GetLockedCategoriesAsync(MediaIdentity media,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!consent.NetworkEnabled) return new(ReadStatus.Disabled);
        var wire = await transport.ReadAsync("api/lockCategories/" + media.LockPrefix, media.Combined, false, refresh,
            b => Schema(b, SponsorJsonContext.Default.LockBucketArray, a => a.All(v => v is not null && v.VideoId is not null
                && !v.Categories.IsDefault && v.Categories.All(c => c is not null))), cancellationToken).ConfigureAwait(false);
        if (wire.Status == ReadStatus.Empty404) return new(ReadStatus.Empty404, [], wire.HttpStatus);
        if (wire.Status != ReadStatus.Success) return new(wire.Status, HttpStatus: wire.HttpStatus);
        var exact = Parse(wire.Bytes!, SponsorJsonContext.Default.LockBucketArray)!.Where(v => v.VideoId == media.Combined)
            .SelectMany(v => v.Categories).Distinct(StringComparer.Ordinal).ToImmutableArray();
        return new(exact.IsEmpty ? ReadStatus.Empty200 : ReadStatus.Success, exact, wire.HttpStatus);
    }

    internal static bool ValidPort(PortVideo p) => p is not null && !string.IsNullOrWhiteSpace(p.BvId)
        && !string.IsNullOrWhiteSpace(p.Cid) && !string.IsNullOrWhiteSpace(p.YoutubeId) && !string.IsNullOrWhiteSpace(p.Uuid);

    public async Task<ReadResult<PortVideo>> GetPortVideoAsync(MediaIdentity media, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!consent.NetworkEnabled) return new(ReadStatus.Disabled);
        var wire = await transport.ReadAsync("api/portVideo/" + media.VideoPrefix, media.Combined, false, refresh,
            b => Schema(b, SponsorJsonContext.Default.PortVideoArray, a => a.All(ValidPort)
                && a.Count(p => p.BvId == media.BvId && p.Cid == media.Cid) <= 1), cancellationToken).ConfigureAwait(false);
        if (wire.Status != ReadStatus.Success) return new(wire.Status, HttpStatus: wire.HttpStatus);
        var exact = Parse(wire.Bytes!, SponsorJsonContext.Default.PortVideoArray)!.Where(v => v.BvId == media.BvId && v.Cid == media.Cid).ToArray();
        if (exact.Length > 1) return new(ReadStatus.SchemaFailure, HttpStatus: wire.HttpStatus, Reason: "AmbiguousPortIdentity");
        return new(exact.Length == 0 ? ReadStatus.Empty200 : ReadStatus.Success, exact.FirstOrDefault(), wire.HttpStatus);
    }

    public async Task<ReadResult<SponsorUserInfo>> GetUserInfoAsync(string publicUserId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!consent.NetworkEnabled) return new(ReadStatus.Disabled);
        if (publicUserId.Length != 64 || publicUserId.Any(c => !char.IsAsciiHexDigit(c)))
            return new(ReadStatus.SchemaFailure, Reason: "InvalidPublicIdentity");
        var path = "api/userInfo?" + StringQuery("publicUserID", publicUserId) + "&"
            + JsonQuery("values", new[] { "userName", "viewCount", "minutesSaved", "vip", "permissions", "segmentCount" }, SponsorJsonContext.Default.StringArray);
        var wire = await transport.ReadAsync(path, publicUserId, false, refresh,
            b => Schema(b, SponsorJsonContext.Default.SponsorUserInfo, _ => true), cancellationToken).ConfigureAwait(false);
        return wire.Status == ReadStatus.Success
            ? new(ReadStatus.Success, Parse(wire.Bytes!, SponsorJsonContext.Default.SponsorUserInfo), wire.HttpStatus)
            : new(wire.Status, HttpStatus: wire.HttpStatus);
    }

    internal (int Entries, int Bytes, int Inflight) InspectCache(bool labels = false) => transport.InspectCache(labels);
    public void Dispose() => transport.Dispose();
}
