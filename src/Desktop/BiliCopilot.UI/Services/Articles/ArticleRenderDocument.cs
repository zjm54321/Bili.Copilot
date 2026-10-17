// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Article;
using System.Net;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Services.Articles;

/// <summary>Pure document composition and exact ownership checks shared by the reader and reproduction.</summary>
internal static partial class ArticleRenderDocument
{
    internal static string Compose(string template, string css, string theme, ArticleDetail article,
        string renderNonce, string formattedViewCount)
    {
        if (article.CommunityInformation?.ViewCount is not >= 0)
            template = template.Replace(" · $view$次阅读", string.Empty);
        var values = new Dictionary<string, string>
        {
            ["theme"] = theme.ToLowerInvariant(), ["style"] = css,
            ["title"] = WebUtility.HtmlEncode(article.Identifier.Title ?? string.Empty),
            ["author"] = WebUtility.HtmlEncode(article.Author?.Profile?.User?.Name ?? string.Empty),
            ["date"] = article.PublishTime.ToString("yyyy-MM-dd HH:mm:ss"),
            ["view"] = formattedViewCount, ["body"] = article.HtmlContent ?? string.Empty, ["nonce"] = renderNonce,
        };
        return PlaceholderRegex().Replace(template, match => values[match.Groups[1].Value]);
    }

    // Recent WebView2 runtimes expose NavigateToString as this data URI, older ones as
    // about:blank. Never grant navigation permission to arbitrary data: documents.
    internal static string GetDataUri(string html)
        => "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

    internal static bool IsOwnedNavigation(bool awaitingStart, bool userInitiated, string? uri, string? expectedDataUri)
        => awaitingStart && !userInitiated && (uri == "about:blank"
            || expectedDataUri is not null && string.Equals(uri, expectedDataUri, StringComparison.Ordinal));

    [GeneratedRegex(@"\$(theme|style|title|author|date|view|body|nonce)\$")]
    private static partial Regex PlaceholderRegex();
}
