#nullable enable

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
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
/// Video-first Live layout: header, host video anchor, transport bar and a toggleable chat list.
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

    // Same idea as the VOD overlay: controls fade after a short idle period while playing.
    private static readonly TimeSpan TransportIdleDelay = TimeSpan.FromSeconds(3);

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
    private bool _pointerOverTransport;
    private int _openTransportFlyouts;
    private bool _fullScreenLayout;
    private bool _fullScreenTransportShown;
    private Grid? _transportOverlayHost;
    private bool _changingTransport;

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
        // Pointer input anywhere on the player stage reveals the transport (handled events included, like the VOD overlay).
        StageGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(OnStagePointerActivity), true);
        StageGrid.AddHandler(PointerPressedEvent, new PointerEventHandler(OnStagePointerActivity), true);
        TransportCard.PointerEntered += OnTransportPointerEntered;
        TransportCard.PointerExited += OnTransportPointerExited;
        TransportCard.GotFocus += OnTransportFocusChanged;
        TransportCard.LostFocus += OnTransportFocusChanged;
        TransportCard.AddHandler(PointerMovedEvent, new PointerEventHandler(OnStagePointerActivity), true);
        TransportCard.AddHandler(PointerPressedEvent, new PointerEventHandler(OnStagePointerActivity), true);
        TransportCard.Loaded += OnTransportLoaded;

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

    internal event EventHandler? VideoLayoutChanged;

    /// <summary>The native picture island forwards activity; outer-tree pointer events cannot cross HWNDs.</summary>
    internal void RevealTransport()
    {
        if (_attached) ShowTransport();
    }

    /// <summary>One transport instance, with the same commands and event handlers in either XamlRoot.</summary>
    internal void AttachTransportOverlay(Grid host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _transportOverlayHost = host;
    }

    private void ApplyStaticCopy()
    {
        BadgeText.Text = LiveText.LiveBadge;
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
        var fullScreenChanged = _fullScreenLayout != fullScreen;
        _fullScreenLayout = fullScreen;
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
        if (fullScreenChanged) MoveTransport(fullScreen);
        if (fullScreen)
        {
            // Both state properties notify: the second notification must not reveal the row again.
            if (fullScreenChanged) HideFullScreenTransport();
        }
        else
        {
            _fullScreenTransportShown = false;
            TransportCard.Visibility = Visibility.Visible;
            TransportCard.IsHitTestVisible = true;
            ShowTransport();
        }
    }

    private void MoveTransport(bool intoIsland)
    {
        var target = intoIsland ? _transportOverlayHost
            ?? throw new InvalidOperationException("Live transport island is unavailable.") : StageGrid;
        if (ReferenceEquals(VisualTreeHelper.GetParent(TransportCard), target)) return;
        if (VisualTreeHelper.GetParent(TransportCard) is not Panel previous)
            throw new InvalidOperationException("Live transport has no movable layout parent.");
        // A popup must be closed before its target changes XamlRoot. The next Loaded/Opening
        // binds both flyouts to the actual target root, not to the outer LiveView root.
        _changingTransport = true;
        try
        {
            QualityFlyout.Hide();
            VolumeFlyout.Hide();
            QualityFlyout.XamlRoot = null;
            VolumeFlyout.XamlRoot = null;
            _idleTimer.Stop();
            _pointerOverTransport = false;
            _openTransportFlyouts = 0;
            previous.Children.Remove(TransportCard);
            Grid.SetRow(TransportCard, intoIsland ? 0 : 1);
            try { target.Children.Add(TransportCard); }
            catch
            {
                Grid.SetRow(TransportCard, ReferenceEquals(previous, StageGrid) ? 1 : 0);
                previous.Children.Add(TransportCard);
                throw;
            }
            StageGrid.RowDefinitions[1].Height = intoIsland ? new GridLength(0) : GridLength.Auto;
        }
        finally { _changingTransport = false; }
        VideoLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportLoaded(object sender, RoutedEventArgs args) => BindTransportFlyoutRoots();

    private void OnTransportFlyoutOpening(object sender, object args) => BindTransportFlyoutRoots();

    private void BindTransportFlyoutRoots()
    {
        if (TransportCard.XamlRoot is not { } root) return;
        if (!ReferenceEquals(QualityFlyout.XamlRoot, root)) QualityFlyout.XamlRoot = root;
        if (!ReferenceEquals(VolumeFlyout.XamlRoot, root)) VolumeFlyout.XamlRoot = root;
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
        QualityButton.Visibility = _session.Qualities.Count > 0 || selected is not null
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

    /// <summary>The feedback line replaces the VOD progress row and only takes space while it has something to say.</summary>
    private void UpdateFeedbackRow()
        => FeedbackRow.Visibility = SendFeedbackText.Visibility == Visibility.Visible
            || CommandFeedbackText.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

    // ---------------------------------------------------------------- transport auto-hide (VOD overlay behaviour)

    /// <summary>Anything that must keep the controls on screen: not playing, pointer/focus/flyout on them, a draft or feedback.</summary>
    private bool MustKeepTransport()
        => !_attached
            || _session.PlaybackState != LivePlaybackState.Playing
            || _pointerOverTransport
            || _openTransportFlyouts > 0
            || ComposerBox.Text.Length > 0
            || FeedbackRow.Visibility == Visibility.Visible
            || IsFocusWithin(TransportCard);

    private void ShowTransport()
    {
        if (_changingTransport) return;
        if (_presentation.IsFullScreen)
        {
            _fullScreenTransportShown = true;
            TransportCard.Visibility = Visibility.Visible;
            TransportCard.IsHitTestVisible = true;
            TransportCard.Opacity = 1;
            _idleTimer.Stop();
            if (!MustKeepFullScreenTransport()) _idleTimer.Start();
            return;
        }

        TransportCard.Opacity = 1;
        _idleTimer.Stop();
        if (!MustKeepTransport()) _idleTimer.Start();
    }

    /// <summary>Called from RefreshAll: never restarts a running countdown, so frequent session updates cannot keep it alive.</summary>
    private void EvaluateTransport()
    {
        // Session messages must not reveal or restart the full-screen transport's idle timer.
        if (_presentation.IsFullScreen)
        {
            if (MustKeepFullScreenTransport()) ShowTransport();
            else if (_fullScreenTransportShown && !_idleTimer.IsRunning) _idleTimer.Start();
            return;
        }
        if (MustKeepTransport())
        {
            TransportCard.Opacity = 1;
            _idleTimer.Stop();
        }
        else if (TransportCard.Opacity > 0 && !_idleTimer.IsRunning)
        {
            _idleTimer.Start();
        }
    }

    private void OnIdleTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_presentation.IsFullScreen)
        {
            if (!MustKeepFullScreenTransport()) HideFullScreenTransport();
            return;
        }

        // Opacity only: the row keeps its height, so the native video is never resized. The card stays hit-testable,
        // so pointer or keyboard focus on it brings it straight back.
        if (!MustKeepTransport()) TransportCard.Opacity = 0;
    }

    private void HideFullScreenTransport()
    {
        _fullScreenTransportShown = false;
        _idleTimer.Stop();
        _pointerOverTransport = false;
        _changingTransport = true;
        try
        {
            TransportCard.IsHitTestVisible = false;
            TransportCard.Opacity = 0;
            TransportCard.Visibility = Visibility.Collapsed;
        }
        finally { _changingTransport = false; }
    }

    private bool MustKeepFullScreenTransport()
        => _pointerOverTransport || _openTransportFlyouts > 0 || IsFocusWithin(TransportCard);

    private void OnStagePointerActivity(object sender, PointerRoutedEventArgs e) => ShowTransport();

    private void OnTransportPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverTransport = true;
        ShowTransport();
    }

    private void OnTransportPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverTransport = false;
        if (!_presentation.IsFullScreen || _fullScreenTransportShown) ShowTransport();
    }

    private void OnTransportFocusChanged(object sender, RoutedEventArgs e)
    {
        if (!_presentation.IsFullScreen || _fullScreenTransportShown) ShowTransport();
    }

    private void OnTransportFlyoutOpened(object sender, object e)
    {
        _openTransportFlyouts++;
        ShowTransport();
    }

    private void OnTransportFlyoutClosed(object sender, object e)
    {
        _openTransportFlyouts = Math.Max(0, _openTransportFlyouts - 1);
        if (!_presentation.IsFullScreen || _fullScreenTransportShown) ShowTransport();
    }

    private bool IsFocusWithin(DependencyObject container)
    {
        if (container is not FrameworkElement element || element.XamlRoot is not { } root
            || FocusManager.GetFocusedElement(root) is not DependencyObject focused) return false;
        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container)) return true;
        }

        return false;
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
