using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private const int TouchSpeedOwner = 1;
    private const int AltSpeedOwner = 2;
    private readonly TemporaryPlaybackRateState _temporarySpeed = new();
    private PlayerOverlay? _playerOverlay;
    private bool _altSpeedHeld;
    private int _inputCancellations;

    internal void FocusPlayerInput() => _playerOverlay?.Focus(FocusState.Pointer);

    private bool CanAcceptPlayerInput => !IsPlaybackClosing && !IsConnecting
        && !IsWindowDeactivated && _inputCancellations == 0
        && Client?.IsDisposed == false && Player?.IsPlaybackInitialized == true
        && !IsPopupVisible && !IsExtraPanelVisible && !IsSubtitleSettingsVisible;

    private async Task BeginTemporarySpeedAsync(int owner)
    {
        if (!CanAcceptPlayerInput || Player?.IsPlaying != true)
        {
            return;
        }

        var client = Client!;
        var player = Player;
        var version = _mediaVersion;
        var wasHeld = _temporarySpeed.IsHeld;
        var pending = _temporarySpeed.HoldAsync(owner, player.PlaybackRate,
            SettingsToolkit.ReadLocalSetting(SettingNames.TempPlaybackRate, 3d),
            rate =>
            {
                // Native attempts (including retryable restoration) are real speed feedback.
                RefreshTemporarySpeedState(showFeedback: true);
                return client.SetSpeedAsync(rate);
            },
            () => version == _mediaVersion && ReferenceEquals(client, Client)
                && ReferenceEquals(player, Player) && !client.IsDisposed);
        RefreshTemporarySpeedState(showFeedback: wasHeld != _temporarySpeed.IsHeld);
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Temporary playback speed begin failed.");
        }

        if (IsCurrentPlayback(version))
        {
            RefreshTemporarySpeedState();
        }
    }

    private async Task ReleaseTemporarySpeedAsync(int owner)
    {
        var version = _mediaVersion;
        var wasHeld = _temporarySpeed.IsHeld;
        var pending = _temporarySpeed.ReleaseAsync(owner);
        RefreshTemporarySpeedState(showFeedback: wasHeld != _temporarySpeed.IsHeld);
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Temporary playback speed restore failed.");
        }

        if (IsCurrentPlayback(version))
        {
            RefreshTemporarySpeedState();
        }
    }

    private void RefreshTemporarySpeedState(bool showFeedback = false)
    {
        IsHoldingSpeedChanging = _temporarySpeed.IsHeld;
        if (IsHoldingSpeedChanging && !IsPlaybackClosing)
        {
            _tripleTimer?.Start();
        }
        else
        {
            _tripleTimer?.Stop();
        }

        if (showFeedback)
        {
            LastSpeedChangingTime = DateTimeOffset.Now;
            IsSpeedChanging = true;
        }
    }

    // Call before advancing _mediaVersion, stopping/reusing a client, or disposing it.
    // Unlike event observers this task propagates restore failure to the transition.
    private Task CancelPlayerInputAsync() => CancelPlayerInputCoreAsync(true);

    private Task RestorePhysicalPlayerInputAsync() => CancelPlayerInputCoreAsync(false);

    private async Task CancelPlayerInputCoreAsync(bool cancelTransport)
    {
        if (!queue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource();
            if (!queue.TryEnqueue(async () =>
            {
                try { await CancelPlayerInputCoreAsync(cancelTransport); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }))
            {
                throw new InvalidOperationException("Player dispatcher is unavailable for input cancellation.");
            }

            await completion.Task;
            return;
        }

        ++_inputCancellations;
        try
        {
            _altSpeedHeld = false;
            IsTouching = false;
            if (cancelTransport) _playerOverlay?.CancelPendingInput();
            else _playerOverlay?.CancelSurfaceInput();
            var wasHeld = _temporarySpeed.IsHeld;
            var pending = _temporarySpeed.CancelAsync();
            RefreshTemporarySpeedState(showFeedback: wasHeld != _temporarySpeed.IsHeld);
            await pending;
        }
        finally
        {
            --_inputCancellations;
        }
    }

    private async void OnPlayerInputLostFocus(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(sender, _playerOverlay)) return;
        try
        {
            await RestorePhysicalPlayerInputAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player focus cancellation failed.");
        }
    }

    private async void OnPlayerWindowActiveChanged(bool active)
    {
        IsWindowDeactivated = !active;
        if (!active)
        {
            try
            {
                await CancelPlayerInputAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Player deactivation cancellation failed.");
            }
        }
    }

    private bool IsPlayerKeyboardSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _playerOverlay) || current is PlayerInteractivePanel)
            {
                return true;
            }

            if (current is Control or Microsoft.UI.Xaml.Documents.Hyperlink)
            {
                return false;
            }
        }

        return source is null;
    }

    private static bool IsAltKey(VirtualKey key)
        => key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu;

    private static bool HasNonAltModifier()
        => new[] { 0x10, 0x11, 0x5B, 0x5C }.Any(key => (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0);

    private async void OnPlayerKeyDown(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            // Chord keys must keep their normal meaning. Stop an accepted Alt hold
            // without swallowing the chord (Alt+Tab, Alt+menu shortcuts, etc.).
            if (_altSpeedHeld && !IsAltKey(args.Key))
            {
                _altSpeedHeld = false;
                await ReleaseTemporarySpeedAsync(AltSpeedOwner);
                return;
            }

            if (args.Handled || !CanAcceptPlayerInput || !IsPlayerKeyboardSource(args.OriginalSource)
                || !IsPlayerKeyboardSource(FocusManager.GetFocusedElement(_playerOverlay!.XamlRoot)))
            {
                return;
            }

            if (IsAltKey(args.Key))
            {
                if (HasNonAltModifier() || Player?.IsPlaying != true)
                {
                    return;
                }

                args.Handled = true;
                _playerOverlay.ShowControlsForInput();
                if (!_altSpeedHeld && !args.KeyStatus.WasKeyDown)
                {
                    _altSpeedHeld = true;
                    await BeginTemporarySpeedAsync(AltSpeedOwner);
                }

                return;
            }

            // Only the established Ctrl+M shortcut consumes a modified key;
            // modified arrows/Space belong to focused controls and the platform.
            IRelayCommand? command = args.Key == VirtualKey.M && AppToolkit.IsOnlyCtrlPressed()
                ? ToggleCompactOverlayCommand : null;
            if (command is null && !AppToolkit.NotModifierKeyPressed())
            {
                return;
            }

            if (IsStatsOverlayShown && args.Key is (VirtualKey.Number1 or VirtualKey.Number2
                or VirtualKey.Number3 or VirtualKey.Number4 or VirtualKey.Number5))
            {
                args.Handled = true;
                _playerOverlay.ShowControlsForInput();
                if (!args.KeyStatus.WasKeyDown)
                {
                    await Client!.SendKeyPressAsync(args.Key.ToString().Replace("Number", string.Empty, StringComparison.Ordinal));
                }

                return;
            }

            command ??= args.Key switch
            {
                VirtualKey.Space => PlayPauseCommand,
                VirtualKey.Left => SkipBackwardCommand,
                VirtualKey.Right => SkipForwardCommand,
                VirtualKey.Up => IncreaseVolumeCommand,
                VirtualKey.Down => DecreaseVolumeCommand,
                VirtualKey.F11 => ToggleFullScreenCommand,
                VirtualKey.Escape => BackToDefaultModeCommand,
                VirtualKey.PageUp => SwitchToPreviousChapterCommand,
                VirtualKey.PageDown => SwitchToNextChapterCommand,
                VirtualKey.I => ToggleStatsOverlayCommand,
                _ => null,
            };
            if (command is null || !command.CanExecute(null))
            {
                return;
            }

            args.Handled = true;
            _playerOverlay.ShowControlsForInput();
            if (args.KeyStatus.WasKeyDown && args.Key is not
                (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down))
            {
                return;
            }

            if (command is IAsyncRelayCommand asyncCommand)
            {
                await asyncCommand.ExecuteAsync(null);
            }
            else
            {
                command.Execute(null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player keyboard command failed.");
        }
    }

    private async void OnPlayerKeyUp(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            if (IsAltKey(args.Key) && _altSpeedHeld)
            {
                _altSpeedHeld = false;
                args.Handled = true;
                if (!IsPlaybackClosing)
                {
                    _playerOverlay?.ShowControlsForInput();
                }

                await ReleaseTemporarySpeedAsync(AltSpeedOwner);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player keyboard release failed.");
        }
    }
}
