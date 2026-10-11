// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Services.Messages;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BiliCopilot.UI.Controls.Message;

/// <summary>系统通知主体. Owns its repeated load/unload subscriptions locally.</summary>
public sealed partial class SystemMessageBody : SystemMessageControlBase
{
    private readonly MessageCallbackGate _callbacks = new();
    private readonly MessageVisibilityObserver _visibility;
    private readonly NavigationViewModel _navigation;
    private SystemMessageSectionDetailViewModel? _owner;
    private FrameworkElement? _firstRow;
    private long _firstRowTagToken;
    private bool _loaded;
    private bool _wired;
    private bool _resetScrollPending;

    public SystemMessageBody()
    {
        InitializeComponent();
        _navigation = this.Get<NavigationViewModel>();
        _visibility = new MessageVisibilityObserver(this, OnVisibilityChanged);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnViewModelChanged(SystemMessageSectionDetailViewModel? oldValue, SystemMessageSectionDetailViewModel? newValue)
    {
        SetOwner(null);
        _resetScrollPending = true;
        if (!_loaded) return;
        SetOwner(newValue);
        Bindings.Update();
        ResetScrollIfNeeded();
        ScheduleChecks();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (!_wired)
        {
            _wired = true;
            SystemMessageView.ViewChanged += OnViewChanged;
            SystemMessageView.SizeChanged += OnViewSizeChanged;
            _visibility.Start();
        }

        SetOwner(ViewModel);
        Bindings.Update();
        ResetScrollIfNeeded();
        ScheduleChecks();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        SetOwner(null);
        if (_wired)
        {
            _wired = false;
            SystemMessageView.ViewChanged -= OnViewChanged;
            SystemMessageView.SizeChanged -= OnViewSizeChanged;
            _visibility.Stop();
        }

        Bindings.StopTracking();
    }

    private void SetOwner(SystemMessageSectionDetailViewModel? owner)
    {
        if (ReferenceEquals(_owner, owner)) return;
        if (_owner is not null)
        {
            _owner.ListUpdated -= OnListUpdated;
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _owner.Items.CollectionChanged -= OnItemsChanged;
        }

        _owner = owner;
        InvalidateWork(forgetRow: true);
        if (_owner is not null)
        {
            _owner.ListUpdated += OnListUpdated;
            _owner.PropertyChanged += OnOwnerPropertyChanged;
            _owner.Items.CollectionChanged += OnItemsChanged;
        }
    }

    private void ResetScrollIfNeeded()
    {
        if (!_resetScrollPending || _owner is null) return;
        _resetScrollPending = false;
        SystemMessageView.ChangeView(0d, 0d, null);
    }

    private void InvalidateWork(bool forgetRow = false)
    {
        _callbacks.Reset(_owner, _loaded && _wired && _owner?.IsActive == true);
        if (forgetRow) SetFirstRow(null);
    }

    private void OnListUpdated(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _owner) || !ReferenceEquals(_owner, ViewModel)) return;
        ScheduleChecks();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _owner) || !ReferenceEquals(_owner, ViewModel)) return;
        if (e.PropertyName is nameof(SystemMessageSectionDetailViewModel.IsActive)
            or nameof(SystemMessageSectionDetailViewModel.IsLoading)
            or nameof(SystemMessageSectionDetailViewModel.IsEmpty)
            or nameof(SystemMessageSectionDetailViewModel.HasError)
            or nameof(SystemMessageSectionDetailViewModel.ErrorMessage)
            or nameof(SystemMessageSectionDetailViewModel.HasMore))
        {
            InvalidateWork(forgetRow: _owner?.IsActive != true);
            ScheduleChecks();
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_owner is null || !ReferenceEquals(sender, _owner.Items) || !ReferenceEquals(_owner, ViewModel)) return;
        InvalidateWork(forgetRow: true);
        ScheduleChecks();
    }

    private void OnVisibilityChanged()
    {
        InvalidateWork();
        ScheduleChecks();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => ScheduleChecks();
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleChecks();

    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _owner is null || sender is not FrameworkElement element) return;
        var row = ResolveRow(element);
        if (row is null || !ReferenceEquals(row, _owner.Items.FirstOrDefault())) return;
        SetFirstRow(element);
        ScheduleFirstItemCheck();
    }

    private void OnRowUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _firstRow)) InvalidateWork(forgetRow: true);
    }

    private void SetFirstRow(FrameworkElement? element)
    {
        if (ReferenceEquals(element, _firstRow)) return;
        if (_firstRow is not null)
        {
            _firstRow.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, _firstRowTagToken);
            _firstRow.DataContextChanged -= OnFirstRowDataContextChanged;
            _firstRow.SizeChanged -= OnViewSizeChanged;
        }

        _firstRow = element;
        if (element is not null)
        {
            _firstRowTagToken = element.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, OnFirstRowTagChanged);
            element.DataContextChanged += OnFirstRowDataContextChanged;
            element.SizeChanged += OnViewSizeChanged;
        }
    }

    private void OnFirstRowTagChanged(DependencyObject sender, DependencyProperty property)
    {
        if (!ReferenceEquals(sender, _firstRow)) return;
        InvalidateWork();
        ScheduleFirstItemCheck();
    }

    private void OnFirstRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _firstRow)) return;
        InvalidateWork();
        ScheduleFirstItemCheck();
    }

    private void ScheduleChecks()
    {
        ScheduleFirstItemCheck();
        ScheduleLoadMoreCheck();
    }

    private bool CanInspect(SystemMessageSectionDetailViewModel owner)
        => _loaded && _wired && IsLoaded && ReferenceEquals(owner, _owner) && ReferenceEquals(owner, ViewModel)
        && owner.IsActive && !owner.IsLoading && !owner.IsEmpty && !owner.HasError
        && string.IsNullOrEmpty(owner.ErrorMessage) && !_navigation.IsOverlayOpen
        && MessageVisibilityObserver.IsTreeVisible(this)
        && MessageVisibilityObserver.IsTreeVisible(SystemMessageView, XamlRoot);

    private void ScheduleLoadMoreCheck()
    {
        var owner = _owner;
        if (owner is null || !CanInspect(owner) || !owner.HasMore) return;
        var ticket = _callbacks.TryQueue(loadMore: true);
        if (ticket is null) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (!_callbacks.IsCurrent(ticket) || !CanInspect(owner) || !owner.HasMore) return;
                if (SystemMessageView.ViewportWidth > 0 && SystemMessageView.ViewportHeight > 0
                    && SystemMessageView.ExtentHeight - SystemMessageView.ViewportHeight - SystemMessageView.VerticalOffset <= 800)
                {
                    owner.LoadItemsCommand.Execute(default);
                }
            }
            finally { _callbacks.Complete(ticket); }
        })) _callbacks.Complete(ticket);
    }

    private void ScheduleFirstItemCheck()
    {
        var owner = _owner;
        if (owner is null || !CanInspect(owner)) return;
        var row = owner.Items.FirstOrDefault();
        if (row is null) return;
        var element = FindFirstRow(row);
        var ticket = _callbacks.TryQueue(loadMore: false);
        if (ticket is null) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (!_callbacks.IsCurrent(ticket) || !CanInspect(owner) || !ReferenceEquals(owner.Items.FirstOrDefault(), row)) return;
                // A recycled template may update Tag only after the VM notification. Give it
                // this one low-priority turn, without requiring another Loaded or polling.
                var realized = element is { IsLoaded: true } && ReferenceEquals(ResolveRow(element), row) && IsWithinScroll(element)
                    ? element : FindFirstRow(row);
                if (realized is null || !ReferenceEquals(ResolveRow(realized), row)) return;
                ConfirmFirstItemIfVisible(owner, row, realized);
            }
            finally { _callbacks.Complete(ticket); }
        })) _callbacks.Complete(ticket);
    }

    private FrameworkElement? FindFirstRow(SystemMessageRow row)
    {
        if (_firstRow is { IsLoaded: true } current && ReferenceEquals(ResolveRow(current), row) && IsWithinScroll(current)) return current;
        SetFirstRow(null);
        // Only the realized local tree, at discrete lifecycle/data/viewport triggers. No polling.
        var pending = new Stack<DependencyObject>();
        pending.Push(SystemMessageView);
        var budget = 512;
        while (pending.Count > 0 && budget-- > 0)
        {
            var node = pending.Pop();
            if (node is FrameworkElement element && element.IsLoaded && ReferenceEquals(ResolveRow(element), row))
            {
                SetFirstRow(element);
                return element;
            }

            var count = Math.Min(VisualTreeHelper.GetChildrenCount(node), Math.Max(0, budget - pending.Count));
            for (var index = count - 1; index >= 0; index--) pending.Push(VisualTreeHelper.GetChild(node, index));
        }

        return null;
    }

    private bool IsWithinScroll(DependencyObject element)
    {
        var budget = 128;
        for (DependencyObject? node = element; node is not null && budget-- > 0; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, SystemMessageView)) return true;
        return false;
    }

    private void ConfirmFirstItemIfVisible(SystemMessageSectionDetailViewModel owner, SystemMessageRow row, FrameworkElement element)
    {
        var root = XamlRoot;
        if (root is null || !IsWithinScroll(element) || !MessageVisibilityObserver.IsTreeVisible(element, root)) return;
        try
        {
            var bounds = element.TransformToVisual(SystemMessageView).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (!MessageRowVisibility.TryClipToViewport(bounds, Math.Min(SystemMessageView.ActualWidth, SystemMessageView.ViewportWidth),
                Math.Min(SystemMessageView.ActualHeight, SystemMessageView.ViewportHeight), out var visible)) return;
            // The SAME visible scroll fragment must also intersect the actual host, not just a different part of a tall row.
            var hostBounds = SystemMessageView.TransformToVisual(root.Content).TransformBounds(visible);
            if (!MessageRowVisibility.TryClipToViewport(hostBounds, root.Size.Width, root.Size.Height, out _)) return;
            owner.ConfirmDisplayedFirstItem(row);
        }
        catch (ArgumentException) { /* Detached/reparented between layout and the queued check. */ }
        catch (InvalidOperationException) { }
        catch (COMException) { }
    }

    private static SystemMessageRow? ResolveRow(FrameworkElement element)
    {
        if (element.Tag is not SystemMessageRow row) return null;
        return element.DataContext is SystemMessageRow context && !ReferenceEquals(context, row) ? null : row;
    }
}

