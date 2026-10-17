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

    public static readonly DependencyProperty ScrollPaddingProperty =
        DependencyProperty.Register(nameof(ScrollPadding), typeof(Thickness), typeof(IncrementVerticalScrollControl), new PropertyMetadata(new Thickness(20, 0, 20, 0)));

    private bool _isControlLoaded;
    private RefreshContainer? _refreshContainer;

    public IncrementVerticalScrollControl()
    {
        InitializeComponent();
        // The shared base detaches its own lifecycle hooks after the first unload.
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

    public Thickness ScrollPadding
    {
        get => (Thickness)GetValue(ScrollPaddingProperty);
        set => SetValue(ScrollPaddingProperty, value);
    }

    /// <summary>x:Bind helper: append failure + retry, hidden while a new append is running.</summary>
    public static Visibility AppendErrorFooterVisibility(bool hasAppendError, bool isAppending)
        => hasAppendError && !isAppending ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: quiet end line; never with an empty list, an error, or a running append.</summary>
    public static Visibility ExhaustedFooterVisibility(bool isExhausted, bool hasAppendError, bool isAppending, int itemCount)
        => isExhausted && !hasAppendError && !isAppending && itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task DelayCheckItemsAsync()
    {
        await Task.Delay(500);
        CheckItemsCount();
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
        => ((IncrementVerticalScrollControl)d).UpdateRefreshContainer();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isControlLoaded)
        {
            return;
        }

        _isControlLoaded = true;
        View.ViewChanged += OnViewChanged;
        View.SizeChanged += OnViewSizeChanged;
        UpdateRefreshContainer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = false;
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
        var command = RefreshCommand;
        try
        {
            if (!_isControlLoaded || command is null || !ReferenceEquals(sender, _refreshContainer))
            {
                return;
            }

            var canExecute = command.IsRunning || command.CanExecute(null);
            // CanExecute may synchronously change the binding or unload this control.
            if (!_isControlLoaded || !ReferenceEquals(command, RefreshCommand)
                || !ReferenceEquals(sender, _refreshContainer) || (!canExecute && !command.IsRunning))
            {
                return;
            }

            await RunRefreshCommandAsync(command);
        }
        catch (Exception ex)
        {
            // Observe the task here; ICommand.Execute would attach Toolkit's async-void rethrow.
            this.Get<ILogger<IncrementVerticalScrollControl>>().LogError(ex, "触摸下拉刷新失败.");
        }
        finally
        {
            // This invocation owns its deferral even if the binding changes or the control unloads.
            deferral.Complete();
        }

        if (_isControlLoaded && ReferenceEquals(command, RefreshCommand) && ReferenceEquals(sender, _refreshContainer))
        {
            CheckItemsCount();
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
        if (!_isControlLoaded)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isControlLoaded && View.ExtentHeight - View.ViewportHeight - View.VerticalOffset <= 1600)
            {
                LoadMoreCommand?.Execute(default);
            }
        });
    }
}
