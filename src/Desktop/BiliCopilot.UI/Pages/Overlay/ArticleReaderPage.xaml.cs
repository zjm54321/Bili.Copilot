// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Article;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// 文章阅读页面.
/// </summary>
/// <remarks>
/// 页面负责在视图模型（文章详情）与阅读器（WebView2）之间握手：
/// 先订阅、再开始加载，并在订阅后主动重放当前状态，因此无论哪一方先就绪都不会丢失初始化.
/// 每次渲染都绑定加载版本号，过期的渲染结果会被忽略.
/// </remarks>
public sealed partial class ArticleReaderPage : ArticleReaderPageBase, IParameterPage
{
    private bool _isAttached;
    private int _renderingVersion = -1;
    private int _shownVersion = -1;
    private CancellationTokenSource? _renderCts;
    private int _webFallbackVersion = -1;
    private bool _webFallbackLoading;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReaderPage"/> class.
    /// </summary>
    public ArticleReaderPage()
    {
        InitializeComponent();
        UpdateReaderPresentation();
    }

    /// <inheritdoc/>
    public void SetParameter(object? parameter)
    {
        if (parameter is ArticleIdentifier article)
        {
            // 先订阅，再开始加载；同步完成的加载也不会错过.
            Attach();
            ViewModel.InitializeCommand.Execute(article);
            SyncReader();
        }
        else if (parameter is ArticleReaderOpusTarget opus)
        {
            Attach();
            ViewModel.InitializeOpusCommand.Execute(opus);
            SyncReader();
        }
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
    {
        Attach();
        SyncReader();
    }

    /// <inheritdoc/>
    protected override void OnPageUnloaded()
        => Detach();

    private void Attach()
    {
        if (_isAttached)
        {
            return;
        }

        _isAttached = true;
        ViewModel.ArticleLoadStarted += OnArticleLoadStarted;
        ViewModel.ArticleInitialized += OnArticleInitialized;
        ViewModel.RenderRetryRequested += OnRenderRetryRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Reader.Initialized += OnReaderInitialized;
        Reader.InitializationFailed += OnReaderInitializationFailed;
        Reader.RenderFailed += OnReaderRenderFailed;
    }

    private void Detach()
    {
        if (!_isAttached)
        {
            return;
        }

        _isAttached = false;
        ViewModel.ArticleLoadStarted -= OnArticleLoadStarted;
        ViewModel.ArticleInitialized -= OnArticleInitialized;
        ViewModel.RenderRetryRequested -= OnRenderRetryRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Reader.Initialized -= OnReaderInitialized;
        Reader.InitializationFailed -= OnReaderInitializationFailed;
        Reader.RenderFailed -= OnReaderRenderFailed;

        // 阅读器会在自身卸载时释放 WebView2；这里只放弃本页面发起的渲染.
        CancelRender();
        Reader.ClearContent();
        _renderingVersion = -1;
        _shownVersion = -1;
        _webFallbackVersion = -1;
        _webFallbackLoading = false;
        UpdateReaderPresentation();
    }

    private void OnArticleLoadStarted(object? sender, EventArgs e)
    {
        _webFallbackVersion = -1;
        _webFallbackLoading = false;
        // 新的文章（或重试）开始加载：放弃旧渲染并隐藏旧正文.
        CancelRender();
        Reader.ClearContent();
        _renderingVersion = -1;
        _shownVersion = -1;
        UpdateReaderPresentation();
    }

    private void OnArticleInitialized(object? sender, EventArgs e)
        => SyncReader();

    private void OnReaderInitialized(object? sender, EventArgs e)
        => SyncReader();

    private void OnRenderRetryRequested(object? sender, EventArgs e)
    {
        CancelRender();
        Reader.ClearContent();
        _renderingVersion = -1;
        _shownVersion = -1;
        if (ViewModel.IsWebFallback) _webFallbackVersion = -1;
        _webFallbackLoading = ViewModel.IsWebFallback;
        if (Reader.IsInitializationFailed)
        {
            // 重新创建阅读器；就绪后通过 Initialized 继续渲染.
            Reader.RetryInitialization();
            UpdateReaderPresentation();
            return;
        }

        SyncReader();
    }

    private void OnReaderInitializationFailed(object? sender, EventArgs e)
    {
        CancelRender();
        _renderingVersion = -1;
        _shownVersion = -1;
        SyncReader();
    }

    private void OnReaderRenderFailed(object? sender, EventArgs e)
    {
        if (_shownVersion != ViewModel.LoadVersion)
        {
            return;
        }

        var version = _shownVersion;
        _shownVersion = -1;
        if (ViewModel.IsWebFallback) ViewModel.ReportWebFallbackResult(version, ArticleReaderRenderResult.NavigationFailed);
        else ViewModel.ReportRenderResult(version, ArticleReaderRenderResult.NavigationFailed);
        UpdateReaderPresentation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArticleReaderPageViewModel.IsLoading) or nameof(ArticleReaderPageViewModel.HasError))
        {
            UpdateReaderPresentation();
            if (ViewModel.HasError && !ViewModel.IsLoading)
            {
                // Defer until the VM has finished publishing the native failure. A failed
                // web attempt is never automatically retried in the same load version.
                DispatcherQueue.TryEnqueue(SyncReader);
            }
        }
    }

    private void SyncReader()
    {
        UpdateReaderPresentation();
        if (_isAttached && !ViewModel.IsLoading && (ViewModel.HasError || ViewModel.IsWebFallback))
        {
            if (_webFallbackVersion != ViewModel.LoadVersion && ViewModel.BrowserUri is { } target)
            {
                if (!Reader.IsInitialized && !Reader.IsInitializationFailed) return;
                _ = RenderWebFallbackAsync(ViewModel.LoadVersion, target);
            }
            return;
        }
        if (!_isAttached
            || !ViewModel.TryGetRenderableContent(out var version, out var content)
            || content is null
            || version == _renderingVersion)
        {
            return;
        }

        if (Reader.IsInitializationFailed)
        {
            // 只报告一次，等待用户重试，不自动循环.
            _renderingVersion = version;
            ViewModel.ReportRenderResult(version, ArticleReaderRenderResult.ReaderUnavailable);
            UpdateReaderPresentation();
            return;
        }

        if (!Reader.IsInitialized)
        {
            // 阅读器就绪后会通过 Initialized 重放.
            return;
        }

        _ = RenderAsync(version, content);
    }

    private async Task RenderAsync(int version, ArticleDetail content)
    {
        CancelRender();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _renderingVersion = version;
        UpdateReaderPresentation();

        ArticleReaderRenderResult result;
        try
        {
            result = await Reader.LoadContentAsync(content, cts.Token);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<ArticleReaderPage>>().LogWarning("文章渲染失败（{ExceptionType}）", ex.GetType().Name);
            result = ArticleReaderRenderResult.NavigationFailed;
        }
        finally
        {
            if (ReferenceEquals(_renderCts, cts))
            {
                _renderCts = default;
            }

            cts.Dispose();
        }

        if (!_isAttached
            || result == ArticleReaderRenderResult.Superseded
            || version != _renderingVersion
            || version != ViewModel.LoadVersion)
        {
            return;
        }

        if (result == ArticleReaderRenderResult.Succeeded)
        {
            _shownVersion = version;
        }

        ViewModel.ReportRenderResult(version, result);
        UpdateReaderPresentation();
    }

    private void CancelRender()
    {
        var cts = _renderCts;
        _renderCts = default;
        cts?.Cancel();
    }

    private void UpdateReaderPresentation()
    {
        // Native HTML waits for its own verified DOM. Ordinary web fallback is visible
        // immediately and lets the Bilibili page own its loading/error presentation.
        var isShown = _isAttached
            && (ViewModel.IsWebFallback && _webFallbackVersion == ViewModel.LoadVersion ||
                _shownVersion >= 0 && _shownVersion == ViewModel.LoadVersion)
            && !ViewModel.IsLoading
            && !ViewModel.HasError;
        Reader.Opacity = isShown ? 1 : 0;
        Reader.IsHitTestVisible = isShown;

        var isPreparing = ViewModel.IsLoading || _webFallbackLoading
            || (_isAttached && !isShown && !ViewModel.HasError && ViewModel.TryGetRenderableContent(out _, out _));
        LoadingView.Visibility = isPreparing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RenderWebFallbackAsync(int version, Uri target)
    {
        if (!_isAttached || version != ViewModel.LoadVersion) return;
        CancelRender();
        Reader.ClearContent();
        _webFallbackVersion = version;
        _renderingVersion = version;
        if (!ViewModel.BeginWebFallback(version)) return;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _renderCts = cts;
        _webFallbackLoading = true;
        // Keep the actual WebView visible during ordinary web navigation. The loading
        // indicator is independent; a hidden WinUI surface must not gate its own readiness.
        UpdateReaderPresentation();
        ArticleReaderRenderResult result;
        try { result = await Reader.LoadWebPageAsync(target, token); }
        catch (Exception ex)
        {
            this.Get<ILogger<ArticleReaderPage>>().LogWarning("文章网页加载失败（{ExceptionType}）", ex.GetType().Name);
            result = ArticleReaderRenderResult.NavigationFailed;
        }
        finally
        {
            if (ReferenceEquals(_renderCts, cts))
            {
                _renderCts = null;
                _webFallbackLoading = false;
            }
            cts.Dispose();
        }
        if (!_isAttached || version != ViewModel.LoadVersion || token.IsCancellationRequested) return;
        if (result == ArticleReaderRenderResult.Succeeded) _shownVersion = version;
        ViewModel.ReportWebFallbackResult(version, result);
        UpdateReaderPresentation();
    }
}

/// <summary>
/// 文章阅读页面基类.
/// </summary>
public abstract class ArticleReaderPageBase : LayoutPageBase<ArticleReaderPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReaderPageBase"/> class.
    /// </summary>
    protected ArticleReaderPageBase() => ViewModel = this.Get<ArticleReaderPageViewModel>();
}
