// Copyright (c) Bili Copilot. All rights reserved.


namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// One captured contact. Touch locks its first unambiguous axis; a committed
/// hold cannot later become a tap or swipe. Mouse drags are not touch gestures.
/// </summary>
internal sealed class PlayerSurfaceGestureState
{
    // Retain the existing player surface's movement threshold.
    private const double MovementThreshold = 5;
    private double _startX;
    private double _startY;
    private bool _isTouch;

    internal PlayerSurfaceGesture Phase { get; private set; }

    internal double DeltaX { get; private set; }

    internal double DeltaY { get; private set; }

    internal void Begin(double x, double y, bool isTouch)
    {
        _startX = x;
        _startY = y;
        _isTouch = isTouch;
        DeltaX = 0;
        DeltaY = 0;
        Phase = PlayerSurfaceGesture.Pressed;
    }

    internal void Move(double x, double y)
    {
        if (Phase is PlayerSurfaceGesture.None or PlayerSurfaceGesture.Holding or PlayerSurfaceGesture.Cancelled)
        {
            return;
        }

        DeltaX = x - _startX;
        DeltaY = y - _startY;
        if (Phase is not (PlayerSurfaceGesture.Pressed or PlayerSurfaceGesture.Undecided))
        {
            return;
        }

        var xDistance = Math.Abs(DeltaX);
        var yDistance = Math.Abs(DeltaY);
        if (Math.Max(xDistance, yDistance) <= MovementThreshold)
        {
            return;
        }

        Phase = !_isTouch ? PlayerSurfaceGesture.Cancelled
            : xDistance > yDistance ? PlayerSurfaceGesture.Horizontal
            : yDistance > xDistance ? PlayerSurfaceGesture.Vertical
            : PlayerSurfaceGesture.Undecided;
    }

    internal bool TryHold()
    {
        if (!_isTouch || Phase != PlayerSurfaceGesture.Pressed)
        {
            return false;
        }

        Phase = PlayerSurfaceGesture.Holding;
        return true;
    }

    internal void Reset()
    {
        Phase = PlayerSurfaceGesture.None;
        DeltaX = 0;
        DeltaY = 0;
    }
}

internal enum PlayerSurfaceGesture
{
    None,
    Pressed,
    Undecided,
    Horizontal,
    Vertical,
    Holding,
    Cancelled,
}
