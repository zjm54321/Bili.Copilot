using BiliCopilot.UI.SponsorBlock.Application;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>Queue callbacks only publish actual Tasks; no async-void bridge or synchronous UI wait.</summary>
internal sealed class SponsorDispatcher : ISponsorDispatcher
{
    private readonly DispatcherQueue queue;
    private readonly SynchronizationContext context;

    internal SponsorDispatcher(DispatcherQueue queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        context = new DispatcherQueueSynchronizationContext(queue);
    }

    public bool HasAccess => queue.HasThreadAccess;
    internal void CheckAccess()
    {
        if (!HasAccess) throw new InvalidOperationException("Sponsor UI owner is unavailable.");
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!queue.TryEnqueue(() => InContext(action)))
            throw new InvalidOperationException("Sponsor dispatcher rejected admission.");
    }

    private void InContext(Action action)
    {
        CheckAccess();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    internal Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Start()
        {
            try { started.TrySetResult(action()); }
            catch (Exception error) { started.TrySetException(error); }
        }
        try { if (HasAccess) InContext(Start); else Post(Start); }
        catch (Exception error) { started.TrySetException(error); }
        return started.Task.Unwrap();
    }

    internal Task<T> ReadAsync<T>(Func<T> action) => InvokeAsync(() => Task.FromResult(action()));
}
