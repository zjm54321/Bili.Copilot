#nullable enable

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEx;

namespace BiliCopilot.UI.Live.Hosting;

/// <summary>
/// Owns only the native render child and its overlay island. The Grid is an outer-tree layout
/// anchor, not an engine or an overlay. Construct and adopt before Initialize allocates HWNDs.
/// </summary>
public sealed class LiveIslandSurface
{
    private WindowEx? _owner;
    private nint _handle;
    private DesktopWindowXamlSource? _source;
    private LivePresentationInput? _presentationInput;
    private ContentControl? _pictureInput;
    private XamlRoot? _root;
    private TaskCompletionSource? _ready;
    private bool _initialized;
    private bool _attached;
    private bool _released;
    private Task? _close;
    private bool _layoutFailed;
    private bool _hasBounds;
    private bool _isPresented; // Explicit host latch; layout/readiness callbacks cannot make a hidden host visible.
    private readonly Action<string> _report;
    private RectInt32? _lastBounds;
    private RectInt32? _lastClip;

    public LiveIslandSurface(Action<string> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Anchor = new Grid();
        TransportOverlayHost = new Grid(); // No background: only the transport card claims input.
    }

    public Grid Anchor { get; }
    internal Grid TransportOverlayHost { get; }
    public nint RenderHwnd => _handle != 0 ? _handle : throw new InvalidOperationException("Live render child is unavailable.");
    public bool ReleaseConfirmed => _released;

    /// <summary>Owner-thread presentation only; hiding never disposes the engine-owned render target.</summary>
    public void SetPresented(bool isPresented)
    {
        CheckUi();
        _isPresented = isPresented && _close is null && !_layoutFailed;
        if (!_isPresented) Hide(); // Native hide is synchronous, before the caller collapses XAML.
        else SafeUpdate();
    }

    public void Initialize(WindowEx owner)
    {
        if (_initialized || _close is not null) throw new InvalidOperationException("Live surface initialization cannot be repeated.");
        _owner = owner;
        CheckUi();
        _initialized = true; // Partial initialization is an owned terminal attempt, never overwritten.
        // STATIC: WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS; hidden until actual layout.
        _handle = CreateWindowExW(0, "STATIC", string.Empty, 0x46000000, 0, 0, 1, 1,
            owner.GetWindowHandle(), 0, 0, 0);
        if (_handle == 0) throw new InvalidOperationException("Live render child creation failed.");
        _source = new DesktopWindowXamlSource(); // Store before fallible Initialize/Content assignment.
        _source.Initialize(Win32Interop.GetWindowIdFromWindow(_handle));
        Anchor.Loaded += OnLoaded;
        Anchor.Unloaded += OnUnloaded;
        Anchor.LayoutUpdated += OnLayoutUpdated;
    }

