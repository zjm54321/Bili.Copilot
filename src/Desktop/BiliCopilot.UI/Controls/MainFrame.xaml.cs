// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class MainFrame : LayoutUserControlBase, IPageActivationHost
{
    private readonly PageArrivalTransition _transition = new();
    private bool _isSwitching;

    public MainFrame()
    {
        InitializeComponent();
        ActivationScope = new PageActivationScope();
        ActivationScope.ActivationChanged += OnActivationChanged;
        Loaded += OnFrameLoaded;
        Unloaded += OnFrameUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnFrameVisibilityChanged);
    }

    internal PageActivationScope ActivationScope { get; }

    PageActivationScope IPageActivationHost.ActivationScope => ActivationScope;

    public object GetCurrentContent()
    {
        // Returns the currently visible cached page.
        foreach (var child in RootGrid.Children)
        {
            if (child.Visibility == Visibility.Visible)
            {
                return child;
            }
        }

        return null!;
    }

    public void NavigateTo(Type pageType, object? parameter = null)
    {
        if (pageType is null)
        {
            return;
        }

        var previous = GetCurrentContent() as UIElement;

        // An explicit main navigation is a forward step: it supersedes any pending back arrival.
        ActivationScope.WithdrawMainBackArrival();

        // Restore the prior one-shot and deactivate every page synchronously before selection/collapse.
        _transition.Cancel();
        _isSwitching = true;
        ActivationScope.NotifyChanged();

        UIElement? targetElement;
        try
        {
            targetElement = SelectPage(pageType, parameter);
        }
        finally
        {
            _isSwitching = false;
        }

        if (targetElement is not null
            && !ReferenceEquals(previous, targetElement)
            && Visibility == Visibility.Visible)
        {
            _transition.Start(targetElement, PageArrivalKind.Forward);
        }

        ActivationScope.NotifyChanged();
    }

    bool IPageActivationHost.OwnsPage(UIElement page)
        => RootGrid.Children.Contains(page);

    bool IPageActivationHost.IsPageActive(UIElement page)
        => IsLoaded
            && !_isSwitching
            && Visibility == Visibility.Visible
            && !ActivationScope.IsMainCovered
            && page.Visibility == Visibility.Visible
            && RootGrid.Children.Contains(page);

    private UIElement? SelectPage(Type pageType, object? parameter)
    {
        UIElement? targetElement = null;

        // Reuse an existing cached page of the same type.
        foreach (var item in RootGrid.Children)
        {
            if (item.GetType() == pageType)
            {
                targetElement = item;
                if (item is IParameterPage mainFramePage)
                {
                    mainFramePage.SetParameter(parameter);
                }

                if (item is IInitializePage initPage)
                {
                    initPage.Initialize();
                }
            }
        }

        if (targetElement == null)
        {
            // Not cached yet: create the page instance once.
            var instance = Activator.CreateInstance(pageType);
            if (instance is UIElement element)
            {
                targetElement = element;
                if (element is IParameterPage mainFramePage)
                {
                    mainFramePage.SetParameter(parameter);
                }

                // Arrival motion is owned by PageArrivalTransition; no implicit show/hide animations
                // are attached so there is a single opacity writer.
                RootGrid.Children.Add(element);
            }
            else
            {
                // Not a UIElement: nothing to show.
                return null;
            }
        }

        // Visibility selection remains authoritative.
        foreach (var child in RootGrid.Children)
        {
            child.Visibility = child == targetElement ? Visibility.Visible : Visibility.Collapsed;
            if (child.Visibility == Visibility.Collapsed && child is ICancelPageViewModel cancelPageViewModel)
            {
                cancelPageViewModel.CancelLoading();
            }
        }

        return targetElement;
    }

    private void OnFrameLoaded(object sender, RoutedEventArgs e)
    {
        _transition.Attach(DispatcherQueue);
        ActivationScope.NotifyChanged();
    }

    private void OnFrameUnloaded(object sender, RoutedEventArgs e)
    {
        ActivationScope.WithdrawMainBackArrival();
        _transition.Detach();
        ActivationScope.NotifyChanged();
    }

    /// <summary>
    /// Consumes the overlay's one-shot back-arrival intent only at the moment the selected
    /// main page is actually visible and uncovered. Hidden pages are never animated.
    /// </summary>
    private void OnActivationChanged(object? sender, EventArgs e)
    {
        if (!ActivationScope.IsMainBackArrivalPending || _isSwitching)
        {
            return;
        }

        if (GetCurrentContent() is not UIElement page)
        {
            // Nothing to arrive at; drop the intent so it cannot fire on a later unrelated show.
            ActivationScope.WithdrawMainBackArrival();
            return;
        }

        if (((IPageActivationHost)this).IsPageActive(page) && ActivationScope.TryConsumeMainBackArrival())
        {
            // Back arrival is fade-only, same source curve as overlay back steps.
            _transition.Start(page, PageArrivalKind.Back);
        }
    }

    private void OnFrameVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Visibility != Visibility.Visible)
        {
            // Never keep animating a hidden frame.
            _transition.Cancel();
        }

        ActivationScope.NotifyChanged();
    }
}
