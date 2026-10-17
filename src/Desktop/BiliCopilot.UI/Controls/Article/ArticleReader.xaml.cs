// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Articles;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Richasy.BiliKernel.Models.Article;
using Richasy.WinUIKernel.Share.Base;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace BiliCopilot.UI.Controls.Article;

/// <summary>
/// 文章阅读器.
/// </summary>
public sealed partial class ArticleReader : LayoutUserControlBase
{
    /// <summary>
    /// NavigateToString 允许的最大内容大小（2 MB）.
    /// </summary>
    internal const int MaxNavigateStringBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 单次渲染等待导航完成的最长时间.
    /// </summary>
    internal static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 旧版 WebView2 为 NavigateToString 报告的地址；新版也可能报告宿主生成的精确 data URI.
    /// </summary>
    internal const string OwnedDocumentUri = "about:blank";

    /// <summary>
    /// 在导航完成后读取文档中渲染标记的脚本. 只读取宿主模板 head 中的第一个标记，不向页面传入任何值.
    /// </summary>
    internal const string ReadRenderMarkerScript = "(function(){var m=document.querySelector('head > meta[name=\"bili-reader-render\"]');return m?m.getAttribute('content'):null;})()";

    private string? _selectedText;
    private WebView2? _webView;
    private CoreSubscriptions? _subscriptions;
    private PendingRender? _pending;
    private int _lifetime;
    private int _renderSequence;
    private bool _isActive;
    private string? _articleId;
    private int _htmlCharacters;
    private int _htmlBytes;

    /// <summary>
    /// 已不再作为当前阅读器、但尚未确认 Close 成功的 WebView2 实例.
    /// 只要它存在，就不会创建替代实例.
    /// </summary>
    private WebView2? _unreleasedView;

    /// <summary>
    /// 未确认释放的实例是否已经有一次 Close 失败. 失败后只有显式重试（<see cref="RetryInitialization"/>）才会再次尝试 Close.
    /// </summary>
    private bool _isReleaseFailed;

    /// <summary>
    /// 记录日志本身失败的次数（日志为尽力而为，失败不影响阅读器状态）.
    /// </summary>
    private int _logFailureCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReader"/> class.
    /// </summary>
    public ArticleReader() => InitializeComponent();

    /// <summary>
    /// 阅读器已就绪（每次重新就绪都会触发）.
    /// </summary>
    public event EventHandler? Initialized;

    /// <summary>
    /// 阅读器初始化失败，或已不可用（例如浏览器进程退出）.
    /// </summary>
    public event EventHandler? InitializationFailed;

    /// <summary>
    /// 已显示的内容在渲染完成后丢失（例如渲染进程异常）.
    /// </summary>
    public event EventHandler? RenderFailed;

    /// <summary>
    /// 是否已加载好（WebView2 已就绪且可用）.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// 阅读器初始化是否失败.
    /// </summary>
    public bool IsInitializationFailed { get; private set; }

    /// <summary>
    /// 是否存在尚未确认释放的 WebView2 实例（上一次 Close 失败）.
    /// </summary>
    internal bool HasUnreleasedWebView => _unreleasedView is not null;

    /// <summary>
    /// 日志写入失败的次数.
    /// </summary>
    internal int LogFailureCount => _logFailureCount;

    /// <summary>
    /// 最近一次非致命生命周期/外部打开失败的类别（仅类别，不含地址、文本或异常内容）.
    /// </summary>
    internal LifecycleFailureKind LastLifecycleFailure { get; private set; }

    /// <summary>
    /// 组合阅读器 HTML. 只做模板占位符的单次替换，标题与作者会被转义.
    /// </summary>
    /// <param name="template">ShowPage.html 模板.</param>
    /// <param name="css">已替换字体参数的样式.</param>
    /// <param name="theme">主题名称.</param>
    /// <param name="article">文章详情.</param>
    /// <param name="renderNonce">本次渲染的随机标记，写入模板 head 中的标记元素.</param>
    /// <returns>HTML.</returns>
    internal static string ComposeHtml(string template, string css, string theme, ArticleDetail article, string renderNonce)
        => ArticleRenderDocument.Compose(template, css, theme, article, renderNonce,
            AppToolkit.FormatCount(article.CommunityInformation?.ViewCount ?? 0));

