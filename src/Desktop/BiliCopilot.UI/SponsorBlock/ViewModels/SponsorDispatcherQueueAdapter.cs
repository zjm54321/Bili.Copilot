// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 可选的 <see cref="ISponsorDispatcher"/> 适配器，供宿主组合时使用（应用调度器或播放器窗口调度器）。
/// DispatcherQueue 先进先出，并在其线程上安装 SynchronizationContext；关闭后拒绝入队时抛出异常，而不是静默丢弃。
/// 不跨窗口假设调度器等价：每个窗口各自构造一个。
/// </summary>
internal sealed class SponsorDispatcherQueueAdapter : ISponsorDispatcher
{
    private readonly DispatcherQueue _queue;

    internal SponsorDispatcherQueueAdapter(DispatcherQueue queue)
        => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <inheritdoc/>
    public bool HasAccess => _queue.HasThreadAccess;

    /// <inheritdoc/>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action()))
        {
            throw new InvalidOperationException("SponsorDispatcherShutdown");
        }
    }
}
