using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Classic.Backends.External;
using BiliCopilot.UI.Classic.Hosting;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    public List<ClassicPlayerWindow> ClassicPlayers { get; } = [];
    private int _classicOpenCount;
    private readonly List<ExternalPlaybackBackend> _pendingClassicExternalCleanup = [];
    private bool _offeringExternalCleanupRetry;
    public bool HasClassicWork => ClassicPlayers.Count > 0 || _classicOpenCount > 0 || _pendingClassicExternalCleanup.Count > 0;
    public bool HasPendingClassicExternalCleanup => _pendingClassicExternalCleanup.Count > 0;
    public void RetainClassicExternalCleanup(ExternalPlaybackBackend backend)
    {
        if (!_pendingClassicExternalCleanup.Contains(backend)) _pendingClassicExternalCleanup.Add(backend);
    }
    // Explicit caller/user retry only. Keep the exact owner reachable until disposal really succeeds.
    public async Task RetryClassicExternalCleanupAsync()
    {
        foreach (var backend in _pendingClassicExternalCleanup.ToArray())
        {
            await backend.DisposeAsync();
            _pendingClassicExternalCleanup.Remove(backend);
        }
    }

    public Task<bool> TryOpenClassicAsync(MediaSnapshot snapshot)
        => TryOpenClassicCoreAsync(() => ClassicRequestMapper.FromSnapshot(snapshot));
    public Task<bool> TryOpenClassicAsync(LiveInformation live)
        => TryOpenClassicCoreAsync(() => ClassicRequestMapper.FromLive(live));

    /// <summary>Explicit single-video external action, independent of the saved playback route.</summary>
    public async Task OpenExternalAsync(ClassicPlaybackRequest request, ExternalPlayerProtocol protocol,
        CancellationToken cancellationToken)
    {
        ++_classicOpenCount;
        try
        {
            CheckExternalOpenAdmission(cancellationToken);
            ArgumentNullException.ThrowIfNull(request);
            if (request.Item.IsPrivate)
                throw new NotSupportedException("私密会话不向外部程序交付媒体。");
            if (!Enum.IsDefined(protocol)) throw new NotSupportedException("不支持所选外部播放器协议。");
            if (request.Item.Type != BiliMediaType.Video || request.Item.Video is null
                || request.Item.PreferPart is null || string.IsNullOrWhiteSpace(request.Item.Video.Identifier.Id)
                || string.IsNullOrWhiteSpace(request.Item.PreferPart.Identifier.Id))
                throw new NotSupportedException("外部快捷播放需要已就绪的视频及当前分 P。");

            request = request with
            {
                Item = request.Item with { StartPosition = 0d },
                Playlist = Array.Empty<ClassicMediaItem>(),
                PlaylistIndex = null,
            };
            var capturedOptions = ClassicSessionOptions.Capture(this.Get<ISettingsToolkit>());
            var owner = ActivatedWindow ?? throw new InvalidOperationException("选择外部播放器需要可见的所属窗口。");
            CheckExternalOpenAdmission(cancellationToken);
            // No protocol-associated saved path exists. Pick explicitly every time, without saving.
            // The picker has no cancellation parameter: retain the pending owner until it returns.
            var file = await this.Get<IFileToolkit>().PickFileAsync(".exe", owner);
            CheckExternalOpenAdmission(cancellationToken);
            if (file is null) return;

            var options = capturedOptions with
            {
                Experience = PlaybackExperience.Classic,
                Backend = ClassicBackendKind.External,
                ExternalPlayerPath = file.Path,
                ExternalProtocol = protocol,
                DecodePreference = ClassicDecodePreference.Auto,
            };
            await this.Get<ClassicPlayerLauncher>().OpenAsync(request, options, cancellationToken);
            CheckExternalOpenAdmission(cancellationToken);
            // OpenAsync can return a retained error shell. No success claim or owner cleanup here.
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsClosed) ReportClassicFailure(ex); }
        finally
        {
            --_classicOpenCount;
            if (!HasClassicWork && Players.Count == 0 && !IsClosed && !IsMainWindowVisible())
                Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
        }
    }

    private void CheckExternalOpenAdmission(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsClosed, this);
    }

    private async Task<bool> TryOpenClassicCoreAsync(Func<ClassicPlaybackRequest> capture)
    {
        var settings = this.Get<ISettingsToolkit>();
        // Do not capture requests/options, resolve PlayerViewModel or instantiate an engine for New.
        if (ClassicSessionOptions.ReadExperience(settings) != PlaybackExperience.Classic) return false;
        // Island is now the main-window New/mpv route. Live keeps its existing browser entry.
        if (ClassicSessionOptions.Capture(settings).Backend == ClassicBackendKind.Island) return false;
        ++_classicOpenCount;
        try
        {
            var request = capture();
            var options = ClassicSessionOptions.Capture(settings);
            if (request.Item.Type == BiliMediaType.Live && options.UseWebPlayerWhenLive)
                options = options with { Backend = ClassicBackendKind.Web }; // Explicit captured live preference.
            await this.Get<ClassicPlayerLauncher>().OpenAsync(request, options);
        }
        catch (Exception ex) { ReportClassicFailure(ex); }
        finally
        {
            --_classicOpenCount;
            if (!HasClassicWork && Players.Count == 0 && !IsClosed && !IsMainWindowVisible())
                Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
        }
        return true; // An unavailable explicit Classic choice must NEVER fall through to New/browser.
    }

    public void RegisterClassicWindow(ClassicPlayerWindow window)
    {
        ClassicPlayers.Add(window);
        if (window.Content is not null) Windows.Add(window); // A retained failed-construction host may have no frontend.
        window.Activated += OnClassicActivated;
    }
    public void UnregisterClassicWindow(ClassicPlayerWindow window)
    {
        window.Activated -= OnClassicActivated;
        ClassicPlayers.Remove(window);
        Windows.Remove(window);
        if (ReferenceEquals(ActivatedWindow, window)) ActivatedWindow = Windows.LastOrDefault()!;
        if (!IsClosed && ClassicPlayers.Count == 0 && Players.Count == 0 && !IsMainWindowVisible())
            Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
    }
    private void OnClassicActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && sender is ClassicPlayerWindow window)
            ActivatedWindow = window;
    }
    public void ReportClassicFailure(Exception exception)
    {
        if (ActivatedWindow is ClassicPlayerWindow { IsClosing: true })
        {
            // A retained closing host rejects new dialogs; do not silently lose its release error.
            var main = Windows.OfType<MainWindow>().FirstOrDefault();
            if (main is not null) { main.Activate(); ActivatedWindow = main; }
        }
        // Consent payloads/media data never enter diagnostics. Preserve actual safe cleanup/source errors.
        var message = exception is AggregateException aggregate
            ? string.Join("\n", aggregate.Flatten().InnerExceptions.Select(error => error.Message))
            : exception.Message;
        if (HasPendingClassicExternalCleanup && !_offeringExternalCleanupRetry)
            OfferExternalCleanupRetry(message);
        else ShowTipCommand.Execute(("Classic：" + message, InfoType.Error));
    }
    private async void OfferExternalCleanupRetry(string message)
    {
        _offeringExternalCleanupRetry = true;
        try
        {
            var main = Windows.OfType<MainWindow>().FirstOrDefault();
            main?.Activate();
            var root = main?.Content?.XamlRoot ?? ActivatedWindow?.Content?.XamlRoot;
            if (root is null) { ShowTipCommand.Execute((message, InfoType.Error)); return; }
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = "外部播放器尚未确认退出",
                Content = message + "\n原进程所有者仍保留。可以明确重试清理；取消不会宣称进程已退出。",
                PrimaryButtonText = "重试清理", CloseButtonText = "暂不重试", DefaultButton = ContentDialogButton.Close,
            };
            using var rendering = main?.SuspendEmbeddedPlayerRendering();
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RetryClassicExternalCleanupAsync();
        }
        catch (Exception ex) { ShowTipCommand.Execute(("外部播放器清理仍未成功：" + ex.Message, InfoType.Error)); }
        finally { _offeringExternalCleanupRetry = false; }
    }
    public Task CloseClassicPlayersAsync() => Task.WhenAll(ClassicPlayers.ToArray().Select(window => window.CloseAsync()));
}
