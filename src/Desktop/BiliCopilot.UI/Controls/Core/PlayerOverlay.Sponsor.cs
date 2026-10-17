// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.SponsorBlock.Controls;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>Associates composer-owned SponsorBlock views; panel openings share the overlay's popup claims.</summary>
public sealed partial class PlayerOverlay
{
    private const double SponsorPanelPreferredWidth = 400;
    private const double SponsorPanelMinimumWidth = 280;
    private const double SponsorPanelMaxHeight = 640;
    private const double SponsorEdge = 12;
    private const double SponsorHeaderRow = 60;
    private const double SponsorNoticeGap = 8;
    private const double SponsorNoticeIdleBottom = 12;

    /// <summary>
    /// <see cref="SponsorEntryVisibility"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty SponsorEntryVisibilityProperty =
        DependencyProperty.Register(nameof(SponsorEntryVisibility), typeof(Visibility), typeof(PlayerOverlay), new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Overlay that last showed SponsorBlock views for a player; weak on both sides.</summary>
    private static readonly ConditionalWeakTable<PlayerViewModel, PlayerOverlay> SponsorHosts = new();

    private SponsorPlaybackViewModel? _sponsorPlayback;
    private SponsorEditorViewModel? _sponsorEditor;
    private SponsorPlaybackViewModel? _sponsorObserved;
    private bool _sponsorViewsApplied;
    private bool _sponsorLayoutHooked;
    private bool _sponsorChromeQueued;
    private Flyout? _sponsorFlyout;
    private SponsorPlayerPanel? _sponsorPanel;
    private PlayerViewModel? _sponsorPageOwner;

    /// <summary>
    /// Raised on the UI thread when the page-header entry of some player became available or
    /// unavailable. Listeners re-query <see cref="IsSponsorPageEntryAvailable"/> for their own player.
    /// </summary>
    internal static event EventHandler? SponsorPageEntryChanged;

    /// <summary>Gets the accessible name / tooltip of every SponsorBlock entry.</summary>
    public static string SponsorEntryName => SponsorUiText.FeatureName;

    /// <summary>
    /// Gets the visibility of the overlay header entries (full screen and the standalone window title
    /// bar). Same rule as the former transport entry: usable views and not the compact overlay.
    /// </summary>
    public Visibility SponsorEntryVisibility
    {
        get => (Visibility)GetValue(SponsorEntryVisibilityProperty);
        private set => SetValue(SponsorEntryVisibilityProperty, value);
    }

    /// <summary>
    /// Whether the ordinary embedded page header may show the SponsorBlock entry for <paramref name="owner"/>:
    /// the overlay currently showing that player has usable views and plays in the normal presentation.
    /// </summary>
    internal static bool IsSponsorPageEntryAvailable(PlayerViewModel? owner)
        => FindSponsorPageHost(owner) is not null;

    /// <summary>
    /// Opens (or closes, when already open) the SponsorBlock panel of the overlay showing
    /// <paramref name="owner"/>, from the page header. The panel drops down from the top of the video.
    /// </summary>
    /// <returns><see langword="false"/> when no usable overlay shows that player.</returns>
    internal static bool TryToggleSponsorPanelFromPage(PlayerViewModel? owner)
    {
        if (FindSponsorPageHost(owner) is not { } host) return false;
        // The page header lives outside the overlay: the overlay's own claim path requires visible controls.
        if (!owner!.IsControlsVisible) host.ShowControlsForInput();
        host.ToggleSponsorPanel();
        return true;
    }

    private static PlayerOverlay? FindSponsorPageHost(PlayerViewModel? owner)
        => owner is not null && SponsorHosts.TryGetValue(owner, out var host)
            && ReferenceEquals(host.ViewModel, owner) && ReferenceEquals(host._sponsorPageOwner, owner) ? host : null;

    /// <summary>
    /// Registers the already-bound SponsorBlock view models for this overlay and shows the
    /// marker strip, notice strip and panel entry. UI thread only; idempotent for the same pair.
    /// A different pair replaces the previous association (the previous panel is closed first).
    /// </summary>
    /// <returns><see langword="false"/> when called off the UI thread.</returns>
    internal bool BindSponsorViews(SponsorPlaybackViewModel playback, SponsorEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(editor);
        if (DispatcherQueue?.HasThreadAccess != true) return false;
        if (!ReferenceEquals(_sponsorPlayback, playback) || !ReferenceEquals(_sponsorEditor, editor))
        {
            DetachSponsorViews();
            _sponsorPlayback = playback;
            _sponsorEditor = editor;
        }

        ApplySponsorViews();
        return true;
    }

    /// <summary>
    /// Forgets the registered pair, closes this overlay's SponsorBlock panel and hides every
    /// SponsorBlock view. Does not call Unbind on the view models. UI thread only.
    /// </summary>
    internal void UnbindSponsorViews()
    {
        _sponsorPlayback = null;
        _sponsorEditor = null;
        DetachSponsorViews();
    }

    private void ApplySponsorViews()
    {
        if (!_loaded || _sponsorPlayback is not { } playback || _sponsorEditor is not { } editor) return;
        HookSponsorLayout(true);
        ObserveSponsorPlayback(playback);
        if (!ReferenceEquals(SponsorNotices.ViewModel, playback)) SponsorNotices.ViewModel = playback;
        IslandTransport?.SetSponsorTimeline(playback);
        TransportControls?.SetSponsorTimeline(playback);
        if (_sponsorPanel is not null)
        {
            _sponsorPanel.PlaybackViewModel = playback;
            _sponsorPanel.EditorViewModel = editor;
        }

        _sponsorViewsApplied = true;
        if (ViewModel is { } owner) SponsorHosts.AddOrUpdate(owner, this);
        UpdateSponsorChrome();
        // Re-measure once the strip's bindings for the new view model have applied.
        QueueSponsorChrome();
    }

    private void DetachSponsorViews()
    {
        // Only this overlay's own panel is closed; its Closed handler releases only its own claim.
        if (_sponsorFlyout?.IsOpen == true) _sponsorFlyout.Hide();
        _sponsorViewsApplied = false;
        ObserveSponsorPlayback(null);
        HookSponsorLayout(false);
        if (SponsorNotices is not null) SponsorNotices.ViewModel = null;
        IslandTransport?.SetSponsorTimeline(null);
        TransportControls?.SetSponsorTimeline(null);
        if (_sponsorPanel is not null)
        {
            _sponsorPanel.PlaybackViewModel = null;
            _sponsorPanel.EditorViewModel = null;
        }

        UpdateSponsorChrome();
    }

    private void ObserveSponsorPlayback(SponsorPlaybackViewModel? playback)
    {
        if (ReferenceEquals(_sponsorObserved, playback)) return;
        if (_sponsorObserved is not null) _sponsorObserved.PropertyChanged -= OnSponsorPlaybackPropertyChanged;
        _sponsorObserved = playback;
        if (playback is not null) playback.PropertyChanged += OnSponsorPlaybackPropertyChanged;
    }

    private void HookSponsorLayout(bool hook)
    {
        if (_sponsorLayoutHooked == hook || TransportLayer is null) return;
        _sponsorLayoutHooked = hook;
        if (hook)
        {
            TransportLayer.SizeChanged += OnSponsorLayoutSizeChanged;
            if (IslandTransport is not null) IslandTransport.SizeChanged += OnSponsorLayoutSizeChanged;
            if (TransportControls is not null) TransportControls.SizeChanged += OnSponsorLayoutSizeChanged;
        }
        else
        {
            TransportLayer.SizeChanged -= OnSponsorLayoutSizeChanged;
            if (IslandTransport is not null) IslandTransport.SizeChanged -= OnSponsorLayoutSizeChanged;
            if (TransportControls is not null) TransportControls.SizeChanged -= OnSponsorLayoutSizeChanged;
        }
    }

    private void OnSponsorPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The view model raises on its UI queue (SponsorViewFeed); ignore a stale instance.
        if (!ReferenceEquals(sender, _sponsorObserved)) return;
        if (e.PropertyName is null or "" or nameof(SponsorPlaybackViewModel.HasOverlayContent)) QueueSponsorChrome();
    }

