// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private int _navigationBusy;

    [RelayCommand]
    private Task PlayNextAsync() => NavigateAsync(true);

    [RelayCommand]
    private Task PlayPreviousAsync() => NavigateAsync(false);

    private async Task NavigateAsync(bool next)
    {
        // Direct command execution does not enforce CanExecute. Both directions
        // share admission, before initialization can pause/stop the current media.
        if (Interlocked.CompareExchange(ref _navigationBusy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (IsPlaybackClosing || IsConnecting || Client?.IsDisposed != false
                || Window is null || Connector is null
                || !(next ? IsNextButtonEnabled : IsPrevButtonEnabled)
                || DateTimeOffset.Now - _lastPlayNextOrPrevTime < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _lastPlayNextOrPrevTime = DateTimeOffset.Now;
            if (next)
            {
                await Connector.PlayNextAsync();
            }
            else
            {
                await Connector.PlayPreviousAsync();
            }
        }
        catch (Exception ex)
        {
            ReportPlaybackFailure(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _navigationBusy, 0);
        }
    }
}
