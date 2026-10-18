// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiliCopilot.UI.Services.UserSpace;

/// <summary>Fixed read-only space endpoints; public SDK authorization, app-owned strict HTTP transport.</summary>
public sealed partial class UserSpaceService : IDisposable
{
    private const int PageSize = 30;
    private const int MaximumBodyBytes = 4 * 1024 * 1024;
    private readonly BiliAuthenticator _authenticator;
    private readonly HttpClient _client;
    private string? _wbiKey;
    private static readonly int[] WbiOrder = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];

    public UserSpaceService(BiliAuthenticator authenticator)
    {
        _authenticator = authenticator;
        _client = new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        });
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36");
    }

    public async Task<IReadOnlyList<UserSpaceVideoRow>> GetFeaturedAsync(string mid, bool followed, CancellationToken token)
    {
        using var json = await GetAsync(followed ? "/x/space/top/arc" : "/x/space/masterpiece", mid,
            new() { ["vmid"] = Id(mid) }, false, token);
        if (followed && Code(json.RootElement) == 53016) return Array.Empty<UserSpaceVideoRow>();
        var data = Data(json);
        if (followed) return new[] { Video(data) with { FeaturedKind = UserSpaceFeaturedKind.Top } };
        return ArrayItems(data, 3).Select(item => Video(item) with { FeaturedKind = UserSpaceFeaturedKind.Masterpiece }).ToArray();
    }

    public async Task<UserSpacePage<UserSpaceVideoRow>> GetVideosAsync(string mid, UserVideoOrder order, int page, CancellationToken token)
    {
        using var json = await GetAsync("/x/space/wbi/arc/search", mid, new()
        {
            ["mid"] = Id(mid), ["order"] = order switch
            {
                UserVideoOrder.Latest => "pubdate", UserVideoOrder.MostPlayed => "click", UserVideoOrder.MostFavorited => "stow",
                _ => throw Schema(),
            },
            ["pn"] = Page(page), ["ps"] = PageSize.ToString(CultureInfo.InvariantCulture),
        }, true, token);
        if (Code(json.RootElement) != 0) _wbiKey = null; // Only an explicit later retry obtains fresh keys.
        var data = Data(json);
        var rows = ArrayItems(Required(Required(data, "list"), "vlist")).Select(Video).ToArray();
        var more = HasMore(Required(data, "page"), page, "pn", "ps", "count");
        RequireProgress(rows.Length, more);
        return new(rows, more, checked(page + 1));
    }

    public async Task<UserSpacePage<UserSpaceOpusRow>> GetOpusAsync(string mid, UserOpusFilter filter, string? offset, CancellationToken token)
    {
        var query = new Dictionary<string, string> { ["host_mid"] = Id(mid), ["type"] = filter switch
        {
            UserOpusFilter.All => "all", UserOpusFilter.Article => "article", UserOpusFilter.Dynamic => "dynamic", _ => throw Schema(),
        } };
        if (!string.IsNullOrEmpty(offset)) query["offset"] = Id(offset);
        using var json = await GetAsync("/x/polymer/web-dynamic/v1/opus/feed/space", mid, query, false, token);
        var data = Data(json);
        var rows = ArrayItems(Required(data, "items")).Select(item =>
        {
            var id = Identifier(Required(item, "opus_id"));
            var originalUri = OpusUrl(Text(item, "jump_url"), id);
            return new UserSpaceOpusRow(id, Text(item, "content"), ImageUrl(Text(Get(item, "cover"), "url"))?.AbsoluteUri.ToArticleCover().Uri,
                originalUri, Text(Get(item, "stat"), "like"), Text(Get(item, "stat"), "view"))
            {
                ArticleId = originalUri?.AbsolutePath.StartsWith("/read/cv", StringComparison.Ordinal) == true
                    ? originalUri.AbsolutePath[8..] : null,
            };
        }).ToArray();
        var more = Boolean(Required(data, "has_more"));
        var next = Text(data, "offset");
        RequireProgress(rows.Length, more);
        if (more && (string.IsNullOrEmpty(next) || next == offset)) throw Schema();
        return new(rows, more, NextOffset: next);
    }

    public async Task<UserSpacePage<UserSpaceAudioRow>> GetAudioAsync(string mid, int page, CancellationToken token)
    {
        using var json = await GetAsync("/audio/music-service/web/song/upper", mid, new()
        {
            ["uid"] = Id(mid), ["pn"] = Page(page), ["ps"] = "30", ["order"] = "1", ["jsonp"] = "jsonp",
        }, false, token);
        var data = Data(json);
        var count = RequiredLong(data, "pageCount");
        if (RequiredLong(data, "pageSize") <= 0) throw Schema();
        var songs = Required(data, "data");
        var rows = songs.ValueKind == JsonValueKind.Null ? Array.Empty<UserSpaceAudioRow>()
            : ArrayItems(songs).Select(item =>
            {
                var id = Identifier(Required(item, "id"));
                return new UserSpaceAudioRow(id, new Uri("https://www.bilibili.com/audio/au" + id));
            }).ToArray();
        var more = page < count;
        RequireProgress(rows.Length, more);
        return new(rows, more, checked(page + 1));
    }

    public async Task<UserSpacePage<UserSpaceCollection>> GetCollectionsAsync(string mid, int page, CancellationToken token)
    {
        // This list endpoint rejects page_size=30 with -400, including signed requests.
        // Collection archive endpoints below independently accept pages of 30 videos.
        using var json = await GetAsync("/x/polymer/web-space/seasons_series_list", mid, new()
        {
            ["mid"] = Id(mid), ["page_num"] = Page(page), ["page_size"] = "20", ["web_location"] = "333.1387",
        }, true, token);
        if (Code(json.RootElement) != 0) _wbiKey = null;
        var lists = Required(Data(json), "items_lists");
        var groups = new List<UserSpaceCollection>();
        foreach (var kind in new[] { UserCollectionKind.Season, UserCollectionKind.Series })
        {
            foreach (var item in ArrayItems(Required(lists, kind == UserCollectionKind.Season ? "seasons_list" : "series_list")))
            {
                var meta = Required(item, "meta");
                var owner = Identifier(Required(meta, "mid"));
                if (owner != mid) throw Schema();
                groups.Add(new(kind, Identifier(Required(meta, kind == UserCollectionKind.Season ? "season_id" : "series_id")),
                    owner, Text(meta, "name"), ImageUrl(Text(meta, "cover"))?.AbsoluteUri.ToImage(160, 90).Uri, Number(meta, "total")));
            }
        }
        var more = HasMore(Required(lists, "page"), page, "page_num", "page_size", "total");
        RequireProgress(groups.Count, more);
        return new(groups, more, checked(page + 1));
    }

    public async Task<UserSpacePage<UserSpaceVideoRow>> GetCollectionVideosAsync(UserSpaceCollection group, int page, CancellationToken token)
    {
        var season = group.Kind == UserCollectionKind.Season;
        using var json = await GetAsync(season ? "/x/polymer/web-space/seasons_archives_list" : "/x/series/archives", group.OwnerId,
            new() { ["mid"] = Id(group.OwnerId), [season ? "season_id" : "series_id"] = Id(group.Id),
                [season ? "page_num" : "pn"] = Page(page), [season ? "page_size" : "ps"] = "30" }, false, token);
        var data = Data(json);
        var pageInfo = Required(data, "page");
        var more = HasMore(pageInfo, page, season ? "page_num" : "num", season ? "page_size" : "size", "total");
        var archives = Required(data, "archives");
        // series/archives returns null, not [], for a valid empty series (total=0).
        var rows = archives.ValueKind == JsonValueKind.Null && RequiredLong(pageInfo, "total") == 0
            ? Array.Empty<UserSpaceVideoRow>() : ArrayItems(archives).Select(Video).ToArray();
        RequireProgress(rows.Length, more);
        return new(rows, more, checked(page + 1));
    }

    private async Task<JsonDocument> GetAsync(string path, string mid, Dictionary<string, string> query, bool wbi, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (wbi && _wbiKey is null) await InitializeWbiAsync(mid, token);
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://api.bilibili.com" + path));
        request.Headers.Referrer = new Uri("https://space.bilibili.com/" + Id(mid));
        // Do not also supply mobi_app/build: the Web authorizer owns its generated parameters.
        _authenticator.AuthorizeRestRequest(request, query, new BiliAuthorizeExecutionSettings
        { ApiType = BiliApiType.Web, ForceNoToken = true, RequireCookie = true });
        if (wbi)
        {
            // Public authorization supplies cookies/Web parameters. Its bootstrap cannot disable
            // redirects, so key acquisition/signing stay in this fixed-endpoint app adapter.
            query.Remove("sign");
            query["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signed = string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(new string(pair.Value.Where(c => !"!'()*".Contains(c)).ToArray()))));
            var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signed + _wbiKey))).ToLowerInvariant();
            request.RequestUri = new Uri("https://api.bilibili.com" + path + "?" + signed + "&w_rid=" + digest);
        }
        if (request.RequestUri is not { Scheme: "https", Host: "api.bilibili.com", Port: 443 } uri || uri.UserInfo.Length != 0)
            throw Schema();
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            if (wbi) _wbiKey = null;
            throw new UserSpaceDataException(UserSpaceLoadState.Error, $"主页请求失败（HTTP {(int)response.StatusCode}）。");
        }
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBodyBytes - (int)body.Length + 1)), token);
            if (count == 0) break;
            if (body.Length + count > MaximumBodyBytes) throw Schema();
            await body.WriteAsync(buffer.AsMemory(0, count), token);
        }
        token.ThrowIfCancellationRequested();
        try { return JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw Schema(); }
    }

    private async Task InitializeWbiAsync(string mid, CancellationToken token)
    {
        using var json = await GetAsync("/x/web-interface/nav", mid, new(), false, token);
        var code = Code(json.RootElement);
        // Public signing keys are documented even in the not-logged-in nav response.
        if (code is not (0 or -101)) throw new UserSpaceDataException(UserSpaceLoadState.Error, $"WBI 信息不可用（{code}）。");
        var image = Required(Required(json.RootElement, "data"), "wbi_img");
        var source = WbiPart(Text(image, "img_url")) + WbiPart(Text(image, "sub_url"));
        _wbiKey = new string(WbiOrder.Select(index => source[index]).ToArray());
    }

    private static string WbiPart(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw Schema();
        var key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (key.Length != 32 || !key.All(char.IsAsciiHexDigit)) throw Schema();
        return key; // These are key-shaped URLs, never fetched as images or followed.
    }

    private static UserSpaceVideoRow Video(JsonElement item)
    {
        var id = Identifier(Required(item, "aid"));
        var cover = ImageUrl(Text(item, "pic"));
        var owner = Get(item, "owner");
        var ownerId = owner.ValueKind == JsonValueKind.Object ? Number(owner, "mid") : Number(item, "mid");
        PublisherProfile? publisher = null;
        if (ownerId is > 0)
        {
            var avatar = ImageUrl(Text(owner, "face"));
            publisher = new(new UserProfile(ownerId.Value.ToString(CultureInfo.InvariantCulture),
                owner.ValueKind == JsonValueKind.Object ? Text(owner, "name") : Text(item, "author"), avatar?.AbsoluteUri.ToImage(96)));
        }
        var stat = Get(item, "stat");
        var counts = new VideoCommunityInformation(id, Number(stat, "view") ?? Number(item, "play"),
            Number(stat, "danmaku") ?? Number(item, "video_review"), Number(stat, "like"), Number(stat, "favorite"),
            Number(stat, "coin"), Number(stat, "reply") ?? Number(item, "comment"), Number(stat, "share"));
        var time = Number(item, "pubdate") ?? Number(item, "created");
        DateTimeOffset? published = time is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(time.Value) : null;
        var video = new VideoInformation(new MediaIdentifier(id, Text(item, "title"), cover?.AbsoluteUri.ToVideoCover()),
            publisher!, Number(item, "duration") ?? Duration(Text(item, "length")), Text(item, "bvid"), published, communityInformation: counts);
        var description = Text(item, "desc") ?? Text(item, "description");
        if (!string.IsNullOrWhiteSpace(description) && description.Trim() != "-") video.AddExtensionIfNotNull(VideoExtensionDataId.Description, description.Trim());
        return new(video, Text(item, "reason")); // Missing author/counters/reason remain missing, not the requested user's invented data.
    }

    private static long? Duration(string? text)
    {
        if (text is null) return null;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        long seconds = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < 0 || index > 0 && value >= 60) return null;
            seconds = checked(seconds * 60 + value);
        }
        return seconds;
    }

    private static JsonElement Data(JsonDocument document)
    {
        var code = Code(document.RootElement);
        if (code != 0) throw new UserSpaceDataException(UserSpaceLoadState.Error, $"主页接口返回错误（{code}）。");
        return Required(document.RootElement, "data");
    }
    private static int Code(JsonElement root) => Required(root, "code") is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var code) ? code : throw Schema();
    private static JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static JsonElement Required(JsonElement e, string name) => Get(e, name) is { ValueKind: not JsonValueKind.Undefined } value ? value : throw Schema();
    private static string? Text(JsonElement e, string name) => Get(e, name).ValueKind switch
    { JsonValueKind.Undefined or JsonValueKind.Null => null, JsonValueKind.String => Get(e, name).GetString(), _ => throw Schema() };
    private static long? Number(JsonElement e, string name)
    {
        var value = Get(e, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0) return number;
        throw Schema();
    }
    private static long RequiredLong(JsonElement e, string name) => Number(e, name) ?? throw Schema();
    private static bool Boolean(JsonElement e) => e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Schema() };
    private static JsonElement[] ArrayItems(JsonElement e, int maximum = 100)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() > maximum) throw Schema();
        return e.EnumerateArray().ToArray();
    }
    private static bool HasMore(JsonElement info, int requested, string pn, string ps, string count)
    {
        var number = RequiredLong(info, pn); var size = RequiredLong(info, ps); var total = RequiredLong(info, count);
        if (number != requested || size is <= 0 or > 100) throw Schema();
        return number * size < total;
    }
    private static void RequireProgress(int count, bool hasMore) { if (count == 0 && hasMore) throw Schema(); }
    private static string Page(int page) => page > 0 ? page.ToString(CultureInfo.InvariantCulture) : throw Schema();
    private static string Identifier(JsonElement value) => Id(value.ValueKind switch
    { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null });
    internal static string Id(string? id) => ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
        && id == n.ToString(CultureInfo.InvariantCulture) ? id! : throw Schema();
    private static Uri? ImageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return null;
        if (!(uri.Host == "hdslb.com" || uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "biliimg.com" || uri.Host.EndsWith(".biliimg.com", StringComparison.OrdinalIgnoreCase))) return null;
        // Normalize the source before applying the card's SDK thumbnail preset.
        var path = uri.AbsolutePath;
        var transform = path.IndexOf('@');
        return new UriBuilder(uri) { Scheme = "https", Port = -1, Path = transform < 0 ? path : path[..transform] }.Uri;
    }
    private static Uri? OpusUrl(string? text, string expectedId)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        else if (text.StartsWith('/')) text = "https://www.bilibili.com" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("www.bilibili.com" or "bilibili.com" or "m.bilibili.com")
            || uri.Port != 443 || uri.UserInfo.Length != 0) return null;

        var path = uri.AbsolutePath;
        if (path.EndsWith('/')) path = path[..^1];
        if (path.StartsWith("/read/cv", StringComparison.Ordinal))
        {
            var articleId = path[8..];
            if (!ulong.TryParse(articleId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number == 0 || articleId != number.ToString(CultureInfo.InvariantCulture)) return null;
            return new Uri("https://www.bilibili.com/read/cv" + articleId);
        }

        // 仅对已确认的站内身份去掉跟踪参数；不接受指向其他 opus 的跳转.
        return path == "/opus/" + expectedId ? new Uri("https://www.bilibili.com/opus/" + expectedId) : null;
    }
    internal static UserSpaceDataException Schema() => new(UserSpaceLoadState.Unavailable, "当前主页数据无法确认，请稍后重试或打开原页面。");
    public void Dispose() => _client.Dispose();
}