    /// <summary>
    /// Runs one coalesced update after the notice strip's own x:Bind has applied the same change,
    /// so its measured height is current. Dropped when the views were detached meanwhile.
    /// </summary>
    private void QueueSponsorChrome()
    {
        if (_sponsorChromeQueued) return;
        var playback = _sponsorObserved;
        _sponsorChromeQueued = DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _sponsorChromeQueued = false;
            if (_sponsorViewsApplied && ReferenceEquals(playback, _sponsorObserved)) UpdateSponsorChrome();
        }) == true;
        if (!_sponsorChromeQueued) UpdateSponsorChrome();
    }

    private void OnSponsorLayoutSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSponsorChrome();

    private void OnSponsorHostPropertyChanged(string? propertyName)
    {
        if (!_sponsorViewsApplied) return;
        if (propertyName is null or "" or nameof(PlayerViewModel.IsControlsVisible) or nameof(PlayerViewModel.Presentation)
            or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.IsPlaybackClosing)
            or nameof(PlayerViewModel.IsWindowDeactivated) or nameof(PlayerViewModel.ErrorMessage))
        {
            UpdateSponsorChrome();
        }
    }

    /// <summary>
    /// Entry and notice visibility plus the notice position. Pure view state: no player input,
    /// no claim and no view-model call. Notices sit above whichever transport is visible; when the
    /// remaining height cannot hold them they fold while the controls are shown and return when
    /// the controls hide, so they never cover transport buttons.
    /// </summary>
    private void UpdateSponsorChrome()
    {
        if (SponsorNoticeHost is null) return;
        var vm = ViewModel;
        var isCompact = vm?.Presentation == PlayerPresentationMode.CompactOverlay;
        var usable = _loaded && _sponsorViewsApplied && vm is not null
            && !vm.IsConnecting && !vm.IsPlaybackClosing && string.IsNullOrEmpty(vm.ErrorMessage);
        var entry = usable && !isCompact ? Visibility.Visible : Visibility.Collapsed;
        if (SponsorEntryVisibility != entry) SponsorEntryVisibility = entry;
        SetSponsorPageOwner(usable && vm!.IsEmbedded && vm.Presentation == PlayerPresentationMode.Normal ? vm : null);

        if (isCompact && _sponsorFlyout?.IsOpen == true)
        {
            _sponsorFlyout.Hide();
        }

        if (!usable || vm is null || vm.IsWindowDeactivated || _sponsorFlyout?.IsOpen == true
            || _sponsorObserved?.HasOverlayContent != true)
        {
            SponsorNoticeHost.Visibility = Visibility.Collapsed;
            return;
        }

        var bottom = SponsorNoticeIdleBottom;
        if (vm.IsControlsVisible)
        {
            FrameworkElement? bar = UsesOverlayTransport(vm.Presentation) ? TransportControls : IslandTransport;
            bottom = (bar?.ActualHeight ?? 0) + SponsorNoticeGap;
        }

        var width = Math.Max(0, TransportLayer.ActualWidth - 32);
        SponsorNotices.Measure(new Size(width, double.PositiveInfinity));
        var room = TransportLayer.ActualHeight - bottom - SponsorNoticeGap;
        var fits = width > 0 && SponsorNotices.DesiredSize.Height > 0 && SponsorNotices.DesiredSize.Height <= room;
        SponsorNoticeHost.Margin = new Thickness(0, 0, 16, bottom);
        SponsorNoticeHost.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSponsorPageOwner(PlayerViewModel? owner)
    {
        if (ReferenceEquals(_sponsorPageOwner, owner)) return;
        _sponsorPageOwner = owner;
        SponsorPageEntryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSponsorPanelButtonClick(object sender, RoutedEventArgs e) => ToggleSponsorPanel();

    private void ToggleSponsorPanel()
    {
        if (!_loaded || !_sponsorViewsApplied || _sponsorPlayback is not { } playback || _sponsorEditor is not { } editor
            || ViewModel is not { } vm || !vm.IsControlsVisible || vm.IsWindowDeactivated
            || vm.IsPlaybackClosing || vm.IsConnecting || RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
        {
            return;
        }

        var flyout = EnsureSponsorFlyout();
        if (flyout.IsOpen)
        {
            flyout.Hide();
            return;
        }

        var panel = _sponsorPanel!;
        panel.PlaybackViewModel = playback;
        panel.EditorViewModel = editor;

        // Open below a visible header when it fits, otherwise at the video's top edge.
        var rootWidth = RootGrid.ActualWidth;
        var rootHeight = RootGrid.ActualHeight;
        var headerShown = IsOverlayHeaderVisible(vm.IsControlsVisible, vm.IsEmbedded, vm.Presentation) == Visibility.Visible;
        var top = headerShown && rootHeight >= 400 ? SponsorHeaderRow : SponsorEdge / 2;
        var panelWidth = Math.Max(SponsorPanelMinimumWidth, Math.Min(SponsorPanelPreferredWidth, rootWidth - (SponsorEdge * 2)));
        panel.Width = panelWidth;
        panel.MaxHeight = Math.Max(160, Math.Min(SponsorPanelMaxHeight, rootHeight - top - SponsorEdge));
        var left = Math.Max(SponsorEdge / 2, rootWidth - panelWidth - SponsorEdge);
        flyout.ShowAt(RootGrid, new FlyoutShowOptions
        {
            // Bottom centres the panel on this point: same right-aligned rectangle, opening downward.
            Position = new Point(left + (panelWidth / 2), top),
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private Flyout EnsureSponsorFlyout()
    {
        if (_sponsorFlyout is not null) return _sponsorFlyout;
        _sponsorPanel = new SponsorPlayerPanel();
        AutomationProperties.SetName(_sponsorPanel, SponsorUiText.FeatureName);
        var flyout = new Flyout
        {
            Content = _sponsorPanel,
            FlyoutPresenterStyle = (Style)Resources["SponsorPanelFlyoutPresenterStyle"],
        };
        // Same claim path as the header/source flyouts: refused when controls are hidden,
        // the window is inactive, playback is closing or connecting.
        flyout.Opened += OnOverlayFlyoutOpened;
        flyout.Opened += OnSponsorFlyoutStateChanged;
        flyout.Closed += OnSponsorFlyoutStateChanged;
        _sponsorFlyout = flyout;
        return flyout;
    }

    private void OnSponsorFlyoutStateChanged(object? sender, object e) => UpdateSponsorChrome();
}
