// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>只有 opus 身份的阅读目标；不会将图文 ID 冒充为专栏 ID.</summary>
/// <param name="Id">图文 ID.</param>
/// <param name="Title">阅读页标题.</param>
public sealed record ArticleReaderOpusTarget(string Id, string? Title);