    /// <summary>
    /// 生成新的渲染标记（128 位随机数的十六进制字符串）. 只保存在宿主内存中.
    /// </summary>
    /// <returns>标记.</returns>
    internal static string CreateRenderNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// 判断脚本读取到的标记是否与本次渲染的预期标记一致.
    /// ExecuteScriptAsync 以 JSON 返回结果，字符串结果为带引号的 JSON 字符串；null、空白或其他值均视为不一致.
    /// </summary>
    /// <param name="scriptResult">ExecuteScriptAsync 的原始结果.</param>
    /// <param name="expectedNonce">预期标记.</param>
    /// <returns>是否一致.</returns>
    internal static bool IsExpectedRenderMarker(string? scriptResult, string? expectedNonce)
        => !string.IsNullOrEmpty(expectedNonce)
            && expectedNonce.All(char.IsAsciiHexDigit)
            && string.Equals(scriptResult, "\"" + expectedNonce + "\"", StringComparison.Ordinal);

    /// <summary>
    /// 将网页中点击的链接解析为可在系统浏览器中打开的地址. 仅接受 http/https.
    /// </summary>
    /// <param name="link">链接.</param>
    /// <returns>地址，无效时返回 <c>null</c>.</returns>
    internal static Uri? ParseExternalLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return default;
        }

        link = link.Trim();
        if (link.StartsWith("//", StringComparison.Ordinal))
        {
            link = "https:" + link;
        }

        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : default;
    }

    /// <summary>
    /// 加载文章内容. DOM 就绪且当前文档的随机渲染标记验证成功后返回成功.
    /// </summary>
    /// <param name="article">文章详情.</param>
    /// <param name="cancellationToken">取消令牌（被取消时返回 <see cref="ArticleReaderRenderResult.Superseded"/>）.</param>
    /// <returns>渲染结果.</returns>
    internal async Task<ArticleReaderRenderResult> LoadContentAsync(
        ArticleDetail article,
        CancellationToken cancellationToken)
    {
        var sequence = ++_renderSequence;
        CompletePending(ArticleReaderRenderResult.Superseded);
        _articleId = article.Identifier.Id;
        _htmlCharacters = 0;
        _htmlBytes = 0;
        _isWebFallback = false;
        if (!IsInitialized || _subscriptions is null)
        {
            return ArticleReaderRenderResult.ReaderUnavailable;
        }

        // 每次渲染使用新的随机标记，旧文档无法预知.
        var nonce = CreateRenderNonce();
        string html;
        try
        {
            html = await BuildHtmlAsync(article, nonce).WaitAsync(RenderTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ArticleReaderRenderResult.Superseded;
        }
        catch (Exception ex)
        {
            ReportRenderDiagnostic("BuildHtml", ex);
            return sequence == _renderSequence ? ArticleReaderRenderResult.AssetMissing : ArticleReaderRenderResult.Superseded;
        }

        if (sequence != _renderSequence || cancellationToken.IsCancellationRequested)
        {
            return ArticleReaderRenderResult.Superseded;
        }

        var core = _subscriptions?.Core;
        if (!IsInitialized || core is null)
        {
            return ArticleReaderRenderResult.ReaderUnavailable;
        }

        _htmlCharacters = html.Length;
        _htmlBytes = Encoding.UTF8.GetByteCount(html);
        if (_htmlBytes > MaxNavigateStringBytes)
        {
            ReportRenderDiagnostic("NavigateToStringSizeLimit");
            return ArticleReaderRenderResult.ContentTooLarge;
        }

        // 本次渲染只认领一次“由本控件发起、针对当前内核”的导航.
        var pending = new PendingRender(core, nonce, ArticleRenderDocument.GetDataUri(html));
        _pending = pending;
        try
        {
            // NavigateToString 被接受只代表导航已发起，最终结果以 NavigationCompleted 为准.
            ReportRenderDiagnostic("NavigateToStringRequested", detail: $"ViewLoaded={_webView?.IsLoaded}, ReaderOpacity={Opacity}");
            core.NavigateToString(html);
        }
        catch (ArgumentException ex)
        {
            ReportRenderDiagnostic("NavigateToStringArgument", ex);
            ClearPending(pending);
            return ArticleReaderRenderResult.ContentTooLarge;
        }
        catch (Exception ex)
        {
            ReportRenderDiagnostic("NavigateToString", ex);
            ClearPending(pending);
            return ArticleReaderRenderResult.NavigationFailed;
        }

        using var registration = cancellationToken.Register(() => pending.TrySetResult(ArticleReaderRenderResult.Superseded));

        // 有界等待：导航迟迟没有完成时按失败处理，交由用户重试，而不是一直空白.
        using var timeoutCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(pending.Completion, Task.Delay(RenderTimeout, timeoutCts.Token));
        await timeoutCts.CancelAsync();
        if (completed != pending.Completion)
        {
            ReportRenderDiagnostic(pending.NavigationId is null ? "TimeoutAwaitingNavigationStart" : "TimeoutAwaitingDocument", navigationId: pending.NavigationId);
            pending.TrySetResult(ArticleReaderRenderResult.NavigationFailed);
        }

        var result = await pending.Completion;

        ClearPending(pending);
        return sequence == _renderSequence ? result : ArticleReaderRenderResult.Superseded;
    }

    /// <summary>
    /// 放弃尚未完成的渲染（不再报告其结果）.
    /// </summary>
    public void ClearContent()
    {
        _renderSequence++;
        CompletePending(ArticleReaderRenderResult.Superseded);
        _isWebFallback = false;
    }

    /// <summary>
    /// 在初始化失败或浏览器进程退出后重新创建阅读器.
    /// </summary>
    public void RetryInitialization()
    {
        if (!_isActive || IsInitialized || !IsInitializationFailed)
        {
            return;
        }

        // 必须先确认旧实例已关闭；关闭失败时保留该实例并继续报告失败，不创建替代实例.
        if (!ReleaseWebView(isExplicitRetry: true))
        {
            InitializationFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = InitializeWebViewAsync();
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        _isActive = true;
        if (_webView is null)
        {
            _ = InitializeWebViewAsync();
        }
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        _isActive = false;
        ClearContent();
        ReleaseWebView();
        IsInitializationFailed = false;
    }

    private static async Task<string> BuildHtmlAsync(
        ArticleDetail article,
        string renderNonce,
        string fontFamily = "Segoe UI",
        double fontSize = 16,
        double lineHeight = 1.5)
    {
        var path = Path.Combine(Package.Current.InstalledPath, "Assets/Reader");
        var theme = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.AppTheme, ElementTheme.Default).ToString();
        if (theme == "Default")
        {
            theme = App.Current.RequestedTheme.ToString();
        }

        var localHtml = await File.ReadAllTextAsync(Path.Combine(path, "ShowPage.html"));
        var localCss = await File.ReadAllTextAsync(Path.Combine(path, $"{theme}.css"));
        localCss = localCss.Replace("$FontFamily$", fontFamily)
            .Replace("$FontSize$", $"{fontSize}")
            .Replace("$LineHeight$", $"{lineHeight}");
        return ComposeHtml(localHtml, localCss, theme, article, renderNonce);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_unreleasedView is not null)
        {
            // 上一个实例尚未确认释放：不创建替代实例，交由用户通过重试显式再次释放.
            IsInitialized = false;
            IsInitializationFailed = true;
            InitializationFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var lifetime = ++_lifetime;
        IsInitialized = false;
        IsInitializationFailed = false;
        var view = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
        };
        _webView = view;
        WebViewHost.Children.Add(view);

        CoreWebView2? core = default;
        try
        {
            await view.EnsureCoreWebView2Async().AsTask().WaitAsync(RenderTimeout);
            core = view.CoreWebView2;
        }
        catch (Exception ex)
        {
            ReportRenderDiagnostic("EnsureCoreWebView2", ex);
            core = default;
        }

        if (lifetime != _lifetime || !ReferenceEquals(_webView, view))
        {
            // 控件已卸载或已开始新的初始化，该实例已交由释放流程（已关闭或作为未确认实例保留）.
            return;
        }

        if (core is null)
        {
            MarkInitializationFailed();
            return;
        }

        try
        {
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            // 正文页面不需要向宿主发送消息；关闭消息通道，页面脚本无法借此触发任何宿主行为.
            core.Settings.IsWebMessageEnabled = false;
            // Retain the exact managed core acquired at initialization. Event senders and
            // repeated WinRT property getters need not return the same projected wrapper.
            _subscriptions = new CoreSubscriptions(this, view, core);
            _subscriptions.Attach();
        }
        catch (Exception ex)
        {
            ReportRenderDiagnostic("ConfigureCoreWebView2", ex);
            MarkInitializationFailed();
            return;
        }

        IsInitialized = true;
        Initialized?.Invoke(this, EventArgs.Empty);
    }

    private void MarkInitializationFailed()
    {
        CompletePending(ArticleReaderRenderResult.ReaderUnavailable);
        ReleaseWebView();
        IsInitializationFailed = true;
        InitializationFailed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 释放当前 WebView2. 实例先转为“未确认释放”，只有 Close 成功后才从宿主移除并丢弃引用.
    /// </summary>
    /// <param name="isExplicitRetry">是否为用户显式重试. 只有显式重试才会再次尝试已经失败过的 Close.</param>
    /// <returns>是否已确认没有未释放的实例.</returns>
    private bool ReleaseWebView(bool isExplicitRetry = false)
    {
        _lifetime++;
        IsInitialized = false;
        var view = _webView;
        if (view is not null && _unreleasedView is null)
        {
            // 首次释放：不再是当前实例，其事件会被 IsCurrentCore 忽略或取消.
            // （存在未释放实例时不会创建当前实例，因此不会覆盖保留的实例引用.）
            _webView = default;
            _unreleasedView = view;
            DetachCoreHandlers(view);
        }

        return TryCloseUnreleasedView(isExplicitRetry);
    }

    private void DetachCoreHandlers(WebView2 view)
    {
        var subscriptions = _subscriptions;
        if (subscriptions is null || !ReferenceEquals(subscriptions.View, view)) return;
        _subscriptions = null;
        try
        {
            subscriptions.Detach();
        }
        catch (Exception ex)
        {
            // 解除订阅失败不代表实例已关闭；残留的处理器只会看到非当前内核（导航被取消、其余事件被忽略）.
            ReportLifecycleFailure(LifecycleFailureKind.HandlerDetach, ex);
        }
    }

    private bool TryCloseUnreleasedView(bool isExplicitRetry)
    {
        var view = _unreleasedView;
        if (view is null)
        {
            return true;
        }

        if (_isReleaseFailed && !isExplicitRetry)
        {
            // 已失败过的释放：普通卸载/失败处理只保留失败与实例，不自动再试.
            return false;
        }

        try
        {
            view.Close();
        }
        catch (Exception ex)
        {
            // 不能把失败当作“已关闭”：保留实例与失败结果（隐藏但仍由宿主持有），阻止创建替代实例.
            _isReleaseFailed = true;
            view.Visibility = Visibility.Collapsed;
            ReportLifecycleFailure(LifecycleFailureKind.Close, ex);
            return false;
        }

        WebViewHost.Children.Remove(view);
        _unreleasedView = default;
        _isReleaseFailed = false;
        return true;
    }

    private void ReportLifecycleFailure(LifecycleFailureKind kind, Exception? exception)
    {
        // 先记录固定类别；之后的日志为尽力而为，本方法不会抛出.
        LastLifecycleFailure = kind;
        try
        {
            // 仅记录固定类别与异常类型名，不记录地址、选中文本、正文或异常消息.
            this.Get<ILogger<ArticleReader>>().LogWarning(
                "文章阅读器操作失败（{Category}，{ExceptionType}）",
                kind,
                exception?.GetType().Name ?? "None");
        }
        catch (Exception)
        {
            // 日志失败只计数，不再递归记录.
            _logFailureCount++;
        }
    }

    private void ReportRenderDiagnostic(string stage, Exception? exception = null, string? webError = null,
        ulong? navigationId = null, string? detail = null)
    {
        try
        {
            var message = exception?.Message;
            // Exception messages are useful here, but must not expose request credentials
            // or an embedded data: document if a runtime includes one in its message.
            if (message is not null && (message.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                || message.Contains("data:text/html", StringComparison.OrdinalIgnoreCase))) message = "[redacted sensitive exception message]";
            if (message is { Length: > 1024 }) message = message[..1024];
            this.Get<ILogger<ArticleReader>>().LogWarning(
                "文章正文渲染诊断 ArticleId={ArticleId} Stage={Stage} HtmlCharacters={HtmlCharacters} Utf8Bytes={Utf8Bytes} NavigationId={NavigationId} WebErrorStatus={WebErrorStatus} ExceptionType={ExceptionType} HResult={HResult} Message={Message} Detail={Detail}",
                _articleId, stage, _htmlCharacters, _htmlBytes, navigationId, webError,
                exception?.GetType().FullName, exception?.HResult, message, detail);
        }
        catch (Exception) { _logFailureCount++; }
    }

    private static string ParseScheme(string? uri)
    {
        if (uri is null) return "None";
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "data";
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return "about";
        if (uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return "https";
        if (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) return "http";
        return "Other";
    }

    private void CompletePending(ArticleReaderRenderResult result)
    {
        var pending = _pending;
        _pending = default;
        pending?.TrySetResult(result);
    }

    private void ClearPending(PendingRender pending)
    {
        if (ReferenceEquals(_pending, pending))
        {
            _pending = default;
        }
    }

    private bool IsCurrentCore(CoreWebView2 sender)
        => _webView is not null && ReferenceEquals(_subscriptions?.Core, sender);

    /// <summary>
    /// 判断一次导航是否为本次渲染认领的导航. 纯逻辑，便于单独验证.
    /// </summary>
    /// <param name="isAwaitingStart">渲染是否仍在等待自己的导航开始（每次渲染只认领一次）.</param>
    /// <param name="isUserInitiated">导航是否由用户手势发起.</param>
    /// <param name="uri">导航地址.</param>
    /// <param name="expectedDataUri">本次渲染预期的数据地址.</param>
    /// <returns>是否认领.</returns>
    internal static bool IsOwnedRenderNavigation(bool isAwaitingStart, bool isUserInitiated, string? uri, string? expectedDataUri = null)
        => ArticleRenderDocument.IsOwnedNavigation(isAwaitingStart, isUserInitiated, uri, expectedDataUri);

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            args.Cancel = true;
            return;
        }

        var pending = _pending;
        ReportRenderDiagnostic("NavigationStartingReceived", navigationId: args.NavigationId,
            detail: $"Scheme={ParseScheme(args.Uri)}, AwaitingStart={pending?.IsAwaitingStart}, UserInitiated={args.IsUserInitiated}");
        if (_isWebFallback && IsBilibiliPage(args.Uri))
        {
            _webNavigationId = args.NavigationId;
            return;
        }
        if (pending is not null
            && ReferenceEquals(pending.Core, sender)
            && IsOwnedRenderNavigation(pending.IsAwaitingStart, args.IsUserInitiated, args.Uri, pending.ExpectedDataUri))
        {
            // 唯一放行的导航：本控件刚通过 NavigateToString 发起的文档.
            pending.Claim(args.NavigationId);
            return;
        }

        // 其余导航一律取消：包括脚本/自动跳转、重定向、其他 about:/data: 地址，以及渲染期间的任何导航.
        // 被取消的导航不会被当作渲染成功（完成事件按导航 ID 匹配）.
        args.Cancel = true;
        if (pending is not null && pending.IsAwaitingStart)
            ReportRenderDiagnostic("NavigationStartingBlocked", navigationId: args.NavigationId,
                detail: $"Scheme={ParseScheme(args.Uri)}, UserInitiated={args.IsUserInitiated}, UriCharacters={args.Uri?.Length ?? 0}");

        // 只有原生报告为用户手势的 http/https 导航才会在系统浏览器中打开.
        if (args.IsUserInitiated && ParseExternalLink(args.Uri) is Uri external)
        {
            LaunchExternal(sender, external);
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // 不在应用内打开新窗口（例如 target="_blank" 或 window.open）.
        args.Handled = true;
        if (IsCurrentCore(sender) && args.IsUserInitiated && ParseExternalLink(args.Uri) is Uri external)
        {
            if (_isWebFallback && IsBilibiliPage(external.AbsoluteUri))
            {
                sender.Navigate(external.AbsoluteUri);
                return;
            }
            LaunchExternal(sender, external);
        }
    }

    private async void LaunchExternal(CoreWebView2 owner, Uri target)
    {
        try
        {
            if (!_isActive || !IsCurrentCore(owner))
            {
                return;
            }

            if (!await Launcher.LaunchUriAsync(target).AsTask())
            {
                ReportLifecycleFailure(LifecycleFailureKind.ExternalLaunch, default);
            }
        }
        catch (Exception ex)
        {
            // 非致命：阅读器保持当前状态，只记录固定类别.
            ReportLifecycleFailure(LifecycleFailureKind.ExternalLaunch, ex);
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        var pending = _pending;
        if (IsCurrentCore(sender) && _isWebFallback && pending is null && _webNavigationId == args.NavigationId && !args.IsSuccess
            && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            ReportRenderDiagnostic("WebFallbackNavigationFailed", webError: args.WebErrorStatus.ToString(), navigationId: args.NavigationId);
            RenderFailed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (IsCurrentCore(sender) && pending is not null && !args.IsSuccess)
            ReportRenderDiagnostic("NavigationCompleted", webError: args.WebErrorStatus.ToString(), navigationId: args.NavigationId,
                detail: $"Owned={pending.NavigationId == args.NavigationId}");
        if (!IsCurrentCore(sender)
            || pending is null
            || !ReferenceEquals(pending.Core, sender)
            || pending.NavigationId != args.NavigationId)
        {
            // 过期导航、被取消的导航或不属于本次渲染的完成事件不能改变状态.
            return;
        }

        if (!args.IsSuccess)
        {
            _pending = default;
            pending.TrySetResult(ArticleReaderRenderResult.NavigationFailed);
            return;
        }

        // 导航成功只说明“某个文档”已加载；还需确认它就是本次渲染组合的文档.
        if (pending.TryBeginVerification())
        {
            _ = VerifyRenderedDocumentAsync(sender, pending, args.NavigationId);
        }
    }

    private void OnDOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        var pending = _pending;
        if (pending is not null && IsPendingCurrent(sender, pending, args.NavigationId) && pending.TryBeginVerification())
        {
            // Body readiness does not depend on slow/failed remote images. The nonce
            // still proves that the parsed DOM belongs to this exact host document.
            _ = VerifyRenderedDocumentAsync(sender, pending, args.NavigationId);
        }
    }

    private bool IsPendingCurrent(CoreWebView2 core, PendingRender pending, ulong navigationId)
        => ReferenceEquals(_pending, pending)
            && IsCurrentCore(core)
            && ReferenceEquals(pending.Core, core)
            && pending.NavigationId == navigationId
            && !pending.Completion.IsCompleted;

    private async Task VerifyRenderedDocumentAsync(CoreWebView2 core, PendingRender pending, ulong navigationId)
    {
        string? result;
        try
        {
            result = await core.ExecuteScriptAsync(ReadRenderMarkerScript).AsTask();
        }
        catch (Exception ex)
        {
            ReportRenderDiagnostic("ExecuteRenderMarkerScript", ex, navigationId: navigationId);
            result = default;
        }

        // 等待期间可能已被新的渲染、清除、超时、卸载或新内核取代；过期的校验结果不能影响任何渲染.
        if (!IsPendingCurrent(core, pending, navigationId))
        {
            return;
        }

        _pending = default;
        if (!IsExpectedRenderMarker(result, pending.ExpectedNonce))
            ReportRenderDiagnostic("RenderMarkerMismatch", navigationId: navigationId,
                detail: result is null or "null" ? "MissingMarker" : "UnexpectedMarker");
        pending.TrySetResult(IsExpectedRenderMarker(result, pending.ExpectedNonce)
            ? ArticleReaderRenderResult.Succeeded
            : ArticleReaderRenderResult.NavigationFailed);
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        var browserExited = args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        ReportRenderDiagnostic("ProcessFailed", detail: args.ProcessFailedKind.ToString());
        var hadPending = _pending is not null;
        CompletePending(browserExited ? ArticleReaderRenderResult.ReaderUnavailable : ArticleReaderRenderResult.NavigationFailed);
        if (browserExited)
        {
            // 浏览器进程退出后该实例不可再用，需要重新创建.
            var view = _webView;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (view is not null && ReferenceEquals(_webView, view))
                {
                    MarkInitializationFailed();
                }
            });
            return;
        }

        if (!hadPending && args.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            RenderFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (args.ContextMenuTarget.HasSelection)
        {
            args.Handled = true;
            if (!string.IsNullOrEmpty(args.ContextMenuTarget.SelectionText) && _webView is not null)
            {
                _selectedText = args.ContextMenuTarget.SelectionText;
                TextMenuFlyout.ShowAt(_webView, new FlyoutShowOptions { Position = args.Location });
            }
        }
        else if (args.ContextMenuTarget.Kind == CoreWebView2ContextMenuTargetKind.Image)
        {
            for (var i = args.MenuItems.Count - 1; i >= 0; i--)
            {
                if (args.MenuItems[i].Name == "back"
                    || args.MenuItems[i].Name == "forward"
                    || args.MenuItems[i].Name == "reload"
                    || args.MenuItems[i].Name == "saveAs"
                    || args.MenuItems[i].Name == "share"
                    || args.MenuItems[i].Name == "webCapture"
                    || args.MenuItems[i].Name == "copyImageLocation"
                    || (args.MenuItems[i].Name == "other" && !string.IsNullOrEmpty(args.MenuItems[i].Label)))
                {
                    args.MenuItems.RemoveAt(i);
                }
            }
        }
        else
        {
            args.Handled = true;
        }
    }

    private async void OnSearchItemClickAsync(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedText))
        {
            return;
        }

        try
        {
            var uri = new Uri($"https://cn.bing.com/search?q={Uri.EscapeDataString(_selectedText)}");
            if (!await Launcher.LaunchUriAsync(uri).AsTask())
            {
                ReportLifecycleFailure(LifecycleFailureKind.SearchLaunch, default);
            }
        }
        catch (Exception ex)
        {
            // 非致命：只记录固定类别，不记录选中文本.
            ReportLifecycleFailure(LifecycleFailureKind.SearchLaunch, ex);
        }
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedText))
        {
            return;
        }

        var dp = new DataPackage();
        dp.SetText(_selectedText);
        Clipboard.SetContent(dp);
        this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success));
    }

    /// <summary>
    /// 非致命生命周期/外部打开失败的类别.
    /// </summary>
    internal enum LifecycleFailureKind
    {
        /// <summary>
        /// 无.
        /// </summary>
        None,

        /// <summary>
        /// 解除内核事件订阅失败.
        /// </summary>
        HandlerDetach,

        /// <summary>
        /// 关闭 WebView2 失败，实例被保留.
        /// </summary>
        Close,

        /// <summary>
        /// 在系统浏览器中打开链接失败.
        /// </summary>
        ExternalLaunch,

        /// <summary>
        /// 打开网页搜索失败.
        /// </summary>
        SearchLaunch,
    }

    private sealed class PendingRender
    {
        private readonly TaskCompletionSource<ArticleReaderRenderResult> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _isVerifying;

        public PendingRender(CoreWebView2 core, string expectedNonce, string expectedDataUri)
        {
            Core = core;
            ExpectedNonce = expectedNonce;
            ExpectedDataUri = expectedDataUri;
        }

        /// <summary>
        /// 发起本次渲染的内核.
        /// </summary>
        public CoreWebView2 Core { get; }

        /// <summary>
        /// 本次渲染写入文档的随机标记，仅保存在宿主内存中，不记录、不发送给页面.
        /// </summary>
        public string ExpectedNonce { get; }

        /// <summary>当前宿主 HTML 的精确 data URI，仅用于认领导航，绝不写入日志.</summary>
        public string ExpectedDataUri { get; }

        /// <summary>
        /// 已认领的导航 ID；认领后不会再被改写.
        /// </summary>
        public ulong? NavigationId { get; private set; }

        /// <summary>
        /// 是否仍在等待自己的导航开始.
        /// </summary>
        public bool IsAwaitingStart => NavigationId is null && !_source.Task.IsCompleted;

        public Task<ArticleReaderRenderResult> Completion => _source.Task;

        public void Claim(ulong navigationId) => NavigationId ??= navigationId;

        /// <summary>
        /// 每次渲染只发起一次文档校验.
        /// </summary>
        /// <returns>是否应开始校验.</returns>
        public bool TryBeginVerification()
        {
            if (_isVerifying || _source.Task.IsCompleted)
            {
                return false;
            }

            _isVerifying = true;
            return true;
        }

        public void TrySetResult(ArticleReaderRenderResult result) => _source.TrySetResult(result);
    }
}
