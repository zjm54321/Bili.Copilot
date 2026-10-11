using BiliCopilot.UI.Controls.Core;
using CommunityToolkit.Mvvm.Input;
using Windows.System;

namespace BiliCopilot.UI.Classic.Input;

// Optional commands are enabled only when their concrete host/backend actually implements them.
public sealed record ClassicInputHostCommands(IAsyncRelayCommand? ToggleFullScreen = null,
    IAsyncRelayCommand? DefaultDisplay = null, IAsyncRelayCommand? ToggleCompactOverlay = null,
    IAsyncRelayCommand? PreviousChapter = null, IAsyncRelayCommand? NextChapter = null,
    IAsyncRelayCommand? ToggleStatistics = null, IAsyncRelayCommand<int>? StatisticsPage = null);

public sealed record ClassicInputPreferences(double BackwardSeconds = 10, double ForwardSeconds = 30,
    double VolumeStep = 0.05, double TemporarySpeed = 3);

/// <summary>Functional input only. The host owns pointer capture, focus ancestry, and display commands.</summary>
public sealed class ClassicPlayerInputController
{
    private readonly ClassicPlaybackSession _session;
    private readonly PlayerSurfaceGestureState _gesture = new();
    private CancellationTokenSource? _holdDelay;
    private CancellationTokenSource? _tapDelay;
    private uint? _pointer;
    private bool _touch;
    private bool _right;
    private bool _secondTap;
    private bool _pendingTap;
    private bool _alt;
    private bool _active = true;
    private bool _blocked;
    private double _startPosition;
    private double _startVolume;
    private long _contactGeneration;
    public ClassicInputPreferences Preferences { get; set; } = new();
    public ClassicInputHostCommands HostCommands { get; set; } = new();
    public double? PreviewSeconds { get; private set; }
    public event EventHandler? PreviewChanged;
    public event EventHandler? ToggleControlsRequested;
    public event EventHandler? ContextMenuRequested;
    public event EventHandler? FocusRequested;
    public event EventHandler? ReleaseCaptureRequested;

    internal ClassicPlayerInputController(ClassicPlaybackSession session) => _session = session;
    private bool Accept => _active && !_blocked && !_session.IsClosing && !_session.IsBusy;

    public Task SetContextAsync(bool active, bool blocked) => _session.OnDispatcherAsync(async () =>
    {
        _active = active;
        _blocked = blocked;
        if (!active || blocked) await CancelAsync();
    });

    // sourceAllowed includes both original-source and currently focused ancestry checks.
    public async Task<bool> KeyDownAsync(VirtualKey key, bool repeat, bool sourceAllowed,
        bool control = false, bool shift = false, bool alt = false, bool windows = false)
    {
        var handled = false;
        await _session.OnDispatcherAsync(async () =>
        {
            var isAlt = key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu;
            if (_alt && !isAlt) { _alt = false; await _session.ReleaseRateAsync(2); return; }
            if (!Accept || !sourceAllowed) return;
            if (isAlt)
            {
                if (control || shift || windows || !_session.Can(ClassicCapabilities.Speed) || _session.State != ClassicPlaybackState.Playing) return;
                handled = true;
                if (!repeat && !_alt) { _alt = true; await _session.HoldRateAsync(2, Preferences.TemporarySpeed); }
                return;
            }
            IAsyncRelayCommand? command = key == VirtualKey.M && control && !shift && !alt && !windows
                ? HostCommands.ToggleCompactOverlay : null;
            if (command is null && (control || shift || alt || windows)) return;
            command ??= key switch
            {
                VirtualKey.Space => ToggleCommand,
                VirtualKey.F11 => HostCommands.ToggleFullScreen,
                VirtualKey.Escape => HostCommands.DefaultDisplay,
                VirtualKey.PageUp => HostCommands.PreviousChapter,
                VirtualKey.PageDown => HostCommands.NextChapter,
                VirtualKey.I => HostCommands.ToggleStatistics,
                _ => null,
            };
            if (command is not null && command.CanExecute(null))
            {
                handled = true;
                if (!repeat) await command.ExecuteAsync(null);
                return;
            }
            if (key is VirtualKey.Left or VirtualKey.Right)
                handled = await SeekStepAsync(key == VirtualKey.Right);
            else if (key is VirtualKey.Up or VirtualKey.Down)
                handled = await VolumeStepAsync(key == VirtualKey.Up);
            else if (key >= VirtualKey.Number1 && key <= VirtualKey.Number5 && HostCommands.StatisticsPage is { } pages)
            {
                var page = (int)key - (int)VirtualKey.Number1 + 1;
                if (pages.CanExecute(page)) { handled = true; if (!repeat) await pages.ExecuteAsync(page); }
            }
        });
        return handled;
    }

