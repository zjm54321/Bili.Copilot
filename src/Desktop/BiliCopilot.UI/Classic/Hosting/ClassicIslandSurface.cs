using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BiliCopilot.UI.Classic.Hosting;

/// <summary>Dedicated native render child. Its XAML slot determines pixel bounds, not a global player.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2216:Disposable types should declare finalizer", Justification = "HWND destruction requires the owning UI thread and confirmed backend release; a finalizer cannot satisfy either precondition. The registered host retains and explicitly disposes this surface.")]
public sealed partial class ClassicIslandSurface : ContentControl, IDisposable
{
    private readonly Action<Exception> _reportFailure;
    private readonly SubclassProcedure _procedure;
    private nint _handle;
    private bool _disposed;
    private XamlRoot? _root;
    public nint RenderHwnd => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(ClassicIslandSurface));

    public ClassicIslandSurface(nint parent, Action<Exception> reportFailure)
    {
        _reportFailure = reportFailure;
        IsTabStop = true;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _procedure = WindowProcedure;
        // WS_CHILD | WS_CLIPSIBLINGS; hidden until the frontend's slot is laid out.
        _handle = CreateWindowExW(0, "STATIC", string.Empty, 0x44000000, 0, 0, 1, 1, parent, 0, 0, 0);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowSubclass(_handle, _procedure, 1, 0))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(_handle);
            _handle = 0;
            throw new Win32Exception(error);
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root = XamlRoot;
        if (_root is not null) _root.Changed += OnRootChanged;
        UpdateBounds();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = null;
        if (!_disposed) ShowWindow(_handle, 0);
        // Unloaded does NOT destroy a surface still owned by a backend.
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();
    private void OnLayoutUpdated(object? sender, object args) => UpdateBounds();
    public void UpdateBounds()
    {
        if (_disposed || !IsLoaded || XamlRoot?.Content is not UIElement root) return;
        var scale = XamlRoot.RasterizationScale;
        var origin = TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);
        if (width <= 0 || height <= 0 || Visibility != Visibility.Visible) { ShowWindow(_handle, 0); return; }
        if (!SetWindowPos(_handle, 0, (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            width, height, 0x0014 | 0x0040)) // NOZORDER | NOACTIVATE | SHOWWINDOW
            _reportFailure(new Win32Exception(Marshal.GetLastWin32Error()));
    }
    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Native child never owns input: hit-test through to the one XAML input surface.
        if (message == 0x0084) return -1; // WM_NCHITTEST -> HTTRANSPARENT (same UI thread).
        return DefSubclassProc(window, message, wParam, lParam);
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1065:Do not raise exceptions in unexpected locations", Justification = "A failed DestroyWindow cannot be reported as successful disposal. The host must receive the failure while the HWND and delegate remain owned for explicit UI-thread retry.")]
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            LayoutUpdated -= OnLayoutUpdated;
            if (_root is not null) _root.Changed -= OnRootChanged;
            if (_handle != 0)
            {
                RemoveWindowSubclass(_handle, _procedure, 1);
                if (!DestroyWindow(_handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _handle = 0;
            }
            _disposed = true;
        }
        finally { GC.KeepAlive(_procedure); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
