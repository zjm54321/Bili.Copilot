// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Services.Articles;

/// <summary>Composes canonical native/API article body output with optional authenticated SDK metadata.</summary>
public sealed class ArticleDetailReader
{
    private readonly ArticleRawHtmlClient _raw;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendMetadata;
    private readonly Action<HttpRequestMessage, Dictionary<string, string>> _authorizeMetadata;
    private readonly ILogger<ArticleDetailReader>? _logger;

    /// <summary>Uses the registered shared SDK services without changing their transport or authentication.</summary>
    public ArticleDetailReader(ArticleRawHtmlClient raw, BiliHttpClient client,
        BiliAuthenticator authenticator, ILogger<ArticleDetailReader> logger)
        : this(raw, client.SendAsync, (request, parameters) => authenticator.AuthorizeRestRequest(request, parameters), logger) { }

    // Explicit offline boundaries only; production uses the public SDK methods above, never reflection.
    internal ArticleDetailReader(ArticleRawHtmlClient raw,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendMetadata,
        Action<HttpRequestMessage, Dictionary<string, string>> authorizeMetadata, ILogger<ArticleDetailReader>? logger = null)
    {
        _raw = raw;
        _sendMetadata = sendMetadata;
        _authorizeMetadata = authorizeMetadata;
        _logger = logger;
    }

    /// <summary>Reads native content first, with a single API fallback and best-effort SDK metadata.</summary>
    public async Task<ArticleDetail?> GetArticleContentAsync(ArticleIdentifier identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ArticleRawHtmlClient.ValidateArticleId(identifier.Id);
        try
        {
            var projection = await ReadBodyAsync(id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Only viewinfo establishes interaction state; its failure must not discard the body.
            var metadata = await TryReadMetadataAsync(id, projection.AuthorId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var avatar = projection.AvatarUrl is null ? null : new BiliImage(new Uri(projection.AvatarUrl, UriKind.Absolute));
            var profile = new UserDetailProfile(new UserProfile(projection.AuthorId, projection.AuthorName, avatar));
            var counts = metadata?.Counters ?? projection.Counters;
            var community = new ArticleCommunityInformation(id, counts.Views, counts.Favorites, counts.Likes,
                counts.Replies, counts.Shares, counts.Coins);
            // Keep the caller's identifier/title/summary/cover and original comment identity.
            // Tags are not available in this projection; [] is not evidence that the server has no tags.
            return new ArticleDetail(identifier, new UserCard(profile, null), community,
                projection.HtmlContent, projection.PublishTime, [], metadata?.IsLiked ?? false, metadata?.IsFavorite ?? false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // SDK SendAsync may wrap cancellation; the VM still receives its own cancelled operation.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<ArticleContentProjection> ReadBodyAsync(string articleId, CancellationToken cancellationToken)
    {
        // Native can return HTTP 200 with empty readInfo for migrated articles. Resolve
        // their canonical CV redirect once before the rate-limited article/view fallback.
        foreach (var canonical in new[] { false, true })
        {
            try
            {
                var html = await _raw.GetHtmlPageAsync(articleId, canonical, cancellationToken).ConfigureAwait(false);
                return ArticleContentParser.ParseNativeHtml(html, articleId, cancellationToken);
            }
            catch (ArticleCompatibilityException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (ex.Failure is ArticleCompatibilityFailure.IdentityMismatch or ArticleCompatibilityFailure.UnsafeUrl) throw;
                _logger?.LogWarning("文章 HTML 路径失败（{Route}，{Failure}）", canonical ? "Canonical" : "Native", ex.Failure);
            }
        }
        // Each fixed route is tried once; never retry a remote -509 response.
        var json = await _raw.GetArticleViewJsonAsync(articleId, cancellationToken).ConfigureAwait(false);
        return ArticleContentParser.ParseArticleViewJson(json, articleId, cancellationToken);
    }

    private async Task<ArticleReadMetadata?> TryReadMetadataAsync(string articleId, string authorId, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadMetadataAsync(articleId, authorId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The SDK may wrap transport/API errors. Keep body access independent of authentication.
            // ArticleDetail requires bool flags, so unavailable interaction state uses its false defaults.
            return null;
        }
    }

    private async Task<ArticleReadMetadata> ReadMetadataAsync(string articleId, string authorId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Article.ArticleViewInfo));
        _authorizeMetadata(request, new Dictionary<string, string> { ["id"] = articleId });
        cancellationToken.ThrowIfCancellationRequested();
        using var response = await _sendMetadata(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        response.EnsureSuccessStatusCode();
        // The SDK already buffered/validated this JSON. This bounds our decoding, not its hidden buffer.
        var json = await ArticleRawHtmlClient.ReadUtf8BodyAsync(response.Content,
            ArticleContentParser.MaximumMetadataCharacters * 4, ArticleContentParser.MaximumMetadataCharacters,
            cancellationToken).ConfigureAwait(false);
        return ArticleContentParser.ParseMetadata(json, authorId, cancellationToken);
    }
}
