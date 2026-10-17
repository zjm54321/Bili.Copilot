// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI;
using Microsoft.UI.Input;
using Windows.Graphics;

namespace BiliCopilot.UI.Controls;

/// <summary>A native client-area rectangle in physical pixels.</summary>
internal readonly record struct RootChromeRect(int X, int Y, int Width, int Height);

/// <summary>One complete snapshot of the main window's title-bar regions.</summary>
internal sealed class RootChromeRegions(RootChromeRect[] caption, RootChromeRect[] passthrough)
{
    internal RootChromeRect[] Caption { get; } = caption;

    internal RootChromeRect[] Passthrough { get; } = passthrough;
}

/// <summary>The two region kinds the root chrome owns. Order of a publish: Passthrough, then Caption.</summary>
internal enum RootChromeRegionKind
{
    Passthrough = 0,
    Caption = 1,
}

/// <summary>
/// The only place that touches the main window's native non-client regions. Each member is exactly ONE native
/// call, so the authority can re-check its generation between every individual Set/Clear.
/// </summary>
internal interface IRootChromeRegionSink
{
    void Set(RootChromeRegionKind kind, RootChromeRect[] rects);

    void Clear(RootChromeRegionKind kind);
}

/// <summary>
/// Owns the main window's caption/passthrough regions; each native call revalidates its generation.
/// </summary>
internal sealed class RootChromeRegionAuthority(
    Func<Action, bool> schedule,
    Func<RootChromeRegions?> measure,
    Func<IRootChromeRegionSink?> resolveSink)
{
    private static readonly RootChromeRegionKind[] PublishOrder = [RootChromeRegionKind.Passthrough, RootChromeRegionKind.Caption];
    private static readonly RootChromeRegionKind[] ClearOrder = [RootChromeRegionKind.Caption, RootChromeRegionKind.Passthrough];

    private long _generation;
    private bool _active;
    private bool _retired;
    private bool _pending;
    private bool _publishing;
    private bool _republish;

    // Per-kind ownership of what may be in the native region: which sink holds it and the rects it holds
    // (null rects = unknown, e.g. a native call whose issuing generation ended while it ran).
    private readonly Owned?[] _owned = new Owned?[2];

    private sealed class Owned(IRootChromeRegionSink sink, RootChromeRect[]? rects)
    {
        internal readonly IRootChromeRegionSink Sink = sink;
        internal RootChromeRect[]? Rects = rects;
    }

    internal bool IsActive => _active && !_retired;

    /// <summary>Become the writer for a new generation. Returns true only on an inactive → active transition.</summary>
    internal bool Activate()
    {
        if (_retired) return false;
        var changed = !_active;
        if (changed)
        {
            _active = true;
            _generation++;
            _pending = false;
        }

        Request();
        return changed;
    }

    /// <summary>
    /// Stop writing. With <paramref name="clearNative"/> the rects this authority wrote are cleared once
    /// (FullScreen/CompactOverlay must not keep a stale caption/passthrough); otherwise they are left for the
    /// next activation to replace (e.g. a temporary unload).
    /// </summary>
    internal void Deactivate(bool clearNative = true)
    {
        if (_retired) return;
        if (_active)
        {
            _active = false;
            _generation++;
            _pending = false;
        }

        if (!clearNative) return;
        // Each clear is admitted only while this same deactivation is still current: a re-entrant activation or
        // publication between the two native calls is never cleared by the older deactivation.
        var generation = _generation;
        foreach (var kind in ClearOrder) ClearKind(kind, generation);
    }

    /// <summary>Window closing: never write again, not even a clear (in-flight publishes stop at the next call).</summary>
    internal void Retire()
    {
        if (_retired) return;
        _retired = true;
        _active = false;
        _generation++;
        _pending = false;
        Array.Clear(_owned);
    }

    /// <summary>Queue one publish for the current generation (coalesced).</summary>
    internal void Request()
    {
        if (!IsActive || _pending) return;
        _pending = true;
        var generation = _generation;
        var queued = false;
        try { queued = schedule(() => Run(generation)); }
        finally
        {
            if (!queued && generation == _generation) _pending = false;
        }
    }

    private bool IsCurrent(long generation) => generation == _generation && IsActive;

    private bool IsCurrentClear(long generation) => generation == _generation && !_active && !_retired;

    private void Run(long generation)
    {
        // A callback of an older generation never owns the current pending slot.
        if (generation != _generation) return;
        _pending = false;
        if (!IsActive) return;
        if (_publishing)
        {
            // A callback pumped inside the in-flight publish's native call: never re-queue from here (a nested pump
            // would spin); the outer publish requests exactly one follow-up when it finishes.
            _republish = true;
            return;
        }

        _publishing = true;
        _republish = false;
        try
        {
            // Measuring reads live layout and may synchronously re-enter (activate/deactivate/retire).
            var regions = measure();
            if (regions is null || !IsCurrent(generation)) return;
            var sink = resolveSink();
            if (sink is null || !IsCurrent(generation)) return;
            foreach (var kind in PublishOrder)
            {
                // Admission is re-checked before EVERY native call: a deactivation/retirement/new generation that
                // happened during the previous call stops the rest of this publish.
                if (!IsCurrent(generation)) return;
                var rects = kind == RootChromeRegionKind.Caption ? regions.Caption : regions.Passthrough;
                var index = (int)kind;
                if (_owned[index] is { Rects: { } held } owned && ReferenceEquals(owned.Sink, sink) && held.AsSpan().SequenceEqual(rects)) continue;
                if (_owned[index] is { } previous && !ReferenceEquals(previous.Sink, sink))
                {
                    // The window behind the chrome changed: take this kind back from the old sink first.
                    _owned[index] = null;
                    previous.Sink.Clear(kind);
                    if (!IsCurrent(generation))
                    {
                        Settle(kind, null, previous.Sink);
                        return;
                    }
                }

                var entry = new Owned(sink, null); // unknown until the native call returns within this generation
                _owned[index] = entry;
                sink.Set(kind, rects);
                if (IsCurrent(generation))
                {
                    if (ReferenceEquals(_owned[index], entry)) entry.Rects = rects;
                    continue;
                }

                Settle(kind, entry, sink);
                return;
            }
        }
        finally
        {
            _publishing = false;
            if (_republish)
            {
                _republish = false;
                Request();
            }
        }
    }

    /// <summary>
    /// A native call returned after its issuing generation ended, so the kind's real content is unknown (the call
    /// may have landed after a re-entrant clear or newer write). Only the CURRENT owner repairs it: an active
    /// generation republishes (unknown content is always rewritten), an inactive one clears it, and a retired
    /// (closing) authority makes no further native call.
    /// </summary>
    private void Settle(RootChromeRegionKind kind, Owned? entry, IRootChromeRegionSink sink)
    {
        if (_retired) return;
        var index = (int)kind;
        if (_owned[index] is { } current && !ReferenceEquals(current, entry)) current.Rects = null;
        else _owned[index] = new Owned(sink, null);
        if (_active) Request();
        else ClearKind(kind, _generation);
    }

    private void ClearKind(RootChromeRegionKind kind, long generation)
    {
        var index = (int)kind;
        if (!IsCurrentClear(generation) || _owned[index] is not { } owned) return;
        _owned[index] = null;
        owned.Sink.Clear(kind);
        if (IsCurrentClear(generation) || _retired) return;

        // A newer activation/publication re-entered during this clear; if it already wrote this kind, the clear may
        // have landed after it. Only ownership on the affected sink becomes unknown, even if retained while unloaded.
        // Active owners schedule repair; inactive owners keep unknown content for the next activation.
        if (_owned[index] is { } newer && ReferenceEquals(newer.Sink, owned.Sink))
        {
            newer.Rects = null;
            if (_active) Request();
        }
    }
}

