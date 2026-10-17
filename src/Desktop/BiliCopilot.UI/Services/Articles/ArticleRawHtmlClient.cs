// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;

namespace BiliCopilot.UI.Services.Articles;

/// <summary>Unauthenticated, fixed-route article transport; never borrows SDK cookies or credentials.</summary>
public sealed class ArticleRawHtmlClient : IDisposable
{
    internal const int MaximumBodyBytes = 8 * 1024 * 1024;
    internal const int MaximumRedirects = 3;
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36 Edg/116.0.1938.69";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _client;

    /// <summary>Creates the app-owned singleton transport with ordinary TLS validation.</summary>
    public ArticleRawHtmlClient() : this(new HttpClientHandler
    {
        UseCookies = false,
        UseDefaultCredentials = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    }) { }

    // Offline fixtures own only their supplied handler; no fallback to a real handler exists here.
    internal ArticleRawHtmlClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>Reads the fallback article/view JSON body once; no redirects or implicit retry.</summary>
    public async Task<string> GetArticleViewJsonAsync(string articleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateArticleId(articleId);
        var uri = new Uri("https://api.bilibili.com/x/article/view?id=" + id + "&gaia_source=main_web");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRedirect(response.StatusCode))
                throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsafeUrl);
            if (!response.IsSuccessStatusCode)
                throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count != 0)
                throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsupportedFormat);
            var json = await ReadUtf8BodyAsync(response.Content, MaximumBodyBytes,
                ArticleContentParser.MaximumJsonCharacters, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return json;
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
        }
    }

    // Empty native state is common for migrated articles. The canonical CV route supplies
    // the server-validated CV-to-opus redirect; never guess an opus ID from an article ID.
    internal async Task<string> GetHtmlPageAsync(string articleId, bool canonical,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateArticleId(articleId);
        var current = new Uri(canonical ? "https://www.bilibili.com/read/cv" + id + "/"
            : "https://www.bilibili.com/read/native/?id=" + id);
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaximumRedirects || response.Headers.Location is not { } location)
                        throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsafeUrl);
                    current = ValidateRedirect(current, location, id);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentEncoding.Count != 0)
                    throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsupportedFormat);
                var html = await ReadUtf8BodyAsync(response.Content, MaximumBodyBytes,
                    ArticleContentParser.MaximumHtmlCharacters, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return html;
            }
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Transport timeout is unavailable content, not caller cancellation or retry permission.
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MissingContent);
        }
    }

    internal static string ValidateArticleId(string? value)
    {
        if (value is null || value.Length is 0 or > 20
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0
            || !string.Equals(value, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.IdentityMismatch);
        return value;
    }

    internal static bool IsVerificationPage(string html)
        => html.Contains("risk-captcha-app", StringComparison.Ordinal)
            || html.Contains("window._riskdata_", StringComparison.Ordinal);

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri ValidateRedirect(Uri previous, Uri location, string articleId)
    {
        if (!Uri.TryCreate(previous, location, out var next)
            || next.Scheme != Uri.UriSchemeHttps || next.Port != 443
            || !string.Equals(next.IdnHost, "www.bilibili.com", StringComparison.OrdinalIgnoreCase)
            || next.UserInfo.Length != 0 || next.Fragment.Length != 0)
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsafeUrl);
        var path = next.AbsolutePath;
        var opusId = path.StartsWith("/opus/", StringComparison.Ordinal) ? path[6..] : string.Empty;
        var isOpus = opusId.Length is > 0 and <= 20 && opusId.All(char.IsAsciiDigit)
            && ulong.TryParse(opusId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) && numericId > 0;
        if (!(path == "/read/cv" + articleId || path == "/read/cv" + articleId + "/"
                || path == "/read/native/" || isOpus)
            || path.Contains('\\') || Uri.UnescapeDataString(path) != path
            // Only the original numeric ID query is needed here. Never forward query credentials/tracking.
            || next.Query.Length != 0 && next.Query != "?id=" + articleId)
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsafeUrl);
        return next;
    }

    internal static async Task<string> ReadUtf8BodyAsync(HttpContent content, int maximumBytes,
        int maximumCharacters, CancellationToken cancellationToken)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.UnsupportedFormat);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, maximumBytes - (int)bytes.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (count > maximumBytes - bytes.Length)
                throw new ArticleCompatibilityException(ArticleCompatibilityFailure.LimitExceeded);
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        var result = DecodeUtf8(bytes, maximumCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static string DecodeUtf8(MemoryStream bytes, int maximumCharacters)
    {
        var data = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) data = data[3..];
        try
        {
            if (StrictUtf8.GetCharCount(data) > maximumCharacters)
                throw new ArticleCompatibilityException(ArticleCompatibilityFailure.LimitExceeded);
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new ArticleCompatibilityException(ArticleCompatibilityFailure.MalformedData);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();
}
