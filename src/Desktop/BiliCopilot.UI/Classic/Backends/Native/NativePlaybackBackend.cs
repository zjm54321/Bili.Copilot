// Copyright (c) Bili Copilot. All rights reserved.

using System.Xml;
using System.Xml.Linq;
using BiliCopilot.UI.Models.Constants;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using HttpClient = Windows.Web.Http.HttpClient;

namespace BiliCopilot.UI.Classic.Backends.Native;

/// <summary>
/// Owns the Windows media objects; the host owns its MediaPlayerElement and dispatcher.
/// Construct on the element's UI thread and keep that dispatcher pumping until disposal completes.
/// </summary>
public sealed class NativePlaybackBackend : IClassicPlaybackBackend
{
    private readonly MediaPlayerElement _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly ClassicDecodePreference _decode;
    private readonly object _sync = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed by DisposeAsync's existing worker only after ReleaseMediaAsync succeeds. A failed close retains this CTS for explicit cancellation/drain retry.")]
    private readonly CancellationTokenSource _samplingCancellation = new();
    private Task _tail = Task.CompletedTask;
    private Task? _sampling;
    private Task? _dispose;
    private TaskCompletionSource<string?>? _ready;
    private long _generation;
    private bool _closing;
    private bool _opened;
    // Retained for the entire playback lifetime, not merely the manifest creation await.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseMediaAsync disposes this HTTP client after dependent media/streams; field is cleared only after successful Dispose and retained if an earlier release is uncertain.")]
    private HttpClient? _http;
    private MemoryStream? _manifestBytes;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseMediaAsync disposes the WinRT input stream after its media dependents, clearing this field only on success and retaining it for failed-close retry.")]
    private IInputStream? _manifestInput;
    private AdaptiveMediaSource? _adaptive;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseMediaAsync disposes this media source after player release; failed disposal or an uncertain player release retains the field instead of dropping ownership.")]
    private MediaSource? _source;
    private MediaPlaybackItem? _item;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseMediaAsync detaches and disposes this player; the owner is cleared only after successful Dispose, and remains alive with its host on release failure.")]
    private MediaPlayer? _player;
    private TypedEventHandler<MediaPlayer, object>? _openedHandler;
    private TypedEventHandler<MediaPlayer, object>? _endedHandler;
    private TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? _failedHandler;
    private TypedEventHandler<MediaPlaybackSession, object>? _stateHandler;
    private ClassicPlaybackState _state = ClassicPlaybackState.Idle;
    private double _position;
    private double _duration;
    private double _volume = 1;
    private double _speed = 1;
    private string? _safeError;

    public NativePlaybackBackend(MediaPlayerElement host, ClassicSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        _host = host;
        _dispatcher = host.DispatcherQueue;
        if (!_dispatcher.HasThreadAccess) throw new ArgumentException("Create the Native backend on the MediaPlayerElement UI thread.", nameof(host));
        _decode = options.DecodePreference;
    }

    public ClassicBackendKind Kind => ClassicBackendKind.Native;
    public ClassicCapabilities Capabilities => ClassicCapabilities.Transport | ClassicCapabilities.Seek
        | ClassicCapabilities.Volume | ClassicCapabilities.Speed | ClassicCapabilities.Quality | ClassicCapabilities.Progress;
    public event EventHandler<ClassicBackendStatus>? StatusChanged;

    public Task OpenAsync(ClassicResolvedMedia media, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        return CommandAsync(generation => OpenCoreAsync(media, generation, cancellationToken), cancellationToken, replace: true);
    }

    public Task PlayAsync(CancellationToken cancellationToken) => CommandAsync(generation =>
    {
        var player = RequirePlayer();
        if (_state == ClassicPlaybackState.Ended) SeekCore(player, 0);
        _state = ClassicPlaybackState.Paused;
        player.Play();
        UpdateStatus(generation, player);
        return Task.CompletedTask;
    }, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) => CommandAsync(generation =>
    {
        var player = RequirePlayer();
        player.Pause();
        UpdateStatus(generation, player);
        return Task.CompletedTask;
    }, cancellationToken);