/// <summary>Per-body queue tickets: an obsolete callback cannot release a newer pending slot.</summary>
internal sealed class MessageCallbackGate
{
    private object? _owner;
    private long _epoch;
    private bool _active;
    private Ticket? _first;
    private Ticket? _more;

    internal sealed record Ticket(object Owner, long Epoch, bool LoadMore);

    internal void Reset(object? owner, bool active)
    {
        _owner = owner;
        _active = active;
        _epoch++;
        _first = _more = null;
    }

    internal Ticket? TryQueue(bool loadMore)
    {
        if (!_active || _owner is null || (loadMore ? _more : _first) is not null) return null;
        var ticket = new Ticket(_owner, _epoch, loadMore);
        if (loadMore) _more = ticket;
        else _first = ticket;
        return ticket;
    }

    internal bool IsCurrent(Ticket ticket) => _active && ticket.Epoch == _epoch && ReferenceEquals(ticket.Owner, _owner);

    internal void Complete(Ticket ticket)
    {
        if (!IsCurrent(ticket)) return;
        if (ticket.LoadMore && ReferenceEquals(_more, ticket)) _more = null;
        if (!ticket.LoadMore && ReferenceEquals(_first, ticket)) _first = null;
    }
}

internal static class MessageRowVisibility
{
    internal static bool TryClipToViewport(Rect bounds, double width, double height, out Rect visible)
    {
        visible = default;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width)
            || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        var left = Math.Max(0, bounds.Left);
        var top = Math.Max(0, bounds.Top);
        var right = Math.Min(width, bounds.Right);
        var bottom = Math.Min(height, bounds.Bottom);
        if (right <= left || bottom <= top) return false;
        visible = new Rect(left, top, right - left, bottom - top);
        return true;
    }
}
