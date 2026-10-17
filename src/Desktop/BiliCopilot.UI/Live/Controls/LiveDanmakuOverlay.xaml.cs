#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Live-only scrolling danmaku renderer. Driven by incoming <see cref="ILiveSession.MessageReceived"/>
/// and a monotonic clock (never media position). At most 32 items are on screen; anything that
/// cannot be placed immediately is dropped—there is no backlog queue.
/// </summary>
public sealed partial class LiveDanmakuOverlay : UserControl
{
    private const int MaxActive = 32;
    private const int MaxPendingHandoff = 32;
    private const int MaxChars = 48;
    private const int MaxLanes = 12;
    private const double LaneGap = 28;
    private const double TopPadding = 10;
    private const double LaneAreaRatio = 0.72;
    private const double StaticSeconds = 4.5;

    private readonly ILiveSession _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<Item> _active = new(MaxActive);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RectangleGeometry _clip = new();
    private readonly UISettings _uiSettings = new();
    private readonly SolidColorBrush _textBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _shadowBrush = new(Color.FromArgb(0xB0, 0, 0, 0));

    private bool _attached;
    private bool _renderingHooked;
    private bool _danmakuVisible = true;
    private LiveBinding? _binding;
    private LivePlaybackState _playback;
    private double _width;
    private double _height;
    private int _pendingHandoff;
    private int _snapshotQueued;

    public LiveDanmakuOverlay(ILiveSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        DanmakuCanvas.Clip = _clip; // Never draw outside the video area.
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Attach();
    }

