using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private PlayerPresentationMode _returnPresentation = PlayerPresentationMode.Normal;
    private PlayerPresentationMode _fullScreenReturnPresentation = PlayerPresentationMode.Normal;
    private long _presentationRequestVersion;
    private (IPlayerHost Host, PlayerPresentationMode Mode, bool FullScreen, bool Embedded)? _publishedHostPresentation;
    private (IPlayerHost Host, PlayerPresentationMode Mode, bool FullScreen, bool Embedded)? _publishingHostPresentation;
    private long _publishingHostPresentationVersion;
    private long _hostPresentationPublicationVersion;

    public Task SetPresentationAsync(PlayerPresentationMode mode)
        => IsPlaybackClosing ? Task.CompletedTask : OwnWork(SetPresentationCoreAsync(mode));

    private async Task SetPresentationCoreAsync(PlayerPresentationMode mode)
    {
        mode = PlayerPresentationModes.Normalize(mode);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Window is not { } capturedHost || capturedHost.IsClosed) return;
        var requestVersion = ++_presentationRequestVersion;
        await CancelPlayerInputAsync();
        if (IsPlaybackClosing || capturedHost.IsClosed) return;
        if (requestVersion != _presentationRequestVersion || !ReferenceEquals(Window, capturedHost)) return;
        var current = PlayerPresentationModes.Normalize(capturedHost.Presentation);
        if (mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay &&
            current is not (PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay))
            _returnPresentation = current;
        if (mode == PlayerPresentationMode.FullScreen && current != PlayerPresentationMode.FullScreen)
            _fullScreenReturnPresentation = current;
        await capturedHost.SetPresentationAsync(mode);
        if (IsPlaybackClosing || capturedHost.IsClosed || requestVersion != _presentationRequestVersion ||
            !ReferenceEquals(Window, capturedHost)) return;
        await ResetPlayerPresenterModeAsync();
    }

    private void OnHostPresentationChanged(object? sender, EventArgs args)
    {
        if (Window is not { } host || !ReferenceEquals(sender, host)) return;
        var state = (Host: host, Mode: Presentation, FullScreen: IsFullScreen, Embedded: IsEmbedded);
        bool Matches((IPlayerHost Host, PlayerPresentationMode Mode, bool FullScreen, bool Embedded)? value)
            => value is { } previous && ReferenceEquals(previous.Host, host) && previous.Mode == state.Mode &&
                previous.FullScreen == state.FullScreen && previous.Embedded == state.Embedded;
        if ((_uiElementSetted && Matches(_publishedHostPresentation)) ||
            (Matches(_publishingHostPresentation) && _publishingHostPresentationVersion == _hostPresentationPublicationVersion)) return;
        var version = ++_hostPresentationPublicationVersion;
        var previousPublishing = _publishingHostPresentation;
        var previousPublishingVersion = _publishingHostPresentationVersion;
        _publishingHostPresentation = state;
        _publishingHostPresentationVersion = version;
        _publishedHostPresentation = null;
        try
        {
            IsTopMost = host.IsTopMost();
            OnPropertyChanged(nameof(Presentation));
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsEmbedded));
            // Pre-bind compatibility callbacks cannot consume the real BindHost first notification.
            if (_uiElementSetted && version == _hostPresentationPublicationVersion && ReferenceEquals(Window, host)) _publishedHostPresentation = state;
        }
        finally
        {
            _publishingHostPresentation = previousPublishing;
            _publishingHostPresentationVersion = previousPublishingVersion;
        }
    }

    public Task ChangeSpeedAsync(double speed)
        => IsPlaybackClosing || IsPlayerInputCancellationPending ? Task.CompletedTask : OwnWork(ChangeSpeedCoreAsync(speed));

    private Task ChangeSpeedCoreAsync(double speed)
    {
        if (!double.IsFinite(speed) || speed < 0.1 || speed > 3 || Client is null || Player is null) return Task.CompletedTask;
        return SetPersistentUserRateAsync(speed);
    }

    public Task SendInlineDanmakuAsync(string text)
    {
        if (IsPlaybackClosing || IsConnecting || Player?.IsPlaybackInitialized != true || string.IsNullOrWhiteSpace(text))
            return Task.FromException(new InvalidOperationException("当前媒体尚未准备好发送弹幕。"));
        return OwnWork(SendInlineDanmakuCoreAsync(text, _mediaVersion, Convert.ToInt32(Player.Position), _activeMediaToken));
    }

    private async Task SendInlineDanmakuCoreAsync(string text, long version, int position, CancellationToken token)
    {
        var success = await DanmakuSend.TrySendDanmakuAsync(text, position, token);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentPlayback(version)) throw new OperationCanceledException("媒体已切换，弹幕输入保持不变。");
        if (!success) throw new InvalidOperationException("弹幕未发送成功，请稍后重试。");
        Danmaku.AddDanmaku(text);
    }

    private async Task DrainTransportCommandsAsync()
    {
        IAsyncRelayCommand[] commands =
        [
            IncreaseVolumeCommand, DecreaseVolumeCommand, IncreaseSpeedCommand, DecreaseSpeedCommand,
            PlayPauseCommand, SkipBackwardCommand, SkipForwardCommand, PlayNextCommand, PlayPreviousCommand,
            ResetSubtitlePositionCommand, ChangeSubtitlePositionCommand, ReloadFontsCommand,
            ChangeSubtitleFontSizeCommand, ChangeSubtitleFontFamilyCommand, SwitchToPreviousChapterCommand,
            SwitchToNextChapterCommand, SendKeyCommand, ToggleStatsOverlayCommand, SetSubtitleDelaySecondsCommand,
            ToggleSubtitleEnabledCommand, TogglePanscanCommand, ToggleMuteStateCommand, CopyPlayUrlCommand,
            TakeScreenshotCommand, SendDanmakuCommand, ChangeAnime4KModeCommand, ChangeArtCNNModeCommand,
            ChangeNnedi3ModeCommand, ChangeRavuModeCommand, ChangeNvidiaVsrModeCommand, ClearShadersCommand,
            InitializeDanmakuCommand, ReloadDanmakuCommand,
        ];
        var tasks = commands.Select(command => command.ExecutionTask).Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) { logger.LogWarning(ex, "Player transport work ended while closing."); }
    }

    private void RetainConnectorWork()
    {
        List<IAsyncRelayCommand> commands = [];
        IEnumerable<IPlayerSectionDetailViewModel> sections = [];
        if (Connector is VideoConnectorViewModel video)
        {
            commands.AddRange((IAsyncRelayCommand[])[video.ToggleFollowCommand, video.ToggleLikeCommand, video.CoinCommand,
                video.TripleCommand, video.InitializeFavoritesCommand, video.FavoriteCommand, video.AddToViewLaterCommand]);
            sections = video.Sections ?? [];
        }
        else if (Connector is PgcConnectorViewModel pgc)
        {
            commands.AddRange((IAsyncRelayCommand[])[pgc.ToggleLikeCommand, pgc.CoinCommand, pgc.TripleCommand,
                pgc.InitializeFavoritesCommand, pgc.FavoriteCommand, pgc.ReloadEpisodeOperationCommand,
                pgc.ToggleFollowCommand]);
            sections = pgc.Sections ?? [];
        }
        foreach (var section in sections)
        {
            commands.Add(section.TryFirstLoadCommand);
            if (section is CommentMainViewModel comments)
                commands.AddRange((IAsyncRelayCommand[])[comments.RefreshCommand, comments.LoadItemsCommand,
                    comments.ChangeSortTypeCommand, comments.SendReplyCommand]);
        }
        var tasks = commands.Select(command => command.ExecutionTask).Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0) _ = OwnWork(DrainAsync());
        async Task DrainAsync()
        {
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Retired connector work finished with an error."); }
        }
    }
}
