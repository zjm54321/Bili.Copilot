// Copyright (c) Bili Copilot. All rights reserved.

using FluentResults;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;

namespace BiliCopilot.UI.Classic.Backends.Island;

/// <summary>
/// Owns the engine, not the HWND. The host must keep its dedicated render HWND alive until
/// DisposeAsync finishes. No MpvPlayer or second mpv event pump is created here.
/// </summary>
public sealed class IslandPlaybackBackend : IClassicPlaybackBackend
{
    private readonly nint _renderHwnd;
    private readonly string _libmpvPath;
    private readonly ClassicDecodePreference _decode;
    private readonly object _sync = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed in the existing DisposeAsync worker finally after cancellation, predecessor and latest sampler drain, including engine-release failure. The MpvClient owner is separately retained on uncertain release.")]
    private readonly CancellationTokenSource _samplingCancellation = new();
    private Task _tail = Task.CompletedTask;
    private Task? _sampling;
    private Task? _dispose;
    private long _generation;
    private bool _closing;
    private MpvClient? _client;
    private TaskCompletionSource<bool>? _ready;
    private bool _releaseUnconfirmed;
    private EventHandler? _loadingHandler;
    private EventHandler? _loadedHandler;
    private EventHandler? _endHandler;
    private EventHandler<MpvError>? _errorHandler;
    private bool _loaded;
    private ClassicPlaybackState _state = ClassicPlaybackState.Idle;
    private double _position;
    private double _duration;
    private double _volume = 1;
    private double _speed = 1;
    private string? _safeError;

    public IslandPlaybackBackend(nint renderHwnd, string libmpvPath, ClassicSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (renderHwnd == 0) throw new ArgumentException("Classic Island requires a render HWND.", nameof(renderHwnd));
        if (string.IsNullOrWhiteSpace(libmpvPath) || !Path.IsPathFullyQualified(libmpvPath))
            throw new ArgumentException("Classic Island requires an absolute libmpv DLL path.", nameof(libmpvPath));
        _renderHwnd = renderHwnd;
        _libmpvPath = libmpvPath;
        _decode = options.DecodePreference;
    }

    public ClassicBackendKind Kind => ClassicBackendKind.Island;
    public ClassicCapabilities Capabilities => ClassicCapabilities.Transport | ClassicCapabilities.Seek
        | ClassicCapabilities.Volume | ClassicCapabilities.Speed | ClassicCapabilities.Quality | ClassicCapabilities.Progress;
    public event EventHandler<ClassicBackendStatus>? StatusChanged;

    public Task OpenAsync(ClassicResolvedMedia media, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        return CommandAsync(generation => OpenCoreAsync(media, generation, cancellationToken), cancellationToken, replace: true);
    }

