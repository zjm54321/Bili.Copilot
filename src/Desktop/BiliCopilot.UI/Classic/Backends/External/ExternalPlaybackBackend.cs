// Copyright (c) Bili Copilot. All rights reserved.
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.Classic.Backends.External;

/// <summary>User-selected protocol, not executable/publisher authentication.</summary>
public enum ExternalPlayerProtocol { Mpv, MpvNet }

/// <summary>Sensitive, exact launch disclosure. Do not log, serialize, or retain this object.</summary>
public sealed class ExternalLaunchConsent
{
    internal ExternalLaunchConsent(string executablePath, ExternalPlayerProtocol selectedProtocol, IReadOnlyList<string> arguments)
    {
        ExecutablePath = executablePath;
        SelectedProtocol = selectedProtocol;
        Arguments = arguments;
        var categories = new List<string> { "媒体 URL", "起播位置" };
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--audio-file=", StringComparison.Ordinal)) categories.Add("独立音轨 URL");
            else if (argument.StartsWith("--user-agent=", StringComparison.Ordinal)) categories.Add("User-Agent");
            else if (argument.StartsWith("--http-header-fields-append=Referer:", StringComparison.OrdinalIgnoreCase)) categories.Add("Referer");
            else if (argument.StartsWith("--http-header-fields-append=Cookie:", StringComparison.OrdinalIgnoreCase)) categories.Add("Cookie 凭据");
        }
        DataCategories = categories.AsReadOnly();
    }

    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public IReadOnlyList<string> DataCategories { get; }
    public ExternalPlayerProtocol SelectedProtocol { get; }
    public string Disclosure => "所选程序及发布者未经应用认证，按用户选择的协议启动。"
        + (SelectedProtocol == ExternalPlayerProtocol.MpvNet
            ? "已选择 mpv.net，强制 --process-instance=multi，禁止使用默认 single 转交到已有实例。兼容依据仅为官方 v7.1.2.0 源码；未检测所选程序版本，未知版本兼容性未经验证。--no-config 等是 mpv 参数，不代表禁用 mpv.net 自身配置。"
            : "已选择独立 mpv 协议；不会根据文件名认证程序或自动切换协议。")
        + "将交付媒体 URL、可能的音轨 URL、User-Agent、Referer 和 Cookie（以本次参数实际包含的类别为准）；本机其他程序可能读取命令行中的 URL 和凭据。确认界面可脱敏，无需展示明文 Cookie。换片或关闭会先请求关闭、必要时终止本应用自建的播放器进程。应用无法控制或读取播放状态，进程退出不会自动播放下一项。";
    public override string ToString() => nameof(ExternalLaunchConsent);
}

/// <summary>
/// Launch-only mpv/mpv.net adapter. All entry points and consent run on the supplied UI dispatcher.
/// The user selects an executable and protocol; neither its name nor the selection authenticates it.
/// No IPC, executable authenticity detection, mpv.net single-instance forwarding, or EOF inference.
/// </summary>
public sealed class ExternalPlaybackBackend : IClassicPlaybackBackend
{
    private readonly DispatcherQueue _dispatcher;
    private readonly string _executablePath;
    private readonly ExternalPlayerProtocol _selectedProtocol;
    private readonly Func<ExternalLaunchConsent, CancellationToken, Task<bool>> _confirm;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Pure managed semaphore; AvailableWaitHandle is never accessed. Existing waiters/finally Release and explicit failed-close retries can still use it, so disposing it would race admitted work.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private EventHandler? _exitHandler;
    private long _generation;
    private bool _closing;
    private Task? _disposeTask;
    private Task? _releaseTask;
    private Task? _stopTask;

