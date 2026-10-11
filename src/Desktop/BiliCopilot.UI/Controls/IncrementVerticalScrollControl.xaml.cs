using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Base;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls;

public sealed partial class IncrementVerticalScrollControl : LayoutUserControlBase
{
    public static readonly DependencyProperty ElementProperty =
        DependencyProperty.Register(nameof(Element), typeof(object), typeof(IncrementVerticalScrollControl), new PropertyMetadata(default));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(IncrementVerticalScrollControl), new PropertyMetadata(default));

    public static readonly DependencyProperty RefreshCommandProperty =
        DependencyProperty.Register(nameof(RefreshCommand), typeof(IAsyncRelayCommand), typeof(IncrementVerticalScrollControl), new PropertyMetadata(default, OnRefreshCommandChanged));

    private bool _isControlLoaded;
    private bool _isCheckPending;
    private long _loadGeneration;
    private long _refreshGeneration;
    private RefreshRequest? _activeRefreshRequest;
    private RefreshContainer? _refreshContainer;

    public IncrementVerticalScrollControl()
    {
        InitializeComponent();

        // The shared base detaches its Loaded/Unloaded hooks after the first unload,
        // so this control manages its repeatable lifecycle explicitly.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public object Element
    {
        get => (object)GetValue(ElementProperty);
        set => SetValue(ElementProperty, value);
    }

    public ICommand LoadMoreCommand
    {
        get => (ICommand)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public IAsyncRelayCommand? RefreshCommand
    {
        get => (IAsyncRelayCommand?)GetValue(RefreshCommandProperty);
        set => SetValue(RefreshCommandProperty, value);
    }

    public async Task DelayCheckItemsAsync()
    {
        var generation = _loadGeneration;
        var refreshGeneration = _refreshGeneration;
        await Task.Delay(500);
        if (_isControlLoaded && generation == _loadGeneration && refreshGeneration == _refreshGeneration)
        {
            CheckItemsCount();
        }
    }

    public void ResetScrollPosition()
    {
        if (View is null)
        {
            return;
        }

        View.ChangeView(0d, 0d, null);
    }

    public double GetScrollPosition()
    {
        if (View is null)
        {
            return 0;
        }

        return View.VerticalOffset;
    }

    private static void OnRefreshCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (IncrementVerticalScrollControl)d;
        control._refreshGeneration++;
        // Retire only this control's bookkeeping. The old async stack still owns its task/deferral.
        control._activeRefreshRequest = null;
        control._isCheckPending = false;
        control.UpdateRefreshContainer();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isControlLoaded)
        {
            return;
        }

        _isControlLoaded = true;
        ++_loadGeneration;
        View.ViewChanged += OnViewChanged;
        View.SizeChanged += OnViewSizeChanged;
        UpdateRefreshContainer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = false;
        ++_loadGeneration;
        ++_refreshGeneration;
        _activeRefreshRequest = null;
        _isCheckPending = false;
        View.ViewChanged -= OnViewChanged;
        View.SizeChanged -= OnViewSizeChanged;
    }

    private void UpdateRefreshContainer()
    {
        if (Root is null || View is null)
        {
            return;
        }

        if (RefreshCommand is not null)
        {
            if (_refreshContainer is null)
            {
                var container = new RefreshContainer
                {
                    PullDirection = RefreshPullDirection.TopToBottom,
                };
                container.RefreshRequested += OnRefreshRequested;
                Root.Children.Remove(View);
                container.Content = View;
                Root.Children.Add(container);
                _refreshContainer = container;
            }
        }
        else if (_refreshContainer is not null)
        {
            var container = _refreshContainer;
            _refreshContainer = null;
            container.RefreshRequested -= OnRefreshRequested;
            container.Content = null;
            Root.Children.Remove(container);
            Root.Children.Add(View);
        }
    }

    private async void OnRefreshRequested(RefreshContainer sender, RefreshRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        RefreshRequest? request = null;
        try
        {
            request = new RefreshRequest(RefreshCommand, _refreshGeneration, _loadGeneration, _isControlLoaded);
            var command = request.Command;
            if (command is null || !ReferenceEquals(sender, _refreshContainer)
                || !IsCurrentRequest(request) || HasCurrentRefreshRequest)
            {
                return;
            }

            var canExecute = command.IsRunning || command.CanExecute(null);
            // CanExecute can synchronously replace the binding, unload, or start another request.
            if (!ReferenceEquals(sender, _refreshContainer) || !IsCurrentRequest(request)
                || HasCurrentRefreshRequest || (!canExecute && !command.IsRunning))
            {
                return;
            }

            _activeRefreshRequest = request;
            await RunRefreshCommandAsync(command);
        }
        catch (Exception ex)
        {
            // Observe the task here; ICommand.Execute would attach Toolkit's async-void rethrow.
            this.Get<ILogger<IncrementVerticalScrollControl>>().LogError(ex, "触摸下拉刷新失败.");
        }
        finally
        {
            var ownsRequest = request is not null && ReferenceEquals(_activeRefreshRequest, request);
            if (ownsRequest) _activeRefreshRequest = null;
            deferral.Complete();
            // At most one queued recheck, only for the generation whose refresh just ended.
            if (ownsRequest && IsCurrentRequest(request!)) CheckItemsCount();
        }
    }

    private static async Task RunRefreshCommandAsync(IAsyncRelayCommand command)
    {
        if (command.IsRunning)
        {
            // Join the exact running task, never re-dispatch a shared header/VM refresh.
            var executionTask = command.ExecutionTask
                ?? throw new InvalidOperationException("正在刷新的命令未提供执行任务。");
            await executionTask;
            return;
        }

        await command.ExecuteAsync(null);
    }

    private bool IsCurrentRequest(RefreshRequest request)
        => _isControlLoaded && request.IsLoaded && request.Generation == _refreshGeneration
            && request.LoadGeneration == _loadGeneration && ReferenceEquals(request.Command, RefreshCommand);

    private bool HasCurrentRefreshRequest
        => _activeRefreshRequest is { } request && IsCurrentRequest(request);

    private bool IsRefreshRunning => HasCurrentRefreshRequest || RefreshCommand?.IsRunning == true;

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        CheckItemsCount();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 100)
        {
            CheckItemsCount();
        }
    }

    private void CheckItemsCount()
    {
        if (!_isControlLoaded || _isCheckPending || IsRefreshRunning)
        {
            return;
        }

        var generation = _loadGeneration;
        var refreshGeneration = _refreshGeneration;
        _isCheckPending = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // A callback from before unload/rebind must not consume the new owner's slot.
            if (!_isControlLoaded || generation != _loadGeneration || refreshGeneration != _refreshGeneration)
            {
                return;
            }

            _isCheckPending = false;

            // Refresh republishes the first page; a queued incremental check must
            // not admit a load-more while it is running.
            if (IsRefreshRunning)
            {
                return;
            }

            if (View.ExtentHeight - View.ViewportHeight - View.VerticalOffset <= 800)
            {
                LoadMoreCommand?.Execute(default);
            }
        }))
        {
            _isCheckPending = false;
        }
    }

    private sealed record RefreshRequest(IAsyncRelayCommand? Command, long Generation, long LoadGeneration, bool IsLoaded);
}
