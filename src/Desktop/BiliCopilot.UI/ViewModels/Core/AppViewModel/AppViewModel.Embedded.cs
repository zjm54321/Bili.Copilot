using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    private readonly SemaphoreSlim _embeddedGate = new(1, 1);
    private CancellationTokenSource? _embeddedOpenCancellation;
    // Non-owning association: a successor open must not be cancelled while closing its predecessor.
    private CancellationTokenSource? _embeddedPlayerOpeningCancellation;
    private long _embeddedOpenVersion;
    public PlayerViewModel? EmbeddedPlayer { get; private set; }
    public bool HasEmbeddedWork => EmbeddedPlayer is not null || _embeddedOpenCancellation is not null;
    internal bool HasIndependentWorkRequiringMainWindow
        => Players.Any(player => !player.IsEmbedded && !player.HasUsableIndependentHost);

    private async Task OpenMainPlayerAsync(MediaSnapshot snapshot)
    {
        var version = ++_embeddedOpenVersion;
        var previousRequest = _embeddedOpenCancellation;
        var previousPlayer = previousRequest is not null && ReferenceEquals(_embeddedPlayerOpeningCancellation, previousRequest)
            ? EmbeddedPlayer : null;
        var request = new CancellationTokenSource();
        _embeddedOpenCancellation = request;
        var entered = false;
        try
        {
            // A callback-reentrant Player close must wait until this exact cancellation scope settles.
            var cancellation = previousPlayer?.BeginSynchronousCancellationScope();
            try
            {
                // Complete callbacks synchronously; failures belong to this transition's cleanup scope.
#pragma warning disable VSTHRD103
                try { previousRequest?.Cancel(); }
#pragma warning restore VSTHRD103
                catch (Exception ex)
                {
                    previousPlayer?.RetainOpeningCancellationFailure(ex);
                    throw;
                }
            }
            finally
            {
                if (cancellation is not null) previousPlayer!.CompleteSynchronousCancellationScope(cancellation);
            }
            await _embeddedGate.WaitAsync(request.Token);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            if (EmbeddedPlayer is { } previous) await previous.CloseAsync();
            request.Token.ThrowIfCancellationRequested();
            if (version != _embeddedOpenVersion || IsClosed) return;

            if (snapshot.Type is not (BiliMediaType.Video or BiliMediaType.Pgc))
                throw new NotSupportedException("主窗口 mpv 播放仅支持视频和剧集；直播请使用原网页入口。");

            var main = Windows.OfType<MainWindow>().FirstOrDefault()
                ?? throw new InvalidOperationException("主窗口尚未创建。");
            var vm = this.Get<PlayerViewModel>();
            vm.MarkEmbedded();
            vm.DecodeOverride = null;
            var mode = GetNewInitialPresentation();
            EmbeddedPlayer = vm;
            _embeddedPlayerOpeningCancellation = request;
            try
            {
                Players.Add(vm);
                vm.IsConnecting = true;
                main.Activate();
                var slot = main.ShowEmbeddedPlayer(vm);
                var host = new MainPlayerHost(main, slot);
                await vm.AttachHostAsync(host, request.Token);
                request.Token.ThrowIfCancellationRequested();
                await vm.SetPresentationAsync(mode);
                request.Token.ThrowIfCancellationRequested();
                await vm.InitializeAsync(snapshot, request.Token);
                request.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                await vm.CloseAsync();
            }
            catch (Exception ex)
            {
                // Keep the failed session + return affordance visible; no hidden fallback engine.
                vm.ErrorMessage = ex.Message;
                vm.IsConnecting = false;
                _logger.LogError(ex, "Embedded player initialization failed.");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Main player transition failed; ownership retained.");
            ShowTipCommand.Execute((ex.Message, InfoType.Error));
        }
        finally
        {
            if (entered) _embeddedGate.Release();
            if (ReferenceEquals(_embeddedOpenCancellation, request)) _embeddedOpenCancellation = null;
            if (ReferenceEquals(_embeddedPlayerOpeningCancellation, request)) _embeddedPlayerOpeningCancellation = null;
            // InitializeAsync links this request for the lifetime of the active media.
            // Dispose releases registrations without cancelling successfully loaded playback.
            request.Dispose();
        }
    }

    private static PlayerPresentationMode GetNewInitialPresentation()
        => SettingsToolkit.ReadLocalSetting(SettingNames.PlayerPresenterMode, PlayerPresenterMode.Default) switch
        {
            PlayerPresenterMode.FullScreen => PlayerPresentationMode.FullScreen,
            PlayerPresenterMode.CompactOverlay => PlayerPresentationMode.CompactOverlay,
            _ => PlayerPresentationMode.Normal,
        };

    private static void CancelEmbeddedOpening(CancellationTokenSource? request)
    {
        // Synchronous callbacks are observed by the transition/close owner, not an unowned exit.
#pragma warning disable VSTHRD103
        request?.Cancel();
#pragma warning restore VSTHRD103
    }

    internal Action? CaptureEmbeddedOpeningCancellation(PlayerViewModel player)
    {
        if (!ReferenceEquals(EmbeddedPlayer, player)) return null;
        var request = _embeddedPlayerOpeningCancellation;
        if (request is null) return null;
        if (ReferenceEquals(_embeddedOpenCancellation, request)) ++_embeddedOpenVersion;
        return () => CancelEmbeddedOpening(request);
    }

    public Task CloseEmbeddedPlayerAsync()
    {
        ++_embeddedOpenVersion;
        var request = _embeddedOpenCancellation;
        void CancelOpening() => CancelEmbeddedOpening(request);
        var current = EmbeddedPlayer;
        Exception? cancellationError = null;
        // An already-published close has observed its original opening CTS. A later request
        // belongs to its own transition, not to a retroactive failure of that older close.
        if (current is null || current.HasPublishedClose)
        {
            try { CancelOpening(); }
            catch (Exception ex) { cancellationError = ex; }
        }
        // Publish the real Player close and stop admission before invoking reentrant callbacks.
        var close = current?.CloseAsync(CancelOpening);
        return CloseEmbeddedCoreAsync(current, close, cancellationError);
    }

    private async Task CloseEmbeddedCoreAsync(PlayerViewModel? target, Task? close, Exception? cancellationError)
    {
        Exception? closeError = null;
        // No embedded gate is owned here; the published Player close drains its actual owned work.
#pragma warning disable VSTHRD003
        try { if (close is not null) await close; }
#pragma warning restore VSTHRD003
        catch (Exception ex) { closeError = ex; }
        await _embeddedGate.WaitAsync();
        try
        {
            // A failed first close still must wait for the opening transition to leave its gate.
            if (closeError is not null)
            {
                if (cancellationError is not null)
                    _logger.LogWarning(cancellationError, "Opening cancellation failed while awaiting the primary player close failure.");
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeError).Throw();
            }
            if (cancellationError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
            // The gate is a drain barrier, not permission to retarget an old Back to a successor.
            if (target is not null) await target.CloseAsync();
        }
        finally { _embeddedGate.Release(); }
    }

    internal void ReleasePlayer(PlayerViewModel vm)
    {
        if (ReferenceEquals(EmbeddedPlayer, vm))
        {
            Windows.OfType<MainWindow>().FirstOrDefault()?.ClearEmbeddedPlayer(vm);
            EmbeddedPlayer = null;
        }
        Players.Remove(vm);
    }

    /// <summary>Explicit new independent session, never a fullscreen transfer.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task OpenIndependentPlayerAsync(MediaSnapshot snapshot)
    {
        var vm = this.Get<PlayerViewModel>();
        Players.Add(vm);
        try
        {
            // PlayerWindow construction is managed-only. Assign ownership before native readiness.
            await vm.AttachHostAsync(new PlayerWindow(), CancellationToken.None);
            await vm.InitializeAsync(snapshot);
        }
        catch (Exception ex)
        {
            // This await is outside InitializeAsync/OwnWork/the media gate, so close cannot self-join.
            // Keep main reachable even when native release fails and the registered owner remains.
            Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
            try { await vm.CloseAsync(); }
            catch (Exception cleanupError)
            {
                _logger.LogError(cleanupError, "Independent player cleanup failed; ownership retained.");
                Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
                ShowTipCommand.Execute((cleanupError.Message, InfoType.Error));
                return;
            }
            if (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Independent player initialization failed; host released.");
                ShowTipCommand.Execute((ex.Message, InfoType.Error));
            }
        }
    }
}