    public ExternalPlaybackBackend(DispatcherQueue dispatcher, string executablePath,
        ExternalPlayerProtocol selectedProtocol,
        Func<ExternalLaunchConsent, CancellationToken, Task<bool>> confirmLaunchAsync)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        CheckThread();
        _confirm = confirmLaunchAsync ?? throw new ArgumentNullException(nameof(confirmLaunchAsync));
        if (selectedProtocol is not (ExternalPlayerProtocol.Mpv or ExternalPlayerProtocol.MpvNet))
            throw new NotSupportedException("不支持所选外部播放器协议。 ");
        _selectedProtocol = selectedProtocol;
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("必须选择绝对路径的外部播放器可执行文件。 ");
        _executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(_executablePath)) throw new ArgumentException("外部播放器不可用。 ");
    }

    public ClassicBackendKind Kind => ClassicBackendKind.External;
    public ClassicCapabilities Capabilities => ClassicCapabilities.None;
    public event EventHandler<ClassicBackendStatus>? StatusChanged;

    public async Task OpenAsync(ClassicResolvedMedia media, CancellationToken cancellationToken)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(media);
        // Privacy admission must precede argument creation, confirmation, and all process effects.
        if (media.Request.Item.IsPrivate) throw new NotSupportedException("私密播放不允许向外部进程交付媒体。 ");
        ObjectDisposedException.ThrowIf(_closing, this);
        var arguments = CreateArguments(media, _selectedProtocol).AsReadOnly();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        var previousProcess = _process;
        var replacementApproved = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var consent = new ExternalLaunchConsent(_executablePath, _selectedProtocol, arguments);
            bool approved;
            try { approved = await _confirm(consent, linked.Token); }
            catch (OperationCanceledException) { throw; }
            catch { throw new InvalidOperationException("外部播放器授权未完成。 "); }
            linked.Token.ThrowIfCancellationRequested();
            if (!approved) throw new OperationCanceledException("用户未授权外部播放器。 ");
            replacementApproved = true;
            await ReleaseProcessAsync();
            linked.Token.ThrowIfCancellationRequested();
            var generation = ++_generation;
            var info = new ProcessStartInfo(_executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            EventHandler handler = (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (!_closing && generation == _generation && ReferenceEquals(_process, process))
                    Publish(ClassicPlaybackState.Idle); // Process exit is NOT natural media EOF.
            });
            process.Exited += handler;
            _process = process;
            _exitHandler = handler;
            try
            {
                if (!process.Start()) throw new InvalidOperationException();
            }
            catch
            {
                // A failed Start normally has no associated process. If one was associated,
                // retain ownership until the ordinary close path can confirm its exit.
                if (!HasAssociatedProcess(process))
                {
                    process.Exited -= handler;
                    process.Dispose();
                    _process = null;
                    _exitHandler = null;
                }
                throw new InvalidOperationException("无法启动选定的外部播放器。 ");
            }

            // Launch success is not evidence that media is playing; capabilities remain None.
            Publish(ClassicPlaybackState.Idle);
        }
        catch (Exception) when (replacementApproved)
        {
            throw new ExternalReplacementException(previousProcess is not null && ReferenceEquals(_process, previousProcess));
        }
        finally { _gate.Release(); }
    }

    private static List<string> CreateArguments(ClassicResolvedMedia media, ExternalPlayerProtocol selectedProtocol)
    {
        static string Https(Uri? uri)
        {
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.UserInfo))
                throw new NotSupportedException("外部播放器需要 HTTPS 媒体地址。 ");
            return uri.AbsoluteUri;
        }

        if (!double.IsFinite(media.StartSeconds) || media.StartSeconds < 0)
            throw new ArgumentException("无效的起播位置。 ");
        var result = new List<string> { "--no-config", "--load-scripts=no", "--ytdl=no", "--input-terminal=no" };
        if (selectedProtocol == ExternalPlayerProtocol.MpvNet)
        {
            // Official mpv.net v7.1.2.0, commit 96afc62165b5a1df186fc79c58813f37061ace9a:
            // Program.cs bypasses WM_COPYDATA forwarding for multi; our Process owns its loop.
            // Never permit default single or expose an override through caller-supplied flags.
            // These mpv flags do NOT disable mpv.net's own application configuration.
            result.Insert(0, "--process-instance=multi");
        }
        // The source service selects part/quality before this adapter; no page/ytdl fallback.
        var primary = Https(media.VideoUri ?? media.AudioUri);
        foreach (var header in media.Headers)
        {
            // mpv's list -append operation appends one literal value, including commas.
            // Each header is its own ArgumentList entry; no list/shell escaping is applied.
            if (header.Value is null || header.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new NotSupportedException("外部播放器不支持此请求头格式。 ");
            if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                result.Add("--user-agent=" + header.Value);
            else if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                result.Add("--http-header-fields-append=" + header.Key + ": " + header.Value);
            else throw new NotSupportedException("外部播放器不支持此请求头。 ");
        }

        if (media.VideoUri is not null && media.AudioUri is not null)
            result.Add("--audio-file=" + Https(media.AudioUri));
        result.Add("--start=" + media.StartSeconds.ToString(CultureInfo.InvariantCulture));
        result.Add("--");
        result.Add(primary);
        return result;
    }

    public Task PlayAsync(CancellationToken cancellationToken) => Unsupported();
    public Task PauseAsync(CancellationToken cancellationToken) => Unsupported();
    public Task SeekAsync(double seconds, CancellationToken cancellationToken) => Unsupported();
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => Unsupported();
    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => Unsupported();
    private static Task Unsupported() => Task.FromException(new NotSupportedException("外部播放器未接入可信 IPC，不能由应用控制播放。 "));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        CheckThread();
        cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable VSTHRD003 // Join the already-running instance Dispose before entering its gate; this Stop creates no work on which Dispose could depend.
        if (_disposeTask is { IsCompleted: false }) return _disposeTask;
