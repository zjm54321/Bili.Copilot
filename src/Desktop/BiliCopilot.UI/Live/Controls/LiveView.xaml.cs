#nullable enable

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Models.Media;
using Windows.System;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Video-first Live layout: header, host video anchor, a floating transport card and a toggleable chat list.
/// The transport card is placed once in the video island's overlay layer (VOD PlayerOverlay model) and serves
/// normal, full-screen and compact modes; presentation changes only switch its layout state.
/// The view owns only its subscriptions, toggles, composer state and the overlay instance it creates.
/// Playback, chat transport and native resources belong to the session/host.
/// </summary>
public sealed partial class LiveView : UserControl
{
    private const double WideChatWidth = 340;
    private const double CompactChatWidth = 284;
    private const double NarrowChatWidth = 260;
    private const double CompactBreakpoint = 960;
    private const double NarrowBreakpoint = 640;
    private const double FollowThreshold = 48;

    // Room-info copy owned by the view (LiveText is outside this lane).
    private const string FollowingCopy = "已关注";
    private const string NotFollowingCopy = "未关注";
    private const string QualityCopy = "清晰度";

    // Same idea as the VOD overlay: controls fade after a short idle period, in every presentation mode.
    private static readonly TimeSpan TransportIdleDelay = TimeSpan.FromSeconds(3);

    // A floating hint must not pin the card over the picture forever.
    private static readonly TimeSpan FeedbackLifetime = TimeSpan.FromSeconds(5);

    // OverlayTransportControls hide animation: the bar slides 80px down while it fades.
    private static readonly System.Numerics.Vector3 OverlayHiddenOffset = new(0, 80, 0);

    private readonly ILiveSession _session;
    private readonly ILivePresentation _presentation;
    private readonly LiveDanmakuOverlay _overlay;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private INotifyCollectionChanged? _messageSource;
    private CancellationTokenSource? _lifetime;
    private bool _attached;
    private int _refreshQueued;

    private bool _commandBusy;
    private bool _volumePumpRunning;
    private double _volumeDesired = double.NaN;
    private bool _syncingVolume;

    private bool _chatRequested = true;
    private bool _narrowOverride;
    private bool _isNarrow;
    private double _lastWidth;

    private Uri? _shownAvatar;
    private bool _syncingQuality;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idleTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _feedbackTimer;
    private bool _pointerOverTransport;
    private int _openTransportFlyouts;
    private bool _transportShown = true;
    private bool _compactLayout;
    private bool _overlayLayout;
    private Grid? _transportOverlayHost;

    private ScrollViewer? _scroller;
    private bool _followLatest = true;
    private int _unseen;

    private LiveBinding? _shownBinding;
    private bool _bindingSeen;
    private long _sendGeneration;
    private bool _sendInFlight;
    private bool _feedbackTransient;
    private bool _composing;

    public LiveView(ILiveSession session, UIElement videoSurface, ILivePresentation presentation)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        ArgumentNullException.ThrowIfNull(videoSurface);
        InitializeComponent();
        _dispatcher = DispatcherQueue;

        // Layout anchor only: the host keeps engine/HWND ownership.
        VideoHost.Child = videoSurface;

        // The card is generated with this view but never laid out in this tree: it waits, unparented,
        // for AttachTransportOverlay to place it in the island overlay layer exactly once.
        RootGrid.Children.Remove(TransportCard);

        _overlay = new LiveDanmakuOverlay(session);
        DanmakuOverlay = _overlay;

        ApplyStaticCopy();
        FullScreenButton.Command = new RelayCommand(() => RunPresentationCommand(() =>
        {
            if (_presentation.IsFullScreen) _presentation.ExitFullScreen();
            else _presentation.EnterFullScreen();
        }));
        CompactOverlayButton.Command = new RelayCommand(() => RunPresentationCommand(() =>
        {
            if (_presentation.IsCompactOverlay) _presentation.ExitCompactOverlay();
            else _presentation.EnterCompactOverlay();
        }));

        _idleTimer = _dispatcher.CreateTimer();
        _idleTimer.Interval = TransportIdleDelay;
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += OnIdleTimerTick;
        _feedbackTimer = _dispatcher.CreateTimer();
        _feedbackTimer.Interval = FeedbackLifetime;
        _feedbackTimer.IsRepeating = false;
        _feedbackTimer.Tick += OnFeedbackTimerTick;
        // Picture activity arrives through RevealTransport (the island's input layer); the card reports its own.
        TransportCard.PointerEntered += OnTransportPointerEntered;
        TransportCard.PointerExited += OnTransportPointerExited;
        TransportCard.GotFocus += OnTransportGotFocus;
        TransportCard.AddHandler(PointerMovedEvent, new PointerEventHandler(OnTransportPointerActivity), true);
        TransportCard.AddHandler(PointerPressedEvent, new PointerEventHandler(OnTransportPointerActivity), true);
        TransportCard.Loaded += OnTransportLoaded;
        ActualThemeChanged += OnActualThemeChanged;

