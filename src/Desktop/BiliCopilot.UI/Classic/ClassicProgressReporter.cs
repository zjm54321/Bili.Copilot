using Richasy.BiliKernel.Bili.Media;

namespace BiliCopilot.UI.Classic;

public sealed record ClassicProgressIdentity(string Aid, string Cid, string? EpisodeId,
    string? SeasonId, bool IsPrivate);

/// <summary>One owned sampling/reporting loop; StopAsync cancels and drains the actual request.</summary>
public sealed class ClassicProgressReporter(IPlayerService playerService) : IAsyncDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private Task? _stop;
    private double _position;
    private bool _playing;

    public void Update(double positionSeconds, bool playing)
    {
        lock (_sync) { _position = positionSeconds; _playing = playing; }
    }

    public void Start(ClassicProgressIdentity? identity, bool enabled)
    {
        lock (_sync)
        {
            if (_stop is { IsCompleted: false }) throw new InvalidOperationException("Previous history stop is still in progress.");
            if (!_loop.IsCompleted) throw new InvalidOperationException("Previous history task was not drained.");
            if (_cancellation is not null) throw new InvalidOperationException("Previous history cancellation source was not released.");
            _stop = null;
            if (!enabled || identity is null || identity.IsPrivate) return;
            _cancellation = new();
            _loop = RunAsync(identity, _cancellation.Token);
        }
    }

    private async Task RunAsync(ClassicProgressIdentity identity, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                double position;
                bool playing;
                lock (_sync) { position = _position; playing = _playing; }
                if (!playing || !double.IsFinite(position) || position < 0) continue;
                var seconds = (int)Math.Min(position, int.MaxValue);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (identity.EpisodeId is null)
                        await playerService.ReportVideoProgressAsync(identity.Aid, identity.Cid, seconds, token).ConfigureAwait(false);
                    else
                        await playerService.ReportEpisodeProgressAsync(identity.Aid, identity.Cid,
                            identity.EpisodeId, identity.SeasonId!, seconds, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic progress report failed; sampling continues."); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
#pragma warning disable VSTHRD003 // Share this instance's published stop completion; the reporting loop does not depend on StopAsync or its caller.
            if (_stop is not null) return _stop;
#pragma warning restore VSTHRD003
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task;
            _ = CompleteStopAsync(_cancellation, _loop, completion);
            return completion.Task;
        }
    }

    private async Task CompleteStopAsync(CancellationTokenSource? cancellation, Task loop, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
#pragma warning disable VSTHRD003 // Join the captured owned reporting loop; it never awaits this stop task and no monitor is held across this await.
            try { await loop.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
#pragma warning restore VSTHRD003
            cancellation?.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, cancellation) && ReferenceEquals(_loop, loop)) _cancellation = null;
            }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
