// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Services.Articles;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Article;
using Richasy.BiliKernel.Models.Article;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 文章阅读器页面视图模型.
/// </summary>
public sealed partial class ArticleReaderPageViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReaderPageViewModel"/> class.
    /// </summary>
    public ArticleReaderPageViewModel(
        ArticleDetailReader detailReader,
        IArticleOperationService operation,
        ILogger<ArticleReaderPageViewModel> logger,
        CommentMainViewModel comment)
    {
        _detailReader = detailReader;
        _operationService = operation;
        _logger = logger;
        CommentModule = comment;
    }

    /// <summary>
    /// 获取当前可交给阅读器渲染的正文.
    /// 仅当当前版本已成功获取到非空正文时返回 <c>true</c>.
    /// </summary>
    /// <param name="version">内容所属的加载版本.</param>
    /// <param name="content">文章详情.</param>
    /// <returns>是否存在可渲染的正文.</returns>
    internal bool TryGetRenderableContent(out int version, out ArticleDetail? content)
    {
        version = _loadVersion;
        content = default;
        if (IsLoading
            || Content is null
            || string.IsNullOrWhiteSpace(Content.HtmlContent)
            || FailureKind is ArticleReaderFailureKind.DetailFailed or ArticleReaderFailureKind.DetailMissing or ArticleReaderFailureKind.EmptyContent)
        {
            return false;
        }

        content = Content;
        return true;
    }

    /// <summary>
    /// 由页面报告某个加载版本的阅读器渲染结果. 过期版本的结果会被忽略.
    /// </summary>
    /// <param name="version">渲染所属的加载版本.</param>
    /// <param name="result">渲染结果.</param>
    internal void ReportRenderResult(int version, ArticleReaderRenderResult result)
    {
        if (version != _loadVersion || IsLoading || Content is null)
        {
            return;
        }

        switch (result)
        {
            case ArticleReaderRenderResult.Superseded:
                return;
            case ArticleReaderRenderResult.Succeeded:
                if (IsReaderFailure(FailureKind))
                {
                    ClearFailure();
                }

                return;
            case ArticleReaderRenderResult.AssetMissing:
                ApplyFailure(ArticleReaderFailureKind.AssetMissing);
                break;
            case ArticleReaderRenderResult.ContentTooLarge:
                ApplyFailure(ArticleReaderFailureKind.ContentTooLarge);
                break;
            case ArticleReaderRenderResult.ReaderUnavailable:
                ApplyFailure(ArticleReaderFailureKind.ReaderUnavailable);
                break;
            default:
                ApplyFailure(ArticleReaderFailureKind.RenderFailed);
                break;
        }
    }

    internal bool BeginWebFallback(int version)
    {
        if (version != _loadVersion || IsLoading || BrowserUri is null) return false;
        IsWebFallback = true;
        ClearFailure();
        return true;
    }

    internal void ReportWebFallbackResult(int version, ArticleReaderRenderResult result)
    {
        if (version != _loadVersion || !IsWebFallback || IsLoading || result == ArticleReaderRenderResult.Superseded) return;
        if (result == ArticleReaderRenderResult.Succeeded) ClearFailure();
        else
        {
            ApplyFailure(result == ArticleReaderRenderResult.ReaderUnavailable
                ? ArticleReaderFailureKind.ReaderUnavailable : ArticleReaderFailureKind.RenderFailed);
        }
    }

    private static bool IsReaderFailure(ArticleReaderFailureKind kind)
        => kind is ArticleReaderFailureKind.ReaderUnavailable
            or ArticleReaderFailureKind.AssetMissing
            or ArticleReaderFailureKind.ContentTooLarge
            or ArticleReaderFailureKind.RenderFailed;

    private static Uri? BuildBrowserUri(string? articleId)
    {
        // 仅接受纯数字的专栏 ID，避免拼接出任意地址.
        if (string.IsNullOrEmpty(articleId) || articleId.Length > 20 || !articleId.All(char.IsAsciiDigit))
        {
            return default;
        }

        return new Uri($"https://www.bilibili.com/read/cv{articleId}");
    }

    private static int ToDisplayCount(int? count)
        => count is >= 0 ? count.Value : UnknownCount;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task InitializeAsync(ArticleIdentifier article)
    {
        // 同一篇文章正在加载时，重复的初始化请求直接合并到当前加载.
        if (IsLoading && Article is not null && Article.Value.Id == article.Id)
        {
            return Task.CompletedTask;
        }

        return LoadAsync(article, isNewIdentity: true);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InitializeOpusAsync(ArticleReaderOpusTarget opus)
    {
        var id = ArticleRawHtmlClient.ValidateArticleId(opus.Id);
        var version = ++_loadVersion;
        var previous = _loadOwnership;
        _loadOwnership = null;
        var cancelPrevious = previous is null ? Task.CompletedTask : CancelPreviousLoadAsync(previous);

        // 与 CV 请求共享版本和取消协议，但不请求 CV 接口，也不沿用上一篇文章的互动身份.
        IsLoading = true;
        IsWebFallback = true;
        ClearFailure();
        ResetDetailState();
        Article = null;
        Title = opus.Title ?? string.Empty;
        IsCommentsOpened = false;
        BrowserUri = new Uri("https://www.bilibili.com/opus/" + id);
        ToggleCommentsCommand.NotifyCanExecuteChanged();
        ArticleLoadStarted?.Invoke(this, EventArgs.Empty);

        await cancelPrevious;
        if (version != _loadVersion) return;
        IsLoading = false;
        ArticleInitialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        if (IsLoading || (Article is null && !IsWebFallback))
        {
            return Task.CompletedTask;
        }

        if (IsWebFallback || IsReaderFailure(FailureKind) && Content is not null)
        {
            // 正文已获取，只需让阅读器重新显示，不再重复请求文章详情.
            ClearFailure();
            RenderRetryRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        return Article is { } article ? LoadAsync(article, isNewIdentity: false) : Task.CompletedTask;
    }

    private bool CanRetry() => HasError && !IsLoading;

    private async Task LoadAsync(ArticleIdentifier article, bool isNewIdentity)
    {
        // 在任何 await 或可观察回调之前同步发布本次加载的所有权（版本号 + 取消源）.
        // 旧加载的延续据此判定自己已过期，不会覆盖本次的文章、状态或取消源.
        var version = ++_loadVersion;
        var previous = _loadOwnership;
        var ownership = new LoadOwnership();
        _loadOwnership = ownership;
        var token = ownership.Token;

        // 立即以本次加载拥有的任务启动对上一次加载的取消（同步获取取消份额并发出取消），
        // 之后才触发属性变更与 ArticleLoadStarted，回调抛出也不会让上一次加载失去取消.
        var cancelPrevious = previous is null ? null : CancelPreviousLoadAsync(previous);
        var isCancelObserved = cancelPrevious is null;

        ArticleDetail? detail = default;
        var failure = ArticleReaderFailureKind.None;
        try
        {
            BeginLoadState(article, isNewIdentity);

            if (cancelPrevious is not null)
            {
                // 等待上一次加载的取消回调执行完毕（SDK 是否响应取消不作保证）.
                isCancelObserved = true;
                await cancelPrevious;

                // 等待期间可能已有更新的加载；过期则不再发起请求.
                if (version != _loadVersion)
                {
                    return;
                }
            }

            try
            {
                detail = await _detailReader.GetArticleContentAsync(article, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 已被更新的加载取代，结果由下方的版本检查丢弃.
                detail = default;
            }
            catch (ArticleCompatibilityException ex)
            {
                failure = ArticleReaderFailureKind.DetailFailed;
                _logger.LogWarning("文章内容格式不兼容（{CompatibilityFailure}）", ex.Failure);
            }
            catch (Exception ex)
            {
                failure = ArticleReaderFailureKind.DetailFailed;

                // 仅记录类别与异常类型，不记录可能包含正文、地址或凭据的原始信息.
                _logger.LogWarning("文章内容加载失败（{Category}，{ExceptionType}）", failure, ex.GetType().Name);
            }
        }
        finally
        {
            if (!isCancelObserved && cancelPrevious is not null)
            {
                // 异常退出时仍观察本次加载拥有的取消任务（该任务不会抛出）.
                await cancelPrevious;
            }

            if (ReferenceEquals(_loadOwnership, ownership))
            {
                _loadOwnership = default;
            }

            // 实际的 SDK 请求（或未发起的请求）已结束；取消源在请求与取消都结束后才释放.
            ownership.ReleaseRequest();
        }

        if (version != _loadVersion)
        {
            // 过期（或已取消）的结果不能覆盖新的文章.
            return;
        }

        if (failure == ArticleReaderFailureKind.None && detail is null)
        {
            failure = ArticleReaderFailureKind.DetailMissing;
            _logger.LogWarning("文章内容加载失败（{Category}）", failure);
        }

        if (failure != ArticleReaderFailureKind.None)
        {
            IsLoading = false;
            ApplyFailure(failure);
            return;
        }

        Content = detail;
        InitializeUser();
        InitializeStats();
        IsLoading = false;

        if (string.IsNullOrWhiteSpace(detail!.HtmlContent))
        {
            // 正文为空不影响作者、计数与评论的展示.
            ApplyFailure(ArticleReaderFailureKind.EmptyContent);
            _logger.LogWarning("文章内容加载失败（{Category}）", ArticleReaderFailureKind.EmptyContent);
            return;
        }

        ArticleInitialized?.Invoke(this, EventArgs.Empty);
    }

    private void BeginLoadState(ArticleIdentifier article, bool isNewIdentity)
    {
        // 先重置状态，再开始请求，避免沿用上一篇文章的作者、计数或错误.
        IsLoading = true;
        IsWebFallback = false;
        ClearFailure();
        ResetDetailState();
        if (isNewIdentity)
        {
            Article = article;
            ToggleCommentsCommand.NotifyCanExecuteChanged();
            Title = article.Title ?? string.Empty;
            IsCommentsOpened = false;
            BrowserUri = BuildBrowserUri(article.Id);

            // 评论与正文相互独立：正文失败时评论仍然可用.
            CommentModule.Initialize(article.Id, Richasy.BiliKernel.Models.CommentTargetType.Article, Richasy.BiliKernel.Models.CommentSortType.Hot);
        }

        ArticleLoadStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 取消上一次加载并等待其取消回调结束.
    /// 取消份额与 CancelAsync 在第一个 await 之前同步获取/发出.
    /// </summary>
    private async Task CancelPreviousLoadAsync(LoadOwnership previous)
    {
        if (!previous.TryAcquireCanceller())
        {
            // 上一次加载的请求已结束，其取消源已释放，无需取消.
            return;
        }

        try
        {
            await previous.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("取消上一次文章加载时回调失败（{ExceptionType}）", ex.GetType().Name);
        }
        finally
        {
            previous.ReleaseCanceller();
        }
    }

    private bool CanInteract() => !IsLoading && Content is not null;

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ToggleLikeAsync()
    {
        if (Article is null || Content is null)
        {
            return;
        }

        var version = _loadVersion;
        var state = !(IsLiked ?? false);
        try
        {
            await _operationService.ToggleLikeAsync(Article.Value, state);
            if (version != _loadVersion)
            {
                return;
            }

            IsLiked = state;
            LikeCount = AdjustCount(LikeCount, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("切换点赞状态时失败（{ExceptionType}）", ex.GetType().Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ToggleFavoriteAsync()
    {
        if (Article is null || Content is null)
        {
            return;
        }

        var version = _loadVersion;
        var state = !(IsFavorited ?? false);
        try
        {
            await _operationService.ToggleFavoriteAsync(Article.Value, state);
            if (version != _loadVersion)
            {
                return;
            }

            IsFavorited = state;
            FavoriteCount = AdjustCount(FavoriteCount, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("切换收藏状态时失败（{ExceptionType}）", ex.GetType().Name);
        }
    }

    private static int AdjustCount(int count, bool increase)
    {
        // 计数未知时保持未知，不凭空生成数字.
        if (count < 0)
        {
            return count;
        }

        return increase ? count + 1 : Math.Max(0, count - 1);
    }

    private bool CanShowComments() => Article is not null;

    [RelayCommand(CanExecute = nameof(CanShowComments))]
    private async Task ToggleCommentsAsync()
    {
        if (!CanShowComments()) return;
        IsCommentsOpened = !IsCommentsOpened;

        if (IsCommentsOpened && CommentModule.Comments.Count == 0)
        {
            await CommentModule.LoadItemsCommand.ExecuteAsync(default);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void ShowUserSpace()
    {
        var user = Content?.Author?.Profile?.User;
        if (user is null)
        {
            return;
        }

        this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), user);
    }

    [RelayCommand(CanExecute = nameof(CanOpenInBrowser))]
    private async Task OpenInBrowserAsync()
    {
        // 仅响应用户点击，打开经过校验的原文地址.
        if (BrowserUri is null)
        {
            return;
        }

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(BrowserUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("在浏览器中打开文章时失败（{ExceptionType}）", ex.GetType().Name);
        }
    }

    private void ResetDetailState()
    {
        Content = default;
        Author = default;
        Avatar = default;
        IsVip = false;
        IsFollowed = false;
        IsLiked = default;
        IsFavorited = default;
        LikeCount = UnknownCount;
        CommentCount = UnknownCount;
        FavoriteCount = UnknownCount;
    }

    private void ClearFailure()
    {
        FailureKind = ArticleReaderFailureKind.None;
        ErrorTitle = default;
        ErrorDescription = default;
        HasError = false;
    }

    private void ApplyFailure(ArticleReaderFailureKind kind)
    {
        FailureKind = kind;
        (ErrorTitle, ErrorDescription) = kind switch
        {
            ArticleReaderFailureKind.DetailFailed => (
                ResourceToolkit.GetLocalizedString(StringNames.ArticleLoadFailed),
                ResourceToolkit.GetLocalizedString(StringNames.ArticleLoadFailedDescription)),
            ArticleReaderFailureKind.DetailMissing or ArticleReaderFailureKind.EmptyContent => (
                ResourceToolkit.GetLocalizedString(StringNames.ArticleContentEmpty),
                ResourceToolkit.GetLocalizedString(StringNames.ArticleContentEmptyDescription)),
            ArticleReaderFailureKind.ContentTooLarge => (
                ResourceToolkit.GetLocalizedString(StringNames.ArticleRenderFailed),
                ResourceToolkit.GetLocalizedString(StringNames.ArticleContentTooLargeDescription)),
            _ => (
                ResourceToolkit.GetLocalizedString(StringNames.ArticleRenderFailed),
                ResourceToolkit.GetLocalizedString(StringNames.ArticleRenderFailedDescription)),
        };
        HasError = kind != ArticleReaderFailureKind.None;
    }

    private void InitializeUser()
    {
        var user = Content?.Author?.Profile?.User;
        if (Content is null || user is null)
        {
            return;
        }

        Author = user.Name;
        Avatar = user.Avatar?.Uri;
        IsVip = Content.Author.Profile.IsVip ?? false;
        IsFollowed = Content.Author.Community?.Relation == Richasy.BiliKernel.Models.User.UserRelationStatus.Following;
    }

    private void InitializeStats()
    {
        if (Content is null)
        {
            return;
        }

        IsLiked = Content.IsLiked;
        IsFavorited = Content.IsFavorited;

        // 缺失的计数保持未知（显示为空），真实的 0 保持为 0.
        var community = Content.CommunityInformation;
        LikeCount = ToDisplayCount(community?.LikeCount);
        CommentCount = ToDisplayCount(community?.CommentCount);
        FavoriteCount = ToDisplayCount(community?.FavoriteCount);
    }

    /// <summary>
    /// 单次加载的取消源所有权. 取消源只在实际请求结束、且所有进行中的取消（含回调）都结束后才释放.
    /// </summary>
    private sealed class LoadOwnership
    {
        private readonly CancellationTokenSource _cts = new();

        // 初始持有者为本次加载的请求流程；每个进行中的取消各持有一份.
        private int _owners = 1;

        public CancellationToken Token => _cts.Token;

        public bool TryAcquireCanceller()
        {
            while (true)
            {
                var current = Volatile.Read(ref _owners);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _owners, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public Task CancelAsync() => _cts.CancelAsync();

        public void ReleaseCanceller() => Release();

        public void ReleaseRequest() => Release();

        private void Release()
        {
            if (Interlocked.Decrement(ref _owners) == 0)
            {
                _cts.Dispose();
            }
        }
    }
}
