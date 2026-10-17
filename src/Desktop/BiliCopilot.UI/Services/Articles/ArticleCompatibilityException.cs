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
}
