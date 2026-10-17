// Copyright (c) Bili Copilot. All rights reserved.
namespace BiliCopilot.UI.Controls.Core;

// No native/event objects or clocks: adapters supply monotonic milliseconds and OS metrics.
internal readonly record struct PlayerTapContext(object Owner, long Generation, long Epoch,
    bool Touch, double Scale, double Width, double Height, int Button = 1);
internal enum PlayerTapIntent { None, Single, Double }
internal readonly record struct PlayerTapAction(PlayerTapIntent Intent, PlayerTapContext Context);

internal sealed class PlayerTapAggregationState
{
    private PlayerTapContext _context;
    private double _x, _y, _pressedAt, _window, _halfWidth, _halfHeight;
    private bool _pressed, _pending, _reserved;
    internal bool Pending => _pending;
    internal double Remaining(double now) => Math.Max(1, _pressedAt + _window - now);

    internal PlayerTapAction Press(PlayerTapContext context, double x, double y, double now,
        double window, double rectangleWidth, double rectangleHeight)
    {
        if (_pending && (_context.Touch != context.Touch || _context.Button != context.Button))
        { Cancel(); return default; } // Never promote a touch sequence into a mouse/button single.
        var same = ReferenceEquals(context.Owner, _context.Owner) && context == _context;
        var elapsed = now - _pressedAt;
        var match = _pending && same && elapsed > 0 && elapsed <= _window
            && Math.Abs(x - _x) <= _halfWidth && Math.Abs(y - _y) <= _halfHeight;
        var previous = _pending && same && !match
            ? new PlayerTapAction(PlayerTapIntent.Single, _context) : default;
        Cancel();
        _context = context;
        _x = x; _y = y; _pressedAt = now;
        _window = Math.Max(1, window);
        _halfWidth = Math.Max(0, rectangleWidth / 2);
        _halfHeight = Math.Max(0, rectangleHeight / 2);
        _pressed = true;
        _reserved = match; // Consume at SECOND DOWN, before an already queued timer callback.
        return previous;
    }

    internal PlayerTapAction Release(PlayerTapContext context, double now)
    {
        if (!_pressed || context != _context || !ReferenceEquals(context.Owner, _context.Owner))
        { Cancel(); return default; }
        _pressed = false;
        var elapsed = now - _pressedAt;
        if (elapsed < 0 || (context.Touch && elapsed > _window)) { Cancel(); return default; }
        if (!context.Touch)
        {
            // Mouse clicks complete on release; only touch waits for a possible second tap.
            var action = new PlayerTapAction(PlayerTapIntent.Single, _context);
            Cancel();
            return action;
        }
        if (_reserved)
        {
            var action = new PlayerTapAction(PlayerTapIntent.Double, _context);
            Cancel();
            return action;
        }
        _pending = true;
        return default;
    }

    internal PlayerTapAction Expire(double now)
    {
        if (!_pending || _pressed || now - _pressedAt < _window) return default;
        var action = new PlayerTapAction(PlayerTapIntent.Single, _context);
        Cancel();
        return action;
    }

    internal void Cancel() { _pending = false; _pressed = false; _reserved = false; _context = default; }
}
