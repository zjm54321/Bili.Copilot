#nullable enable

using System.ComponentModel;
using System.Runtime.InteropServices;
using BiliCopilot.UI.Hosting;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace BiliCopilot.UI.Live.Hosting;

public sealed partial class LiveWindow
{
    private PlayerPresentationMode _presentation;
    private PlayerPresentationMode _fullScreenReturn;
    private NormalRestoration? _normalRestoration;
    private bool _changingPresenter;
    private bool _restoreRequested;
    private long _presentationVersion;
    private bool? _pendingBoundsSuppression;
    private RectInt32? _compactBounds;
    private LivePresentationInput? _windowInput;

    public bool IsFullScreen => _presentation == PlayerPresentationMode.FullScreen;
    public bool IsCompactOverlay => _presentation == PlayerPresentationMode.CompactOverlay;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void EnterFullScreen() => SetPresentation(PlayerPresentationMode.FullScreen);
    public void ExitFullScreen()
    {
        if (IsFullScreen) SetPresentation(_fullScreenReturn);
    }

    public void EnterCompactOverlay() => SetPresentation(PlayerPresentationMode.CompactOverlay);
    public void ExitCompactOverlay()
    {
        if (IsCompactOverlay) RestorePresentation();
    }

    public void RestorePresentation()
    {
        CheckUi();
        if (_changingPresenter)
        {
            _restoreRequested = true;
            return;
        }

        if (_normalRestoration is not null && !_parentClosed)
            SetPresentation(PlayerPresentationMode.Normal);
    }

    private bool CanUsePresentation => !_parentClosed && !IsClosing && _isPresented
        && ReferenceEquals(_mainWindow.CurrentEmbeddedLiveOwner, this) && _mainWindow.IsEmbeddedLiveVisible;

