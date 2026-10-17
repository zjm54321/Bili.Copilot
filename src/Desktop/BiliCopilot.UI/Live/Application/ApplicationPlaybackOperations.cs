#nullable enable


namespace BiliCopilot.UI.Live.Application;

/// <summary>Publishes app entry work before callbacks; shutdown joins real workers, not cancellation waiters.</summary>
internal sealed class ApplicationPlaybackOperations(Action checkAccess)
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Work> _work = [];
    private Task? _stop;
    private Task? _stopWorker;
    internal bool IsStopping => _stop is not null;
    internal bool HasWork => _work.Count != 0 || _stop is { IsCompleted: false };

    internal Task RunAsync(Func<CancellationToken, Task> action)
    {
        checkAccess();
        if (IsStopping) throw new InvalidOperationException("应用正在关闭，未开始新的播放。");
        var work = new Work();
        _work.Add(work);
        work.Worker = RunCoreAsync(work, action);
        // T1-01: work.Done is published before action callbacks; callers receive the retained asynchronous operation, not a blocking UI wait.
        #pragma warning disable VSTHRD003
        return work.Done.Task;
        #pragma warning restore VSTHRD003
    }

    private async Task RunCoreAsync(Work work, Func<CancellationToken, Task> action)
    {
        await Task.Yield();
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await action(_lifetime.Token);
            work.Done.TrySetResult();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            work.Done.TrySetCanceled(_lifetime.Token);
        }
        catch
        {
            work.Done.TrySetException(new InvalidOperationException("播放操作未完成。"));
        }
        finally { _work.Remove(work); }
    }

    internal Task StopAsync(Func<Task> closeOwners)
    {
        checkAccess();
        if (_stop is not null) return _stop;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stop = done.Task; // Shut admission before invoking cancellation/close callbacks.
        var work = _work.ToArray();
        var cancellation = LiveRouteCoordinator.Invoke(() => _lifetime.CancelAsync());
        var close = LiveRouteCoordinator.Invoke(closeOwners);
        _stopWorker = StopCoreAsync(done, work, cancellation, close);
        return done.Task;
    }

    private static async Task StopCoreAsync(TaskCompletionSource done, Work[] work, Task cancellation, Task close)
    {
        await Task.Yield();
        var failed = false;
        try {
            // T1-02: Cancellation and playback close start before this join; neither producer waits for the shutdown consumer.
            #pragma warning disable VSTHRD003
            await Task.WhenAll(cancellation, close);
            #pragma warning restore VSTHRD003
        }
        catch
        {
            // WhenAll has observed both actual completions. A finished cancellation callback fault
            // is not failed physical release; an unsuccessful owner close still fails closed.
            failed = !close.IsCompletedSuccessfully;
        }
        foreach (var item in work)
        {
            try {
                // T1-03: Each Work.Done was prepublished; shutdown observes its actual result after starting cancellation and close.
                #pragma warning disable VSTHRD003
                await item.Done.Task;
                #pragma warning restore VSTHRD003
            } catch { /* The entry's result is observed separately. */ }
            if (item.Worker is not null)
                // T1-04: The retained Work.Worker must finish as well as Done; cancellation or a published result is not physical drain.
                #pragma warning disable VSTHRD003
                await item.Worker;
                #pragma warning restore VSTHRD003
        }
        if (failed) done.TrySetException(new InvalidOperationException("应用播放关闭未完成，所有者仍被保留。"));
        else done.TrySetResult();
    }

    private sealed class Work
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Worker;
    }
}
