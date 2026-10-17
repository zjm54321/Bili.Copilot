// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class OverlayFrame : LayoutUserControlBase, IPageActivationHost
{
    private readonly PageArrivalTransition _transition = new();
    private PageActivationScope? _sharedScope;
    private PageActivationScope? _fallbackScope;
    private bool _isSwitching;

    // True between NavigateTo on a hidden overlay and the overlay becoming visible,
    // so the main frame is deactivated before the overlay covers it.
    private bool _isOpenPending;

    public OverlayFrame()
    {
        InitializeComponent();
        Loaded += OnFrameLoaded;
        Unloaded += OnFrameUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnFrameVisibilityChanged);
    }

    PageActivationScope IPageActivationHost.ActivationScope => ResolveScope();

    public List<object> GetBackStack()
        => [.. RootGrid.Children];

    public void ClearBackStack()
    {
        // Restore Y=0/opacity=1 and deactivate before pages are removed.
        // Clearing is not a back step: withdraw any pending main back arrival.
        ResolveScope().WithdrawMainBackArrival();
        _transition.Cancel();
        BeginSwitch();
        try
        {
            RootGrid.Children.Clear();
        }
        finally
        {
            _isSwitching = false;
        }

        _isOpenPending = false;
        EndSwitch();
    }

    public void GoBack()
    {
        var previousTop = RootGrid.Children.Count > 0 ? RootGrid.Children[^1] : null;

        // Only a back step taken while the overlay is actually showing hides the main frame.
        // A back on a temporarily closed overlay (main already visible) is not a main arrival.
        var wasShowing = Visibility == Visibility.Visible;
        _transition.Cancel();
        BeginSwitch();
        try
        {
            if (RootGrid.Children.Count > 0)
            {
                RootGrid.Children.RemoveAt(RootGrid.Children.Count - 1);
            }

            // If pages remain, show the last one.
            if (RootGrid.Children.Count > 0)
            {
                foreach (var child in RootGrid.Children)
                {
                    child.Visibility = Visibility.Collapsed;
                }

                RootGrid.Children[^1].Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isSwitching = false;
        }

        if (RootGrid.Children.Count == 0)
        {
            _isOpenPending = false;
            if (previousTop is not null && wasShowing)
            {
                // One-shot intent: the main frame plays back fade-only when its selected page
                // actually becomes visible/uncovered (RootLayout's IsOverlayOpen binding owns that).
                ResolveScope().OfferMainBackArrival();
            }
        }
        else if (!ReferenceEquals(previousTop, RootGrid.Children[^1]) && IsShowingOrOpening)
        {
            // Back arrival is fade-only (no reversed forward offset).
            _transition.Start(RootGrid.Children[^1], PageArrivalKind.Back);
        }

        EndSwitch();
    }

    public void NavigateTo(Type pageType, object? parameter = null)
    {
        if (pageType is null)
        {
            return;
        }

        var previousTop = GetCurrentContent() as UIElement;

        // Opening/forwarding an overlay supersedes any pending main back arrival.
        ResolveScope().WithdrawMainBackArrival();
        _transition.Cancel();
        if (Visibility != Visibility.Visible)
        {
            _isOpenPending = true;
        }

        // Covers (and synchronously deactivates) the main frame and deactivates the old overlay top
        // before any selection or collapse happens.
        BeginSwitch();
        UIElement? targetElement;
        try
        {
            targetElement = SelectPage(pageType, parameter);
        }
        finally
        {
            _isSwitching = false;
        }

        if (targetElement is null)
        {
            // Nothing was shown; do not keep the main frame covered by an empty overlay.
            if (RootGrid.Children.Count == 0)
            {
                _isOpenPending = false;
            }
        }
        else if (!ReferenceEquals(previousTop, targetElement) && IsShowingOrOpening)
        {
            _transition.Start(targetElement, PageArrivalKind.Forward);
        }

        EndSwitch();
    }

    public object? GetCurrentContent()
    {
        if (RootGrid.Children.Count > 0)
        {
            return RootGrid.Children[^1];
        }

        return default;
    }

    bool IPageActivationHost.OwnsPage(UIElement page)
        => RootGrid.Children.Contains(page);

    bool IPageActivationHost.IsPageActive(UIElement page)
        => IsLoaded
            && !_isSwitching
            && Visibility == Visibility.Visible
            && RootGrid.Children.Count > 0
            && ReferenceEquals(RootGrid.Children[^1], page)
            && page.Visibility == Visibility.Visible;

    private bool IsShowingOrOpening => Visibility == Visibility.Visible || _isOpenPending;

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

                // Arrival motion is owned by PageArrivalTransition; no implicit show/hide animations.
                RootGrid.Children.Add(element);
            }
            else
            {
                // Not a UIElement: nothing to show.
                return null;
            }
        }
        else if (RootGrid.Children.Count > 1)
        {
            // Move the target to the top of the stack.
            RootGrid.Children.Move(Convert.ToUInt32(RootGrid.Children.IndexOf(targetElement)), Convert.ToUInt32(RootGrid.Children.Count - 1));
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

    private void BeginSwitch()
    {
        _isSwitching = true;
        var scope = ResolveScope();
        scope.NotifyChanged();
        scope.SetMainCovered(ComputeCovering(includePendingOpen: true));
    }

    private void EndSwitch()
    {
        var scope = ResolveScope();
        scope.SetMainCovered(ComputeCovering(includePendingOpen: true));
        scope.NotifyChanged();
    }

    private bool ComputeCovering(bool includePendingOpen)
        => (includePendingOpen && _isOpenPending)
            || (RootGrid.Children.Count > 0 && Visibility == Visibility.Visible);

    private PageActivationScope ResolveScope()
    {
        if (_sharedScope is not null)
        {
            return _sharedScope;
        }

        // The sibling MainFrame in the same navigation host owns the per-window scope.
        var parent = VisualTreeHelper.GetParent(this) as Panel ?? Parent as Panel;
        var mainFrame = parent?.Children.OfType<MainFrame>().FirstOrDefault();
        if (mainFrame is not null)
        {
            _sharedScope = mainFrame.ActivationScope;
            return _sharedScope;
        }

        return _fallbackScope ??= new PageActivationScope();
    }

    private void OnFrameLoaded(object sender, RoutedEventArgs e)
    {
        _transition.Attach(DispatcherQueue);
        var scope = ResolveScope();
        scope.SetMainCovered(ComputeCovering(includePendingOpen: true));
        scope.NotifyChanged();
    }

    private void OnFrameUnloaded(object sender, RoutedEventArgs e)
    {
        _transition.Detach();
        var scope = ResolveScope();
        scope.WithdrawMainBackArrival();
        scope.SetMainCovered(false);
        scope.NotifyChanged();
    }

    private void OnFrameVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        // Either the open completed or the overlay was hidden (including temporary close).
        _isOpenPending = false;
        var scope = ResolveScope();
        if (Visibility != Visibility.Visible)
        {
            _transition.Cancel();
        }
        else if (RootGrid.Children.Count > 0)
        {
            // Overlay re-shown with pages (e.g. SearchCore reopening a temporarily closed stack):
            // the main frame is covered again, so a stale back intent must not survive.
            scope.WithdrawMainBackArrival();
        }

        scope.SetMainCovered(ComputeCovering(includePendingOpen: false));
        scope.NotifyChanged();
    }
}
