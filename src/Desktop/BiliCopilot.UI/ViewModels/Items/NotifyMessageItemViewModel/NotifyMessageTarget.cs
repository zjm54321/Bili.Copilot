// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Models.User;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Items;

internal enum NotifyMessageTargetKind { Web, Video, Dynamic, Article }

/// <summary>Decodes content identity, never the notification's own ID or a comment ID.</summary>
internal sealed record NotifyMessageTarget(NotifyMessageTargetKind Kind, string? Id, Uri? WebUri)
{
    internal static NotifyMessageTarget Resolve(NotifyMessage message)
    {
        // SDK 0.0.2-preview2 projects item.uri to SourceId. Type is the notification
        // category (Reply/At/Like), not item.type; Properties may carry extra metadata.
        var candidates = new[] { message.SourceId, Property(message, "uri"), Property(message, "nativeuri") };
        Uri? fallback = null;
        foreach (var candidate in candidates)
        {
            var target = FromUri(candidate);
            if (target.Kind != NotifyMessageTargetKind.Web) return target;
            fallback ??= target.WebUri;
        }

        var type = Property(message, "type") ?? message.Business;
        var id = Property(message, "subjectid") ?? Property(message, "sourceid") ?? message.SourceId;
        if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase) || type == "视频")
        {
            var videoId = VideoId(Property(message, "bvid") ?? Property(message, "aid") ?? id, allowNumber: true);
            if (videoId is not null) return Video(videoId);
        }
        else if ((string.Equals(type, "article", StringComparison.OrdinalIgnoreCase) || type == "专栏") && NumberId(id) is string articleId)
        {
            return Article(articleId);
        }
        else if (string.Equals(type, "dynamic", StringComparison.OrdinalIgnoreCase) && NumberId(id) is string dynamicId)
        {
            return Dynamic(dynamicId);
        }

        return new(NotifyMessageTargetKind.Web, null, fallback);
    }

    private static NotifyMessageTarget FromUri(string? value)
    {
        var empty = new NotifyMessageTarget(NotifyMessageTargetKind.Web, null, null);
        if (string.IsNullOrWhiteSpace(value)) return empty;
        value = value.Trim();
        if (VideoId(value, allowNumber: false) is string bareVideoId) return Video(bareVideoId);
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return empty;
        var web = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        var native = uri.Scheme == "bilibili";
        if (!web && !native) return empty;
        var fallback = web ? new NotifyMessageTarget(NotifyMessageTargetKind.Web, null, uri) : empty;
        if (web && !IsBiliHost(uri.Host)) return fallback;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = native ? uri.Host : segments.FirstOrDefault() ?? string.Empty;
        var item = native ? segments.FirstOrDefault() : segments.Skip(1).FirstOrDefault();
        if (string.Equals(route, "video", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = VideoId(item ?? Query(uri, "bvid") ?? Query(uri, "aid"), allowNumber: true);
            if (videoId is not null) return Video(videoId);
        }
        else if (string.Equals(route, "read", StringComparison.OrdinalIgnoreCase)
            || (native && string.Equals(route, "article", StringComparison.OrdinalIgnoreCase)))
        {
            var articleId = item;
            if (articleId?.StartsWith("cv", StringComparison.OrdinalIgnoreCase) == true) articleId = articleId[2..];
            articleId = NumberId(articleId ?? Query(uri, "id"));
            if (articleId is not null) return Article(articleId);
        }
        else if (string.Equals(route, "opus", StringComparison.OrdinalIgnoreCase)
            || (native && string.Equals(route, "dynamic", StringComparison.OrdinalIgnoreCase)))
        {
            if (NumberId(item) is string dynamicId) return Dynamic(dynamicId);
        }
        else if (web && string.Equals(uri.Host, "t.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            if (NumberId(route) is string dynamicId) return Dynamic(dynamicId);
        }
        else if (!native)
        {
            if (VideoId(route, allowNumber: false) is string videoId) return Video(videoId);
            if (route.StartsWith("cv", StringComparison.OrdinalIgnoreCase) && NumberId(route[2..]) is string articleId) return Article(articleId);
        }

        return fallback;
    }

    private static bool IsBiliHost(string host) => string.Equals(host, "bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);

    private static string? VideoId(string? value, bool allowNumber)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("av", StringComparison.OrdinalIgnoreCase)) return NumberId(value[2..]);
        // The existing BV converter expects the canonical 12-character, base58 identifier.
        if (value.Length == 12 && value.StartsWith("BV1", StringComparison.Ordinal)
            && value.Skip(3).All(c => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c)))
        {
            return NumberId(IdToolkit.Bv2Av(value).ToString(CultureInfo.InvariantCulture));
        }

        return allowNumber ? NumberId(value) : null;
    }

    private static string? NumberId(string? value) => value is { Length: > 0 }
        && value.All(c => c is >= '0' and <= '9')
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? value : null;

    private static string? Property(NotifyMessage message, string name)
    {
        if (message.Properties is null) return null;
        foreach (var pair in message.Properties)
        {
            if (string.Equals(pair.Key.Replace("_", string.Empty, StringComparison.Ordinal), name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value)) return pair.Value;
        }

        return null;
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }

        return null;
    }

    private static NotifyMessageTarget Video(string id) => new(NotifyMessageTargetKind.Video, id, new Uri($"https://www.bilibili.com/video/av{id}"));
    private static NotifyMessageTarget Article(string id) => new(NotifyMessageTargetKind.Article, id, new Uri($"https://www.bilibili.com/read/cv{id}"));
    // This checkout has no dynamic-detail route: retain the actual dynamic identity in
    // the browser fallback rather than sending the user to a feed or an unrelated video.
    private static NotifyMessageTarget Dynamic(string id) => new(NotifyMessageTargetKind.Dynamic, id, new Uri($"https://t.bilibili.com/{id}"));
}
