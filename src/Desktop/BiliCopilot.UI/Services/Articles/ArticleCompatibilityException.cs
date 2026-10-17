// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Articles;

internal enum ArticleCompatibilityFailure
{
    UnsupportedFormat,
    MalformedData,
    IdentityMismatch,
    MissingAuthor,
    MissingPublication,
    MissingContent,
    UnsupportedModule,
    UnsupportedParagraph,
    UnsupportedNode,
    UnsafeUrl,
    InvalidMetadata,
    LimitExceeded,
    UnsupportedHtml,
    RemoteApiFailure,
    RateLimited,
    RemoteChallenge,
}

// Structural diagnostics, validated requested article ID/API code and parser-owned paths/type labels.
// Undefined means an absent kind; null means no validated protocol code. No cookies,
// arbitrary response-supplied property names, URLs or body text enter this record.
internal sealed record ArticleStructure(
    System.Text.Json.JsonValueKind ReadInfo = default, bool ReadInfoEmpty = false,
    System.Text.Json.JsonValueKind Id = default, System.Text.Json.JsonValueKind Author = default,
    System.Text.Json.JsonValueKind AuthorId = default, System.Text.Json.JsonValueKind AuthorName = default,
    System.Text.Json.JsonValueKind Publication = default, System.Text.Json.JsonValueKind Content = default,
    System.Text.Json.JsonValueKind Opus = default, System.Text.Json.JsonValueKind Paragraphs = default,
    System.Text.Json.JsonValueKind Detail = default, System.Text.Json.JsonValueKind Modules = default,
    System.Text.Json.JsonValueKind Metadata = default, System.Text.Json.JsonValueKind MetadataAuthorId = default,
    System.Text.Json.JsonValueKind Like = default, System.Text.Json.JsonValueKind Favorite = default,
    System.Text.Json.JsonValueKind Stats = default,
    System.Text.Json.JsonValueKind ViewData = default, System.Text.Json.JsonValueKind ViewType = default,
    System.Text.Json.JsonValueKind ApiCode = default, int? RemoteApiCode = null,
    string? ArticleId = null, string? ContentPath = null, string? ContentType = null);

internal sealed class ArticleCompatibilityException : Exception
{
    internal const string SafeMessage = "文章数据格式暂不支持，请稍后重试。";

    internal ArticleCompatibilityException(ArticleCompatibilityFailure failure) : base(failure == ArticleCompatibilityFailure.RateLimited
        ? "文章请求过于频繁，请稍后再试。" : SafeMessage)
        => Failure = failure;

    public ArticleCompatibilityException() : this(ArticleCompatibilityFailure.UnsupportedFormat) { }
    public ArticleCompatibilityException(string? message) : base(message ?? SafeMessage) { }
    public ArticleCompatibilityException(string? message, Exception? innerException) : base(message ?? SafeMessage, innerException) { }

    internal ArticleCompatibilityFailure Failure { get; } = ArticleCompatibilityFailure.UnsupportedFormat;
    internal ArticleStructure Structure { get; private set; } = new();
    internal void SetStructure(ArticleStructure structure) => Structure = structure with
    {
        ArticleId = Structure.ArticleId ?? structure.ArticleId,
        ContentPath = Structure.ContentPath ?? structure.ContentPath,
        ContentType = Structure.ContentType ?? structure.ContentType,
    };
    internal void SetContext(string articleId, string path, string? type = null)
        => Structure = Structure with { ArticleId = articleId, ContentPath = path, ContentType = type };
    internal string Stage { get; private set; } = "Unknown";
    internal ArticleHttpDiagnostics Transport { get; private set; } = new();
    internal void SetStage(string stage) => Stage = stage;
    internal void SetTransport(ArticleHttpDiagnostics transport) => Transport = transport;
}

// Only response metadata, never request headers, cookies, query strings or response bodies.
internal sealed record ArticleHttpDiagnostics(int? Status = null, string? ContentType = null,
    string? ContentEncoding = null, bool Redirected = false, string? RedirectTarget = null,
    string? RequestTarget = null, string? PageKind = null, int? HtmlCharacters = null);

internal sealed record ArticleHtmlPage(string Html, ArticleHttpDiagnostics Transport);

internal sealed record ArticleParseNotice(string ArticleId, string Path, string Type, ArticleCompatibilityFailure Failure);
