// Copyright (c) Bili Copilot. All rights reserved.
using System.Globalization;
using System.Text.RegularExpressions;
using BiliCopilot.UI.Models.Constants;

namespace BiliCopilot.UI.Classic.Backends.Web;

/// <summary>
/// Page-only routing using the official player.html parameter contract. No service/account calls.
/// CID is preferred to a part ordinal; this request has no verified ordinal, so never invent p.
/// </summary>
public static class WebPlaybackRoute
{
    public static Uri CreatePageUri(ClassicMediaItem item, double startSeconds)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsPrivate) throw new NotSupportedException("持久网页播放不支持私密会话。 ");
        if (item.PreferQuality is > 0)
            throw new NotSupportedException("网页后端不能保证指定画质；未打开网页。自动画质（0）可用。 ");
        if (item.PreferQuality is < 0 || !double.IsFinite(startSeconds) || startSeconds < 0
            || (item.StartPosition is double requested && (!double.IsFinite(requested) || requested < 0 || requested != startSeconds)))
            throw new ArgumentException("网页播放指令无效或与已解析起播位置不一致。 ");

        var explicitStart = item.StartPosition.HasValue || startSeconds != 0;
        var query = new List<string>();
        if (item.Type == BiliMediaType.Video)
        {
            var id = item.Video?.Identifier.Id ?? string.Empty;
            var bvid = IsBvid(id);
            var aid = id.StartsWith("av", StringComparison.Ordinal) ? id[2..] : id;
            if (!bvid && !IsPositiveInteger(aid)) throw InvalidIdentity();
            if (item.PreferPart is null && !explicitStart)
                return new Uri("https://www.bilibili.com/video/" + (bvid ? id : "av" + aid));
            query.Add(bvid ? "bvid=" + id : "aid=" + aid);
            if (item.PreferPart is not null)
            {
                var cid = item.PreferPart.Identifier.Id;
                if (!IsPositiveInteger(cid)) throw InvalidIdentity();
                query.Add("cid=" + cid);
            }
        }
        else if (item.Type == BiliMediaType.Pgc)
        {
            // A season is not an episode and does not identify a guessed first episode.
            var episodeId = item.Episode?.Identifier.Id;
            if (!IsPositiveInteger(episodeId) || item.PreferPart is not null) throw InvalidIdentity();
            if (!explicitStart) return new Uri("https://www.bilibili.com/bangumi/play/ep" + episodeId);
            query.Add("episodeId=" + episodeId);
            var seasonId = item.Season?.Identifier.Id;
            if (seasonId is not null)
            {
                if (!IsPositiveInteger(seasonId)) throw InvalidIdentity();
                query.Add("seasonId=" + seasonId);
            }
        }
        else if (item.Type == BiliMediaType.Live)
        {
            if (startSeconds != 0 || item.PreferPart is not null)
                throw new NotSupportedException("直播网页不能映射分P或非零起播位置。 ");
            var room = item.Live?.Identifier.Id;
            if (!IsPositiveInteger(room)) throw InvalidIdentity();
            return new Uri("https://live.bilibili.com/" + room);
        }
        else throw InvalidIdentity();

        if (explicitStart) query.Add("t=" + Uri.EscapeDataString(startSeconds.ToString("R", CultureInfo.InvariantCulture)));
        return new Uri("https://player.bilibili.com/player.html?" + string.Join("&", query));
    }

    internal static bool IsAllowedTopLevel(Uri uri, Uri initialPage, BiliMediaType type)
    {
        if (!IsHttps(uri)) return false;
        if (uri.Host == "player.bilibili.com")
        {
            if (!TryPlayerQuery(uri, out var query)) return false;
            var correctPurpose = type switch
            {
                BiliMediaType.Video => !query.ContainsKey("episodeId") && (query.ContainsKey("aid") || query.ContainsKey("bvid")),
                BiliMediaType.Pgc => query.ContainsKey("episodeId"),
                _ => false,
            };
            if (!correctPurpose) return false;
            if (initialPage.Host != "player.bilibili.com") return true;
            // Redirects may reorder/add documented parameters, but may not discard/change the
            // explicit identity/CID/start instructions. Reject instead of silently downgrading.
            if (!TryPlayerQuery(initialPage, out var instructions)) return false;
            foreach (var instruction in instructions)
                if (!query.TryGetValue(instruction.Key, out var value) || value != instruction.Value) return false;
            return true;
        }

        if (initialPage.Host == "player.bilibili.com") return false;
        // Query/trailing slash normalization redirects are allowed on these purpose paths;
        // every redirect is re-evaluated, never broad *.bilibili.com trust.
        return type switch
        {
            BiliMediaType.Video => uri.Host == "www.bilibili.com" && Regex.IsMatch(uri.AbsolutePath, @"^/video/(BV[0-9A-Za-z]+|av[0-9]+)/?$"),
            BiliMediaType.Pgc => uri.Host == "www.bilibili.com" && Regex.IsMatch(uri.AbsolutePath, @"^/bangumi/play/ep[0-9]+/?$"),
            BiliMediaType.Live => uri.Host == "live.bilibili.com" && Regex.IsMatch(uri.AbsolutePath, @"^/[0-9]+/?$"),
            _ => false,
        };
    }

    internal static bool IsAllowedFrame(string address)
        => address == "about:blank" || (Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && IsHttps(uri) && uri.Host == "player.bilibili.com" && TryPlayerQuery(uri, out _));

    private static bool TryPlayerQuery(Uri uri, out Dictionary<string, string> query)
    {
        query = new(StringComparer.Ordinal);
        if (uri.AbsolutePath != "/player.html" || uri.Query.Length < 2) return false;
        foreach (var field in uri.Query[1..].Split('&'))
        {
            var pair = field.Split('=', 2);
            if (pair.Length != 2) return false;
            var key = Uri.UnescapeDataString(pair[0]);
            var value = Uri.UnescapeDataString(pair[1]);
            var valid = key switch
            {
                "aid" or "cid" or "p" or "episodeId" or "seasonId" => IsPositiveInteger(value),
                "bvid" => IsBvid(value),
                "t" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && double.IsFinite(seconds) && seconds >= 0,
                _ => false,
            };
            if (!valid || !query.TryAdd(key, value)) return false;
        }
        return query.ContainsKey("aid") || query.ContainsKey("bvid") || query.ContainsKey("episodeId");
    }

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static bool IsBvid(string value) => Regex.IsMatch(value, @"^BV[0-9A-Za-z]+$");
    private static bool IsPositiveInteger(string? value) => value is not null
        && Regex.IsMatch(value, @"^[0-9]+$") && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    private static Exception InvalidIdentity() => new NotSupportedException("缺少可验证的网页媒体标识、CID 或剧集标识；不会猜测分P序号或剧集。 ");
}