/// <summary>Physical-pixel geometry, same rounding as the replaced bar (Convert.ToInt32 of DIP × scale).</summary>
internal static class RootChromeGeometry
{
    internal static RootChromeRect? ToPhysical(double x, double y, double width, double height, double scale)
    {
        if (!(scale > 0) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0) return null;

        // Clip to the client origin instead of dropping a partly scrolled-out element, and round the edges
        // (not the size) so adjacent regions never leave a one-pixel gap at fractional scales.
        var left = Convert.ToInt32(Math.Max(0, x) * scale);
        var top = Convert.ToInt32(Math.Max(0, y) * scale);
        var right = Convert.ToInt32((x + width) * scale);
        var bottom = Convert.ToInt32((y + height) * scale);
        return right > left && bottom > top ? new RootChromeRect(left, top, right - left, bottom - top) : null;
    }
}

/// <summary>
/// Title/search sharing in the main bar, matching the replaced bar's compact display mode: when the search box no
/// longer fits beside the title, the title steps aside; it comes back only once the bar has grown by the missing
/// width, so a one-pixel resize can't flip it back and forth.
/// </summary>
internal static class RootChromeCompactState
{
    internal static (bool Compact, double Threshold) Next(bool compact, double threshold, double barWidth,
        double contentAvailable, double contentRequired)
    {
        if (!compact)
        {
            return contentRequired > contentAvailable + 0.5
                ? (true, barWidth + (contentRequired - contentAvailable))
                : (false, 0);
        }

        return barWidth >= threshold ? (false, 0) : (true, threshold);
    }
}

/// <summary>Writes the main window's regions through the app window's non-client pointer source.</summary>
internal sealed class NonClientRegionSink(WindowId windowId) : IRootChromeRegionSink
{
    internal WindowId WindowId => windowId;

    // One native call per member; the authority orders them (Passthrough then Caption; clears Caption first) and
    // re-checks its generation between them.
    public void Set(RootChromeRegionKind kind, RootChromeRect[] rects)
        => InputNonClientPointerSource.GetForWindowId(windowId).SetRegionRects(ToNative(kind), ToNative(rects));

    public void Clear(RootChromeRegionKind kind)
        => InputNonClientPointerSource.GetForWindowId(windowId).ClearRegionRects(ToNative(kind));

    private static NonClientRegionKind ToNative(RootChromeRegionKind kind)
        => kind == RootChromeRegionKind.Caption ? NonClientRegionKind.Caption : NonClientRegionKind.Passthrough;

    private static RectInt32[] ToNative(RootChromeRect[] rects)
        => rects.Select(rect => new RectInt32(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
}
