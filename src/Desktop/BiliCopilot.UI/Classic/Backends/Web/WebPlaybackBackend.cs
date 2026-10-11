// Copyright (c) Bili Copilot. All rights reserved.
using BiliCopilot.UI.Models.Constants;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;

namespace BiliCopilot.UI.Classic.Backends.Web;

/// <summary>
/// Page-only adapter, no DASH resolution or trusted playback bridge. UI-thread-only entry points.
/// Host supplies a NEW WebView2 with no Source/core and never navigates it independently.
/// Host must await DisposeAsync, then Close/remove the surface; never destroy it during Ensure.
/// </summary>
public sealed class WebPlaybackBackend : IClassicPlaybackBackend
{
    private readonly WebView2 _surface;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Pure managed semaphore with no AvailableWaitHandle use; admitted waiters and finally Release may outlive a close attempt. It must not be disposed beneath them.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CoreWebView2? _core;
    private Uri? _allowedPage;
    private BiliMediaType _mediaType;
    private ulong? _navigationId;
    private long _generation;
    private long _navigationGeneration;
    private TaskCompletionSource<bool>? _unloaded;
    private ulong? _unloadNavigationId;
    private bool _closing;
    private Task? _disposeTask;

    public WebPlaybackBackend(WebView2 surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        CheckThread();
        if (_surface.CoreWebView2 is not null || _surface.Source is not null)
            throw new ArgumentException("网页播放器必须使用未导航、未初始化的新 WebView2。 ");
    }

    public ClassicBackendKind Kind => ClassicBackendKind.Web;
    public ClassicCapabilities Capabilities => ClassicCapabilities.None;
    /// <summary>Host must display this explanation; choosing Web is not a per-open consent dialog.</summary>
    public string Disclosure => "网页播放器使用本应用 Classic/WebView2 持久配置，网站可能记录浏览历史；不会导入应用账号、扩展或控制脚本。显式 CID/起播参数通过官方嵌入播放器交付，实际播放由网页处理。请使用网页控件；应用不能读取播放位置、控制画质或自动续播。";
    public event EventHandler<ClassicBackendStatus>? StatusChanged;

