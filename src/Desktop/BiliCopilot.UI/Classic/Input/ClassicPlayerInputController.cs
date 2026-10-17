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
    private readonly PlayerTapAggregationState _taps = new();
    private PlayerTapContext _tapContext;
    private long _inputEpoch;
    private double _scale = 1, _width, _height;
    internal Func<bool>? TapAdmission { get; set; }
    internal double DoubleClickMilliseconds { get; set; }
    internal double DoubleClickWidth { get; set; }
    internal double DoubleClickHeight { get; set; }
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
            _taps.Cancel();
            CancelDelay(ref _tapDelay);
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
        bool primary, bool rightButton, bool sourceAllowed, bool generated, bool modified,
        double scale = 1, double width = 0, double height = 0, double? rootX = null, double? rootY = null)
    {
        var accepted = false;
        await _session.OnDispatcherAsync(async () =>
        {
            if (!Accept || !sourceAllowed || generated || (!touch && modified)) { await CancelAsync(); return; }
            if (_pointer is not null || (touch && !primary)) { await CancelAsync(); return; }
            _contactGeneration = _session.Generation;
            _pointer = id;
            _touch = touch;
            _right = !touch && rightButton;
            CancelDelay(ref _tapDelay);
            _scale = scale; _width = width; _height = height;
            _tapContext = CurrentTapContext(touch);
            if (_right || _alt) _taps.Cancel();
            else Observe(ExecuteTapAsync(_taps.Press(_tapContext, (rootX ?? x) * scale, (rootY ?? y) * scale,
                TapNow, DoubleClickMilliseconds, DoubleClickWidth, DoubleClickHeight)));
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
        if (_gesture.Phase != PlayerSurfaceGesture.Pressed) { CancelDelay(ref _holdDelay); _taps.Cancel(); }
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
        var menu = tap && _right;
        var action = tap && !_right ? _taps.Release(_tapContext, TapNow) : default;
        ResetContact();
        if (phase == PlayerSurfaceGesture.Holding) await _session.ReleaseRateAsync(1);
        else if (phase == PlayerSurfaceGesture.Horizontal && preview is double seconds && _session.SeekCommand.CanExecute(seconds))
            await _session.SeekCommand.ExecuteAsync(seconds);
        else if (menu) ContextMenuRequested?.Invoke(this, EventArgs.Empty);
        else if (tap && !menu)
        {
            await ExecuteTapAsync(action);
            if (_taps.Pending)
            {
                _tapDelay = new();
                Observe(TapAfterDelayAsync(_session.Generation, _inputEpoch, _tapDelay.Token));
            }
        }
    });

    public Task CancelAsync() => _session.OnDispatcherAsync(async () =>
    {
        CancelDelay(ref _holdDelay);
        CancelDelay(ref _tapDelay);
        _inputEpoch++;
        _taps.Cancel();
        _alt = false;
        ResetContact();
        await _session.CancelRateAsync();
    });

    private bool ContactCurrent => Accept && _contactGeneration == _session.Generation && (TapAdmission?.Invoke() ?? true);
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
        if (_gesture.TryHold()) { _taps.Cancel(); await _session.HoldRateAsync(1, Preferences.TemporarySpeed); }
    }
    private async Task TapAfterDelayAsync(long generation, long epoch, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(_taps.Remaining(TapNow)), token);
        await _session.OnDispatcherAsync(async () =>
        {
            if (token.IsCancellationRequested || epoch != _inputEpoch || !Accept || generation != _session.Generation || _pointer is not null) return;
            await ExecuteTapAsync(_taps.Expire(TapNow));
            if (_taps.Pending) Observe(TapAfterDelayAsync(generation, epoch, token));
        });
    }
    private static double TapNow => System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency;
    private PlayerTapContext CurrentTapContext(bool touch) => new(_session, _session.Generation, _inputEpoch, touch, _scale, _width, _height);
    private bool TapCurrent(PlayerTapContext context) => Accept && !_alt
        && context == CurrentTapContext(context.Touch) && (TapAdmission?.Invoke() ?? true);
    private async Task ExecuteTapAsync(PlayerTapAction action)
    {
        if (action.Intent == PlayerTapIntent.None || !TapCurrent(action.Context)) return;
        if (action.Intent == PlayerTapIntent.Single && action.Context.Touch)
        { ToggleControlsRequested?.Invoke(this, EventArgs.Empty); return; }
        var command = action.Intent == PlayerTapIntent.Double && !action.Context.Touch
            ? HostCommands.ToggleFullScreen : ToggleCommand;
        if (command is not null && command.CanExecute(null) && TapCurrent(action.Context)) await command.ExecuteAsync(null);
    }
    private void ResetContact()
    {
        var hadPointer = _pointer is not null;
        _pointer = null;
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