    /// <summary>
    /// View-owned toggle (set by <see cref="LiveView"/>). Hiding clears and stops drawing only;
    /// it has no effect on playback or on the chat connection.
    /// </summary>
    internal bool IsDanmakuVisible
    {
        get => _danmakuVisible;
        set
        {
            if (_danmakuVisible == value) return;
            _danmakuVisible = value;
            DanmakuCanvas.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) ClearAll();
        }
    }

    // ---------------------------------------------------------------- lifecycle

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _session.PropertyChanged += OnSessionPropertyChanged; // Subscribe first ...
        _session.MessageReceived += OnMessageReceived;
        Snapshot(); // ... then read current state.
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.MessageReceived -= OnMessageReceived;
        ClearAll(); // Also unhooks CompositionTarget.Rendering.
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        Detach();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var heightChanged = Math.Abs(e.NewSize.Height - _height) > 0.5;
        _width = e.NewSize.Width;
        _height = e.NewSize.Height;
        _clip.Rect = new Rect(0, 0, Math.Max(0, _width), Math.Max(0, _height));
        if (heightChanged) ClearAll(); // Lanes are height-derived; restart cleanly instead of overlapping.
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            // Safety net only; coalesced so a misbehaving publisher cannot flood the dispatcher.
            if (Interlocked.Exchange(ref _snapshotQueued, 1) == 0
                && !_dispatcher.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _snapshotQueued, 0);
                    if (_attached) Snapshot();
                }))
            {
                Interlocked.Exchange(ref _snapshotQueued, 0);
            }

            return;
        }

        if (_attached) Snapshot();
    }

    private void Snapshot()
    {
        var binding = _session.Binding;
        var playback = _session.PlaybackState;
        if (binding != _binding || playback != LivePlaybackState.Playing)
        {
            // Room/account change or leaving Playing: nothing from the old scope stays on screen.
            ClearAll();
        }

        _binding = binding;
        _playback = playback;
        UpdateStatus(playback);
    }

    private void UpdateStatus(LivePlaybackState playback)
    {
        var hint = playback == LivePlaybackState.Playing ? string.Empty : LiveText.PlaybackHint(playback);
        var visible = hint.Length > 0;
        StatusText.Text = hint;
        StatusCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var spinning = visible && playback is LivePlaybackState.Opening or LivePlaybackState.Idle
            && _uiSettings.AnimationsEnabled;
        StatusRing.IsActive = spinning;
        StatusRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- incoming handoff

    private void OnMessageReceived(object? sender, LiveChatMessage message)
    {
        if (_dispatcher.HasThreadAccess)
        {
            Admit(message);
            return;
        }

        // Safety net only (the session already dispatches): bounded, drop when full.
        if (Interlocked.Increment(ref _pendingHandoff) > MaxPendingHandoff)
        {
            Interlocked.Decrement(ref _pendingHandoff);
            return;
        }

        if (!_dispatcher.TryEnqueue(() =>
            {
                Interlocked.Decrement(ref _pendingHandoff);
                Admit(message);
            }))
        {
            Interlocked.Decrement(ref _pendingHandoff);
        }
    }

    private void Admit(LiveChatMessage message)
    {
        if (!_attached || !_danmakuVisible || !IsLoaded || _playback != LivePlaybackState.Playing) return;
        if (_binding is not { } binding || message.Binding != binding) return;
        if (_active.Count >= MaxActive || _width < 48 || _height < 48) return;

        var text = Normalize(message.Text);
        if (text.Length == 0) return;

        var fontSize = _height < 280 ? 16 : _height < 480 ? 19 : 22;
        var laneHeight = Math.Ceiling(fontSize * 1.5);
        var lanes = (int)Math.Clamp(Math.Floor((_height * LaneAreaRatio - TopPadding) / laneHeight), 1, MaxLanes);
        var host = CreateVisual(text, fontSize);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(host.DesiredSize.Width);
        if (width <= 0) return;

        var now = _clock.Elapsed.TotalSeconds;
        var reduced = !_uiSettings.AnimationsEnabled;
        var speed = Math.Clamp(_width / 8.0, 90, 260); // DIP per second; uniform so lanes never catch up.
        var lane = reduced ? FindStaticLane(lanes) : FindMovingLane(lanes, speed);
        if (lane < 0) return; // Screen full: drop, never queue.

        var startX = reduced ? Math.Max(0, (_width - width) / 2) : _width;
        var transform = new TranslateTransform { X = startX };
        host.RenderTransform = transform;
        Canvas.SetTop(host, TopPadding + (lane * laneHeight));
        DanmakuCanvas.Children.Add(host);
        _active.Add(new Item(host, transform, lane, width, startX, speed, now, reduced));
        HookRendering();
    }

    private int FindMovingLane(int lanes, double speed)
    {
        for (var lane = 0; lane < lanes; lane++)
        {
            var free = true;
            foreach (var item in _active)
            {
                if (item.Lane != lane) continue;
                if (item.Static)
                {
                    free = false;
                    break;
                }

                var tail = item.X + item.Width;
                // Needs a full gap behind the previous tail, and must not overtake it if it is slower.
                if (tail + LaneGap > _width
                    || (speed > item.Speed && _width / speed < tail / item.Speed))
                {
                    free = false;
                    break;
                }
            }

            if (free) return lane;
        }

        return -1;
    }

    private int FindStaticLane(int lanes)
    {
        for (var lane = 0; lane < lanes; lane++)
        {
            var used = false;
            foreach (var item in _active)
            {
                if (item.Lane == lane)
                {
                    used = true;
                    break;
                }
            }

            if (!used) return lane;
        }

        return -1;
    }

    private Grid CreateVisual(string text, double fontSize)
    {
        var shadow = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = _shadowBrush,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = false,
            Margin = new Thickness(1.5, 1.5, 0, 0),
        };
        var main = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = false,
        };
        var host = new Grid { IsHitTestVisible = false, Opacity = 0.94 };
        host.Children.Add(shadow);
        host.Children.Add(main);
        return host;
    }

    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (text.Length <= MaxChars) return text;
        var cut = MaxChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), "…");
    }

    // ---------------------------------------------------------------- frame loop

    private void HookRendering()
    {
        if (_renderingHooked || _active.Count == 0) return;
        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        _renderingHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_attached || !_danmakuVisible)
        {
            ClearAll();
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var item = _active[i];
            var elapsed = now - item.Start;
            if (item.Static)
            {
                if (elapsed >= StaticSeconds) RemoveAt(i);
                continue;
            }

            var x = item.StartX - (item.Speed * elapsed);
            if (x + item.Width < 0)
            {
                RemoveAt(i);
                continue;
            }

            item.X = x;
            item.Transform.X = x;
        }

        if (_active.Count == 0) UnhookRendering();
    }

    private void RemoveAt(int index)
    {
        DanmakuCanvas.Children.Remove(_active[index].Host);
        _active.RemoveAt(index);
    }

    private void ClearAll()
    {
        UnhookRendering();
        _active.Clear();
        DanmakuCanvas.Children.Clear();
    }

    private sealed class Item
    {
        public Item(Grid host, TranslateTransform transform, int lane, double width, double startX, double speed, double start, bool isStatic)
        {
            Host = host;
            Transform = transform;
            Lane = lane;
            Width = width;
            StartX = startX;
            X = startX;
            Speed = speed;
            Start = start;
            Static = isStatic;
        }

        public Grid Host { get; }

        public TranslateTransform Transform { get; }

        public int Lane { get; }

        public double Width { get; }

        public double StartX { get; }

        public double X { get; set; }

        public double Speed { get; }

        public double Start { get; }

        public bool Static { get; }
    }
}