#pragma warning restore VSTHRD003
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        return _stopTask = StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken))
            throw new InvalidOperationException("外部播放器操作尚未结束；关闭未完成，可明确重试。 ");
        try
        {
            await ReleaseProcessAsync();
            if (!_closing) Publish(ClassicPlaybackState.Idle);
        }
        finally { _gate.Release(); }
    }

    private Task ReleaseProcessAsync()
    {
        // Concurrent close callers share the in-flight work. A completed failed attempt can
        // only be retried by another explicit Stop/Dispose/Open call, never by a timer loop.
        if (_releaseTask is { IsCompleted: false }) return _releaseTask;
        return _releaseTask = ReleaseOwnedProcessAsync();
    }

    private async Task ReleaseOwnedProcessAsync()
    {
        ++_generation;
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.CloseMainWindow(); }
                catch { /* No usable window/close message: still allow a bounded graceful wait. */ }
                if (!await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(2)))
                {
                    try { process.Kill(entireProcessTree: false); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    if (!await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(3)))
                        throw new TimeoutException();
                }
            }

            if (!process.HasExited) throw new TimeoutException();
            // Exit is confirmed. Only now release the Process owner and its event subscription.
            process.Exited -= _exitHandler;
            _exitHandler = null;
            _process = null;
            process.Dispose();
        }
        catch { throw new InvalidOperationException("无法确认自建外部播放器已经关闭；已保留进程所有权，可明确重试关闭。 "); }
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited) return true;
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        return process.HasExited;
    }

    private static bool HasAssociatedProcess(Process process)
    {
        try { _ = process.Id; return true; }
        catch (InvalidOperationException) { return false; }
    }

    public ValueTask DisposeAsync()
    {
        CheckThread();
        if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
        {
            _closing = true;
            ++_generation;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _ = CompleteDisposeAsync(completion);
        }
        return new ValueTask(_disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            try { await _lifetime.CancelAsync(); }
            catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic external cancellation callback failed; cleanup continues."); }
            await CloseAsync();
            _lifetime.Dispose();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task CloseAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("外部播放器操作尚未结束；关闭未完成，可明确重试。 ");
        try { await ReleaseProcessAsync(); }
        finally
        {
            StatusChanged = null;
            _gate.Release();
        }
    }

    private void Publish(ClassicPlaybackState state)
    {
        // Subscribers are UI-thread callbacks; their failures must not leak launch arguments.
        try { StatusChanged?.Invoke(this, new(state, 0, 0, 0, 1, null)); }
        catch { }
    }

    private void CheckThread()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("外部播放器操作必须在所属 UI 线程执行。 ");
    }
}
