// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 文章阅读器的失败类别（仅用于界面状态与有限诊断，不携带原始内容）.
/// </summary>
internal enum ArticleReaderFailureKind
{
    /// <summary>
    /// 无失败.
    /// </summary>
    None,

    /// <summary>
    /// 获取文章详情时出错.
    /// </summary>
    DetailFailed,

    /// <summary>
    /// 服务未返回文章详情.
    /// </summary>
    DetailMissing,

    /// <summary>
    /// 文章详情存在，但正文为空.
    /// </summary>
    EmptyContent,

    /// <summary>
    /// 阅读器（WebView2）初始化失败或已不可用.
    /// </summary>
    ReaderUnavailable,

    /// <summary>
    /// 阅读器模板资源缺失或无法读取.
    /// </summary>
    AssetMissing,

    /// <summary>
    /// 正文超出阅读器可直接载入的大小.
    /// </summary>
    ContentTooLarge,

    /// <summary>
    /// 阅读器导航未成功完成，或渲染进程异常.
    /// </summary>
    RenderFailed,
}

/// <summary>
/// 阅读器单次渲染的结果.
/// </summary>
internal enum ArticleReaderRenderResult
{
    /// <summary>
    /// 当前宿主文档的 DOM 已就绪，且随机渲染标记已验证（不等待远程图片）.
    /// </summary>
    Succeeded,

    /// <summary>
    /// 被更新的渲染、清除或卸载取代；调用方应忽略.
    /// </summary>
    Superseded,

    /// <summary>
    /// 阅读器尚未就绪或初始化失败.
    /// </summary>
    ReaderUnavailable,

    /// <summary>
    /// 阅读器模板资源缺失.
    /// </summary>
    AssetMissing,

    /// <summary>
    /// 内容过大，无法通过 NavigateToString 载入.
    /// </summary>
    ContentTooLarge,

    /// <summary>
    /// 导航失败或渲染进程异常.
    /// </summary>
    NavigationFailed,
}