    public Task PlayAsync(CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        var client = RequireClient();
        if (_state == ClassicPlaybackState.Ended)
            Check(await client.SetCurrentPositionAsync(0).ConfigureAwait(false), "Island replay seek failed.");
        Check(await client.ResumeAsync().ConfigureAwait(false), "Island resume failed.");
        await SampleAsync(generation, client).ConfigureAwait(false);
    }, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        var client = RequireClient();
        Check(await client.PauseAsync().ConfigureAwait(false), "Island pause failed.");
        await SampleAsync(generation, client).ConfigureAwait(false);
    }, cancellationToken);

    public Task SeekAsync(double seconds, CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new BackendFailure("Invalid Island seek position.");
        var client = RequireClient();
        Check(await client.SetCurrentPositionAsync(seconds).ConfigureAwait(false), "Island seek failed.");
        await SampleAsync(generation, client).ConfigureAwait(false);
    }, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 1) throw new BackendFailure("Island volume must be between 0 and 1.");
        if (_client is not null) Check(await _client.SetVolumeAsync(volume * 100).ConfigureAwait(false), "Island volume change failed.");
        _volume = volume;
        Publish(generation);
    }, cancellationToken);

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        if (!double.IsFinite(speed) || speed <= 0) throw new BackendFailure("Invalid Island playback speed.");
        if (_client is not null) Check(await _client.SetSpeedAsync(speed).ConfigureAwait(false), "Island speed change failed.");
        _speed = speed;
        Publish(generation);
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        await ReleaseClientAsync().ConfigureAwait(false);
        _state = ClassicPlaybackState.Idle;
        _position = _duration = 0;
        _safeError = null;
        Publish(generation);
    }, cancellationToken, replace: true);

    private async Task OpenCoreAsync(ClassicResolvedMedia media, long generation, CancellationToken cancellationToken)
    {
        // A client per open is deliberate: mpv's public file events have no media identity.
        // Rebinding handlers on a reused client would mislabel a previous file's late end event.
        await ReleaseClientAsync().ConfigureAwait(false);
        EnsureCurrent(generation, cancellationToken);
        _state = ClassicPlaybackState.Loading;
        _safeError = null;
        _position = _duration = 0;
        Publish(generation);
        try
        {
            if (!File.Exists(_libmpvPath)) throw new BackendFailure("Classic Island libmpv DLL is unavailable.");
            var decode = _decode switch
            {
                ClassicDecodePreference.Auto => HardwareDecodeType.Auto,
                ClassicDecodePreference.Software => HardwareDecodeType.None,
                ClassicDecodePreference.D3D11 => HardwareDecodeType.D3D11va,
                _ => throw new BackendFailure("Unsupported Classic Island decode preference."),
            };
            var uri = media.VideoUri ?? media.AudioUri;
            if (uri is null || !uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new BackendFailure("Classic Island requires an HTTP(S) media URL.");
            if (media.AudioUri is { } audioUri && (!audioUri.IsAbsoluteUri
                || (audioUri.Scheme != Uri.UriSchemeHttps && audioUri.Scheme != Uri.UriSchemeHttp)))
                throw new BackendFailure("Classic Island requires an HTTP(S) audio URL.");
            if (!double.IsFinite(media.StartSeconds) || media.StartSeconds < 0)
                throw new BackendFailure("Invalid Island start position.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in media.Headers)
            {
                if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n'))
                    throw new BackendFailure("Invalid Island media header.");
                headers.Add(header.Key, header.Value);
            }
            headers.Remove("User-Agent", out var userAgent);

            // CreateAsync already initializes and starts the library's event pump.
            _client = await MpvClient.CreateAsync(_libmpvPath,
                new MpvInitializeOptions { UseConfig = false, LoadScripts = false }).ConfigureAwait(false);
            EnsureCurrent(generation, cancellationToken);
            var client = _client;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) { _ready = ready; if (!IsCurrent(generation)) ready.TrySetCanceled(); }
            using var readyCancellation = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
            // Complete readiness directly, never enqueue behind the Open that is waiting for it.
            _loadedHandler = (_, _) =>
            {
                lock (_sync)
                    if (IsCurrent(generation) && ReferenceEquals(_client, client)) ready.TrySetResult(true);
            };
            _endHandler = (_, _) =>
            {
                lock (_sync)
                {
                    if (!IsCurrent(generation) || !ReferenceEquals(_client, client)) return;
                    if (ready.TrySetException(new BackendFailure("Island media ended before it became ready."))) return;
                }
                Callback(generation, client, async () =>
            {
                if (_state == ClassicPlaybackState.Failed) return;
                if (!_loaded) throw new BackendFailure("Island media ended before it became ready.");
                // ReachFileEnd also represents errors/stops. Only the actual EOF state is Ended.
                await SampleAsync(generation, client).ConfigureAwait(false);
                if (_state != ClassicPlaybackState.Ended) throw new BackendFailure("Island media stopped unexpectedly.");
                });
            };
            _errorHandler = (_, error) =>
            {
                var failure = new BackendFailure($"Island playback failed (mpv {(int)error}).");
                lock (_sync)
                {
                    if (!IsCurrent(generation) || !ReferenceEquals(_client, client)) return;
                    if (ready.TrySetException(failure)) return;
                }
                Callback(generation, client, () => throw failure);
            };
            client.ReachFileLoaded += _loadedHandler;
            client.ReachFileEnd += _endHandler;
            client.ErrorOccurred += _errorHandler;

            // These Task-returning setters throw on native failure (verified library IL).
            await client.SetVideoOutputAsync(VideoOutputType.GpuNext).ConfigureAwait(false);
            await client.SetGpuApiAsync(GpuApiType.D3D11).ConfigureAwait(false);
            await client.SetGpuContextAsync(GpuContextType.D3D11).ConfigureAwait(false);
            await client.SetHardwareDecodeAsync(decode).ConfigureAwait(false);
            Check(await client.UseKeepOpenAsync(true).ConfigureAwait(false), "Island keep-open configuration failed.");
            EnsureCurrent(generation, cancellationToken);
            await client.PlayAsync(uri.AbsoluteUri, new MpvPlayOptions
            {
                WindowHandle = _renderHwnd,
                AudioTracks = media.VideoUri is not null && media.AudioUri is not null ? [media.AudioUri.AbsoluteUri] : null,
                HttpHeaders = headers,
                UserAgent = userAgent,
                EnableCookies = true,
                EnableYtdl = false,
                StartPosition = media.StartSeconds,
                InitialVolume = _volume * 100,
                InitialSpeed = _speed,
            }).ConfigureAwait(false);
            await ready.Task.ConfigureAwait(false);
            EnsureCurrent(generation, cancellationToken);
            _loaded = true;
            if (_decode == ClassicDecodePreference.D3D11
                && !string.Equals(MpvNative.GetPropertyString(client.Handle, "hwdec-current"), "d3d11va", StringComparison.Ordinal))
                throw new BackendFailure("D3D11 decoding is unavailable for this Island media/device.");
            await SampleAsync(generation, client).ConfigureAwait(false);
            _sampling ??= SampleLoopAsync();
        }
        catch
        {
            await ReleaseClientAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task SampleAsync(long generation, MpvClient client)
    {
        if (!_loaded || !IsCurrent(generation) || _state == ClassicPlaybackState.Failed) return;
        var state = await client.GetPlayerStateAsync().ConfigureAwait(false);
        if (state.IsFailed) throw new BackendFailure("Island playback state is unavailable.");
        var position = await client.GetCurrentPositionAsync().ConfigureAwait(false);
        var duration = await client.GetDurationAsync().ConfigureAwait(false);
        var volume = await client.GetVolumeAsync().ConfigureAwait(false);
        var speed = await client.GetSpeedAsync().ConfigureAwait(false);
        if (!IsCurrent(generation)) return;
        // Live streams and an EOF file need not expose duration/time-pos. Never read Value on failure.
        if (position.IsSuccess && double.IsFinite(position.Value)) _position = Math.Max(0, position.Value);
        if (duration.IsSuccess && double.IsFinite(duration.Value)) _duration = Math.Max(0, duration.Value);
        if (volume.IsSuccess && double.IsFinite(volume.Value)) _volume = Math.Clamp(volume.Value / 100, 0, 1);
        if (speed.IsSuccess && double.IsFinite(speed.Value) && speed.Value > 0) _speed = speed.Value;
        _state = state.Value switch
        {
            MpvPlayerState.Playing => ClassicPlaybackState.Playing,
            MpvPlayerState.Paused => ClassicPlaybackState.Paused,
            MpvPlayerState.Buffering or MpvPlayerState.Seeking => ClassicPlaybackState.Loading,
            MpvPlayerState.End => ClassicPlaybackState.Ended,
            _ => ClassicPlaybackState.Loading,
        };
        Publish(generation);
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            // One awaited sample at a time, sharing the command queue; this is not an mpv event pump.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_samplingCancellation.Token).ConfigureAwait(false))
            {
                Task sample;
                lock (_sync)
                {
                    if (_closing) return;
                    var generation = _generation;
                    sample = EnqueueLocked(async () =>
                    {
                        if (!IsCurrent(generation) || _client is null) return;
                        try { await SampleAsync(generation, _client).ConfigureAwait(false); }
                        catch (Exception error) { Fail(generation, error); }
                    });
                }
                await sample.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_samplingCancellation.IsCancellationRequested) { }
    }

    private MpvClient RequireClient() => _client is not null && _loaded && !_releaseUnconfirmed && _state != ClassicPlaybackState.Failed
        ? _client : throw new BackendFailure("Island media is not open.");

    private async Task ReleaseClientAsync()
    {
        var client = _client;
        _loaded = false;
        if (client is null) return;
        if (_releaseUnconfirmed)
            throw new BackendFailure("Island engine release remains unconfirmed; its HWND and owner must be retained. Library disposal cannot safely be retried.");
        lock (_sync) { _ready?.TrySetCanceled(); _ready = null; }
        client.ReachFileLoading -= _loadingHandler;
        client.ReachFileLoaded -= _loadedHandler;
        client.ReachFileEnd -= _endHandler;
        client.ErrorOccurred -= _errorHandler;
        _loadingHandler = _loadedHandler = _endHandler = null;
        _errorHandler = null;
        Exception? stopFailure = null;
        try { Check(await client.StopAsync().ConfigureAwait(false), "Island stop failed."); }
        catch (Exception error) { stopFailure = error; }
        try
        {
            // This awaits the library's disposal, NOT a join of its private untracked event task.
            // The pinned MpvKernel API does not expose that join; do not claim otherwise.
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // MpvClient sets IsDisposed before finishing cleanup. A second no-op call is NOT proof.
            _releaseUnconfirmed = true;
            throw new BackendFailure("Island engine release is unconfirmed; retain its HWND and owner. Safe retry is unavailable.");
        }
        _client = null; // Only a successful disposal releases ownership.
        if (stopFailure is not null) _safeError = "Island stop failed, but engine disposal completed.";
    }

    private Task CommandAsync(Func<long, Task> action, CancellationToken cancellationToken, bool replace = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (replace) { _generation++; _ready?.TrySetCanceled(); }
            var generation = _generation;
            return EnqueueLocked(async () =>
            {
                EnsureCurrent(generation, cancellationToken);
                try
                {
                    await action(generation).ConfigureAwait(false);
                    EnsureCurrent(generation, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    Fail(generation, error);
                    throw new InvalidOperationException(SafeMessage(error));
                }
            });
        }
    }

    private void Callback(long generation, MpvClient client, Func<Task> action)
    {
        lock (_sync)
        {
            if (_closing || generation != _generation) return;
            _ = EnqueueLocked(async () =>
            {
                if (!IsCurrent(generation) || !ReferenceEquals(_client, client)) return;
                try { await action().ConfigureAwait(false); }
                catch (Exception error) { Fail(generation, error); }
            });
        }
    }

    private Task EnqueueLocked(Func<Task> action)
    {
        var predecessor = _tail;
        var task = Task.Run(async () =>
        {
#pragma warning disable VSTHRD003 // Captured old tail precedes this operation and cannot depend on it; worker joins outside _sync. No new Task.Run wrapper is introduced.
            await predecessor.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await action().ConfigureAwait(false);
        });
        _tail = ObserveAsync(task);
        return task;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Single-await tail observer holds no lock; it drains the preceding worker without changing that worker task's fault or depending on later commands.")]
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private bool IsCurrent(long generation) { lock (_sync) return !_closing && generation == _generation; }
    private void EnsureCurrent(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(generation)) throw new OperationCanceledException("Island operation was superseded.");
    }

    private void Fail(long generation, Exception error)
    {
        if (!IsCurrent(generation)) return;
        _state = ClassicPlaybackState.Failed;
        _safeError = SafeMessage(error);
        Publish(generation);
    }

    private static string SafeMessage(Exception error) => error is BackendFailure ? error.Message : "Classic Island operation failed; no backend was substituted.";
    private static void Check(Result result, string safeError) { if (result.IsFailed) throw new BackendFailure(safeError); }
    private sealed class BackendFailure : Exception
    {
        public BackendFailure() { }
        public BackendFailure(string? message) : base(message) { }
        public BackendFailure(string? message, Exception? innerException) : base(message, innerException) { }
    }

    private void Publish(long generation)
    {
        lock (_sync)
        {
            if (_closing || generation != _generation) return;
            var status = new ClassicBackendStatus(_state, _position, _duration, _volume, _speed, _safeError);
            if (StatusChanged is not { } handlers) return;
            foreach (EventHandler<ClassicBackendStatus> handler in handlers.GetInvocationList())
            {
                try { handler(this, status); }
                catch { /* A subscriber cannot break engine cleanup or the command chain. */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is not null) return new(_dispose);
            _closing = true;
            _generation++;
            _ready?.TrySetCanceled();
            var cancelSampling = _samplingCancellation.CancelAsync();
            var pending = _tail;
            _dispose = Task.Run(async () =>
            {
                try
                {
#pragma warning disable VSTHRD003 // This instance's timer cancellation has no dependency on the close worker.
                    try { await cancelSampling.ConfigureAwait(false); }
                    catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic Island sampler cancellation failed; cleanup continues."); }
#pragma warning restore VSTHRD003
#pragma warning disable VSTHRD003 // Dispose is not appended to the captured tail; ready was cancelled before this lock-free predecessor join.
                    await pending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    // Read the latest sampler only after draining the admitted Open that can create it.
#pragma warning disable VSTHRD003 // Closing prevents sampler admission; any already-admitted sample is drained by the completed predecessor.
                    await (_sampling ?? Task.CompletedTask).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    await ReleaseClientAsync().ConfigureAwait(false);
                }
                finally { _samplingCancellation.Dispose(); StatusChanged = null; }
            });
            return new(_dispose);
        }
    }
}
