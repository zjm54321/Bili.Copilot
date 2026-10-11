// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Classic.Backends.External;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private bool _externalShortcutPending;

    [RelayCommand]
    private Task OpenWithMpvAsync() => OpenExternalShortcutAsync(ExternalPlayerProtocol.Mpv);

    [RelayCommand]
    private Task OpenWithMpvNetAsync() => OpenExternalShortcutAsync(ExternalPlayerProtocol.MpvNet);

    private Task OpenExternalShortcutAsync(ExternalPlayerProtocol protocol)
    {
        if (!queue.HasThreadAccess || _externalShortcutPending) return Task.CompletedTask;
        _externalShortcutPending = true;
        return OwnWork(OpenExternalShortcutCoreAsync(protocol));
    }

    private async Task OpenExternalShortcutCoreAsync(ExternalPlayerProtocol protocol)
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        try
        {
            if (!IsCurrentPlayback(version) || token.IsCancellationRequested || IsConnecting
                || Player?.IsPlaybackInitialized != true || _snapshot is not { Type: BiliMediaType.Video } snapshot
                || Connector is not VideoConnectorViewModel connector
                || connector._view?.Information is not { } video || connector._part is not { } part) return;

            // Capture before the first await. SDK models are read-only references; no live snapshot
            // or connector is consulted after picker/approval. Do not export the original playlist.
            var captured = new MediaSnapshot(video, snapshot.IsPrivate || connector.IsPrivatePlay)
            {
                PreferPart = part,
                PreferQuality = SelectedSource?.Data.Quality,
                StartPosition = 0d,
            };
            var request = ClassicRequestMapper.FromSnapshot(captured);
            if (!IsCurrentPlayback(version) || token.IsCancellationRequested) return;
            await this.Get<AppViewModel>().OpenExternalAsync(request, protocol, token);
            // The independent Classic owner, including an error shell, is not ours to close.
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentPlayback(version)) this.Get<AppViewModel>().ReportClassicFailure(ex);
        }
        finally { _externalShortcutPending = false; }
    }
}
