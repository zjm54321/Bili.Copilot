#nullable enable

using System.Net.WebSockets;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.Live;

// Internal composition seams only: no additional public application contract.
internal interface ILiveChatDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action callback);
}

internal sealed class LiveChatDispatcher : ILiveChatDispatcher
{
    private readonly DispatcherQueue _queue;
    internal LiveChatDispatcher(DispatcherQueue queue) => _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    public bool HasThreadAccess => _queue.HasThreadAccess;
    public bool TryEnqueue(Action callback) => _queue.TryEnqueue(() => callback());
}

internal static class LiveChatDispatch
{
    internal static async Task<T> InvokeAsync<T>(ILiveChatDispatcher dispatcher, Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (dispatcher.HasThreadAccess)
            return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = 0;
        using var cancellation = token.Register(() =>
        {
            if (Interlocked.CompareExchange(ref admission, 2, 0) == 0)
                completion.TrySetCanceled(token);
        });
        if (!dispatcher.TryEnqueue(() =>
        {
            // Canceled queued work has no transport ownership and may never admit work later.
            if (Interlocked.CompareExchange(ref admission, 1, 0) != 0)
                return;
            try { completion.TrySetResult(action()); }
            catch (Exception) { completion.TrySetException(new LiveChatFailure("Live chat UI handoff is unavailable.", false)); }
        }))
        {
            Interlocked.CompareExchange(ref admission, 2, 0);
            completion.TrySetException(new LiveChatFailure("Live chat UI handoff is unavailable.", false));
        }
        return await completion.Task.ConfigureAwait(false);
    }
}

internal interface ILiveChatSocket : IDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri endpoint, CancellationToken token);
    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token);
    Task SendAsync(ArraySegment<byte> buffer, CancellationToken token);
    Task CloseOutputAsync(CancellationToken token);
    void Abort();
}

internal sealed class LiveChatSocket : ILiveChatSocket
{
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _invoker;

    internal LiveChatSocket()
    {
        _socket = new ClientWebSocket();
        try
        {
            _socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            // The discovery handler is NOT reused, and no Cookie header/options are set.
            // An explicit invoker also disables redirects on the WSS upgrade itself.
            _invoker = new HttpMessageInvoker(LiveHttpApi.CreateSecureHandler(), disposeHandler: true);
        }
        catch { _socket.Dispose(); throw; }
    }

    public WebSocketState State => _socket.State;
    public Task ConnectAsync(Uri endpoint, CancellationToken token) => _socket.ConnectAsync(endpoint, _invoker, token);
    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => _socket.ReceiveAsync(buffer, token);
    public Task SendAsync(ArraySegment<byte> buffer, CancellationToken token) => _socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token);
    public Task CloseOutputAsync(CancellationToken token) => _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
    public void Abort() => _socket.Abort();
    public void Dispose()
    {
        try { _socket.Dispose(); }
        finally { _invoker.Dispose(); }
    }
}
