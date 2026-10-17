// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models.Media;
using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls;

/// <summary>Passive raw-order projection; sampling never owns or invokes pagination.</summary>
public sealed class VideoCategorySource : DependencyObject
{
    public static readonly DependencyProperty ItemRowProperty = DependencyProperty.RegisterAttached(
        "ItemRow", typeof(object), typeof(VideoCategorySource), new PropertyMetadata(null, OnItemRowChanged));
    private static readonly DependencyProperty RegistrationProperty = DependencyProperty.RegisterAttached(
        "Registration", typeof(object), typeof(VideoCategorySource), new PropertyMetadata(null));
    private static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(VideoCategorySource), new PropertyMetadata(null));
    private static readonly DependencyProperty PageProperty = DependencyProperty.RegisterAttached(
        "PageCoordinator", typeof(object), typeof(VideoCategorySource), new PropertyMetadata(null));

    public static object? GetItemRow(DependencyObject element) => element.GetValue(ItemRowProperty);
    public static void SetItemRow(DependencyObject element, object? value) => element.SetValue(ItemRowProperty, value);
    private static void OnItemRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        var old = element.GetValue(RegistrationProperty) as RowRegistration;
        if (e.NewValue is null) { old?.Dispose(); element.ClearValue(RegistrationProperty); }
        else if (old is not null) old.Row = e.NewValue; // Keep exactly one Loaded/Unloaded/viewport subscription.
        else element.SetValue(RegistrationProperty, new RowRegistration(element, e.NewValue));
    }

    private readonly FrameworkElement _host;
    private readonly Func<IEnumerable?> _raw;
    private readonly Func<INotifyPropertyChanged?> _model;
    private readonly Func<object, VideoInformation?> _video;
    private readonly SponsorDispatcher _dispatch;
    private readonly HashSet<RowRegistration> _rows = [];
    private readonly List<(DependencyObject Node, DependencyProperty Property, long Token)> _visibility = [];
    private INotifyCollectionChanged? _collection;
    private IEnumerable? _boundRaw;
    private INotifyPropertyChanged? _boundModel;
    private PageCoordinator? _page;
    private object? _owner;
    private bool _loaded, _dirty, _wasActive;
    private object? _rawTicket;
    private long _rawGeneration;
    private PendingSample? _pending;
    private sealed record PendingSample(object Owner, SponsorVideoListAccount Account, long PreferenceRevision,
        long RawGeneration, bool Initializing);

    public VideoCategorySource(FrameworkElement host, Func<IEnumerable?> raw,
        Func<INotifyPropertyChanged?> model, Func<object, VideoInformation?>? video = null)
    {
        _host = host; _raw = raw; _model = model;
        _video = video ?? (row => row is VideoItemViewModel vm ? vm.Data : row as VideoInformation);
        _dispatch = new(host.DispatcherQueue);
        host.SetValue(SourceProperty, this);
        host.Loaded += OnLoaded;
        host.Unloaded += OnUnloaded;
    }

    public ObservableCollection<object> VisibleItems { get; } = new();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _owner = new object();
        if (PageActivation.TryFindHost(_host, out var frame, out var page) && frame is not null && page is not null)
        {
            var state = page.GetValue(PageProperty) as PageCoordinator;
            if (state is null)
            {
                state = new PageCoordinator(page, frame, _host);
                page.SetValue(PageProperty, state);
            }
            if (ReferenceEquals(state.Frame, frame)) { _page = state; state.Add(this); }
        }
        BindInputs();
        WatchVisibility();
        _host.LayoutUpdated += OnLayout;
        _host.EffectiveViewportChanged += OnHostViewport;
        ++_rawGeneration; _dirty = false;
        Display(ReadRaw(), null);
        CheckActiveEntry();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false; _owner = null; _pending = null; _wasActive = false;
        ++_rawGeneration; _rawTicket = null; _dirty = false;
        _host.LayoutUpdated -= OnLayout;
        _host.EffectiveViewportChanged -= OnHostViewport;
        foreach (var watch in _visibility) watch.Node.UnregisterPropertyChangedCallback(watch.Property, watch.Token);
        _visibility.Clear();
        DetachInputs();
        var page = _page; _page = null;
        page?.Remove(this);
        if (!_loaded && _owner is null) Display(ReadRaw(), null);
    }

    /// <summary>Rebind only to the host's current raw collection and model.</summary>
    public void Rebind()
    {
        if (!_loaded) return;
        var owner = new object(); _owner = owner;
        ++_rawGeneration; _pending = null; _rawTicket = null;
        BindInputs();
        _dirty = true;
        _page?.InvalidateScope();
        if (!_loaded || !ReferenceEquals(_owner, owner)) return;
        FlushRaw();
        if (_loaded && ReferenceEquals(_owner, owner)) RequestSample();
    }

    /// <summary>Publish the raw commit before the host's ordinary Store underfill check.</summary>
    public void RawCommitted()
    {
        if (!_loaded || _owner is not { } owner) return;
        ++_rawGeneration; _pending = null; _dirty = true;
        _page?.InvalidateScope();
        if (!_loaded || !ReferenceEquals(_owner, owner)) return;
        FlushRaw();
        if (_loaded && ReferenceEquals(_owner, owner)) RequestSample();
    }

    private void BindInputs()
    {
        DetachInputs();
        _boundRaw = _raw(); _collection = _boundRaw as INotifyCollectionChanged;
        _boundModel = _model();
        if (_collection is not null) _collection.CollectionChanged += OnRawChanged;
        if (_boundModel is not null) _boundModel.PropertyChanged += OnModelChanged;
    }
    private void DetachInputs()
    {
        if (_collection is not null) _collection.CollectionChanged -= OnRawChanged;
        if (_boundModel is not null) _boundModel.PropertyChanged -= OnModelChanged;
        _collection = null; _boundModel = null; _boundRaw = null;
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_loaded && ReferenceEquals(sender, _boundModel) && !ReferenceEquals(_raw(), _boundRaw)) Rebind();
    }
    private void OnRawChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_loaded || !ReferenceEquals(sender, _collection)) return;
        _dirty = true; ++_rawGeneration; _pending = null;
        _page?.InvalidateScope();
        QueueRaw();
    }
    private void QueueRaw()
    {
        if (_rawTicket is not null || _owner is not { } owner) return;
        var ticket = new object(); _rawTicket = ticket;
        _dispatch.Post(() =>
        {
            if (!ReferenceEquals(_rawTicket, ticket)) return;
            _rawTicket = null;
            if (!_loaded || !ReferenceEquals(_owner, owner) || !_dirty) return;
            FlushRaw();
            if (_loaded && ReferenceEquals(_owner, owner)) RequestSample();
        });
    }
    private void FlushRaw()
    {
        if (_page is { IsApplying: true }) { _dirty = true; QueueRaw(); return; }
        var raw = ReadRaw();
        _dirty = false;
        if (_page is not null) _page.Apply();
        else Display(raw, null);
    }
    private object[] ReadRaw() => _raw()?.Cast<object>().ToArray() ?? [];

    private void RequestSample()
    {
        if (_dirty || !IsActive()) return;
        _pending = _page?.CaptureSample(this);
        if (_pending is not null) _page?.QueuePass();
    }
    private bool OwnsSample(PendingSample sample) => ReferenceEquals(sample.Owner, _owner)
        && sample.RawGeneration == _rawGeneration && IsActive();
    private void CheckActiveEntry()
    {
        var active = IsActive();
        var entered = active && !_wasActive;
        _wasActive = active;
        if (!active) _pending = null;
        else if (entered) RequestSample();
    }
    private void OnLayout(object? sender, object args) => GeometryChanged();
    private void OnHostViewport(FrameworkElement sender, EffectiveViewportChangedEventArgs args) => GeometryChanged();
    private void GeometryChanged()
    {
        if (!_loaded) return;
        _page?.CheckActivation();
        if (_pending is not null) _page?.QueuePass(); // Geometry can finish an existing sample, not create one.
    }
    private void WatchVisibility()
    {
        var remaining = 128;
        for (DependencyObject? node = _host; node is not null && remaining-- > 0; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not UIElement) continue;
            foreach (var property in new[] { UIElement.VisibilityProperty, UIElement.OpacityProperty })
                _visibility.Add((node, property, node.RegisterPropertyChangedCallback(property, (_, _) => GeometryChanged())));
        }
    }
    private bool IsActive()
    {
        if (!_loaded || _page is null || !_page.IsActive || !TreeVisible(_host)) return false;
        return PageActivation.TryFindHost(_host, out var frame, out var page)
            && ReferenceEquals(frame, _page.Frame) && ReferenceEquals(page, _page.Page);
    }
    private static bool TreeVisible(FrameworkElement element)
    {
        var root = element.XamlRoot;
        if (!element.IsLoaded || root?.IsHostVisible != true) return false;
        var remaining = 128;
        for (DependencyObject? node = element; node is not null && remaining-- > 0; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && (ui.Visibility != Visibility.Visible || !double.IsFinite(ui.Opacity) || ui.Opacity <= 0)) return false;
            if (ReferenceEquals(node, root.Content)) return true;
        }
        return false;
    }

    private void Display(object[] raw, HashSet<object>? removed)
    {
        var target = removed is null ? raw : raw.Where(item => !removed.Contains(item)).ToArray();
        ReconcileVisibleItems(target);
    }

    private void ReconcileVisibleItems(object[] target)
    {
        var common = 0;
        while (common < VisibleItems.Count && common < target.Length
               && ReferenceEquals(VisibleItems[common], target[common]))
        {
            common++;
        }

        if (common == VisibleItems.Count && common == target.Length)
        {
            return;
        }

        if (common == VisibleItems.Count)
        {
            for (var i = common; i < target.Length; i++)
            {
                VisibleItems.Add(target[i]);
            }
            return;
        }

        for (var i = common; i < target.Length; i++)
        {
            var expected = target[i];
            if (i < VisibleItems.Count)
            {
                if (ReferenceEquals(VisibleItems[i], expected))
                {
                    continue;
                }

                var existingIndex = -1;
                for (var j = i + 1; j < VisibleItems.Count; j++)
                {
                    if (ReferenceEquals(VisibleItems[j], expected))
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    VisibleItems.Move(existingIndex, i);
                }
                else
                {
                    VisibleItems.Insert(i, expected);
                }
            }
            else
            {
                VisibleItems.Add(expected);
            }
        }

        while (VisibleItems.Count > target.Length)
        {
            VisibleItems.RemoveAt(VisibleItems.Count - 1);
        }
    }

    private sealed class RowRegistration : IDisposable
    {
        internal readonly FrameworkElement Element;
        internal object Row;
        internal Rect? Viewport;
        internal XamlRoot? Root;
        internal VideoCategorySource? Source;
        internal RowRegistration(FrameworkElement element, object row)
        {
            Element = element; Row = row;
            element.Loaded += Loaded; element.Unloaded += Unloaded;
            element.EffectiveViewportChanged += ViewportChanged;
            if (element.IsLoaded) Connect();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Connect();
        private void Connect()
        {
            Disconnect();
            var remaining = 128;
            for (DependencyObject? node = Element; node is not null && remaining-- > 0; node = VisualTreeHelper.GetParent(node))
                if (node.GetValue(SourceProperty) is VideoCategorySource source) { Source = source; source._rows.Add(this); break; }
        }
        private void Unloaded(object sender, RoutedEventArgs args) { Disconnect(); Viewport = null; Root = null; }
        private void Disconnect() { Source?._rows.Remove(this); Source = null; }
        private void ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            Viewport = args.EffectiveViewport; Root = sender.XamlRoot;
            Source?.GeometryChanged();
        }
        public void Dispose()
        {
            Disconnect();
            Element.Loaded -= Loaded; Element.Unloaded -= Unloaded; Element.EffectiveViewportChanged -= ViewportChanged;
        }
    }

    private bool Visible(RowRegistration row)
    {
        if (!IsActive() || !ReferenceEquals(row.Source, this) || !TreeVisible(row.Element)
            || !ReferenceEquals(row.Root, row.Element.XamlRoot) || row.Viewport is not { } visible || !ValidRect(visible)) return false;
        try
        {
            visible.Intersect(new Rect(0, 0, row.Element.ActualWidth, row.Element.ActualHeight));
            var foundHost = false;
            var remaining = 128;
            for (DependencyObject? node = row.Element; node is not null && remaining-- > 0; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _host)) foundHost = true;
                if (node is UIElement && node is not FrameworkElement) return false;
                if (node is FrameworkElement element)
                {
                    if (!element.IsLoaded || element.XamlRoot != row.Root || element.Projection is not null || element.Transform3D is not null
                        || !Translation(element.TransformToVisual(row.Element), out var offset)) return false;
                    var bounds = new Rect(offset.X, offset.Y, element.ActualWidth, element.ActualHeight);
                    if (!ValidRect(bounds)) return false;
                    visible.Intersect(bounds);
                    if (element.Clip is RectangleGeometry clip)
                    {
                        var shift = new Point();
                        if (clip.Transform is not null && !Translation(clip.Transform, out shift)) return false;
                        var rectangle = new Rect(offset.X + shift.X + clip.Rect.X, offset.Y + shift.Y + clip.Rect.Y, clip.Rect.Width, clip.Rect.Height);
                        if (!ValidRect(rectangle)) return false;
                        visible.Intersect(rectangle);
                    }
                    else if (element.Clip is not null) return false;
                }
                if (!ValidRect(visible)) return false;
                if (ReferenceEquals(node, row.Root?.Content)) return foundHost;
            }
        }
        catch (Exception) { return false; } // Unsupported/detached geometry is unqueried, never inferred visible.
        return false;
    }
    private static bool ValidRect(Rect rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && double.IsFinite(rect.Right)
        && double.IsFinite(rect.Bottom) && rect.Width > 0 && rect.Height > 0;
    private static bool Translation(GeneralTransform transform, out Point origin)
    {
        origin = transform.TransformPoint(new Point());
        var x = transform.TransformPoint(new Point(1, 0)); var y = transform.TransformPoint(new Point(0, 1));
        return double.IsFinite(origin.X) && double.IsFinite(origin.Y) && Math.Abs(x.X - origin.X - 1) < .000001
            && Math.Abs(x.Y - origin.Y) < .000001 && Math.Abs(y.X - origin.X) < .000001 && Math.Abs(y.Y - origin.Y - 1) < .000001;
    }

    /// <summary>Stored on the actual page root, not in a static cross-page/window element registry.</summary>
    private sealed class PageCoordinator
    {
        internal readonly UIElement Page;
        internal readonly IPageActivationHost Frame;
        private readonly SponsorDispatcher _dispatch;
        private readonly List<VideoCategorySource> _sources = [];
        private SponsorRuntime? _runtime;
        private SponsorVideoListAdapter? _adapter;
        private SponsorVideoListScope? _scope;
        private SponsorVideoListSnapshot? _submitted;
        private SponsorVideoListAccount? _account;
        private CancellationTokenRegistration _accountRegistration;
        private bool _accountCanceled, _initializing;
        private List<Sample> _samples = [];
        private XamlRoot? _root;
        private long _generation, _pass, _preference = -1;
        private string? _storeVersion;
        private bool _savedNetwork, _savedContent;
        private ImmutableArray<string> _savedCategories = [];
        private bool _applying;
        private object? _queueTicket;
        private sealed record Sample(VideoCategorySource Source, long Generation, SponsorVideoListRow Row);

        internal PageCoordinator(UIElement page, IPageActivationHost frame, FrameworkElement anchor)
        { Page = page; Frame = frame; _dispatch = new(anchor.DispatcherQueue); }
        internal bool IsActive => Frame.IsPageActive(Page) && (Page as FrameworkElement)?.XamlRoot?.IsHostVisible == true;
        internal bool IsApplying => _applying;
        internal void Add(VideoCategorySource source)
        {
            if (_sources.Contains(source)) return;
            _sources.Add(source);
            if (_sources.Count != 1) return;
            Frame.ActivationScope.ActivationChanged += ActivationChanged;
            _root = (Page as FrameworkElement)?.XamlRoot;
            if (_root is not null) _root.Changed += RootChanged;
            try
            {
                _runtime = source._host.Get<SponsorRuntime>(); _adapter = _runtime.VideoLists;
                var context = _adapter.ReadContext();
                var view = _runtime.Coordinator.Snapshot;
                _preference = context.PreferenceRevision; _storeVersion = view.StoreVersion;
                _initializing = InitialLoadPending(view);
                WatchAccount(context.Account);
                RememberPreferences(view);
                _adapter.Changed += AdapterChanged;
                _runtime.Coordinator.Updated += ApplicationChanged;
            }
            catch (Exception) { _adapter = null; } // Unsupported dispatcher/closing app: original source only.
        }
        internal void Remove(VideoCategorySource source)
        {
            _sources.Remove(source);
            if (_sources.Count != 0) { InvalidateScope(); return; }
            var adapter = _adapter;
            var old = _scope; _scope = null; _submitted = null; _samples.Clear(); _queueTicket = null;
            Frame.ActivationScope.ActivationChanged -= ActivationChanged;
            if (_root is not null) _root.Changed -= RootChanged;
            _root = null;
            if (_adapter is not null) _adapter.Changed -= AdapterChanged;
            if (_runtime is not null) _runtime.Coordinator.Updated -= ApplicationChanged;
            _account = null; _accountRegistration.Dispose(); _accountRegistration = default;
            _adapter = null; _runtime = null;
            if (old is not null) adapter?.Deactivate(old.Id, old.Generation);
        }
        private void ActivationChanged(object? sender, EventArgs args) => CheckActivation();
        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => CheckActivation();
        internal void CheckActivation()
        {
            RefreshPermissions();
            if (_samples.Count > 0 && !ValidSamples(_samples)) InvalidateScope();
            foreach (var source in _sources.ToArray()) source.CheckActiveEntry();
            if (!IsActive || !_sources.Any(source => source.IsActive()))
            {
                foreach (var source in _sources) source._pending = null;
                if (_scope is not null || _submitted is not null) InvalidateScope();
            }
            else if (_sources.Any(source => source._pending is not null)) QueuePass();
        }
        internal void InvalidateScope()
        {
            foreach (var source in _sources) source._pending = null;
            _queueTicket = null;
            var old = _scope; _scope = null; _submitted = null; _samples.Clear();
            if (old is not null) _adapter?.Deactivate(old.Id, old.Generation);
            Apply();
        }
        internal void QueuePass()
        {
            if (_queueTicket is not null || _adapter is null) return;
            if (RefreshPermissions() is null || !_sources.Any(source => source._pending is not null)) return;
            var ticket = new object(); _queueTicket = ticket;
            _dispatch.Post(() =>
            {
                if (!ReferenceEquals(_queueTicket, ticket)) return;
                _queueTicket = null;
                TryPass();
            });
        }
        private void TryPass()
        {
            var context = RefreshPermissions();
            if (_adapter is null || context is null || !IsActive || !_sources.Any(source => source._pending is not null)) return;
            if (!context.Enabled || !SettingsReady(_runtime!.Coordinator.Snapshot)) { Apply(); return; }
            var requests = new List<(VideoCategorySource Source, PendingSample Sample)>();
            foreach (var source in _sources)
                if (source._pending is { } sample) requests.Add((source, sample));
            var selected = new List<Sample>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var source in _sources.Where(source => source.IsActive() && !source._dirty))
            {
                var visible = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var row in source._rows) if (source.Visible(row)) visible.Add(row.Row);
                foreach (var original in source.ReadRaw())
                {
                    if (selected.Count == 128) break;
                    if (!visible.Contains(original) || !seen.Add(original) || source._video(original) is not { } video) continue;
                    selected.Add(new(source, source._rawGeneration, new(original, video)));
                }
            }
            if (selected.Count == 0) return; // Retain the pending sample until effective geometry is available.
            foreach (var source in _sources) source._pending = null; // Consume before projection/Activate can reenter.
            _scope ??= new(Guid.NewGuid(), ++_generation, CancellationToken.None);
            var scope = _scope;
            _adapter.Activate(scope);
            var current = RefreshPermissions();
            if (_scope != scope) return;
            if (current is null || !current.Enabled || LoadFailed
                || !SettingsReady(_runtime!.Coordinator.Snapshot) || current.Account != context.Account
                || current.PreferenceRevision != context.PreferenceRevision || context.Account.Lifetime.IsCancellationRequested
                || !IsActive || !ValidSamples(selected) || !requests.All(item => item.Source.OwnsSample(item.Sample)))
            { InvalidateScope(); return; }
            _samples = selected;
            _submitted = new(scope, context.Account, context.PreferenceRevision, ++_pass, selected.Select(sample => sample.Row).ToImmutableArray());
            _ = _adapter.RequestAsync(_submitted); // Backend owns/tracks the actual worker; Source never creates a waiter or refill loop.
        }
        private bool ValidSamples(List<Sample> samples) => samples.All(sample => _sources.Contains(sample.Source)
            && sample.Source.IsActive() && sample.Generation == sample.Source._rawGeneration
            && sample.Source.ReadRaw().Any(item => ReferenceEquals(item, sample.Row.Original))
            && ReferenceEquals(sample.Source._video(sample.Row.Original), sample.Row.Video));
        private bool LoadFailed => _runtime?.LastLoadResult is { Status: not ApplicationStatus.Success };
        private static bool SettingsReady(SponsorApplicationView view) => !view.Closed && view.StoreVersion is not null
            && (view.Persistence is PersistenceStatus.Success or PersistenceStatus.Missing)
            && (view.AuxiliaryPersistence is null or PersistenceStatus.Success);
        private bool InitialLoadPending(SponsorApplicationView view) => !view.Closed && view.StoreVersion is null
            && view.Persistence is null && view.AuxiliaryPersistence is null && _runtime?.LastLoadResult is null;

        internal PendingSample? CaptureSample(VideoCategorySource source)
        {
            var context = RefreshPermissions();
            if (context is null || _runtime is null || context.Account.Lifetime.IsCancellationRequested
                || LoadFailed || source._owner is not { } owner || !source.IsActive()) return null;
            var initial = _initializing && InitialLoadPending(_runtime.Coordinator.Snapshot);
            if (!initial && (!context.Enabled || !SettingsReady(_runtime.Coordinator.Snapshot))) return null;
            return new(owner, context.Account, context.PreferenceRevision, source._rawGeneration, initial);
        }

        private SponsorVideoListContext? RefreshPermissions()
        {
            if (_adapter is null || _runtime is null) return null;
            var context = _adapter.ReadContext();
            var view = _runtime.Coordinator.Snapshot;
            var pending = InitialLoadPending(view);
            // Only initial settings readiness can advance an existing pending sample.
            var initialized = _initializing && !pending && _storeVersion is null && SettingsReady(view) && !LoadFailed;
            _initializing &= pending;
            if (_account != context.Account || _accountCanceled != context.Account.Lifetime.IsCancellationRequested)
            {
                WatchAccount(context.Account); // Publish the new identity before Deactivate can notify/reenter.
                InvalidateScope(); // Revoke pending samples even before a backend scope exists.
            }
            foreach (var source in _sources)
            {
                if (source._pending is not { } sample) continue;
                if (!source.OwnsSample(sample) || sample.Account != context.Account
                    || sample.Account.Lifetime.IsCancellationRequested || LoadFailed)
                { source._pending = null; continue; }
                if (sample.Initializing)
                {
                    if (pending && sample.PreferenceRevision == context.PreferenceRevision) continue;
                    if (initialized && context.Enabled)
                        source._pending = sample with { Initializing = false, PreferenceRevision = context.PreferenceRevision };
                    else source._pending = null;
                }
                else if (!context.Enabled || !SettingsReady(view) || sample.PreferenceRevision != context.PreferenceRevision)
                    source._pending = null;
            }
            if ((!pending && (!context.Enabled || !SettingsReady(view))) || LoadFailed)
                if (_scope is not null || _submitted is not null) InvalidateScope();
            return context;
        }

        private void WatchAccount(SponsorVideoListAccount account)
        {
            var previous = _accountRegistration; _accountRegistration = default;
            _account = account; _accountCanceled = account.Lifetime.IsCancellationRequested;
            previous.Dispose();
            if (_accountCanceled) return;
            // Backend scope registration does not exist yet while the first sample waits for geometry/load.
            _accountRegistration = account.Lifetime.Register(() =>
            {
                try { _dispatch.Post(() => { if (_account == account) { RefreshPermissions(); Apply(); } }); }
                catch (InvalidOperationException) when (account.Lifetime.IsCancellationRequested) { /* Closing dispatcher: all admission paths also reject the canceled token. */ }
            });
        }

        // A failed initial store load can publish without changing Adapter.Enabled/preferences.
        private void ApplicationChanged(SponsorApplicationView view) => AdapterChanged();
        private void AdapterChanged()
        {
            var context = RefreshPermissions();
            if (context is null) return;
            var view = _runtime!.Coordinator.Snapshot;
            var preferencesChanged = _savedNetwork != view.Settings.NetworkEnabled
                || _savedContent != view.Auxiliary.Filters.ContentEnabled
                || !_savedCategories.SequenceEqual(view.Auxiliary.Filters.ContentCategories, StringComparer.Ordinal);
            var saved = _storeVersion is not null && view.StoreVersion is not null && _storeVersion != view.StoreVersion
                && preferencesChanged && _preference != context.PreferenceRevision
                && (view.Persistence is PersistenceStatus.Success or PersistenceStatus.Missing)
                && (view.AuxiliaryPersistence is null or PersistenceStatus.Success);
            _storeVersion = view.StoreVersion; _preference = context.PreferenceRevision;
            RememberPreferences(view);
            Apply();
            // A successful committed preference transition, not Changed itself, grants this pass.
            if (saved && context.Enabled) foreach (var source in _sources.ToArray()) source.RequestSample();
            else if (_sources.Any(source => source._pending is not null)) QueuePass();
        }
        private void RememberPreferences(SponsorApplicationView view)
        {
            _savedNetwork = view.Settings.NetworkEnabled; _savedContent = view.Auxiliary.Filters.ContentEnabled;
            _savedCategories = view.Auxiliary.Filters.ContentCategories;
        }
        internal void Apply()
        {
            if (_applying) return;
            _applying = true;
            try
            {
                var context = _adapter?.ReadContext();
                var projection = _adapter?.Current;
                var view = _runtime?.Coordinator.Snapshot;
                var valid = context is { Enabled: true } && view is not null && SettingsReady(view) && !LoadFailed
                    && projection is { Status: SponsorVideoListStatus.Projected }
                    && _submitted is not null && ReferenceEquals(projection.Snapshot, _submitted)
                    && projection.Snapshot.Scope == _scope && projection.Snapshot.Account == context.Account
                    && projection.Snapshot.PreferenceRevision == context.PreferenceRevision && projection.Snapshot.PassRevision == _pass
                    && IsActive && ValidSamples(_samples);
                var retained = valid ? new HashSet<object>(projection!.RetainedRows, ReferenceEqualityComparer.Instance) : null;
                foreach (var source in _sources.ToArray())
                {
                    var raw = source.ReadRaw();
                    HashSet<object>? removed = valid ? new(_samples.Where(sample => ReferenceEquals(sample.Source, source)
                        && !retained!.Contains(sample.Row.Original)).Select(sample => sample.Row.Original), ReferenceEqualityComparer.Instance) : null;
                    source.Display(raw, removed);
                }
            }
            finally { _applying = false; }
        }
    }
}
