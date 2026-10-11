using System.ComponentModel;
using BiliCopilot.UI.Classic.Input;
using BiliCopilot.UI.Forms;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Richasy.WinUIKernel.Share.Base;
using Windows.Graphics;

namespace BiliCopilot.UI.Classic.Hosting;

public sealed class ClassicPlayerWindow : Window, ITipWindow
{
    private readonly Action<ClassicPlayerWindow> _unregister;
    private readonly Action<Exception> _reportFailure;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClassicPlayerContent? _view;
    private ClassicInputBridge? _input;
    private Action? _detachPanelStateChanged;
    private ClassicPlaybackSession? _session;
    private IClassicPlaybackBackend? _backendOwner;
    private bool _backendOwnerReleased;
    private Action? _releaseSurface;
    private volatile Task? _closeTask;
    private readonly object _closeSync = new();
    private bool _destroying;
    private bool _fullWindow;
    private bool _active = true;
    private readonly SemaphoreSlim _dialogs = new(1, 1);
    private ContentDialog? _activeDialog;
    public ClassicDisplayMode DisplayMode { get; private set; }
    public ClassicPlaybackSession? Session => _session;
    public bool IsClosing => _closeTask is not null;
    public bool HasCleanupOwner => _session is not null || _backendOwner is not null;
    public bool ReleaseConfirmed => _session?.BackendReleaseConfirmed ?? (_backendOwner is null || _backendOwnerReleased);

    public ClassicPlayerWindow(Action<ClassicPlayerWindow> unregister, Action<Exception> reportFailure)
    {
        _unregister = unregister;
        _reportFailure = reportFailure;
        Title = "哔哩助理";
        AppWindow.Resize(new SizeInt32(1100, 720));
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnWindowChanged;
        Activated += OnActivated;
    }