    private void SetPresentation(PlayerPresentationMode mode)
    {
        CheckUi();
        if (_changingPresenter || _parentClosed || (mode != PlayerPresentationMode.Normal && !CanUsePresentation)) return;
        if (_presentation == mode && (mode != PlayerPresentationMode.Normal || _normalRestoration is null)) return;
        var window = _mainWindow.AppWindow;
        var version = ++_presentationVersion;
        _changingPresenter = true;
        try
        {
            if (mode != PlayerPresentationMode.Normal && _normalRestoration is null)
            {
                var presenter = window.Presenter as OverlappedPresenter
                    ?? throw new InvalidOperationException("Live requires a restorable main-window presenter.");
                var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!PInvoke.GetWindowPlacement(new(_mainWindow.GetWindowHandle()), ref placement))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading Live window placement failed.");
                _normalRestoration = new(presenter, placement, _mainWindow.MinWidth, _mainWindow.MinHeight,
                    presenter.IsAlwaysOnTop, presenter.HasBorder, presenter.HasTitleBar,
                    window.TitleBar.PreferredHeightOption, _pendingBoundsSuppression ?? _mainWindow.SuppressPlayerBoundsPersistence);
                _pendingBoundsSuppression = null;
            }

            // MainWindow persists all Overlapped resize notifications, including those raised during restoration.
            _mainWindow.SuppressPlayerBoundsPersistence = true;
            _windowInput?.CancelDrag();
            _surface?.CancelPresentationInput();
            if (IsCompactOverlay && window.Presenter.Kind == AppWindowPresenterKind.CompactOverlay)
            {
                var position = window.Position;
                var size = window.Size;
                _compactBounds = new(position.X, position.Y, size.Width, size.Height);
            }
            if (mode == PlayerPresentationMode.FullScreen)
            {
                if (!IsFullScreen) _fullScreenReturn = _presentation;
                window.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else if (mode == PlayerPresentationMode.CompactOverlay)
            {
                _mainWindow.MinWidth = 480;
                _mainWindow.MinHeight = 270;
                var compact = CompactOverlayPresenter.Create();
                compact.InitialSize = CompactOverlaySize.Medium;
                window.SetPresenter(compact);
                if (_compactBounds is { } bounds)
                {
                    var work = (DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest) ?? DisplayArea.Primary).WorkArea;
                    bounds.Width = Math.Min(bounds.Width, work.Width);
                    bounds.Height = Math.Min(bounds.Height, work.Height);
                    bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
                    bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
                    window.MoveAndResize(bounds);
                }
            }
            else if (_normalRestoration is { } saved)
            {
                window.SetPresenter(saved.Presenter);
                _mainWindow.MinWidth = saved.MinWidth;
                _mainWindow.MinHeight = saved.MinHeight;
                saved.Presenter.SetBorderAndTitleBar(saved.HasBorder, saved.HasTitleBar);
                saved.Presenter.IsAlwaysOnTop = saved.TopMost;
                window.TitleBar.PreferredHeightOption = saved.TitleBarHeight;
                var placement = saved.Placement;
                if (!PInvoke.SetWindowPlacement(new(_mainWindow.GetWindowHandle()), in placement))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Restoring Live window placement failed.");
            }

            PublishPresentation(mode);
            RefreshNativeBounds();
            if (mode == PlayerPresentationMode.Normal && _normalRestoration is { } restored)
            {
                _normalRestoration = null;
                _pendingBoundsSuppression = restored.BoundsSuppressed;
                // Let queued native size/position events drain before persistence resumes.
                if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (version == _presentationVersion)
                    {
                        _mainWindow.SuppressPlayerBoundsPersistence = restored.BoundsSuppressed;
                        _pendingBoundsSuppression = null;
                    }
                }))
                {
                    _mainWindow.SuppressPlayerBoundsPersistence = restored.BoundsSuppressed;
                    _pendingBoundsSuppression = null;
                }
            }
        }
        catch
        {
            if (mode != PlayerPresentationMode.Normal) _restoreRequested = true;
            throw;
        }
        finally
        {
            _changingPresenter = false;
            if (_restoreRequested)
            {
                _restoreRequested = false;
                RestorePresentation();
            }
        }
    }

    private void PublishPresentation(PlayerPresentationMode mode)
    {
        _presentation = mode;
        PropertyChanged?.Invoke(this, new(nameof(IsFullScreen)));
        PropertyChanged?.Invoke(this, new(nameof(IsCompactOverlay)));
    }

    private void SynchronizePresenter()
    {
        if (_changingPresenter || _normalRestoration is null || _parentClosed) return;
        var mode = _mainWindow.AppWindow.Presenter.Kind switch
        {
            AppWindowPresenterKind.FullScreen => PlayerPresentationMode.FullScreen,
            AppWindowPresenterKind.CompactOverlay => PlayerPresentationMode.CompactOverlay,
            _ => PlayerPresentationMode.Normal,
        };
        if (mode == PlayerPresentationMode.Normal) RestorePresentation();
        else if (mode != _presentation)
        {
            PublishPresentation(mode);
            RefreshNativeBounds();
        }
    }

    private void RefreshNativeBounds()
    {
        _view?.UpdateLayout();
        _surface?.SafeUpdate();
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_parentClosed && !ReleaseConfirmed) _surface?.SafeUpdate();
        });
    }

    private void AttachPresentationInput()
    {
        if (_view is not null)
        {
            _view.VideoLayoutChanged += OnVideoLayoutChanged;
            _windowInput = new LivePresentationInput(_view, () => CanUsePresentation && IsCompactOverlay,
                BeginCompactMove, ExitFullScreenFromInput, keyboardRoot: _mainWindow.Content as UIElement);
        }
        Surface.AttachPresentationInput(() => CanUsePresentation && IsCompactOverlay,
            BeginCompactMove, ExitFullScreenFromInput, () => _view?.RevealTransport());
    }

    private void OnVideoLayoutChanged(object? sender, EventArgs args) => RefreshNativeBounds();

    private void DetachPresentationInput()
    {
        if (_view is not null) _view.VideoLayoutChanged -= OnVideoLayoutChanged;
        _windowInput?.Detach();
        _windowInput = null;
        _surface?.DetachPresentationInput();
    }

    private void CompletePresentationRestoration()
    {
        if (_normalRestoration is not null)
            throw new InvalidOperationException("Live window presentation restoration is unconfirmed.");
        // Do not let a late restoration callback overwrite a replacement video's persistence lease.
        ++_presentationVersion;
        if (_pendingBoundsSuppression is { } suppressed)
            _mainWindow.SuppressPlayerBoundsPersistence = suppressed;
        _pendingBoundsSuppression = null;
    }

    private bool ExitFullScreenFromInput()
    {
        if (!CanUsePresentation || !IsFullScreen) return false;
        try { ExitFullScreen(); }
        catch (Exception) { Report("Live full-screen restoration failed."); }
        return true;
    }

    private void BeginCompactMove()
    {
        if (!_changingPresenter && CanUsePresentation && IsCompactOverlay)
            LivePresentationInput.MoveWindow(_mainWindow.GetWindowHandle());
    }

    private sealed record NormalRestoration(OverlappedPresenter Presenter, WINDOWPLACEMENT Placement,
        double MinWidth, double MinHeight, bool TopMost, bool HasBorder, bool HasTitleBar,
        TitleBarHeightOption TitleBarHeight, bool BoundsSuppressed);
}
