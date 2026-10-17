// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Microsoft.Web.WebView2.Core;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Controls.Article;

public sealed partial class ArticleReader
{
    private bool _isWebFallback;
    private ulong? _webNavigationId;

    internal static bool IsBilibiliPage(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
            && (uri.IdnHost.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    internal Task<ArticleReaderRenderResult> LoadWebPageAsync(Uri target, CancellationToken token)
    {
        // 初次导航仅接受规范的站内 CV/opus 地址，后续网页仍沿用现有站内导航和 Cookie 策略.
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || target.IdnHost != "www.bilibili.com"
            || target.Port != 443 || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
            throw new ArgumentException("阅读目标必须是站内文章或图文地址。", nameof(target));
        var isOpus = target.AbsolutePath.StartsWith("/opus/", StringComparison.Ordinal);
        var prefix = isOpus ? "/opus/" : "/read/cv";
        if (!target.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("阅读目标必须是站内文章或图文地址。", nameof(target));
        var id = Services.Articles.ArticleRawHtmlClient.ValidateArticleId(target.AbsolutePath[prefix.Length..]);
        var sequence = ++_renderSequence;
        CompletePending(ArticleReaderRenderResult.Superseded);
        _articleId = isOpus ? "opus:" + id : id;
        _htmlCharacters = 0;
        _htmlBytes = 0;
        _isWebFallback = true;
        _webNavigationId = null;
        var core = _subscriptions?.Core;
        if (!IsInitialized || core is null) return Task.FromResult(ArticleReaderRenderResult.ReaderUnavailable);
        if (token.IsCancellationRequested) return Task.FromResult(ArticleReaderRenderResult.Superseded);

        // Reuse both the existing WebView profile and the application's current sign-in
        // resolver. Cookies are installed only for Bilibili, never copied to external sites.
        try
        {
            foreach (var entry in this.Get<IBiliCookiesResolver>().GetCookies())
            {
                var cookie = core.CookieManager.CreateCookie(entry.Key, entry.Value, ".bilibili.com", "/");
                cookie.IsSecure = true;
                cookie.IsHttpOnly = entry.Key == "SESSDATA";
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
        }
        catch (Exception ex)
        {
            // Cookie exceptions may contain values; record the type only. Existing
            // profile cookies remain available even when resolver injection fails.
            ReportRenderDiagnostic("WebFallbackCookieInjection", detail: ex.GetType().FullName);
        }

        try
        {
            ReportRenderDiagnostic("WebFallbackNavigate");
            core.Navigate("https://www.bilibili.com" + prefix + id);
            // Ordinary browsing deliberately bypasses the host-HTML claim/nonce pipeline.
            // Show the WebView immediately; its own page handles loading and challenges.
            return Task.FromResult(sequence == _renderSequence ? ArticleReaderRenderResult.Succeeded : ArticleReaderRenderResult.Superseded);
        }
        catch (Exception ex)
        {
            if (sequence == _renderSequence) ReportRenderDiagnostic("WebFallbackFailed", ex);
            return Task.FromResult(sequence == _renderSequence ? ArticleReaderRenderResult.NavigationFailed : ArticleReaderRenderResult.Superseded);
        }
    }

    // Each subscription forwards its retained core, rather than trusting projected
    // sender/property reference identity. Old subscriptions still fail IsCurrentCore.
    private sealed class CoreSubscriptions(ArticleReader owner, WebView2 view, CoreWebView2 core)
    {
        public WebView2 View { get; } = view;
        public CoreWebView2 Core { get; } = core;

        public void Attach()
        {
            Core.NavigationStarting += NavigationStarting;
            Core.NavigationCompleted += NavigationCompleted;
            Core.DOMContentLoaded += DOMContentLoaded;
            Core.ContextMenuRequested += ContextMenuRequested;
            Core.NewWindowRequested += NewWindowRequested;
            Core.ProcessFailed += ProcessFailed;
        }

        public void Detach()
        {
            Core.NavigationStarting -= NavigationStarting;
            Core.NavigationCompleted -= NavigationCompleted;
            Core.DOMContentLoaded -= DOMContentLoaded;
            Core.ContextMenuRequested -= ContextMenuRequested;
            Core.NewWindowRequested -= NewWindowRequested;
            Core.ProcessFailed -= ProcessFailed;
        }

        private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!ReferenceEquals(sender, Core)) owner.ReportRenderDiagnostic("ProjectedCoreWrapperChanged");
            owner.OnNavigationStarting(Core, args);
        }
        private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => owner.OnNavigationCompleted(Core, args);
        private void DOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => owner.OnDOMContentLoaded(Core, args);
        private void ContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args) => owner.OnContextMenuRequested(Core, args);
        private void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => owner.OnNewWindowRequested(Core, args);
        private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => owner.OnProcessFailed(Core, args);
    }
}