    public Task SeekAsync(double seconds, CancellationToken cancellationToken) => CommandAsync(generation =>
    {
        SeekCore(RequirePlayer(), seconds);
        if (_state == ClassicPlaybackState.Ended) _state = ClassicPlaybackState.Paused;
        UpdateStatus(generation, RequirePlayer());
        return Task.CompletedTask;
    }, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => CommandAsync(generation =>
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 1) throw new BackendFailure("Native volume must be between 0 and 1.");
        if (_player is not null) _player.Volume = volume; // WinRT already uses normalized 0–1.
        _volume = volume;
        Publish(generation);
        return Task.CompletedTask;
    }, cancellationToken);

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => CommandAsync(generation =>
    {
        if (!double.IsFinite(speed) || speed <= 0) throw new BackendFailure("Invalid Native playback speed.");
        if (_player is not null) _player.PlaybackSession.PlaybackRate = speed;
        _speed = speed;
        Publish(generation);
        return Task.CompletedTask;
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => CommandAsync(async generation =>
    {
        await ReleaseMediaAsync().ConfigureAwait(false);
        _state = ClassicPlaybackState.Idle;
        _position = _duration = 0;
        _safeError = null;
        Publish(generation);
    }, cancellationToken, replace: true);

    private async Task OpenCoreAsync(ClassicResolvedMedia media, long generation, CancellationToken cancellationToken)
    {
        await ReleaseMediaAsync().ConfigureAwait(false);
        EnsureCurrent(generation, cancellationToken);
        _state = ClassicPlaybackState.Loading;
        _safeError = null;
        _position = _duration = 0;
        Publish(generation);
        try
        {
            // Windows MediaPlayer exposes no verified per-player Software/D3D11 decoder switch.
            // Reject those captured choices rather than claiming that Auto applied them.
            if (_decode != ClassicDecodePreference.Auto)
                throw new BackendFailure("Native supports Auto decode only; forced Software/D3D11 decoding is unavailable.");
            if (media.Request.Item.Type == BiliMediaType.Live || media.Live is not null)
                throw new BackendFailure("Classic Native does not support live protocols; select another backend explicitly.");
            if (!double.IsFinite(media.StartSeconds) || media.StartSeconds < 0)
                throw new BackendFailure("Invalid Native start position.");
            var manifest = CreateManifest(media);
            _http = new HttpClient();
            foreach (var header in media.Headers)
            {
                if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n')
                    || !_http.DefaultRequestHeaders.TryAppendWithoutValidation(header.Key, header.Value))
                    throw new BackendFailure("A required Native media header was rejected.");
            }
            _manifestBytes = new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToString()));
            _manifestInput = _manifestBytes.AsInputStream();
            // Do not cancel-and-forget this WinRT operation: it owns the retained stream/HTTP client.
            var result = await AdaptiveMediaSource.CreateFromStreamAsync(_manifestInput, media.VideoUri!, "application/dash+xml", _http);
            _adaptive = result.MediaSource;
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || _adaptive is null)
                throw new BackendFailure($"Native DASH creation failed ({result.Status}).");
            EnsureCurrent(generation, cancellationToken);
            _adaptive.AdvancedSettings.AllSegmentsIndependent = false;
            _source = MediaSource.CreateFromAdaptiveMediaSource(_adaptive);
            _item = new MediaPlaybackItem(_source);
            _player = new MediaPlayer { AutoPlay = false, Volume = _volume };
            var player = _player;
            var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                EnsureCurrent(generation, cancellationToken);
                _ready = ready;
            }
            // Ready completion does not enter the command queue: Open itself is awaiting it.
            // The event callbacks do not touch the player, so detachment plus queue drain suffices
            // for this backend's work. Every state-bearing callback checks player and generation.
            _openedHandler = (_, _) => CompleteReady(generation, ready, null);
            _failedHandler = (_, args) =>
            {
                var error = $"Native playback failed ({args.Error})."; // No ErrorMessage/URL/header logging.
                CompleteReady(generation, ready, error);
                Callback(generation, player, () => throw new BackendFailure(error));
            };
            _endedHandler = (_, _) => Callback(generation, player, () =>
            {
                if (_state == ClassicPlaybackState.Failed) return;
                ReadValues(player);
                _state = ClassicPlaybackState.Ended;
                Publish(generation);
            });
            _stateHandler = (_, _) => Callback(generation, player, () => UpdateStatus(generation, player));
            player.MediaOpened += _openedHandler;
            player.MediaFailed += _failedHandler;
            player.MediaEnded += _endedHandler;
            player.PlaybackSession.PlaybackStateChanged += _stateHandler;
            await OnUIAsync(() => _host.SetMediaPlayer(player)).ConfigureAwait(false);
            EnsureCurrent(generation, cancellationToken);
            player.Source = _item;
            var openError = await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (openError is not null) throw new BackendFailure(openError);
            EnsureCurrent(generation, cancellationToken);
            _opened = true;
            // Apply the selected resume point only after actual MediaOpened, before playback.
            if (media.StartSeconds > 0) SeekCore(player, media.StartSeconds);
            player.PlaybackSession.PlaybackRate = _speed;
            player.Play();
            UpdateStatus(generation, player);
            _sampling ??= SampleLoopAsync();
        }
        catch
        {
            await ReleaseMediaAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static XDocument CreateManifest(ClassicResolvedMedia media)
    {
        if (media.Dash is not { } dash || media.VideoSegment is not { } video || !IsHttp(media.VideoUri)
            || !double.IsFinite(dash.Duration) || dash.Duration <= 0)
            throw new BackendFailure("Native requires a finite on-demand DASH video with SegmentBase metadata.");
        if ((media.AudioSegment is null) != (media.AudioUri is null) || (media.AudioUri is not null && !IsHttp(media.AudioUri)))
            throw new BackendFailure("Native DASH audio metadata is incomplete.");
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        XElement Representation(DashSegmentInformation segment, Uri uri, bool isVideo)
        {
            if (string.IsNullOrWhiteSpace(segment.IndexRange) || string.IsNullOrWhiteSpace(segment.Initialization))
                throw new BackendFailure("Native DASH SegmentBase ranges are unavailable.");
            var representation = new XElement(ns + "Representation",
                new XAttribute("id", segment.Id), new XAttribute("bandwidth", segment.Bandwidth),
                new XAttribute("codecs", segment.Codecs), new XAttribute("mimeType", segment.MimeType),
                new XElement(ns + "BaseURL", uri.AbsoluteUri),
                new XElement(ns + "SegmentBase", new XAttribute("indexRange", segment.IndexRange),
                    new XElement(ns + "Initialization", new XAttribute("range", segment.Initialization))));
            if (isVideo)
            {
                representation.Add(new XAttribute("width", segment.Width), new XAttribute("height", segment.Height));
            }
            return representation;
        }
        var period = new XElement(ns + "Period",
            new XElement(ns + "AdaptationSet", new XAttribute("contentType", "video"), Representation(video, media.VideoUri!, true)));
        if (media.AudioSegment is { } audio && media.AudioUri is { } audioUri)
            period.Add(new XElement(ns + "AdaptationSet", new XAttribute("contentType", "audio"), Representation(audio, audioUri, false)));
        return new XDocument(new XElement(ns + "MPD", new XAttribute("type", "static"),
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"),
            new XAttribute("minBufferTime", "PT4S"),
            new XAttribute("mediaPresentationDuration", XmlConvert.ToString(TimeSpan.FromSeconds(dash.Duration))), period));
    }

    private static bool IsHttp(Uri? uri) => uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void SeekCore(MediaPlayer player, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new BackendFailure("Invalid Native seek position.");
        if (!_opened || !player.PlaybackSession.CanSeek) throw new BackendFailure("Native media is not ready or cannot seek.");
        var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
        player.PlaybackSession.Position = TimeSpan.FromSeconds(duration > 0 ? Math.Min(seconds, duration) : seconds);
    }

    private MediaPlayer RequirePlayer() => _player is not null && _opened && _state != ClassicPlaybackState.Failed
        ? _player : throw new BackendFailure("Native media is not ready.");

    private void ReadValues(MediaPlayer player)
    {
        _position = Math.Max(0, player.PlaybackSession.Position.TotalSeconds);
        _duration = Math.Max(0, player.PlaybackSession.NaturalDuration.TotalSeconds);
        _volume = Math.Clamp(player.Volume, 0, 1);
        _speed = player.PlaybackSession.PlaybackRate;
    }

    private void UpdateStatus(long generation, MediaPlayer player)
    {
        if (!_opened || !IsCurrent(generation) || _state is ClassicPlaybackState.Failed or ClassicPlaybackState.Ended) return;
        ReadValues(player);
        _state = player.PlaybackSession.PlaybackState switch
        {
            MediaPlaybackState.Playing => ClassicPlaybackState.Playing,
            MediaPlaybackState.Paused => ClassicPlaybackState.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => ClassicPlaybackState.Loading,
            _ => ClassicPlaybackState.Idle,
        };
        Publish(generation);
    }

    private void CompleteReady(long generation, TaskCompletionSource<string?> ready, string? error)
    {
        lock (_sync)
        {
            if (!_closing && generation == _generation && ReferenceEquals(_ready, ready)) ready.TrySetResult(error);
        }
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_samplingCancellation.Token).ConfigureAwait(false))
            {
                Task sample;
                lock (_sync)
                {
                    if (_closing) return;
                    var generation = _generation;
                    sample = EnqueueLocked(() =>
                    {
                        if (!IsCurrent(generation) || _player is null) return Task.CompletedTask;
                        try { UpdateStatus(generation, _player); }
                        catch (Exception error) { Fail(generation, error); }
                        return Task.CompletedTask;
                    });
                }
                await sample.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_samplingCancellation.IsCancellationRequested) { }
    }

    private Task OnUIAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new BackendFailure("Native host dispatcher is unavailable."));
        return completion.Task;
    }

    private async Task ReleaseMediaAsync()
    {
        lock (_sync) { _ready?.TrySetCanceled(); _ready = null; }
        _opened = false;
        var player = _player;
        Exception? failure = null;
        // Try every release even when an earlier WinRT object or dispatcher rejects cleanup.
        void Release(Action action) { try { action(); } catch (Exception error) { failure ??= error; } }
        if (player is not null)
        {
            Release(() => player.MediaOpened -= _openedHandler);
            Release(() => player.MediaFailed -= _failedHandler);
            Release(() => player.MediaEnded -= _endedHandler);
            Release(() => player.PlaybackSession.PlaybackStateChanged -= _stateHandler);
            try { await OnUIAsync(() => _host.SetMediaPlayer(null)).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            Release(player.Pause);
            Release(() => player.Source = null);
            // A failed engine disposal leaves both the owner and its dependent media alive.
            try { player.Dispose(); }
            catch { throw new BackendFailure("Native engine release is unconfirmed; retain the player and host for explicit retry."); }
            _player = null;
        }
        _openedHandler = _endedHandler = null;
        _failedHandler = null;
        _stateHandler = null;
        _item = null; // MediaPlaybackItem is not IClosable; release after detaching the player.
        // Clear each dependent owner only after its own release succeeds. Stop on failure so
        // dependencies are not disposed beneath an object whose release is still uncertain.
        try
        {
            if (_source is not null) { _source.Dispose(); _source = null; }
            if (_adaptive is not null) { _adaptive.Dispose(); _adaptive = null; }
            if (_manifestInput is not null) { _manifestInput.Dispose(); _manifestInput = null; }
            if (_manifestBytes is not null) { await _manifestBytes.DisposeAsync().ConfigureAwait(false); _manifestBytes = null; }
            if (_http is not null) { _http.Dispose(); _http = null; }
        }
        catch { throw new BackendFailure("Native media release is unconfirmed; retain the remaining owners and host for explicit retry."); }
        if (failure is not null) _safeError = "Native detach/pause failed, but all owned resources were released.";
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

    private void Callback(long generation, MediaPlayer player, Action action)
    {
        lock (_sync)
        {
            if (_closing || generation != _generation) return;
            _ = EnqueueLocked(() =>
            {
                if (!IsCurrent(generation) || !ReferenceEquals(_player, player)) return Task.CompletedTask;
                try { action(); }
                catch (Exception error) { Fail(generation, error); }
                return Task.CompletedTask;
            });
        }
    }

    private Task EnqueueLocked(Func<Task> action)
    {
        var predecessor = _tail;
        var task = Task.Run(async () =>
        {
#pragma warning disable VSTHRD003 // Captured old tail has no dependency on this operation; the existing worker joins outside _sync before executing this action.
            await predecessor.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await action().ConfigureAwait(false);
        });
        _tail = ObserveAsync(task);
        return task;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Single-await old-tail observer holds no gate, preserving the original worker's fault while allowing later queued cleanup to run.")]
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private bool IsCurrent(long generation) { lock (_sync) return !_closing && generation == _generation; }
    private void EnsureCurrent(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(generation)) throw new OperationCanceledException("Native operation was superseded.");
    }

    private void Fail(long generation, Exception error)
    {
        if (!IsCurrent(generation)) return;
        _state = ClassicPlaybackState.Failed;
        _safeError = SafeMessage(error);
        Publish(generation);
    }

    private static string SafeMessage(Exception error) => error is BackendFailure ? error.Message
        : error is TimeoutException ? "Native media did not become ready within 30 seconds."
        : "Classic Native operation failed; no backend was substituted.";
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
                catch { /* Subscriber failure must not prevent media resource cleanup. */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is not null && !_dispose.IsFaulted && !_dispose.IsCanceled) return new(_dispose);
            _closing = true;
            _generation++;
            _ready?.TrySetCanceled();
            var cancelSampling = _samplingCancellation.CancelAsync();
            var pending = _tail;
            _dispose = Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // This instance's timer cancellation never depends on the disposal worker.
                try { await cancelSampling.ConfigureAwait(false); }
                catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic Native sampler cancellation failed; cleanup continues."); }
#pragma warning restore VSTHRD003
#pragma warning disable VSTHRD003 // Dispose is outside its own tail and cancelled ready before this lock-free predecessor join.
                await pending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
#pragma warning disable VSTHRD003 // Read latest sampler after predecessor drain; closing prevents any further sampler admission.
                await (_sampling ?? Task.CompletedTask).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                await ReleaseMediaAsync().ConfigureAwait(false);
                _samplingCancellation.Dispose(); StatusChanged = null;
            });
            return new(_dispose);
        }
    }
}
