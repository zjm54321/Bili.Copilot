// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.Input;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private bool _independentShortcutPending;

    [RelayCommand]
    private Task OpenInNewWindowAsync()
    {
        if (!queue.HasThreadAccess || _independentShortcutPending) return Task.CompletedTask;
        _independentShortcutPending = true;
        return OwnWork(OpenInNewWindowCoreAsync());
    }

    private async Task OpenInNewWindowCoreAsync()
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        var player = Player;
        var client = Client;
        var connector = Connector as VideoConnectorViewModel;

        bool IsCurrentSource() => player is not null && client is not null && connector is not null
            && IsCurrentPlayback(version) && !token.IsCancellationRequested && !IsConnecting
            && player.IsPlaybackInitialized && ReferenceEquals(player, Player)
            && ReferenceEquals(client, Client) && ReferenceEquals(connector, Connector);

        try
        {
            if (!IsCurrentSource() || _snapshot is not { Type: BiliMediaType.Video } snapshot
                || connector!._view?.Information is not { } video || connector._part is not { } part) return;

            // Capture before input restoration/pause can await. Never reopen from a later live snapshot.
            var position = player!.Position;
            var captured = new MediaSnapshot(video, snapshot.IsPrivate || connector.IsPrivatePlay)
            {
                PreferPart = part,
                PreferQuality = SelectedSource?.Data.Quality,
                StartPosition = double.IsFinite(position) && position >= 0 ? position : 0d,
            };
            var prepared = false;
            await RunWithoutPlayerInputAsync(async () =>
            {
                if (!IsCurrentSource()) return;
                if (player.PlaybackState == MpvPlayerState.Playing)
                {
                    // PauseAsync has no token parameter; keep the existing media gate until its
                    // actual Result arrives. Never toggle a paused player back to playing.
                    var result = await client!.PauseAsync();
                    if (!IsCurrentSource()) return;
                    if (!result.IsSuccess)
                        throw new InvalidOperationException("暂停当前播放器失败，未打开新窗口。");
                }

                prepared = IsCurrentSource();
            });

            if (!prepared || !IsCurrentSource()) return;
            // The media/input gate has been released. Reuse the App's complete independent-owner
            // lifecycle without registering the old media token to close the new owner.
            await this.Get<AppViewModel>().OpenIndependentPlayerAsync(captured);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrentSource()) { }
        catch (Exception)
        {
            if (IsCurrentSource())
                this.Get<AppViewModel>().ShowTipCommand.Execute(("无法在新窗口打开当前视频，请重试。", InfoType.Error));
        }
        finally { _independentShortcutPending = false; }
    }
}