        MessageList.ContainerContentChanging += OnMessageContainerChanging;
        MessageList.Loaded += OnMessageListLoaded;
        ComposerBox.TextCompositionStarted += OnCompositionStarted;
        ComposerBox.TextCompositionEnded += OnCompositionEnded;
        RootGrid.SizeChanged += OnRootSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _lifetime = new CancellationTokenSource();
        Attach(); // Subscribe first, then read the snapshot inside Attach.
    }

    /// <summary>Preferred initial window size in DIPs. The host clamps it to the monitor work area.</summary>
    public static Size PreferredWindowSize { get; } = new(1180, 720);

    /// <summary>Minimum window size in DIPs. Fits small tablet work areas; the chat list auto-collapses below it.</summary>
    public static Size MinimumWindowSize { get; } = new(560, 400);

    /// <summary>
    /// The single danmaku overlay created for this view. The native host attaches this exact
    /// instance inside its video island; the view never renders danmaku over the HWND itself.
    /// </summary>
    public UIElement DanmakuOverlay { get; }

    /// <summary>The native picture island forwards pointer movement; outer-tree pointer events cannot cross HWNDs.</summary>
    internal void RevealTransport()
    {
        if (_attached) ShowTransport();
    }

    /// <summary>
    /// Places the single transport card in the island overlay layer. Called once, before the island is shown;
    /// the card then stays there for every presentation mode and its flyouts always use the island XamlRoot.
    /// </summary>
    internal void AttachTransportOverlay(Grid host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_transportOverlayHost is not null || VisualTreeHelper.GetParent(TransportCard) is not null)
            throw new InvalidOperationException("Live transport overlay is already attached.");
        host.Children.Add(TransportCard);
        _transportOverlayHost = host;
        BindTransportRoot();
    }

    private void ApplyStaticCopy()
    {
        QualityCaption.Text = QualityCopy;
        VolumeCaption.Text = LiveText.Volume;
        ChatHeaderText.Text = LiveText.ChatHeader;
        JumpText.Text = LiveText.JumpToLatest;
        AutomationProperties.SetName(RefreshButton, LiveText.RefreshName);
        AutomationProperties.SetName(VolumeSlider, LiveText.Volume);
        AutomationProperties.SetName(OverlayToggle, LiveText.OverlayToggleName);
        AutomationProperties.SetName(ChatToggle, LiveText.ChatToggleName);
        AutomationProperties.SetName(MessageList, LiveText.ChatHeader);
        AutomationProperties.SetName(JumpButton, LiveText.JumpToLatest);
        AutomationProperties.SetName(ComposerBox, LiveText.ComposerName);
        AutomationProperties.SetName(SendButton, LiveText.SendName);
        AutomationProperties.SetName(VolumeButton, LiveText.Volume);

        // Icon-only commands carry their name as a tooltip, as on the VOD transport.
        ToolTipService.SetToolTip(RefreshButton, LiveText.RefreshName);
        ToolTipService.SetToolTip(OverlayToggle, LiveText.OverlayToggleName);
        ToolTipService.SetToolTip(ChatToggle, LiveText.ChatToggleName);
        ToolTipService.SetToolTip(SendButton, LiveText.SendName);
        ToolTipService.SetToolTip(VolumeButton, LiveText.Volume);
    }

    // ---------------------------------------------------------------- lifecycle

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _session.PropertyChanged += OnSessionPropertyChanged;
        _presentation.PropertyChanged += OnPresentationPropertyChanged;
        BindMessages();
        RefreshAll();
        UpdatePresentation();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _presentation.PropertyChanged -= OnPresentationPropertyChanged;
        if (_messageSource is not null) _messageSource.CollectionChanged -= OnMessagesChanged;
        _messageSource = null;
        MessageList.ItemsSource = null;
        if (_scroller is not null) _scroller.ViewChanged -= OnScrollViewChanged;
        _scroller = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime is null || _lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        Attach();
        HookScroller();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return; // Re-parenting can raise Loaded before the old Unloaded.
        RunPresentationCommand(_presentation.RestorePresentation);
        // Cancel view-owned awaits; never dispose here so already-captured tokens stay valid.
        _lifetime?.Cancel();
        _lifetime = null;
        _idleTimer.Stop();
        _feedbackTimer.Stop();
        Detach();
    }

    private void BindMessages()
    {
        var messages = _session.Messages;
        if (ReferenceEquals(MessageList.ItemsSource, messages) && _messageSource is not null) return;
        if (_messageSource is not null) _messageSource.CollectionChanged -= OnMessagesChanged;
        _messageSource = messages;
        _messageSource.CollectionChanged += OnMessagesChanged;
        MessageList.ItemsSource = messages; // The bounded session collection; never copied.
        _unseen = 0;
        SetFollow(true);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            // The session publishes on the UI thread; this is only a coalesced safety net.
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0
                && !_dispatcher.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    if (_attached) RefreshAll();
                }))
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
            }

            return;
        }

        if (_attached) RefreshAll();
    }

    // ---------------------------------------------------------------- state projection

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_attached && args.PropertyName is nameof(ILivePresentation.IsFullScreen) or nameof(ILivePresentation.IsCompactOverlay))
            UpdatePresentation();
    }

    private void RunPresentationCommand(Action command)
    {
        if (!_attached) return;
        try { command(); }
        catch (Exception) { SetCommandFeedback(LiveText.CommandFailed); }
    }

    private void UpdatePresentation()
    {
        var fullScreen = _presentation.IsFullScreen;
        var compact = _presentation.IsCompactOverlay;
        FullScreenIcon.Symbol = fullScreen ? FluentIcons.Common.Symbol.FullScreenMinimize : FluentIcons.Common.Symbol.FullScreenMaximize;
        CompactOverlayIcon.Symbol = compact ? FluentIcons.Common.Symbol.ContractDownLeft : FluentIcons.Common.Symbol.ContractUpRight;
        var fullScreenTip = ResourceToolkit.GetLocalizedString(fullScreen ? StringNames.ExitFullScreen : StringNames.EnterFullScreen);
        var compactTip = ResourceToolkit.GetLocalizedString(compact ? StringNames.ExitCompactOverlay : StringNames.EnterCompactOverlay);
        ToolTipService.SetToolTip(FullScreenButton, fullScreenTip);
        ToolTipService.SetToolTip(CompactOverlayButton, compactTip);
        AutomationProperties.SetName(FullScreenButton, fullScreenTip);
        AutomationProperties.SetName(CompactOverlayButton, compactTip);
        var hideHeader = compact || fullScreen;
        PageHeader.Visibility = hideHeader ? Visibility.Collapsed : Visibility.Visible;
        HeaderDivider.Visibility = hideHeader ? Visibility.Collapsed : Visibility.Visible;
        RootGrid.RowDefinitions[0].Height = new GridLength(hideHeader ? 0 : 56);
        ApplyLayout();
        ApplyTransportLayout(fullScreen || compact, compact);
        ShowTransport(); // A mode switch is user activity: show, then the usual idle countdown.
    }

    /// <summary>
    /// Layout/style state only — the card never changes parent and the command order never changes.
    /// Normal = the VOD IslandTransportControls card; full screen and compact = the VOD OverlayTransportControls bar
    /// (edge-to-edge gradient, centred primary play, player icon buttons). Compact keeps the VOD compact essentials
    /// (play/pause, volume, exit compact); the danmaku box, refresh, quality and full-screen commands step aside.
    /// </summary>
    private void ApplyTransportLayout(bool overlay, bool compact)
    {
        if (_compactLayout != compact || _overlayLayout != overlay)
        {
            // Anchored popups would float at the old card position after the window resizes.
            QualityFlyout.Hide();
            VolumeFlyout.Hide();
        }

        _compactLayout = compact;
        _overlayLayout = overlay;
        var full = compact ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = full;
        ComposerGroup.Visibility = full;
        FullScreenButton.Visibility = full;

        TransportCard.Style = LocalStyle(overlay ? "LiveOverlayTransportCardStyle" : "LiveIslandTransportCardStyle");
        if (overlay) TransportCard.Height = compact ? 120 : 160; // OverlayTransportControls NarrowState / default height.
        else TransportCard.ClearValue(HeightProperty);
        TransportContent.Padding = overlay ? new Thickness(16, 0, 8, 0) : new Thickness(12, 8, 12, 8);
        TransportContent.VerticalAlignment = overlay ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        // Overlay: * | Auto | * keeps play centred (VOD Left / Middle / Right); island: Auto | * | Auto.
        CommandLeftColumn.Width = overlay ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CommandMiddleColumn.Width = overlay ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        CommandRightColumn.Width = overlay ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CommandRow.ColumnSpacing = overlay ? 0 : 8;
        Grid.SetColumn(ComposerGroup, overlay ? 0 : 1);
        Grid.SetColumn(PlaybackGroup, overlay ? 1 : 0);
        ComposerGroup.HorizontalAlignment = overlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ComposerGroup.Margin = overlay ? new Thickness(0, 0, 24, 0) : default; // VOD LeftPresenter margin.
        ComposerGroup.MaxWidth = overlay ? 400 : double.PositiveInfinity;
        WindowGroup.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        WindowGroup.Margin = overlay ? new Thickness(0, 0, 8, 0) : default; // VOD right commands keep 8px after the last one.

        // The composer and hint text sit on the black gradient in overlay modes; keep them legible in light theme.
        var overlayTheme = overlay ? ElementTheme.Dark : ElementTheme.Default;
        ComposerGroup.RequestedTheme = overlayTheme;
        FeedbackRow.RequestedTheme = overlayTheme;

        PlayPauseButton.Style = LocalStyle(overlay ? "LiveOverlayPlayButtonStyle" : "LiveIslandPlayButtonStyle");
        PlayPauseIcon.FontSize = overlay ? 22 : 14;
        RefreshIcon.FontSize = overlay ? 16 : 14;
        OverlayToggle.Style = LocalStyle(overlay ? "LiveOverlayDanmakuToggleStyle" : "LiveIslandDanmakuToggleStyle");
        var iconStyle = LocalStyle(overlay ? "LiveOverlayIconButtonStyle" : "LiveIslandIconButtonStyle");
        RefreshButton.Style = iconStyle;
        SendButton.Style = iconStyle;
        QualityButton.Style = iconStyle;
        VolumeButton.Style = iconStyle;
        CompactOverlayButton.Style = iconStyle;
        FullScreenButton.Style = iconStyle;
        UpdateQuality();
    }

    private void OnTransportLoaded(object sender, RoutedEventArgs args) => BindTransportRoot();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => BindTransportRoot();

    /// <summary>Flyouts open in the island root, never the outer LiveView root; the island follows the view's theme.</summary>
    private void BindTransportRoot()
    {
        if (TransportCard.XamlRoot is not { } root) return; // Not loaded in the island yet; Loaded binds it.
        if (!ReferenceEquals(QualityFlyout.XamlRoot, root)) QualityFlyout.XamlRoot = root;
        if (!ReferenceEquals(VolumeFlyout.XamlRoot, root)) VolumeFlyout.XamlRoot = root;
        if (root.Content is FrameworkElement islandRoot && islandRoot.RequestedTheme != ActualTheme)
            islandRoot.RequestedTheme = ActualTheme;
    }

    private void RefreshAll()
    {
        BindMessages();

        var binding = _session.Binding;
        if (!_bindingSeen || binding != _shownBinding)
        {
            var first = !_bindingSeen;
            _bindingSeen = true;
            _shownBinding = binding;
            if (!first) OnBindingChanged();
        }

        var playback = _session.PlaybackState;
        var chat = _session.ChatState;
        var busy = _session.IsBusy;

        TitleText.Text = string.IsNullOrWhiteSpace(_session.Title) ? LiveText.UntitledRoom : _session.Title;
        PlaybackStatusText.Text = LiveText.Playback(playback);
        PlaybackDot.Fill = PlaybackBrush(playback);
        ChatStatusText.Text = LiveText.Chat(chat);
        ChatDot.Fill = ChatBrush(chat);

        var transportReady = !busy && !_commandBusy
            && playback is LivePlaybackState.Playing or LivePlaybackState.Paused
                or LivePlaybackState.Ended or LivePlaybackState.Failed;
        PlayPauseButton.IsEnabled = transportReady;
        RefreshButton.IsEnabled = transportReady;
        var playing = playback == LivePlaybackState.Playing;
        PlayPauseIcon.Symbol = playing ? FluentIcons.Common.Symbol.Pause : FluentIcons.Common.Symbol.Play;
        AutomationProperties.SetName(PlayPauseButton, playing ? LiveText.Pause : LiveText.Play);
        ToolTipService.SetToolTip(PlayPauseButton, playing ? LiveText.Pause : LiveText.Play);

        VolumeSlider.IsEnabled = binding is not null && !busy
            && playback is LivePlaybackState.Playing or LivePlaybackState.Paused;
        if (!_volumePumpRunning) SyncVolume(_session.Volume);

        UpdateRoomInfo();
        UpdateQuality();
        UpdateComposerState();
        UpdateEmptyText();
        UpdateJumpButton();
        EvaluateTransport();
    }

    /// <summary>Anchor avatar/name, follow state (hidden while unknown) and audience metric (hidden until received).</summary>
    private void UpdateRoomInfo()
    {
        var anchor = _session.AnchorName;
        var hasAnchor = !string.IsNullOrWhiteSpace(anchor);
        AnchorNameText.Text = hasAnchor ? anchor : string.Empty;
        AnchorNameText.Visibility = hasAnchor ? Visibility.Visible : Visibility.Collapsed;
        AnchorPicture.DisplayName = hasAnchor ? anchor : string.Empty; // Initials fallback while/if the image is unavailable.
        AnchorButton.IsEnabled = !string.IsNullOrEmpty(_session.Anchor?.Id);
        var spaceName = ResourceToolkit.GetLocalizedString(StringNames.UserSpace);
        var anchorAction = hasAnchor ? $"{anchor}：{spaceName}" : spaceName;
        AutomationProperties.SetName(AnchorButton, anchorAction);
        ToolTipService.SetToolTip(AnchorButton, anchorAction);

        var avatar = _session.AnchorAvatar;
        if (!Equals(avatar, _shownAvatar))
        {
            // Only rebuild the image when the URI really changes; RefreshAll runs on every session notification.
            _shownAvatar = avatar;
            AnchorPicture.ProfilePicture = avatar is { IsAbsoluteUri: true }
                ? new BitmapImage(avatar) { DecodePixelWidth = 72, DecodePixelType = DecodePixelType.Logical }
                : null;
        }

        var following = _session.IsFollowing;
        FollowTag.Visibility = following is null ? Visibility.Collapsed : Visibility.Visible;
        if (following is { } isFollowing) FollowText.Text = isFollowing ? FollowingCopy : NotFollowingCopy;

        var count = _session.AudienceCount;
        AudiencePanel.Visibility = count is null ? Visibility.Collapsed : Visibility.Visible;
        if (count is { } audience)
        {
            AudienceText.Text = string.Format(CultureInfo.CurrentCulture, "{0} {1:N0}", _session.AudienceLabel, audience);
            AutomationProperties.SetName(AudiencePanel, AudienceText.Text);
        }

        AnchorMetaRow.Visibility = hasAnchor || following is not null || count is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Quality button (VOD IslandFormatButton): shown once the room reports any quality; enabled only when a switch is allowed.</summary>
    private void UpdateQuality()
    {
        var selected = _session.SelectedQuality;
        QualityButton.Visibility = !_compactLayout && (_session.Qualities.Count > 0 || selected is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;
        QualityButton.IsEnabled = _session.CanChangeQuality && !_commandBusy;
        var name = selected is null ? QualityCopy : $"{QualityCopy}：{selected.Description}";
        AutomationProperties.SetName(QualityButton, name);
        ToolTipService.SetToolTip(QualityButton, name);
        if (_openTransportFlyouts > 0 && QualityView.ItemsSource is not null) SyncQualityView();
    }

    private void OnQualityFlyoutOpened(object sender, object e)
    {
        OnTransportFlyoutOpened(sender, e);
        SyncQualityView();
    }

    /// <summary>Mirror the session list and its actual (not optimistic) selection into the flyout list.</summary>
    private void SyncQualityView()
    {
        _syncingQuality = true;
        try
        {
            var qualities = _session.Qualities;
            if (!ReferenceEquals(QualityView.ItemsSource, qualities)) QualityView.ItemsSource = qualities;
            var selected = _session.SelectedQuality;
            var index = -1;
            for (var i = 0; i < qualities.Count && selected is not null; i++)
            {
                if (qualities[i]?.Quality == selected.Quality) { index = i; break; }
            }

            if (index >= 0) QualityView.Select(index);
            else QualityView.DeselectAll();
        }
        finally
        {
            _syncingQuality = false;
        }
    }

    private async void OnQualityItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        if (_syncingQuality || !_attached || !_session.CanChangeQuality
            || args.InvokedItem is not PlayerFormatInformation quality
            || !_session.Qualities.Any(item => item?.Quality == quality.Quality)) return;
        if (_session.SelectedQuality is { } current && current.Quality == quality.Quality)
        {
            QualityFlyout.Hide(); // Re-picking the current quality is a no-op, as in the VOD format button.
            return;
        }

        QualityFlyout.Hide();
        try
        {
            // Parameter is the list item itself (session contract). Failures surface through PlaybackError.
            await _session.SelectQualityCommand.ExecuteAsync(quality);
        }
        catch (Exception)
        {
            if (_attached) SetCommandFeedback(LiveText.CommandFailed);
        }
    }

    private void OnBindingChanged()
    {
        // Room/account changed (including refresh): the old composer binding is void.
        // A late acknowledgement for the previous binding must not touch this room's UI.
        _sendGeneration++;
        _sendInFlight = false;
        ShowSendFeedback(null, null);
        SetCommandFeedback(null);
        _unseen = 0;
        SetFollow(true);
    }

    private Brush PlaybackBrush(LivePlaybackState state) => state switch
    {
        LivePlaybackState.Playing => LocalBrush("LiveStateLiveBrush"),
        LivePlaybackState.Opening or LivePlaybackState.Paused => LocalBrush("LiveStateWarnBrush"),
        LivePlaybackState.Failed or LivePlaybackState.Ended => LocalBrush("LiveStateErrorBrush"),
        _ => LocalBrush("LiveStateIdleBrush"),
    };

    private Brush ChatBrush(LiveChatState state) => state switch
    {
        LiveChatState.Connected => LocalBrush("LiveStateLiveBrush"),
        LiveChatState.Connecting or LiveChatState.Authenticating or LiveChatState.Retrying => LocalBrush("LiveStateWarnBrush"),
        LiveChatState.Failed or LiveChatState.Unavailable => LocalBrush("LiveStateErrorBrush"),
        _ => LocalBrush("LiveStateIdleBrush"),
    };

    private Brush LocalBrush(string key) => (Brush)Resources[key];

    private Style LocalStyle(string key) => (Style)Resources[key];

    // ---------------------------------------------------------------- transport

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
        => await RunCommandAsync(token => _session.PlaybackState == LivePlaybackState.Playing
            ? _session.PauseAsync(token)
            : _session.PlayAsync(token)); // The session routes Paused/Ended play to a fresh live-edge load.

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RunCommandAsync(token => _session.RefreshAsync(token));

    private async Task RunCommandAsync(Func<CancellationToken, Task> command)
    {
        if (_commandBusy || !_attached || _lifetime is not { IsCancellationRequested: false } lifetime) return;
        var token = lifetime.Token;
        _commandBusy = true;
        SetCommandFeedback(null);
        RefreshAll();
        try
        {
            await command(token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by unload/close or superseded by the session; nothing to report.
        }
        catch (Exception)
        {
            // Fixed copy only; exception text never reaches the UI.
            if (_attached) SetCommandFeedback(LiveText.CommandFailed);
        }
        finally
        {
            _commandBusy = false;
            if (_attached) RefreshAll();
        }
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_attached) return; // Also ignores events raised while InitializeComponent is still running.
        UpdateVolumeVisual(e.NewValue);
        if (_syncingVolume) return;
        _volumeDesired = Math.Clamp(e.NewValue / 100d, 0d, 1d);
        if (!_volumePumpRunning) _ = PumpVolumeAsync();
    }

    /// <summary>
    /// Coalesces slider movement into at most one in-flight session call; the newest value wins.
    /// All exceptions are handled here so the discarded task can never fault unobserved.
    /// </summary>
    private async Task PumpVolumeAsync()
    {
        _volumePumpRunning = true;
        try
        {
            var sent = double.NaN;
            while (_attached && _lifetime is { IsCancellationRequested: false } lifetime && !_volumeDesired.Equals(sent))
            {
                sent = _volumeDesired;
                await _session.SetVolumeAsync(sent, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (_attached) SetCommandFeedback(LiveText.VolumeFailed);
        }
        finally
        {
            _volumePumpRunning = false;
            // If the session dropped a request (busy), snap back to the real value.
            if (_attached) RefreshAll();
        }
    }

    private void SyncVolume(double volume)
    {
        if (!double.IsFinite(volume)) return;
        var target = Math.Clamp(volume, 0d, 1d) * 100d;
        if (Math.Abs(VolumeSlider.Value - target) < 0.5)
        {
            UpdateVolumeVisual(VolumeSlider.Value);
            return;
        }

        _syncingVolume = true;
        try { VolumeSlider.Value = target; }
        finally { _syncingVolume = false; }
    }

    private void UpdateVolumeVisual(double percent)
    {
        VolumeText.Text = LiveText.VolumeValue(percent);
        VolumeIcon.Symbol = percent < 0.5 ? FluentIcons.Common.Symbol.SpeakerMute : FluentIcons.Common.Symbol.Speaker2;
    }

    // ---------------------------------------------------------------- toggles and responsive layout

    private void OnOverlayToggleClick(object sender, RoutedEventArgs e)
    {
        // View-only: hides/clears overlay drawing. Playback and the chat connection are untouched.
        _overlay.IsDanmakuVisible = OverlayToggle.IsChecked == true;
    }

    /// <summary>
    /// Same in-app route as the VOD publisher avatar (VideoConnectorViewModel.OpenUserSpace). The shared navigation
    /// queue first closes this Live owner (navigation exits Live), then opens the user space overlay page.
    /// </summary>
    private void OnAnchorClick(object sender, RoutedEventArgs e)
    {
        if (_session.Anchor is { } anchor)
            this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), anchor);
    }

    private void OnChatToggleClick(object sender, RoutedEventArgs e)
    {
        var show = ChatPanel.Visibility != Visibility.Visible; // SideSplitButton semantics: a click flips the panel.
        if (_isNarrow)
        {
            _narrowOverride = show;
            if (show) _chatRequested = true;
        }
        else
        {
            _chatRequested = show;
        }

        ApplyLayout();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _lastWidth = e.NewSize.Width;
        var narrow = _lastWidth < NarrowBreakpoint;
        if (!narrow) _narrowOverride = false;
        _isNarrow = narrow;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var show = !_presentation.IsCompactOverlay && !_presentation.IsFullScreen
            && _chatRequested && (!_isNarrow || _narrowOverride);
        ChatPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.Width = new GridLength(!show ? 0 : _isNarrow ? NarrowChatWidth
            : _lastWidth < CompactBreakpoint ? CompactChatWidth : WideChatWidth);
        ChatToggleIcon.Symbol = show ? FluentIcons.Common.Symbol.PanelRightContract : FluentIcons.Common.Symbol.PanelRightExpand;
        if (show) UpdateJumpButton();
    }

    // ---------------------------------------------------------------- chat list

    private void OnMessageListLoaded(object sender, RoutedEventArgs e) => HookScroller();

    private void HookScroller()
    {
        if (_scroller is not null || !_attached) return;
        _scroller = FindDescendant<ScrollViewer>(MessageList);
        if (_scroller is null) return;
        _scroller.ViewChanged += OnScrollViewChanged;
        ApplyScrollMode();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is null || e.IsIntermediate) return;
        var atEnd = _scroller.ScrollableHeight - _scroller.VerticalOffset <= FollowThreshold;
        if (atEnd) _unseen = 0;
        SetFollow(atEnd);
        UpdateJumpButton();
    }

    private void SetFollow(bool follow)
    {
        if (_followLatest == follow) return;
        _followLatest = follow;
        ApplyScrollMode();
    }

    private void ApplyScrollMode()
    {
        // At the end: built-in bounded follow. Scrolled back: keep the rows the user is reading in place,
        // including when the session trims its oldest rows at the 200 cap.
        if (MessageList.ItemsPanelRoot is ItemsStackPanel panel)
        {
            panel.ItemsUpdatingScrollMode = _followLatest
                ? ItemsUpdatingScrollMode.KeepLastItemInView
                : ItemsUpdatingScrollMode.KeepItemsInView;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_attached || !_dispatcher.HasThreadAccess) return;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when !_followLatest:
                _unseen = Math.Min(_unseen + (e.NewItems?.Count ?? 1), 1000);
                break;
            case NotifyCollectionChangedAction.Reset:
                _unseen = 0;
                SetFollow(true);
                break;
        }

        UpdateEmptyText();
        UpdateJumpButton();
    }

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        var messages = _session.Messages;
        _unseen = 0;
        SetFollow(true);
        if (messages.Count > 0) MessageList.ScrollIntoView(messages[messages.Count - 1], ScrollIntoViewAlignment.Leading);
        UpdateJumpButton();
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void UpdateJumpButton()
    {
        var show = !_followLatest && _session.Messages.Count > 0;
        JumpButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var text = LiveText.NewMessages(_unseen);
        JumpText.Text = text;
        AutomationProperties.SetName(JumpButton, text);
    }

    private void UpdateEmptyText()
    {
        var empty = _session.Messages.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            EmptyText.Text = _session.ChatState == LiveChatState.Connected
                ? LiveText.EmptyConnected
                : LiveText.Chat(_session.ChatState);
        }
    }

    private void OnMessageContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Records' ToString() includes binding/account data; give assistive tech only the visible text.
        if (args.InRecycleQueue || args.Item is not LiveChatMessage message) return;
        AutomationProperties.SetName(args.ItemContainer, $"{DisplayName(message)}：{message.Text}");
    }

    private static string DisplayName(LiveChatMessage message)
        => string.IsNullOrWhiteSpace(message.UserName) ? LiveText.AnonymousUser : message.UserName;

    // ---------------------------------------------------------------- composer and manual send

    private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _composing = true;

    private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _composing = false;

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_attached) return;
        if (_feedbackTransient) ShowSendFeedback(null, null);
        UpdateComposerState();
        ShowTransport(); // A draft keeps the controls up; clearing it restarts the idle countdown.
    }

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Plain Enter sends; Enter that commits an IME candidate does not.
        if (e.Key != VirtualKey.Enter || _composing || e.KeyStatus.WasKeyDown) return;
        e.Handled = true;
        await SendAsync();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_sendInFlight || !_attached || _lifetime is not { IsCancellationRequested: false } lifetime) return;
        var text = ComposerBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_session.Binding is not { } binding || !_session.CanSend)
        {
            ShowSendFeedback(LiveText.SendUnavailable, "LiveStateWarnBrush");
            return;
        }

        var generation = ++_sendGeneration;
        _sendInFlight = true;
        ShowSendFeedback(LiveText.Sending, null);
        UpdateComposerState();

        LiveSendResult? result = null;
        try
        {
            result = await _session.SendManuallyAsync(binding, text, lifetime.Token);
        }
        catch (Exception)
        {
            // Includes cancellation: after dispatch, cancellation does not prove non-delivery.
            result = null;
        }

        // Stale attempt: room/account changed or view unloaded while waiting. Do not touch current UI.
        if (generation != _sendGeneration) return;
        _sendInFlight = false;
        if (!_attached) return;
        if (_session.Binding != binding || (result is not null && result.Binding != binding))
        {
            ShowSendFeedback(null, null);
            UpdateComposerState();
            return;
        }

        switch (result?.Outcome)
        {
            case LiveSendOutcome.Accepted:
                // No optimistic row: the message appears when the room echoes it back.
                if (string.Equals(ComposerBox.Text, text, StringComparison.Ordinal)) ComposerBox.Text = string.Empty;
                ShowSendFeedback(LiveText.SendAccepted, null, transient: true);
                break;
            case LiveSendOutcome.Rejected:
                ShowSendFeedback(LiveText.SendRejected(result.ApiCode), "LiveStateErrorBrush");
                break;
            case LiveSendOutcome.NotSent:
                ShowSendFeedback(LiveText.SendNotSent, "LiveStateWarnBrush");
                break;
            default:
                // Unknown or no result: keep the text, never resend automatically.
                ShowSendFeedback(LiveText.SendUnknown, "LiveStateWarnBrush");
                break;
        }

        UpdateComposerState();
    }

    private void UpdateComposerState()
    {
        var canSend = _session.CanSend && _session.Binding is not null;
        ComposerBox.PlaceholderText = canSend ? LiveText.ComposerPlaceholder : LiveText.ComposerPlaceholderUnavailable;
        SendButton.IsEnabled = canSend && !_sendInFlight && !string.IsNullOrWhiteSpace(ComposerBox.Text);
    }

    private void ShowSendFeedback(string? text, string? brushKey, bool transient = false)
    {
        _feedbackTransient = transient;
        if (string.IsNullOrEmpty(text))
        {
            SendFeedbackText.Text = string.Empty;
            SendFeedbackText.Visibility = Visibility.Collapsed;
            UpdateFeedbackRow();
            return;
        }

        SendFeedbackText.Text = text;
        if (brushKey is null) SendFeedbackText.ClearValue(TextBlock.ForegroundProperty);
        else SendFeedbackText.Foreground = LocalBrush(brushKey);
        SendFeedbackText.Visibility = Visibility.Visible;
        UpdateFeedbackRow();
        ShowTransport();
    }

    private void SetCommandFeedback(string? text)
    {
        CommandFeedbackText.Text = text ?? string.Empty;
        CommandFeedbackText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateFeedbackRow();
        if (!string.IsNullOrEmpty(text)) ShowTransport();
    }

    /// <summary>
    /// The feedback line sits on top of the floating card (the VOD progress row position) and only takes space while
    /// it has something to say. Settled hints dismiss themselves; "sending" stays until the attempt resolves.
    /// </summary>
    private void UpdateFeedbackRow()
    {
        var visible = SendFeedbackText.Visibility == Visibility.Visible
            || CommandFeedbackText.Visibility == Visibility.Visible;
        FeedbackRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _feedbackTimer.Stop();
        if (visible && !_sendInFlight) _feedbackTimer.Start();
    }

    private void OnFeedbackTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_attached || _sendInFlight) return;
        ShowSendFeedback(null, null);
        SetCommandFeedback(null);
        EvaluateTransport(); // The hint no longer holds the card: resume the idle countdown.
    }

    // ---------------------------------------------------------------- transport auto-hide (VOD overlay behaviour, every mode)

    /// <summary>What holds the card on screen: pointer on it, an open flyout, a visible draft or a hint.</summary>
    private bool MustKeepTransport()
        => !_attached
            || _pointerOverTransport
            || _openTransportFlyouts > 0
            || (ComposerGroup.Visibility == Visibility.Visible && ComposerBox.Text.Length > 0)
            || FeedbackRow.Visibility == Visibility.Visible;

    /// <summary>Reveal the card and (re)start the idle countdown unless something holds it.</summary>
    private void ShowTransport()
    {
        SetTransportShown(true);
        _idleTimer.Stop();
        if (!MustKeepTransport()) _idleTimer.Start();
    }

    /// <summary>Called from RefreshAll: session updates never reveal the card nor restart a running countdown.</summary>
    private void EvaluateTransport()
    {
        if (!_transportShown) return;
        if (MustKeepTransport()) _idleTimer.Stop();
        else if (!_idleTimer.IsRunning) _idleTimer.Start();
    }

    private void OnIdleTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!MustKeepTransport()) SetTransportShown(false);
    }

    /// <summary>
    /// Hidden = faded and not hit-testable, so clicks, drags and double-clicks go to the picture layer below.
    /// It stays Visible for the fade and for keyboard focus (focusing it reveals it again).
    /// </summary>
    private void SetTransportShown(bool shown)
    {
        _transportShown = shown;
        if (!shown)
        {
            _idleTimer.Stop();
            _pointerOverTransport = false;
        }

        TransportCard.IsHitTestVisible = shown;
        TransportCard.Opacity = shown ? 1 : 0;
        // Overlay bar (full screen / compact) also slides like OverlayTransportControls; the island card only fades.
        TransportCard.Translation = shown || !_overlayLayout ? System.Numerics.Vector3.Zero : OverlayHiddenOffset;
    }

    private void OnTransportPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        if (_transportShown) ShowTransport();
    }

    private void OnTransportPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverTransport = true;
        ShowTransport();
    }

    private void OnTransportPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverTransport = false;
        if (_transportShown) ShowTransport();
    }

    private void OnTransportGotFocus(object sender, RoutedEventArgs e) => ShowTransport();

    private void OnTransportFlyoutOpened(object sender, object e)
    {
        _openTransportFlyouts++;
        ShowTransport();
    }

    private void OnTransportFlyoutClosed(object sender, object e)
    {
        _openTransportFlyouts = Math.Max(0, _openTransportFlyouts - 1);
        if (_transportShown) ShowTransport();
    }

    // ---------------------------------------------------------------- helpers

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