    public void AttachOverlay(UIElement overlay)
    {
        CheckUi();
        if (_attached || _source is null || _close is not null) throw new InvalidOperationException("Live overlay attachment is unavailable.");
        _attached = true;
        // The designer-created instance moves into the child island. No second renderer and no
        // assumption that a sibling in the outer XAML tree can paint above a native video HWND.
        // Keep input inside the same child island as the picture; an outer XAML sibling cannot
        // receive pointer events over the native HWND. The danmaku itself remains non-interactive.
        _pictureInput = new ContentControl
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsTabStop = true,
            Content = overlay,
        };
        var root = new Grid(); // Transparent outside its children; never an opaque video backdrop.
        root.Children.Add(_pictureInput);
        root.Children.Add(TransportOverlayHost); // Top layer; empty space passes through to the picture.
        _source.Content = root;
        UpdateBounds();
    }

    internal void AttachPresentationInput(Func<bool> canDrag, Action move, Func<bool> exitFullScreen,
        Action pointerActivity)
    {
        CheckUi();
        if (_source?.Content is not UIElement root || _pictureInput is null)
            throw new InvalidOperationException("Live island input is unavailable.");
        DetachPresentationInput();
        // Only the picture handles double-click/drag. Buttons/flyouts in the sibling transport
        // layer keep their own input; Esc is observed across the entire island, including them.
        _presentationInput = new LivePresentationInput(_pictureInput, canDrag, move, exitFullScreen,
            pointerActivity, exitFullScreenOnDoubleTap: true, keyboardRoot: root);
    }

    internal void CancelPresentationInput() => _presentationInput?.CancelDrag();

    internal void DetachPresentationInput()
    {
        _presentationInput?.Detach();
        _presentationInput = null;
    }

    public async Task ReadyAsync(CancellationToken token)
    {
        CheckUi();
        if (_layoutFailed || !_attached || _close is not null) throw new InvalidOperationException("Live layout cannot become ready.");
        if (_ready is not null) throw new InvalidOperationException("Live layout readiness is already owned.");
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(() => _ready.TrySetCanceled(token));
        UpdateBounds();
        // T3-01: Layout events complete _ready while the dispatcher is alive; this wait does not abandon backend/native ownership.
        #pragma warning disable VSTHRD003
        await _ready.Task; // Only a layout-event waiter; no backend/native task is abandoned.
        #pragma warning restore VSTHRD003
        token.ThrowIfCancellationRequested();
        UpdateBounds();
        if (!_hasBounds) throw new InvalidOperationException("Live video layout is unavailable.");
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => SafeUpdate();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DetachRoot();
        Hide(); // Reparenting/unload never destroys an engine-owned render target.
    }
    private void OnLayoutUpdated(object? sender, object args) => SafeUpdate();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SafeUpdate();

    public void SafeUpdate()
    {
        if (_close is not null || _layoutFailed) return;
        try { UpdateBounds(); }
        catch
        {
            _layoutFailed = true;
            Hide();
            _ready?.TrySetException(new InvalidOperationException("Live video layout failed."));
            _report("Live video layout failed; playback surface has been hidden.");
        }
    }

    private void UpdateBounds()
    {
        CheckUi();
        if (_close is not null || _layoutFailed || !_attached || _source?.SiteBridge is null) return;
        if (!_isPresented) { Hide(); return; }
        if (!ReferenceEquals(_root, Anchor.XamlRoot))
        {
            DetachRoot();
            _root = Anchor.XamlRoot;
            if (_root is not null) _root.Changed += OnRootChanged;
        }
        if (!TryBounds(out var bounds, out var clip)) { Hide(); return; }
        var changed = _lastBounds?.Equals(bounds) != true;
        if (changed) ShowWindow(_handle, 0);
        if (_lastClip?.Equals(clip) != true)
        {
            var region = CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
            if (region == 0) throw new InvalidOperationException("Live clipping region creation failed.");
            if (SetWindowRgn(_handle, region, true) == 0)
            {
                DeleteObject(region); // On success Windows owns the region; never delete it twice.
                throw new InvalidOperationException("Live clipping region attachment failed.");
            }
            _lastClip = clip;
        }
        if (changed)
        {
            _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, bounds.Width, bounds.Height));
            _source.SiteBridge.MoveInZOrderAtTop();
            // Native layout may reenter a host hide/close callback. Recheck before showing.
            if (!_isPresented || _close is not null) { Hide(); return; }
            // Region and island size are ready before SHOWWINDOW. Never TOPMOST.
            if (!SetWindowPos(_handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0050))
                throw new InvalidOperationException("Live render child positioning failed.");
            _lastBounds = bounds;
        }
        if (!_isPresented || _close is not null) { Hide(); return; }
        _hasBounds = true;
        _ready?.TrySetResult();
    }

    private bool TryBounds(out RectInt32 bounds, out RectInt32 clip)
    {
        bounds = clip = default;
        if (_owner?.Content is not FrameworkElement content || !Anchor.IsLoaded
            || Anchor.XamlRoot is null || Anchor.XamlRoot != content.XamlRoot
            || !_owner.AppWindow.IsVisible || IsIconic(_owner.GetWindowHandle())) return false;
        var scale = Anchor.XamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0 || Anchor.ActualWidth <= 0 || Anchor.ActualHeight <= 0) return false;
        var origin = Anchor.TransformToVisual(content).TransformPoint(new Point());
        var extent = Anchor.TransformToVisual(content).TransformPoint(new Point(Anchor.ActualWidth, Anchor.ActualHeight));
        // LiveView uses translation-only layout. Do not pretend rotated/scaled XAML has HWND parity.
        if (Math.Abs(extent.X - origin.X - Anchor.ActualWidth) > 0.01
            || Math.Abs(extent.Y - origin.Y - Anchor.ActualHeight) > 0.01) return false;
        var rect = new Rect(origin.X, origin.Y, extent.X - origin.X, extent.Y - origin.Y);
        var visible = rect;
        DependencyObject? node = Anchor;
        while (node is FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0) return false;
            var local = element.TransformToVisual(content);
            var limits = local.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            visible.Intersect(limits);
            if (element.Clip is RectangleGeometry rectangle)
                visible.Intersect(local.TransformBounds(rectangle.Rect));
            else if (element.Clip is not null) return false; // Unsupported shape: hide, never bleed.
            if (ReferenceEquals(element, content)) break;
            node = VisualTreeHelper.GetParent(element);
        }
        if (!ReferenceEquals(node, content) || visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        var x = Pixel(rect.X * scale); var y = Pixel(rect.Y * scale);
        var width = Pixel(rect.Width * scale); var height = Pixel(rect.Height * scale);
        if (width <= 0 || height <= 0) return false;
        bounds = new(x, y, width, height);
        // Round clipping inward; not even a partial native pixel may overlap the controls.
        var left = Math.Clamp((int)Math.Ceiling(visible.Left * scale) - x, 0, width);
        var top = Math.Clamp((int)Math.Ceiling(visible.Top * scale) - y, 0, height);
        var right = Math.Clamp((int)Math.Floor(visible.Right * scale) - x, left, width);
        var bottom = Math.Clamp((int)Math.Floor(visible.Bottom * scale) - y, top, height);
        clip = new(left, top, right - left, bottom - top);
        return clip.Width > 0 && clip.Height > 0;
    }

    private static int Pixel(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new InvalidOperationException("Live layout geometry is invalid.");
        return checked((int)Math.Round(value));
    }
    private void Hide()
    {
        CancelPresentationInput();
        _hasBounds = false;
        _lastBounds = _lastClip = null;
        if (_handle != 0) ShowWindow(_handle, 0);
    }
    private void DetachRoot()
    {
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = null;
    }
    private void CheckUi()
    {
        if (_owner?.DispatcherQueue.HasThreadAccess != true)
            throw new InvalidOperationException("Live surface requires its owning UI dispatcher.");
    }

    /// <summary>
    /// Fallible owner-thread terminal operation, not a best-effort IDisposable contract.
    /// The caller must first drain opening/readiness and prove backend/chat release. Every admitted
    /// call returns the same actual terminal completion, including failure; no native release retry.
    /// An existing completion may be observed from any thread without another native operation.
    /// The caller must retain this object and await that completion before closing its parent HWND.
    /// No finalizer can establish these prerequisites or run this owner-thread operation.
    /// </summary>
    public Task CloseAsync()
    {
        if (Volatile.Read(ref _close) is { } existing) return existing;
        // Wrong-thread first calls are rejected before admission, including partial initialization.
        if (_owner is not null) CheckUi();
        else if (!Anchor.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Live surface requires its owning UI dispatcher.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _close, completion.Task); // Before event detachment or native reentrancy.
        try
        {
            _isPresented = false;
            Hide();
            _ready?.TrySetCanceled();
            DetachPresentationInput();
            Anchor.Loaded -= OnLoaded;
            Anchor.Unloaded -= OnUnloaded;
            Anchor.LayoutUpdated -= OnLayoutUpdated;
            DetachRoot();
            // Do not attempt HWND destruction if island release fails. Retain both fields on any
            // failure, even when island disposal succeeded but the subsequent DestroyWindow failed.
            _source?.Dispose();
            if (_handle != 0 && !DestroyWindow(_handle))
                throw new InvalidOperationException("Live render child release is unconfirmed.");
            _source = null;
            _handle = 0;
            _released = true;
            completion.TrySetResult();
        }
        catch
        {
            completion.TrySetException(new InvalidOperationException("Live surface close is incomplete; ownership is retained."));
        }
        return completion.Task;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}
