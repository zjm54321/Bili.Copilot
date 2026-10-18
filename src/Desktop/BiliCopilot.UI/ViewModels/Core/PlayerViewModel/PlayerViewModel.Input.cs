using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.SponsorBlock.Playback;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private const int TouchSpeedOwner = 1;
    private const int AltSpeedOwner = 2;
    // Existing speed sliders, quick-speed choices and keyboard commands all top out at 3x.
    private const double MaximumPlaybackRate = 3d;
    private const string TemporaryAccelerationFactorKey = "TemporaryAccelerationFactor";
    private const double DefaultTemporaryAccelerationFactor = 1.5d;
    private readonly TemporaryPlaybackRateState _temporarySpeed = new();
    private PlayerOverlay? _playerOverlay;
    private bool _altSpeedHeld;
    private int _inputCancellations;
    // Requested physical owners, including a begin waiting for the gate. Not a Sponsor bit.
    private readonly Dictionary<int, long> _temporarySpeedIntentOwners = [];

    internal static double ReadTemporaryAccelerationFactor()
    {
        var settings = GlobalDependencies.Kernel.GetRequiredService<ISettingsToolkit>();
        if (settings.IsSettingKeyExist(TemporaryAccelerationFactorKey))
            return NormalizeTemporaryAccelerationFactor(settings.ReadLocalSetting(TemporaryAccelerationFactorKey, DefaultTemporaryAccelerationFactor));

        // The legacy key stored an absolute target. Its old default 3.0 is ambiguous, so
        // migrate that exact value to 1.5; retain other reasonable customized factors.
        var legacy = SettingsToolkit.ReadLocalSetting(SettingNames.TempPlaybackRate, 3d);
        var factor = legacy == 3d ? DefaultTemporaryAccelerationFactor : NormalizeTemporaryAccelerationFactor(legacy);
        settings.WriteLocalSetting(TemporaryAccelerationFactorKey, factor);
        return factor;
    }

    internal static void WriteTemporaryAccelerationFactor(double value)
        => GlobalDependencies.Kernel.GetRequiredService<ISettingsToolkit>().WriteLocalSetting(
            TemporaryAccelerationFactorKey, NormalizeTemporaryAccelerationFactor(value));

    private static double NormalizeTemporaryAccelerationFactor(double value)
        => double.IsFinite(value) && value is >= 1.1d and <= 4d ? value : DefaultTemporaryAccelerationFactor;

    internal void FocusPlayerInput() => _playerOverlay?.Focus(FocusState.Pointer);

    internal bool IsPlayerInputCancellationPending => _inputCancellations != 0;

    private bool CanAcceptPlayerInput => !IsPlaybackClosing && !IsConnecting
        && !IsWindowDeactivated && !IsPlayerInputCancellationPending
        && Client?.IsDisposed == false && Player?.IsPlaybackInitialized == true
        && !IsPopupVisible && !IsExtraPanelVisible && !IsSubtitleSettingsVisible;

    private async Task BeginTemporarySpeedAsync(int owner)
    {
        if (!CanAcceptPlayerInput || Player?.IsPlaying != true || _sponsor?.CanUseBinding != true)
        {
            return;
        }

        var client = Client!;
        var player = Player;
        var version = _mediaVersion;
        var controller = _sponsor;
        var ticket = controller.NoteUserIntent(PlayerIntentKind.PhysicalRate);
        _temporarySpeedIntentOwners[owner] = ticket.IntentId;
        var requestedHold = false;
        await _physicalRateGate.WaitAsync();
        try
        {
            if (!CanAcceptPlayerInput || !IsCurrentPlayback(version)) return;
            var factor = ReadTemporaryAccelerationFactor();
            _sponsorPhysicalRate ??= await controller.AcquirePhysicalRateAsync(ticket);
            var handoff = _sponsorPhysicalRate;
            if (!CanAcceptPlayerInput || !IsCurrentPlayback(version))
            {
                controller.ReleasePhysicalRate(handoff);
                _sponsorPhysicalRate = null;
                return;
            }
            var wasHeld = _temporarySpeed.IsHeld;
            requestedHold = true;
            // The physical handoff captures the pre-hold rate. Reuse it for overlapping
            // owners/repeats, rather than multiplying the already accelerated player rate.
            var temporaryRate = Math.Min(MaximumPlaybackRate, handoff.Baseline * factor);
            var pending = _temporarySpeed.HoldAsync(owner, handoff.Baseline,
                temporaryRate,
                async rate =>
                {
                    RefreshTemporarySpeedState(showFeedback: true);
                    await controller.WritePhysicalRateAsync(handoff, rate);
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

            if (IsCurrentPlayback(version)) RefreshTemporarySpeedState();
        }
        finally
        {
            if (!requestedHold && _temporarySpeedIntentOwners.TryGetValue(owner, out var intentId)
                && intentId == ticket.IntentId) _temporarySpeedIntentOwners.Remove(owner);
            controller.AbandonIntent(ticket);
            _physicalRateGate.Release();
        }
    }

    private async Task ReleaseTemporarySpeedAsync(int owner)
    {
        // Classify synchronously: the surface callback during Cancel is cleanup, not a new
        // user intent. A real independent release (even while begin awaits) still wins.
        var independentRelease = _temporarySpeedIntentOwners.Remove(owner);
        var ticket = independentRelease && _sponsor?.CanUseBinding == true
            ? _sponsor.NoteUserIntent(PlayerIntentKind.PhysicalRate) : null;
        await _physicalRateGate.WaitAsync();
        try
        {
            var version = _mediaVersion;
            var wasHeld = _temporarySpeed.IsHeld;
            var pending = _temporarySpeed.ReleaseAsync(owner);
            RefreshTemporarySpeedState(showFeedback: wasHeld != _temporarySpeed.IsHeld);
            try
            {
                await pending;
                ReleaseSponsorPhysicalIfRestored();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Temporary playback speed restore failed.");
            }

            if (IsCurrentPlayback(version)) RefreshTemporarySpeedState();
        }
        finally
        {
            if (ticket is not null) _sponsor?.AbandonIntent(ticket);
            _physicalRateGate.Release();
        }
    }

    private void ReleaseSponsorPhysicalIfRestored()
    {
        if (!_temporarySpeed.IsActive && _sponsorPhysicalRate is { } handoff)
        {
            _sponsor!.ReleasePhysicalRate(handoff);
            _sponsorPhysicalRate = null;
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
        await _physicalRateGate.WaitAsync();
        try
        {
            _altSpeedHeld = false;
            IsTouching = false;
            _temporarySpeedIntentOwners.Clear(); // Before synchronous surface -> RestoreSpeed callbacks.
            if (cancelTransport) _playerOverlay?.CancelPendingInput();
            else _playerOverlay?.CancelSurfaceInput();
            var wasHeld = _temporarySpeed.IsHeld;
            var pending = _temporarySpeed.CancelAsync();
            RefreshTemporarySpeedState(showFeedback: wasHeld != _temporarySpeed.IsHeld);
            await pending;
            ReleaseSponsorPhysicalIfRestored();
        }
        finally
        {
            --_inputCancellations;
            _physicalRateGate.Release();
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

    private static readonly int[] NonAltModifierKeys = [0x10, 0x11, 0x5B, 0x5C];

    private static bool HasNonAltModifier()
        => NonAltModifierKeys.Any(key => (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0);

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
                    await SendOpaqueUserKeyAsync(args.Key.ToString().Replace("Number", string.Empty, StringComparison.Ordinal));
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