    public void Attach(ClassicPlaybackSession session, ClassicPlayerContent content, Action? releaseSurface)
    {
        if (_view is not null || (_session is not null && !ReferenceEquals(_session, session)))
            throw new InvalidOperationException("Classic host is already attached.");
        _session = session;
        _view = content;
        _releaseSurface = releaseSurface;
        Content = content.Root;
        content.Root.Loaded += OnLoaded;
        _input = new ClassicInputBridge(this, session.Input, content, _reportFailure);
        _detachPanelStateChanged = content.SubscribePanelStateChanged?.Invoke(OnPanelStateChanged);
        session.Input.HostCommands = new ClassicInputHostCommands(
            new AsyncRelayCommand(() => SetDisplayModeAsync(DisplayMode == ClassicDisplayMode.FullScreen ? ClassicDisplayMode.Default : ClassicDisplayMode.FullScreen), CanInput),
            new AsyncRelayCommand(() => SetDisplayModeAsync(ClassicDisplayMode.Default), CanInput),
            new AsyncRelayCommand(() => SetDisplayModeAsync(DisplayMode == ClassicDisplayMode.CompactOverlay ? ClassicDisplayMode.Default : ClassicDisplayMode.CompactOverlay), CanInput));
        session.PropertyChanged += OnSessionChanged;
    }
    private bool CanInput() => _session is not null && !_session.IsClosing && _closeTask is null;
    public void AdoptSession(ClassicPlaybackSession session, Action? releaseSurface)
    {
        if (_session is not null) throw new InvalidOperationException("Classic host already owns a session.");
        _session = session;
        _releaseSurface = releaseSurface;
    }
    public void AdoptBackend(IClassicPlaybackBackend backend, Action? releaseSurface)
    {
        if (_backendOwner is not null) throw new InvalidOperationException("Classic host already owns a backend.");
        _backendOwner = backend;
        _releaseSurface = releaseSurface;
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => _loaded.TrySetResult();
    public async Task ShowAsync(ClassicDisplayMode mode)
    {
        Activate();
#pragma warning disable VSTHRD003 // Loaded is completed by the window event while this await yields the UI dispatcher; no host or backend gate is held.
        await _loaded.Task;
#pragma warning restore VSTHRD003
        if (_closeTask is not null) throw new OperationCanceledException();
        await SetDisplayModeAsync(mode);
    }
    public Task SetDisplayModeAsync(ClassicDisplayMode mode)
    {
        if (_view is null || _closeTask is not null) throw new InvalidOperationException("Classic host is not available.");
        if (mode == ClassicDisplayMode.FullWindow && _view.SetFullWindow is null)
            throw new NotSupportedException("当前 Classic 前端尚未提供全窗口布局。");
        switch (mode)
        {
            case ClassicDisplayMode.Default:
            case ClassicDisplayMode.NewWindow: // This host is already an independently registered new window.
            case ClassicDisplayMode.FullWindow:
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                break;
            case ClassicDisplayMode.FullScreen:
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                break;
            case ClassicDisplayMode.CompactOverlay:
                AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
                break;
            default: throw new NotSupportedException("未知的 Classic 显示模式。");
        }
        _fullWindow = mode == ClassicDisplayMode.FullWindow;
        _view.SetFullWindow?.Invoke(_fullWindow);
        DisplayMode = mode;
        return Task.CompletedTask;
    }

    public async Task<bool> ConfirmAsync(string title, string disclosure, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _dialogs.WaitAsync(token);
        try
        {
        if (_view?.Root.XamlRoot is null || _closeTask is not null) return false;
        await RefreshInputContextAsync(true);
        var dialog = new ContentDialog
        {
            XamlRoot = _view.Root.XamlRoot, Title = title, Content = disclosure,
            PrimaryButtonText = "允许本次", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        _activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            token.ThrowIfCancellationRequested();
            return _closeTask is null && result == ContentDialogResult.Primary;
        }
        finally { _activeDialog = null; await RefreshInputContextAsync(false); }
        }
        finally { _dialogs.Release(); }
    }

    public async Task ShowTipAsync(string text, InfoType type = InfoType.Error)
    {
        await _dialogs.WaitAsync();
        try
        {
            if (_view?.Root.XamlRoot is null || _closeTask is not null) return;
            await RefreshInputContextAsync(true);
            _activeDialog = new ContentDialog { XamlRoot = _view.Root.XamlRoot,
                Title = "Classic 播放提示", Content = text, CloseButtonText = "关闭" };
            await _activeDialog.ShowAsync();
        }
        finally { _activeDialog = null; _dialogs.Release(); await RefreshInputContextAsync(false); }
    }

    public Task RefreshInputContextAsync(bool forceBlocked = false) => _session?.Input.SetContextAsync(_active,
        forceBlocked || _closeTask is not null || (_view?.IsInputBlocked?.Invoke() ?? false)) ?? Task.CompletedTask;
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _active = args.WindowActivationState != WindowActivationState.Deactivated;
        Observe(RefreshInputContextAsync());
    }
    private void OnPanelStateChanged(object? sender, EventArgs args)
    {
        // Read the window's actual activation state; never reactivate input from a view callback.
        Observe(RefreshInputContextAsync());
    }
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Layout/XamlRoot changes size the dedicated native slot, including per-monitor scale changes.
        if (_view?.InputSurface is ClassicIslandSurface island) island.UpdateBounds();
    }
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_session?.IsClosing == true && _closeTask is null) Observe(CloseAsync());
    }
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_destroying) return;
        args.Cancel = true;
        Observe(CloseAsync());
    }

    public Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (_closeSync)
        {
            if (_closeTask is not null && (!_closeTask.IsCompleted || _destroying || _closeTask.IsCompletedSuccessfully)) return _closeTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task; // Publish before callbacks can reenter.
        }
        if (DispatcherQueue.HasThreadAccess) Observe(CloseCoreAsync(completion));
        else if (!DispatcherQueue.TryEnqueue(() => Observe(CloseCoreAsync(completion))))
            completion.TrySetException(new InvalidOperationException("Classic 窗口调度器已关闭，无法安全释放播放表面。"));
        return completion.Task;
    }
    private async Task CloseCoreAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { _activeDialog?.Hide(); } catch (Exception ex) { failures.Add(ex); }
        try
        {
            if (_session is not null) await _session.CloseAsync();
            else if (_backendOwner is not null)
            {
                await _backendOwner.DisposeAsync();
                _backendOwnerReleased = true;
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            // The registered host itself retains the session/backend and surface for user retry.
        }
        finally
        {
            if (ReleaseConfirmed)
            {
            // Nonfatal input/history errors do not prevent destruction once engine release is proven.
            try { _detachPanelStateChanged?.Invoke(); _detachPanelStateChanged = null; }
            catch (Exception ex) { failures.Add(ex); }
            try { _input?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
            try { _releaseSurface?.Invoke(); } catch (Exception ex) { failures.Add(ex); }
            try { _view?.Detach?.Invoke(); } catch (Exception ex) { failures.Add(ex); }
            try
            {
                if (_view is not null) _view.Root.Loaded -= OnLoaded;
                AppWindow.Closing -= OnClosing;
                AppWindow.Changed -= OnWindowChanged;
                Activated -= OnActivated;
            }
            catch (Exception ex) { failures.Add(ex); }
            try { _unregister(this); } catch (Exception ex) { failures.Add(ex); }
            _destroying = true;
            _loaded.TrySetCanceled();
            try { Close(); } catch (Exception ex) { failures.Add(ex); }
            }
        }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Classic 窗口关闭未完整成功。", failures));
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Single-await host observer yields the UI dispatcher without holding a gate; the observed close/input operation never depends on this observer.")]
    private async void Observe(Task task)
    {
        try { await task; } catch (Exception ex) { _reportFailure(ex); }
    }
}
