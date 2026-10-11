// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 消息页面.
/// </summary>
public sealed partial class MessagePage : MessagePageBase
{
    private bool _activeRequest;
    private bool _loaded;
    private bool _navigated;
    private bool _observing;
    private bool _initializePending;
    private readonly NavigationViewModel _navigation;
    private readonly MessageVisibilityObserver _visibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePage"/> class.
    /// </summary>
    public MessagePage()
    {
        InitializeComponent();
        _navigation = this.Get<NavigationViewModel>();
        _visibility = new MessageVisibilityObserver(this, UpdateActive);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigated = true;
        UpdateActive();
    }

    /// <inheritdoc/>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navigated = false;
        UpdateActive();
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) _initializePending = true;
        _loaded = true;
        Bindings.Update();
        UpdateActive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        UpdateActive();
        Bindings.StopTracking();
    }

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _navigation) && e.PropertyName == nameof(NavigationViewModel.IsOverlayOpen)) UpdateActive();
    }

    // Loaded and navigation are independent facts, not competing requests to set true.
    private void UpdateActive()
    {
        var observe = _loaded && _navigated;
        if (observe != _observing)
        {
            _observing = observe;
            if (observe)
            {
                _navigation.PropertyChanged += OnNavigationPropertyChanged;
                _visibility.Start();
            }
            else
            {
                _navigation.PropertyChanged -= OnNavigationPropertyChanged;
                _visibility.Stop();
            }
        }

        var active = MessageVisibilityObserver.ShouldActivate(_loaded, _navigated,
            observe && MessageVisibilityObserver.IsTreeVisible(this), _navigation.IsOverlayOpen);
        if (_activeRequest != active)
        {
            _activeRequest = active;
            ViewModel.SetActive(active);
        }

        if (active && _initializePending)
        {
            _initializePending = false;
            ViewModel.InitializeCommand.Execute(default);
        }
    }
}

/// <summary>
/// 消息页面基类.
/// </summary>
public abstract class MessagePageBase : LayoutPageBase<MessagePageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePageBase"/> class.
    /// </summary>
    protected MessagePageBase() => ViewModel = this.Get<MessagePageViewModel>();
}

/// <summary>Local message-page/body visibility subscriptions; no shell or shared-base changes.</summary>
internal sealed class MessageVisibilityObserver(FrameworkElement owner, Action changed)
{
    private readonly List<DependencyObject> _ancestors = new();
    private readonly List<(DependencyObject Element, DependencyProperty Property, long Token)> _tokens = new();
    private XamlRoot? _root;
    private bool _started;

    internal static bool ShouldActivate(bool loaded, bool navigated, bool treeVisible, bool overlayOpen)
        => loaded && navigated && treeVisible && !overlayOpen;

    internal void Start()
    {
        if (_started) return;
        _started = true;
        RefreshAncestors();
        owner.EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    internal void Stop()
    {
        if (!_started) return;
        _started = false;
        owner.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        DetachAncestors();
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = null;
    }

    private void RefreshAncestors()
    {
        var ancestors = new List<DependencyObject>();
        for (DependencyObject? node = owner; node is not null && ancestors.Count < 128; node = VisualTreeHelper.GetParent(node)) ancestors.Add(node);
        if (!_ancestors.SequenceEqual(ancestors))
        {
            DetachAncestors();
            _ancestors.AddRange(ancestors);
            foreach (var node in ancestors)
            {
                if (node is UIElement)
                {
                    Watch(node, UIElement.VisibilityProperty);
                    Watch(node, UIElement.IsHitTestVisibleProperty);
                    Watch(node, UIElement.OpacityProperty);
                }

                if (node is Control) Watch(node, Control.IsEnabledProperty);
                if (node is FrameworkElement element) element.SizeChanged += OnSizeChanged;
            }
        }

        if (!ReferenceEquals(_root, owner.XamlRoot))
        {
            if (_root is not null) _root.Changed -= OnRootChanged;
            _root = owner.XamlRoot;
            if (_root is not null) _root.Changed += OnRootChanged;
        }
    }

    private void Watch(DependencyObject element, DependencyProperty property)
        => _tokens.Add((element, property, element.RegisterPropertyChangedCallback(property, OnVisibilityChanged)));

    private void DetachAncestors()
    {
        foreach (var (element, property, token) in _tokens) element.UnregisterPropertyChangedCallback(property, token);
        _tokens.Clear();
        foreach (var element in _ancestors.OfType<FrameworkElement>()) element.SizeChanged -= OnSizeChanged;
        _ancestors.Clear();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_started && _ancestors.Contains(sender)) changed();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_started && sender is DependencyObject element && _ancestors.Contains(element)) changed();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs e)
    {
        if (!_started) return;
        RefreshAncestors();
        changed();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs e)
    {
        if (!_started || !ReferenceEquals(sender, _root)) return;
        RefreshAncestors();
        changed();
    }

    internal static bool IsTreeVisible(FrameworkElement element, XamlRoot? expectedRoot = null)
    {
        var root = element.XamlRoot;
        if (root is null || !root.IsHostVisible || (expectedRoot is not null && !ReferenceEquals(root, expectedRoot))) return false;
        var remaining = 128;
        for (DependencyObject? node = element; node is not null && remaining-- > 0; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && (ui.Visibility != Visibility.Visible || !ui.IsHitTestVisible || !(ui.Opacity > 0))) return false;
            if (node is Control control && !control.IsEnabled) return false;
            if (node is FrameworkElement current && (!current.IsLoaded || !ReferenceEquals(current.XamlRoot, root)
                || !double.IsFinite(current.ActualWidth) || !double.IsFinite(current.ActualHeight)
                || current.ActualWidth <= 0 || current.ActualHeight <= 0)) return false;
            if (ReferenceEquals(node, root.Content)) return true;
        }

        return false;
    }
}
