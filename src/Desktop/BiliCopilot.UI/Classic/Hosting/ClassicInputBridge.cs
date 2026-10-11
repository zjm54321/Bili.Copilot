using BiliCopilot.UI.Classic.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BiliCopilot.UI.Classic.Hosting;

/// <summary>The only WinUI-to-Classic controller bridge. Native/Web controls retain their own input.</summary>
public sealed class ClassicInputBridge : IDisposable
{
    private readonly ClassicPlayerWindow _window;
    private readonly ClassicPlayerInputController _input;
    private readonly ClassicPlayerContent _view;
    private readonly Action<Exception> _report;
    private readonly KeyEventHandler _keyDown;
    private readonly KeyEventHandler _keyUp;
    private Microsoft.UI.Xaml.Input.Pointer? _captured;
    private bool _disposed;

    public ClassicInputBridge(ClassicPlayerWindow window, ClassicPlayerInputController input,
        ClassicPlayerContent view, Action<Exception> report)
    {
        _window = window; _input = input; _view = view; _report = report;
        _keyDown = OnKeyDown; _keyUp = OnKeyUp;
        view.Root.AddHandler(UIElement.KeyDownEvent, _keyDown, true);
        view.Root.AddHandler(UIElement.KeyUpEvent, _keyUp, true);
        view.InputSurface.PointerPressed += OnPressed;
        view.InputSurface.PointerMoved += OnMoved;
        view.InputSurface.PointerEntered += OnPointerEntered;
        view.InputSurface.PointerReleased += OnReleased;
        view.InputSurface.PointerWheelChanged += OnWheel;
        view.InputSurface.PointerCanceled += OnPointerLost;
        view.InputSurface.PointerCaptureLost += OnPointerLost;
        view.Root.LostFocus += OnLostFocus;
        view.Root.Loaded += OnLoaded;
        view.Root.Unloaded += OnUnloaded;
        input.FocusRequested += OnFocusRequested;
        input.ReleaseCaptureRequested += OnReleaseCapture;
        input.ToggleControlsRequested += OnToggleControls;
        input.ContextMenuRequested += OnContextMenu;
        input.PreviewChanged += OnPreview;
    }
    private bool Allowed(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _view.InputSurface) || ReferenceEquals(node, _view.Root)) return true;
            if (node is Control or Microsoft.UI.Xaml.Documents.Hyperlink) return false;
        }
        return false;
    }
    private static bool Down(int key) => (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0;
    private static bool Modified => Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
    private bool Blocked => _disposed || _window.IsClosing || _window.Session?.IsClosing != false || (_view.IsInputBlocked?.Invoke() ?? false);
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Handled || Blocked || !Allowed(e.OriginalSource)
                || !Allowed(FocusManager.GetFocusedElement(_view.Root.XamlRoot))) return;
            var reserve = Recognized(e.Key);
            var session = _window.Session;
            var generation = session?.Generation;
            var task = _input.KeyDownAsync(e.Key, e.KeyStatus.WasKeyDown, true,
                Down(0x11), Down(0x10), Down(0x12), Down(0x5B) || Down(0x5C));
            // Admission is synchronous on the owning dispatcher; reserve recognized unmodified keys
            // while an admitted backend command is awaited, not after routed input has escaped.
            if (!task.IsCompleted && reserve) e.Handled = true;
            var accepted = await task;
            e.Handled |= accepted;
            if (accepted && !Blocked && ReferenceEquals(session, _window.Session) && session?.Generation == generation)
                _view.ShowControlsForInput?.Invoke();
        }
        catch (Exception ex) { _report(ex); }
    }
    private bool Recognized(VirtualKey key)
    {
        var session = _window.Session;
        if (session is null || session.IsBusy) return false;
        if (key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return !Down(0x10) && !Down(0x11) && !Down(0x5B) && !Down(0x5C)
                && session.State == ClassicPlaybackState.Playing && session.Capabilities.HasFlag(ClassicCapabilities.Speed);
        if (key == VirtualKey.M && Down(0x11) && !Down(0x10) && !Down(0x12)) return true;
        if (Modified) return false;
        return key switch
        {
            VirtualKey.Space => session.Capabilities.HasFlag(ClassicCapabilities.Transport),
            VirtualKey.Left or VirtualKey.Right => session.Capabilities.HasFlag(ClassicCapabilities.Seek),
            VirtualKey.Up or VirtualKey.Down => session.Capabilities.HasFlag(ClassicCapabilities.Volume),
            VirtualKey.F11 or VirtualKey.Escape => true,
            _ => false,
        };
    }
    private async void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        try { await _input.KeyUpAsync(e.Key); } catch (Exception ex) { _report(ex); }
    }
    private async void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (e.Handled || Blocked || !Allowed(e.OriginalSource) || e.IsGenerated) return;
            var type = e.Pointer.PointerDeviceType;
            if (type is not (PointerDeviceType.Mouse or PointerDeviceType.Touch)) return;
            var point = e.GetCurrentPoint(_view.InputSurface);
            if (type == PointerDeviceType.Mouse && (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed || Modified)) return;
            if (_captured is not null)
            {
                e.Handled = true;
                await _input.CancelAsync();
                return;
            }
            if (!_view.InputSurface.CapturePointer(e.Pointer)) { await _input.CancelAsync(); return; }
            _captured = e.Pointer;
            _view.SetInteractionActive?.Invoke(true);
            e.Handled = true;
            var accepted = await _input.PointerPressedAsync(e.Pointer.PointerId, point.Position.X, point.Position.Y,
                type == PointerDeviceType.Touch, point.Properties.IsPrimary, point.Properties.IsRightButtonPressed, true, false, Modified);
            if (!accepted) ReleaseCapture();
        }
        catch (Exception ex) { ReleaseCapture(); _report(ex); }
    }
    private async void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_captured is null) { ShowForMouseActivity(e); return; }
            if (_captured?.PointerId != e.Pointer.PointerId) return;
            e.Handled = true;
            var point = e.GetCurrentPoint(_view.InputSurface);
            await _input.PointerMovedAsync(e.Pointer.PointerId, point.Position.X, point.Position.Y, _view.InputSurface.ActualWidth);
        }
        catch (Exception ex) { _report(ex); }
    }
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        try { ShowForMouseActivity(e); } catch (Exception ex) { _report(ex); }
    }
    private void ShowForMouseActivity(PointerRoutedEventArgs e)
    {
        if (_captured is not null || e.Handled || Blocked || e.IsGenerated || Modified
            || !_view.Root.IsLoaded || !Allowed(e.OriginalSource)
            || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var buttons = e.GetCurrentPoint(_view.InputSurface).Properties;
        if (buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed
            || buttons.IsXButton1Pressed || buttons.IsXButton2Pressed) return;
        // Mouse and touchpad cursor movement share this route; never infer hardware from deltas.
        // Hover only reveals chrome: no capture, gesture mutation, focus transfer or Handled change.
        _view.ShowControlsForInput?.Invoke();
    }
    private async void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_captured?.PointerId != e.Pointer.PointerId) return;
        try
        {
            if (_captured?.PointerId != e.Pointer.PointerId) return;
            e.Handled = true;
            var point = e.GetCurrentPoint(_view.InputSurface);
            await _input.PointerReleasedAsync(e.Pointer.PointerId, point.Position.X, point.Position.Y, _view.InputSurface.ActualWidth);
        }
        catch (Exception ex) { _report(ex); }
        finally { ReleaseCapture(); }
    }
    private async void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (e.Handled || Blocked || !Allowed(e.OriginalSource) || Modified || e.IsGenerated) return;
            var properties = e.GetCurrentPoint(_view.InputSurface).Properties;
            var horizontal = properties.IsHorizontalMouseWheel;
            var capability = horizontal ? ClassicCapabilities.Seek : ClassicCapabilities.Volume;
            if (_window.Session?.Capabilities.HasFlag(capability) != true || properties.MouseWheelDelta == 0) return;
            e.Handled = true;
            var session = _window.Session;
            var generation = session?.Generation;
            var accepted = await _input.WheelAsync(properties.MouseWheelDelta, horizontal, true,
                e.Pointer.PointerDeviceType == PointerDeviceType.Mouse, false, false);
            if (accepted && !Blocked && ReferenceEquals(session, _window.Session) && session?.Generation == generation)
                _view.ShowControlsForInput?.Invoke();
        }
        catch (Exception ex) { _report(ex); }
    }
    private async void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_captured?.PointerId != e.Pointer.PointerId) return;
        try { await _input.CancelAsync(); } catch (Exception ex) { _report(ex); }
    }
    private async void OnLostFocus(object sender, RoutedEventArgs e)
    {
        try { await _input.CancelAsync(); } catch (Exception ex) { _report(ex); }
    }
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _view.InputSurface.PointerEntered -= OnPointerEntered;
        try { await _input.CancelAsync(); } catch (Exception ex) { _report(ex); }
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _view.InputSurface.PointerEntered -= OnPointerEntered;
        _view.InputSurface.PointerEntered += OnPointerEntered;
    }
    private void OnFocusRequested(object? sender, EventArgs e)
    {
        if (_view.InputSurface is Control surface) surface.Focus(FocusState.Pointer);
        else if (_view.Root is Control root) root.Focus(FocusState.Pointer);
    }
    private void OnReleaseCapture(object? sender, EventArgs e) => ReleaseCapture();
    private void OnToggleControls(object? sender, EventArgs e) => _view.ToggleControls?.Invoke();
    private void OnContextMenu(object? sender, EventArgs e) => _view.ShowContextMenu?.Invoke();
    private void OnPreview(object? sender, EventArgs e) => _view.PreviewPosition?.Invoke(_input.PreviewSeconds);
    private void ReleaseCapture()
    {
        var pointer = _captured;
        _captured = null;
        _view.SetInteractionActive?.Invoke(false);
        if (pointer is not null) _view.InputSurface.ReleasePointerCapture(pointer);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Root.RemoveHandler(UIElement.KeyDownEvent, _keyDown);
        _view.Root.RemoveHandler(UIElement.KeyUpEvent, _keyUp);
        _view.InputSurface.PointerPressed -= OnPressed;
        _view.InputSurface.PointerMoved -= OnMoved;
        _view.InputSurface.PointerEntered -= OnPointerEntered;
        _view.InputSurface.PointerReleased -= OnReleased;
        _view.InputSurface.PointerWheelChanged -= OnWheel;
        _view.InputSurface.PointerCanceled -= OnPointerLost;
        _view.InputSurface.PointerCaptureLost -= OnPointerLost;
        _view.Root.LostFocus -= OnLostFocus;
        _view.Root.Loaded -= OnLoaded;
        _view.Root.Unloaded -= OnUnloaded;
        _input.FocusRequested -= OnFocusRequested;
        _input.ReleaseCaptureRequested -= OnReleaseCapture;
        _input.ToggleControlsRequested -= OnToggleControls;
        _input.ContextMenuRequested -= OnContextMenu;
        _input.PreviewChanged -= OnPreview;
        ReleaseCapture();
    }
}
