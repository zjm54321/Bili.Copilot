// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.Articles;
using BiliCopilot.UI.ViewModels.Components;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Article;
using Richasy.BiliKernel.Models.Article;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 文章阅读器页面视图模型.
/// </summary>
public sealed partial class ArticleReaderPageViewModel
{
    /// <summary>
    /// 未知计数. 计数转换器会将负数显示为空，避免把缺失的数据显示成 0.
    /// </summary>
    public const int UnknownCount = -1;

    private readonly ArticleDetailReader _detailReader;
    private readonly IArticleOperationService _operationService;
    private readonly ILogger<ArticleReaderPageViewModel> _logger;

    private LoadOwnership? _loadOwnership;
    private int _loadVersion;
    private ArticleDetail? _content;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLikeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFavoriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowUserSpaceCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string? _author;

    [ObservableProperty]
    private Uri? _avatar;

    [ObservableProperty]
    private bool _isVip;

    [ObservableProperty]
    private bool _isFollowed;

    [ObservableProperty]
    private bool? _isLiked;

    [ObservableProperty]
    private bool? _isFavorited;

    [ObservableProperty]
    private int _likeCount = UnknownCount;

    [ObservableProperty]
    private int _commentCount = UnknownCount;

    [ObservableProperty]
    private int _favoriteCount = UnknownCount;

    [ObservableProperty]
    private bool _isCommentsOpened;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorTitle;

    [ObservableProperty]
    private string? _errorDescription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
    [NotifyCanExecuteChangedFor(nameof(OpenInBrowserCommand))]
    private Uri? _browserUri;

    /// <summary>
    /// 新的一次加载开始（新的文章或重试），阅读器应清除旧内容.
    /// </summary>
    public event EventHandler? ArticleLoadStarted;

    /// <summary>
    /// 文章已加载，正文可交给阅读器渲染.
    /// </summary>
    public event EventHandler? ArticleInitialized;

    /// <summary>
    /// 用户请求重新渲染当前正文（不重新获取文章详情）.
    /// </summary>
    public event EventHandler? RenderRetryRequested;

    /// <summary>
    /// 文章标识.
    /// </summary>
    public ArticleIdentifier? Article { get; private set; }

    /// <summary>
    /// 文章内容.
    /// </summary>
    public ArticleDetail? Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(IsArticleAvailable));
                ToggleLikeCommand.NotifyCanExecuteChanged();
                ToggleFavoriteCommand.NotifyCanExecuteChanged();
                ShowUserSpaceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 当前加载的版本号. 每次开始新的加载（含重试）都会递增，用于丢弃过期结果.
    /// </summary>
    public int LoadVersion => _loadVersion;

    /// <summary>
    /// 是否已获取到当前文章的详情（作者、互动状态可用）.
    /// </summary>
    public bool IsArticleAvailable => Content is not null;

    /// <summary>
    /// 是否有可在浏览器中打开的原文地址.
    /// </summary>
    public bool CanOpenInBrowser => BrowserUri is not null;

    /// <summary>当前加载已切换到阅读页内的 B 站原文，重试时继续使用原文模式.</summary>
    public bool IsWebFallback { get; private set; }

    /// <summary>
    /// 当前失败类别.
    /// </summary>
    internal ArticleReaderFailureKind FailureKind { get; private set; }

    /// <summary>
    /// 评论模块.
    /// </summary>
    public CommentMainViewModel CommentModule { get; }
}
