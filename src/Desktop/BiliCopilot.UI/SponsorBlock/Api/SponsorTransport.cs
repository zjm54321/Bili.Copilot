using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Api;

public sealed record SponsorNetworkConsent(bool NetworkEnabled = false, bool ViewedReportingEnabled = false);

internal sealed record WireResponse(ReadStatus Status, byte[]? Bytes = null, int? HttpStatus = null);

/// <summary>Instance-owned bounded reads. A caller's cancellation only stops its wait, not other joined callers.</summary>
internal sealed class SponsorTransport : IDisposable
{
    internal const int CacheByteLimit = 500 * 1024;
    internal const int ResponseByteLimit = 1024 * 1024;
    private sealed record CacheEntry(WireResponse Response, DateTimeOffset Expires, bool Labels);
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requestBudget = new(4);
    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<WireResponse>> inflight = new(StringComparer.Ordinal);
    private bool disposed;
    private int physicalUsers;
    private bool cleanupStarted;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? shutdownFailure;

    internal SponsorTransport(HttpMessageHandler handler, TimeProvider clock, TimeSpan timeout)
    {
        this.clock = clock; this.timeout = timeout;
        http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(SponsorClient.UserAgent);
    }

    internal async Task<WireResponse> ReadAsync(string path, string identity, bool labels, bool refresh,
        Func<byte[], bool> schema, CancellationToken callerCancellation)
    {
        if (callerCancellation.IsCancellationRequested) return new(ReadStatus.Cancelled);
        Task<WireResponse> task;
        var key = "https://www.bsbsb.top|" + path + "|" + identity;
        lock (gate)
        {
            if (disposed) return new(ReadStatus.Cancelled);
            Prune();
            if (!refresh && cache.TryGetValue(key, out var entry)) return entry.Response;
            if (!inflight.TryGetValue(key, out task!))
            {
                if (inflight.Count >= 32) return new(ReadStatus.Busy);
                // Reserve before scheduling: a cancelled waiter does not end the physical read.
                ++physicalUsers;
                try
                {
                    // Task.Run prevents synchronous handler completion racing insertion under the owner lock.
                    task = Task.Run(() => LoadAsync(key, path, labels, schema));
                }
                catch
                {
                    --physicalUsers; // Scheduling failed while admission is still locked open.
                    throw;
                }
                inflight.Add(key, task);
            }
        }
        try { return await task.WaitAsync(callerCancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(ReadStatus.Cancelled); }
    }

    private async Task<WireResponse> LoadAsync(string key, string path, bool labels, Func<byte[], bool> schema)
    {
        WireResponse result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(SponsorClient.Service, path));
            result = await ExchangeCoreAsync(request, lifetime.Token).ConfigureAwait(false);
            if (result.Status == ReadStatus.Success && (result.Bytes is null || !schema(result.Bytes)))
                result = new(ReadStatus.SchemaFailure, HttpStatus: result.HttpStatus);
            lock (gate)
            {
                if (!disposed && result.Status is ReadStatus.Success or ReadStatus.Empty404
                    && (result.Bytes?.Length ?? 0) <= CacheByteLimit)
                {
                    cache.Remove(key);
                    while (cache.Count(p => p.Value.Labels == labels) >= (labels ? 5000 : 1000)
                        || cache.Where(p => p.Value.Labels == labels).Sum(p => p.Value.Response.Bytes?.Length ?? 0)
                            + (result.Bytes?.Length ?? 0) > CacheByteLimit)
                    {
                        var oldest = cache.Where(p => p.Value.Labels == labels).OrderBy(p => p.Value.Expires).First();
                        cache.Remove(oldest.Key);
                    }
                    cache[key] = new(result, clock.GetUtcNow().AddHours(1), labels);
                }
            }
            return result;
        }
        finally
        {
            try { lock (gate) inflight.Remove(key); }
            finally { ReleasePhysicalUser(); }
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in cache.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) cache.Remove(key);
    }

    internal async Task<WireResponse> ExchangeAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        Action? onDispatch = null, bool readBody = true)
    {
        lock (gate)
        {
            if (disposed) return new(ReadStatus.Cancelled);
            ++physicalUsers; // Direct exchanges/writes are not represented by inflight.
        }
        try { return await ExchangeCoreAsync(request, cancellationToken, onDispatch, readBody).ConfigureAwait(false); }
        finally { ReleasePhysicalUser(); }
    }

    // The caller already owns a physical user, including before the first lifetime/semaphore access.
    private async Task<WireResponse> ExchangeCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        Action? onDispatch = null, bool readBody = true)
    {
        using var bounded = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        // Explicit forwarding registrations let us join cancellation execution, not just unlink it.
        // The deadline forwards too, so its timer cannot leave bounded callbacks running after drain.
        await using var callerRegistration = cancellationToken.Register(() => CancelRequest(bounded));
        await using var lifetimeRegistration = lifetime.Token.Register(() => CancelRequest(bounded));
        await using var deadlineRegistration = deadline.Token.Register(() => CancelRequest(bounded));
        deadline.CancelAfter(timeout);
        var admitted = false;
        int? observedStatus = null;
        try
        {
            await requestBudget.WaitAsync(bounded.Token).ConfigureAwait(false);
            admitted = true;
            bounded.Token.ThrowIfCancellationRequested();
            onDispatch?.Invoke();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            observedStatus = code;
            if (request.Method == HttpMethod.Get && code == 404) return new(ReadStatus.Empty404, HttpStatus: code);
            if (request.Method == HttpMethod.Get && code != 200) return new(ReadStatus.HttpFailure, HttpStatus: code);
            if (!response.IsSuccessStatusCode) return new(ReadStatus.HttpFailure, HttpStatus: code);
            if (!readBody) return new(ReadStatus.Success, HttpStatus: code);
            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var length = await stream.ReadAsync(buffer, bounded.Token).ConfigureAwait(false);
                if (length == 0) break;
                if (bytes.Length + length > ResponseByteLimit) return new(ReadStatus.SchemaFailure, HttpStatus: code);
                await bytes.WriteAsync(buffer, 0, length).ConfigureAwait(false);
            }
            return new(ReadStatus.Success, bytes.ToArray(), code);
        }
        catch (OperationCanceledException) { return new(ReadStatus.Cancelled, HttpStatus: observedStatus); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        { return new(ReadStatus.TransportFailure, HttpStatus: observedStatus); }
        finally { if (admitted) requestBudget.Release(); }
    }

    internal (int Entries, int Bytes, int Inflight) InspectCache(bool labels)
    {
        lock (gate) return (cache.Count(p => p.Value.Labels == labels),
            cache.Where(p => p.Value.Labels == labels).Sum(p => p.Value.Response.Bytes?.Length ?? 0), inflight.Count);
    }

    public void Dispose()
    {
        bool cancel;
        lock (gate)
        {
            cancel = !disposed;
            if (cancel)
            {
                disposed = true;
                cache.Clear();
                ++physicalUsers; // Cancellation execution is an owner, even with no requests left.
            }
        }
        if (cancel)
        {
            // Keep the existing synchronous contract, but never execute callbacks under gate.
            try { lifetime.Cancel(); }
            catch (Exception error) { RecordShutdownFailure(error); }
            finally { ReleasePhysicalUser(); }
        }
        // Later disposal calls observe the same retained terminal failure; they never retry cleanup.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure;
        lock (gate) failure = shutdownFailure;
        failure?.Throw();
    }

    private void CancelRequest(CancellationTokenSource requestCancellation)
    {
        // An admitted registration may execute after admission closes. Reserve also for the
        // DisposeAsync self-unregistration case, where callback execution has not returned yet.
        lock (gate) ++physicalUsers;
        try { requestCancellation.Cancel(); }
        catch (Exception error) { RecordShutdownFailure(error); }
        finally { ReleasePhysicalUser(); }
    }

    private void ReleasePhysicalUser()
    {
        lock (gate)
        {
            if (--physicalUsers != 0 || !disposed || cleanupStarted) return;
            cleanupStarted = true;
        }
        // Exactly one terminal attempt, after all registration drains and semaphore releases.
        // Never replace a request/ack outcome with a cleanup exception or spawn a cleanup Task.
        try { http.Dispose(); }
        catch (Exception error) { RecordShutdownFailure(error); }
        try { lifetime.Dispose(); }
        catch (Exception error) { RecordShutdownFailure(error); }
        try { requestBudget.Dispose(); }
        catch (Exception error) { RecordShutdownFailure(error); }
    }

    private void RecordShutdownFailure(Exception error)
    {
        // Retain the first failure, without logging handler data or growing an exception history.
        lock (gate) shutdownFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
    }
}
