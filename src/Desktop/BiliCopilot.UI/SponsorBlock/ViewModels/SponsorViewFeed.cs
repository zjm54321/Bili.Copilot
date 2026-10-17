// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 把应用调度器上发布的不可变视图搬运到本界面的 DispatcherQueue。
/// 先订阅、后读取快照；只保留最新修订；解绑后丢弃所有迟到的回调。
/// 不停止、不释放门面或播放器。
/// </summary>
internal sealed class SponsorViewFeed
{
    private readonly SponsorApplicationCoordinator _app;
    private readonly DispatcherQueue _queue;
    private readonly Action<SponsorApplicationView> _apply;
    private SponsorApplicationView? _pending;
    private int _scheduled;
    private int _closed;
    private long _appliedRevision = -1;

    internal SponsorViewFeed(SponsorApplicationCoordinator app, DispatcherQueue queue, Action<SponsorApplicationView> apply)
    {
        _app = app;
        _queue = queue;
        _apply = apply;
    }

    /// <summary>界面线程上最近一次应用的视图。</summary>
    internal SponsorApplicationView? Current { get; private set; }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal void Start()
    {
        // Subscribe before snapshot: anything published in between is ordered by Revision.
        _app.Updated += OnUpdated;
        OnUpdated(_app.Snapshot);
    }

    internal void Stop()
    {
        Interlocked.Exchange(ref _closed, 1);
        _app.Updated -= OnUpdated;
        Interlocked.Exchange(ref _pending, null);
        Current = null;
    }

    // Raised on the APP dispatcher. Never touches UI state here.
    private void OnUpdated(SponsorApplicationView view)
    {
        if (IsClosed || view is null)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _pending);
            if (current is not null && current.Revision >= view.Revision)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref _pending, view, current) == current)
            {
                break;
            }
        }

        if (Interlocked.Exchange(ref _scheduled, 1) == 0 && !_queue.TryEnqueue(Drain))
        {
            // The UI queue is shutting down; nothing can be shown any more.
            Volatile.Write(ref _scheduled, 0);
        }
    }

    private void Drain()
    {
        Volatile.Write(ref _scheduled, 0);
        var view = Interlocked.Exchange(ref _pending, null);
        if (IsClosed || view is null || view.Revision <= _appliedRevision)
        {
            return;
        }

        _appliedRevision = view.Revision;
        Current = view;
        _apply(view);
    }
}

/// <summary>
/// 播放动作所需的完整当前票据。始终在点击时从门面的原子快照读取，
/// 不复用界面里可能已过期的副本。
/// </summary>
internal static class SponsorTickets
{
    internal static PlayerTicket? Read(SponsorApplicationCoordinator app, Guid attachment)
        => Read(app.Snapshot, attachment);

    internal static PlayerTicket? Read(SponsorApplicationView view, Guid attachment)
    {
        var player = view.Players.FirstOrDefault(p => p.Attachment == attachment);
        return player is { Attached: true, Controller.Stamp: { } stamp }
            ? new PlayerTicket(attachment, stamp, view.DraftRevision)
            : null;
    }

    /// <summary>
    /// 执行一次带票据的动作。若结果被归类为接纳前拒绝（没有任何效果），
    /// 且仍是同一媒体、同一次访问、同一草稿修订，只用最新票据再提交一次。
    /// 媒体、访问或草稿修订一旦变化就不再提交。
    /// </summary>
    internal static async Task<ApplicationResult> RunAsync(
        SponsorApplicationCoordinator app,
        Guid attachment,
        PlaybackOwner expectedOwner,
        Func<PlayerTicket, Task<ApplicationResult>> action,
        Func<ApplicationResult, bool>? canRetry = null)
    {
        var first = Read(app, attachment);
        if (first is null)
        {
            return new(ApplicationStatus.Stale, "NoPlayerTicket");
        }

        if (first.Stamp.Owner != expectedOwner)
        {
            return new(ApplicationStatus.Stale, "MediaChanged");
        }

        var result = await action(first);
        if (!(canRetry?.Invoke(result) ?? IsUnadmittedStale(result)))
        {
            return result;
        }

        var second = Read(app, attachment);
        if (second is null || second.Stamp == first.Stamp || second.DraftRevision != first.DraftRevision
            || !SponsorPlayerAttachment.SameVisit(first.Stamp, second.Stamp))
        {
            return result;
        }

        return await action(second);
    }

    // Fetch also returns Stale AFTER physical work. Only its explicit pre-work reason is replayable.
    internal static bool IsReadAdmissionRefusal(ApplicationResult result)
        => result.Status == ApplicationStatus.Stale && result.Reason == "ReadAdmissionStale";

    // Existing action/mutation classification is unchanged; reads supply the stricter predicate above.
    private static bool IsUnadmittedStale(ApplicationResult result)
        => (result.Status == ApplicationStatus.Stale && result.Reason is null)
        || (result.Write?.Status == WriteStatus.NotSent && result.Reason == "StaleDisabledOrBusy");
}