    public Task KeyUpAsync(VirtualKey key) => _session.OnDispatcherAsync(async () =>
    {
        if (key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        { _alt = false; await _session.ReleaseRateAsync(2); }
    });

    public async Task<bool> WheelAsync(int delta, bool horizontal, bool sourceAllowed,
        bool mouseDevice, bool generated, bool modified)
    {
        var handled = false;
        await _session.OnDispatcherAsync(async () =>
        {
            if (!Accept || !sourceAllowed || !mouseDevice || generated || modified || delta == 0) return;
            handled = horizontal ? await SeekStepAsync(delta > 0) : await VolumeStepAsync(delta > 0);
        });
        return handled;
    }

    // Host calls only for Touch/Mouse; capture before calling, and release if false.
    public async Task<bool> PointerPressedAsync(uint id, double x, double y, bool touch,
        bool primary, bool rightButton, bool sourceAllowed, bool generated, bool modified)
    {
        var accepted = false;
        await _session.OnDispatcherAsync(async () =>
        {
            if (!Accept || !sourceAllowed || generated || (!touch && modified)) return;
            if (_pointer is not null || (touch && !primary)) { await CancelAsync(); return; }
            _contactGeneration = _session.Generation;
            _pointer = id;
            _touch = touch;
            _right = !touch && rightButton;
            _secondTap = touch && _pendingTap;
            _pendingTap = false;
            CancelDelay(ref _tapDelay);
            _gesture.Begin(x, y, touch);
            _startPosition = _session.PositionSeconds;
            _startVolume = _session.Volume;
            accepted = true;
            FocusRequested?.Invoke(this, EventArgs.Empty);
            if (touch)
            {
                _holdDelay = new();
                Observe(HoldAfterDelayAsync(id, _contactGeneration, _holdDelay.Token));
            }
        });
        return accepted;
    }

    public Task PointerMovedAsync(uint id, double x, double y, double width) => _session.OnDispatcherAsync(async () =>
    {
        if (_pointer != id) return;
        if (!ContactCurrent) { await CancelAsync(); return; }
        _gesture.Move(x, y);
        if (_gesture.Phase != PlayerSurfaceGesture.Pressed) { CancelDelay(ref _holdDelay); _secondTap = false; }
        if (_gesture.Phase == PlayerSurfaceGesture.Horizontal && width > 0 && _session.Can(ClassicCapabilities.Seek))
        {
            PreviewSeconds = Math.Clamp(_startPosition + _gesture.DeltaX / width / 2 * _session.DurationSeconds,
                0, Math.Max(0, _session.DurationSeconds - 0.1));
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_gesture.Phase == PlayerSurfaceGesture.Vertical && _session.Can(ClassicCapabilities.Volume))
            await _session.SetVolumeCommand.ExecuteAsync(Math.Clamp(_startVolume - _gesture.DeltaY / 1000, 0, 1));
    });

    public Task PointerReleasedAsync(uint id, double x, double y, double width) => _session.OnDispatcherAsync(async () =>
    {
        if (_pointer != id) return;
        await PointerMovedAsync(id, x, y, width);
        if (_pointer != id || !ContactCurrent) { await CancelAsync(); return; }
        CancelDelay(ref _holdDelay);
        var phase = _gesture.Phase;
        var preview = PreviewSeconds;
        var tap = phase == PlayerSurfaceGesture.Pressed;
        var toggle = tap && (!_touch || _secondTap) && !_right;
        var menu = tap && _right;
        var singleTouch = tap && _touch && !_secondTap;
        ResetContact();
        if (phase == PlayerSurfaceGesture.Holding) await _session.ReleaseRateAsync(1);
        else if (phase == PlayerSurfaceGesture.Horizontal && preview is double seconds && _session.SeekCommand.CanExecute(seconds))
            await _session.SeekCommand.ExecuteAsync(seconds);
        else if (menu) ContextMenuRequested?.Invoke(this, EventArgs.Empty);
        else if (toggle && ToggleCommand.CanExecute(null)) await ToggleCommand.ExecuteAsync(null);
        else if (singleTouch)
        {
            _pendingTap = true;
            _tapDelay = new();
            Observe(TapAfterDelayAsync(_session.Generation, _tapDelay.Token));
        }
    });

    public Task CancelAsync() => _session.OnDispatcherAsync(async () =>
    {
        CancelDelay(ref _holdDelay);
        CancelDelay(ref _tapDelay);
        _pendingTap = false;
        _alt = false;
        ResetContact();
        await _session.CancelRateAsync();
    });

    private bool ContactCurrent => _active && !_blocked && !_session.IsClosing && _contactGeneration == _session.Generation;
    private IAsyncRelayCommand ToggleCommand => _session.State == ClassicPlaybackState.Playing ? _session.PauseCommand : _session.PlayCommand;
    private async Task<bool> SeekStepAsync(bool forward)
    {
        var position = Math.Clamp(_session.PositionSeconds + (forward ? Preferences.ForwardSeconds : -Preferences.BackwardSeconds),
            0, Math.Max(0, _session.DurationSeconds - 0.1));
        if (!_session.SeekCommand.CanExecute(position)) return false;
        await _session.SeekCommand.ExecuteAsync(position);
        return true;
    }
    private async Task<bool> VolumeStepAsync(bool up)
    {
        var volume = Math.Clamp(_session.Volume + (up ? Preferences.VolumeStep : -Preferences.VolumeStep), 0, 1);
        if (!_session.SetVolumeCommand.CanExecute(volume)) return false;
        await _session.SetVolumeCommand.ExecuteAsync(volume);
        return true;
    }
    private async Task HoldAfterDelayAsync(uint pointer, long generation, CancellationToken token)
    {
        await Task.Delay(500, token);
        if (token.IsCancellationRequested || !ContactCurrent || generation != _session.Generation || _pointer != pointer) return;
        if (_gesture.TryHold()) { _secondTap = false; await _session.HoldRateAsync(1, Preferences.TemporarySpeed); }
    }
    private async Task TapAfterDelayAsync(long generation, CancellationToken token)
    {
        await Task.Delay(200, token);
        if (token.IsCancellationRequested || !Accept || generation != _session.Generation || _pointer is not null || !_pendingTap) return;
        _pendingTap = false;
        ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
    }
    private void ResetContact()
    {
        var hadPointer = _pointer is not null;
        _pointer = null;
        _secondTap = false;
        _right = false;
        _gesture.Reset();
        PreviewSeconds = null;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        if (hadPointer) ReleaseCaptureRequested?.Invoke(this, EventArgs.Empty);
    }
    private static void CancelDelay(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Single-await input observer holds no gate; the existing operation never depends on this observer. Its original task completion/fault is unchanged.")]
    private static async void Observe(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
        catch (Exception) { System.Diagnostics.Trace.TraceWarning("Classic input observed operation failed."); }
    }
}
