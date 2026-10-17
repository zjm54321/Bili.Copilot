// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Articles;

internal abstract record ArticleContentProjection(
    string ArticleId, string AuthorId, string AuthorName, DateTimeOffset PublishTime,
    string HtmlContent, string? AvatarUrl, ArticleCounters Counters);

// Both branches contain canonical, encoded body output, never raw readInfo JSON/HTML.
internal sealed record LegacyArticleProjection(
    string ArticleId, string AuthorId, string AuthorName, DateTimeOffset PublishTime,
    string HtmlContent, string? AvatarUrl, ArticleCounters Counters)
    : ArticleContentProjection(ArticleId, AuthorId, AuthorName, PublishTime, HtmlContent, AvatarUrl, Counters);

internal sealed record ModuleArticleProjection(
    string ArticleId, string AuthorId, string AuthorName, DateTimeOffset PublishTime,
    string Title, string HtmlContent, string? AvatarUrl, ArticleCounters Counters)
    : ArticleContentProjection(ArticleId, AuthorId, AuthorName, PublishTime, HtmlContent, AvatarUrl, Counters);

// Unknown is null, never a synthesized zero. Body-module display strings are not counts.
internal sealed record ArticleCounters(
    int? Views = null, int? Favorites = null, int? Likes = null,
    int? Replies = null, int? Shares = null, int? Coins = null);

// Only ParseMetadata creates this receipt. Missing metadata remains null in the reader;
// the body projection never claims authenticated liked/favorite state.
internal sealed record ArticleReadMetadata(
    string AuthorId, bool IsLiked, bool IsFavorite, ArticleCounters Counters);