    public async Task OpenAsync(ClassicResolvedMedia media, CancellationToken cancellationToken)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(media);
        if (media.Request.Item.IsPrivate) throw new NotSupportedException("持久网页播放不支持私密会话。 ");
        ObjectDisposedException.ThrowIf(_closing, this);
        var page = ValidatePage(media);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var generation = ++_generation;
            if (_core is null)
            {
                try
                {
                    // Stable profile below the current package only. No app-account/profile import.
                    var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Classic", "WebView2");
                    var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false };
                    var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, path, options);
                    linked.Token.ThrowIfCancellationRequested();
                    // Native initialization cannot be cancelled: await it even when close arrives.
                    await _surface.EnsureCoreWebView2Async(environment);
                    _core = _surface.CoreWebView2;
                    InstallPolicy(_core);
                }
                catch (OperationCanceledException) { throw; }
                catch { throw new InvalidOperationException("网页播放器初始化失败。 "); }
            }

            linked.Token.ThrowIfCancellationRequested();
            if (_closing || generation != _generation) throw new OperationCanceledException();
            _allowedPage = page;
            _mediaType = media.Request.Item.Type;
            _navigationId = null;
            _navigationGeneration = generation;
            Publish(ClassicPlaybackState.Loading);
            linked.Token.ThrowIfCancellationRequested();
            if (_closing || generation != _generation) throw new OperationCanceledException();
            // Policy is installed before the first content navigation; never import cookies/headers.
            try { _core.Navigate(page.AbsoluteUri); }
            catch { throw new InvalidOperationException("无法打开网页播放器。 "); }
        }
        finally { _gate.Release(); }
    }

    private static Uri ValidatePage(ClassicResolvedMedia media)
    {
        if (media.Headers.Count != 0 || media.Dash is not null || media.VideoSegment is not null
            || media.AudioSegment is not null || media.VideoUri is not null || media.AudioUri is not null || media.Live is not null)
            throw new NotSupportedException("网页后端仅接收页面路由，不接收 DASH、直播流或账号请求头。 ");
        var page = WebPlaybackRoute.CreatePageUri(media.Request.Item, media.StartSeconds);
        // Source must use the same page-only mapping, rather than strip instructions before Open.
        if (media.PageUri is null || media.PageUri != page)
            throw new NotSupportedException("网页源路由尚未与 CID/起播参数协议对齐；未打开网页。 ");
        return page;
    }

    private void InstallPolicy(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_allowedPage is null && args.Uri == "about:blank")
        {
            _unloadNavigationId = args.NavigationId;
            return;
        }
        var allowed = !_closing && ReferenceEquals(sender, _core) && _allowedPage is not null
            && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
            && WebPlaybackRoute.IsAllowedTopLevel(uri, _allowedPage, _mediaType);
        args.Cancel = !allowed;
        if (allowed)
        {
            _navigationId = args.NavigationId;
            _navigationGeneration = _generation;
        }
    }

    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        => args.Cancel = _closing || !ReferenceEquals(sender, _core) || _allowedPage is null
            || !WebPlaybackRoute.IsAllowedFrame(args.Uri);
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        => args.Handled = true;
    private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        => args.Cancel = true;
    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }
    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }
    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (ReferenceEquals(sender, _core) && _unloaded is not null && args.NavigationId == _unloadNavigationId)
        {
            _unloaded.TrySetResult(args.IsSuccess);
            return;
        }
        if (_closing || _allowedPage is null || !ReferenceEquals(sender, _core)
            || _navigationGeneration != _generation || args.NavigationId != _navigationId) return;
        // Document loaded is NOT evidence of Playing or Ended; no bridge exists.
        Publish(args.IsSuccess ? ClassicPlaybackState.Idle : ClassicPlaybackState.Failed,
            args.IsSuccess ? null : "网页加载失败，或导航被安全策略阻止。 ");
    }

    public Task PlayAsync(CancellationToken cancellationToken) => Unsupported();
    public Task PauseAsync(CancellationToken cancellationToken) => Unsupported();
    public Task SeekAsync(double seconds, CancellationToken cancellationToken) => Unsupported();
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => Unsupported();
    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => Unsupported();
    private static Task Unsupported() => Task.FromException(new NotSupportedException("网页后端没有可信播放桥接，请使用网页内控件。 "));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CheckThread();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await UnloadPageAsync();
            if (!_closing) Publish(ClassicPlaybackState.Idle);
        }
        finally { _gate.Release(); }
    }

    private async Task UnloadPageAsync()
    {
        ++_generation;
        _allowedPage = null;
        _navigationId = null;
        if (_core is null) return;
        try
        {
            _unloadNavigationId = null;
            _unloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _core.Stop();
            _core.Navigate("about:blank");
            if (!await _unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException();
        }
        catch { throw new InvalidOperationException("无法卸载网页播放器；宿主必须关闭 WebView2。 "); }
        finally { _unloaded = null; _unloadNavigationId = null; }
    }

    public ValueTask DisposeAsync()
    {
        CheckThread();
        if (_disposeTask is null)
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
            catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic web cancellation callback failed; cleanup continues."); }
            await CloseAsync();
            _lifetime.Dispose();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task CloseAsync()
    {
        // Includes any late EnsureCoreWebView2Async completion: host cannot destroy surface early.
        await _gate.WaitAsync();
        try
        {
            await UnloadPageAsync();
            // Failed unload retains core/policy owners and lifetime CTS with the failed host.
            if (_core is not null)
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.FrameNavigationStarting -= OnFrameNavigationStarting;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.NewWindowRequested -= OnNewWindowRequested;
                _core.DownloadStarting -= OnDownloadStarting;
                _core.PermissionRequested -= OnPermissionRequested;
                _core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                _core = null;
            }
            StatusChanged = null;
            // Surface/environment browser resources are closed by host in finally after this task.
            // Do not delete the on-disk profile while the browser process may still hold it.
        }
        finally { _gate.Release(); }
    }

    private void Publish(ClassicPlaybackState state, string? error = null)
    {
        try { StatusChanged?.Invoke(this, new(state, 0, 0, 0, 1, error)); }
        catch { }
    }

    private void CheckThread()
    {
        if (!_surface.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("网页播放器操作必须在所属 UI 线程执行。 ");
    }
}
