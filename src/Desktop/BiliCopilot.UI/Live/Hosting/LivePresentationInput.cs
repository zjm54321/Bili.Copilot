#nullable enable

using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BiliCopilot.UI.Live.Hosting;

/// <summary>Shared outer-tree/island input. A held press must cross the system drag threshold.</summary>
internal sealed class LivePresentationInput
{
    private readonly UIElement _root;
    private readonly UIElement _keyboardRoot;
    private readonly Func<bool> _canDrag;
    private readonly Action _move;
    private readonly Func<bool> _exitFullScreen;
    private readonly Action? _pointerActivity;
    private readonly PointerEventHandler _pressed;
    private readonly PointerEventHandler _moved;
    private readonly KeyEventHandler _keyDown;
    private uint? _pointerId;
    private System.Drawing.Point _pressPoint;
    private Point? _lastActivityPosition;

    public LivePresentationInput(UIElement root, Func<bool> canDrag, Action move, Func<bool> exitFullScreen,
        Action? pointerActivity = null, bool exitFullScreenOnDoubleTap = false,
        UIElement? keyboardRoot = null)
    {
        _root = root;
        _keyboardRoot = keyboardRoot ?? root;
        _canDrag = canDrag;
        _move = move;
        _exitFullScreen = exitFullScreen;
        _pointerActivity = pointerActivity;
        _pressed = OnPressed;
        _moved = OnMoved;
        _keyDown = OnKeyDown;
        root.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
        root.AddHandler(UIElement.PointerMovedEvent, _moved, true);
        _keyboardRoot.AddHandler(UIElement.KeyDownEvent, _keyDown, true);
        root.PointerReleased += OnPointerEnded;
        root.PointerCanceled += OnPointerEnded;
        root.PointerCaptureLost += OnPointerEnded;
        if (exitFullScreenOnDoubleTap) root.DoubleTapped += OnDoubleTapped;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        CancelDrag();
        // The picture's transparent input control owns island keyboard focus even in full screen.
        if (_root is ContentControl focusTarget) focusTarget.Focus(FocusState.Pointer);
        if (args.Handled || !_canDrag() || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || !args.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        // Never steal a button, slider, composer, selector or its template's pointer gesture.
        for (var node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, _root);
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is Control and not UserControl) return;
        }

        if (!PInvoke.GetCursorPos(out _pressPoint) || !_root.CapturePointer(args.Pointer)) return;
        _pointerId = args.Pointer.PointerId;
        args.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(_root).Position;
        if (!args.IsGenerated && args.Pointer.PointerDeviceType == PointerDeviceType.Mouse
            && _lastActivityPosition?.Equals(position) != true)
        {
            _lastActivityPosition = position;
            _pointerActivity?.Invoke();
        }
        if (_pointerId != args.Pointer.PointerId) return;
        if (!_canDrag() || !args.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
        {
            CancelDrag();
            return;
        }

        if (!PInvoke.GetCursorPos(out var point)
            || (Math.Abs((long)point.X - _pressPoint.X) <= Math.Max(1, GetSystemMetrics(68))
                && Math.Abs((long)point.Y - _pressPoint.Y) <= Math.Max(1, GetSystemMetrics(69)))) return;
        args.Handled = true;
        // Synchronous hand-off while the button is STILL down; never enqueue a move after release.
        CancelDrag();
        _move();
    }

    private void OnPointerEnded(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId) CancelDrag();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && _exitFullScreen()) args.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (!args.Handled && _exitFullScreen()) args.Handled = true;
    }

    public void CancelDrag()
    {
        if (_pointerId is null) return;
        _pointerId = null;
        _root.ReleasePointerCaptures();
    }

    public void Detach()
    {
        CancelDrag();
        _root.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
        _root.RemoveHandler(UIElement.PointerMovedEvent, _moved);
        _keyboardRoot.RemoveHandler(UIElement.KeyDownEvent, _keyDown);
        _root.PointerReleased -= OnPointerEnded;
        _root.PointerCanceled -= OnPointerEnded;
        _root.PointerCaptureLost -= OnPointerEnded;
        _root.DoubleTapped -= OnDoubleTapped;
    }

    internal static void MoveWindow(nint window)
    {
        if ((GetAsyncKeyState(1) & 0x8000) == 0 || !PInvoke.GetCursorPos(out var point)) return;
        var position = unchecked((nint)((point.X & 0xffff) | ((point.Y & 0xffff) << 16)));
        ReleaseCapture();
        SendMessageW(window, 0x00A1, 2, position); // MAIN HWND: WM_NCLBUTTONDOWN, HTCAPTION.
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nuint parameter, nint position);
}
