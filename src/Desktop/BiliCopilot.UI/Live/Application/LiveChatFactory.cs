#nullable enable

using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Live.Application;

/// <summary>
/// Retains handler allocation even if the real chat constructor throws while disposing it.
/// The forwarding handler adds no headers/redirects/cookies and never retries a failed release.
/// </summary>
internal sealed class LiveChatFactory
{
    private OwnedHandler? _attempt;
    internal bool HasWork => _attempt is { ReleaseConfirmed: false };

    internal ILiveChatClient Create(IBiliCookiesResolver cookies, IBiliTokenResolver tokens,
        Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
    {
        ForgetReleased();
        if (_attempt is not null) throw Failure();
        var handler = new OwnedHandler();
        _attempt = handler; // Managed owner precedes secure-handler allocation and constructor transfer.
        try
        {
            handler.InnerHandler = LiveChatClient.CreateSecureHandler();
            return new LiveChatClient(handler, cookies, tokens, account, dispatcher);
        }
        catch
        {
            // Chat's constructor normally disposes its transferred handler itself. Never do it twice.
            if (!handler.DisposeStarted)
            {
                try { handler.Dispose(); } catch { }
            }
            ForgetReleased();
            throw Failure();
        }
    }

    internal void ForgetReleased()
    {
        if (_attempt is { ReleaseConfirmed: true }) _attempt = null;
    }

    private static InvalidOperationException Failure() => new("直播聊天初始化或释放未完成，未启动替代连接。");

    internal sealed class OwnedHandler : DelegatingHandler
    {
        internal bool DisposeStarted { get; private set; }
        internal bool ReleaseConfirmed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            if (DisposeStarted)
            {
                if (!ReleaseConfirmed) throw Failure();
                return;
            }
            DisposeStarted = true;
            try { base.Dispose(true); ReleaseConfirmed = true; }
            catch { throw Failure(); }
        }
    }
}
