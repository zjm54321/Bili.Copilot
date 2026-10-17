// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using System.ComponentModel;
using System.Numerics;
using System.Windows.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using WinUIEx;

namespace BiliCopilot.UI.Controls.Core;

public sealed partial class PlayerOverlay : PlayerControlBase
{
    private readonly DispatcherQueueTimer _controlTimer;
    private readonly DispatcherQueueTimer _cursorTimer;
    private PlayerViewModel? _subscribedViewModel;
    private bool _isTouch;
    private bool _isTransportInteracting;
    private bool _loaded;
    private readonly IslandPopupClaims _popups = new();
    private long _inputEpoch;
    private Point _lastPointerPosition;
    private EmbeddedInteractionNoticeOwner? _embeddedInteractionNotice;
    private long _embeddedInteractionNoticeEpoch;
    private WeakReference<PlayerViewModel>? _dismissedInteractionNoticeViewModel;
    private long _dismissedInteractionNoticeGeneration;
    private object? _headerWidthTicket;
    private double _headerWidth;
    private readonly List<OwnedTip> _tips = new();
    private double _embeddedHeaderWidth;
    private PlayerPresentationMode _lastPresentation;
    private TransportRelocation? _relocation;
    private long _relocationEpoch;
    private CompactHeaderDrag? _compactHeaderDrag;

    private sealed class CompactHeaderDrag(PlayerViewModel owner, MainPlayerHost host, FrameworkElement surface,
        Microsoft.UI.Xaml.Input.Pointer pointer, Point screenOrigin, Windows.Graphics.PointInt32 windowOrigin,
        double thresholdX, double thresholdY)
    {
        internal readonly PlayerViewModel Owner = owner;
        internal readonly MainPlayerHost Host = host;
        internal readonly FrameworkElement Surface = surface;
        internal readonly Microsoft.UI.Xaml.Input.Pointer Pointer = pointer;
        internal readonly Point ScreenOrigin = screenOrigin;
        internal readonly Windows.Graphics.PointInt32 WindowOrigin = windowOrigin;
        internal readonly double ThresholdX = thresholdX, ThresholdY = thresholdY;
        internal bool Started;
    }

    /// <summary>One header click: the overlay VM and input epoch captured when the click started.</summary>
    private sealed record EmbeddedHeaderRequest(PlayerViewModel Owner, long Epoch);

    /// <summary>
    /// One-shot play/pause relocation owned by a VM/epoch; only the current request may start or clean up.
    /// </summary>
    private sealed class TransportRelocation(PlayerViewModel owner, long epoch, PlayerPresentationMode target,
        Point oldFraction, double parentEntryOffsetY, double durationMilliseconds)
    {
        internal readonly PlayerViewModel Owner = owner;
        internal readonly long Epoch = epoch;
        internal readonly PlayerPresentationMode Target = target;
        internal readonly Point OldFraction = oldFraction;
        internal readonly double ParentEntryOffsetY = parentEntryOffsetY;
        internal readonly double DurationMilliseconds = durationMilliseconds;
        internal UIElement? Element;
        internal Vector3KeyFrameAnimation? Translation;
        internal ScalarKeyFrameAnimation? Opacity;
        internal FrameworkElement? LayoutWaiter;
        internal EventHandler<object>? LayoutHandler;
    }

    /// <summary>One displayed tip and the player owner/media it was requested for.</summary>
    private sealed class OwnedTip(TipPopup popup, PlayerViewModel? owner, long generation)
    {
        internal readonly TipPopup Popup = popup;
        internal readonly PlayerViewModel? Owner = owner;
        internal readonly long Generation = generation;
    }

    private sealed class EmbeddedInteractionNoticeOwner(PlayerViewModel viewModel, VideoConnectorViewModel connector,
        long generation, long epoch)
    {
        internal readonly PlayerViewModel ViewModel = viewModel;
        internal readonly VideoConnectorViewModel Connector = connector;
        internal readonly long Generation = generation;
        internal readonly long Epoch = epoch;
        internal PropertyChangedEventHandler? ConnectorChanged;
        internal InfoBar? Bar;
        internal HyperlinkButton? Action;
        internal TypedEventHandler<InfoBar, InfoBarClosedEventArgs>? Closed;
    }

    public PlayerOverlay()
    {
        InitializeComponent();
        _controlTimer = DispatcherQueue.CreateTimer();
        _controlTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _controlTimer.Tick += OnControlTimerTick;
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(500);
        _cursorTimer.Tick += OnCursorTimerTick;
        // Observe, without consuming, movement already handled by the captured
        // player surface or ordinary transport controls.
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnObservedPointerMoved), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnObservedPointerPressed), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnObservedPointerWheelChanged), true);
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    public void ShowTip(string text, InfoType type = InfoType.Error)
    {
        // Capture the requesting owner/media on the UI thread; an off-thread caller is bound when dispatched.
        var captured = DispatcherQueue.HasThreadAccess;
        var owner = captured ? ViewModel : null;
        var generation = owner?.MediaGeneration ?? 0;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!captured)
            {
                owner = ViewModel;
                generation = owner?.MediaGeneration ?? 0;
            }

            if (!_loaded || !ReferenceEquals(ViewModel, owner)
                || (owner is not null && (owner.MediaGeneration != generation || owner.IsPlaybackClosing)))
            {
                return;
            }

            var tip = new OwnedTip(new TipPopup() { Text = text }, owner, generation);
            _tips.Add(tip);
            TipContainer.Children.Add(tip.Popup);
            TipContainer.Visibility = Visibility.Visible;
            try
            {
                await tip.Popup.ShowAsync(type);
            }
            finally
            {
                RemoveTip(tip);
            }
        });
    }

    /// <summary>Remove only this tip; the shared container collapses only when no other tip is still shown.</summary>
    private void RemoveTip(OwnedTip tip)
    {
        if (!_tips.Remove(tip)) return; // Already retired by unload/owner/media cleanup.
        TipContainer.Children.Remove(tip.Popup);
        if (TipContainer.Children.Count == 0) TipContainer.Visibility = Visibility.Collapsed;
    }

    private void ClearTips(Func<OwnedTip, bool> retire)
    {
        foreach (var tip in _tips.Where(retire).ToArray()) RemoveTip(tip);
    }

    /// <summary>
    /// 置顶仅用于独立播放窗口的常规呈现；内嵌主窗口、全屏和小窗均不显示（按 VM 呈现模式判断，而非播放器内核的异步全屏标记）.
    /// </summary>
    internal static Visibility IsTopMostButtonVisible(PlayerPresentationMode presentation, bool isEmbedded)
        => !isEmbedded && presentation == PlayerPresentationMode.Normal ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>独立播放窗口保留原 AppTitleBar；内嵌主窗口时绝不实例化（它会写入宿主窗口的原生标题栏区域）.</summary>
    internal static bool IsStandaloneHeaderLoaded(bool isEmbedded) => !isEmbedded;

    /// <summary>内嵌主窗口的全屏/小窗使用纯视觉标题区.</summary>
    internal static bool IsEmbeddedHeaderLoaded(bool isEmbedded) => isEmbedded;

    internal static Visibility GetCompactOnlyVisibility(PlayerPresentationMode presentation)
        => presentation == PlayerPresentationMode.CompactOverlay ? Visibility.Visible : Visibility.Collapsed;

    internal static Visibility GetVisibilityUnlessCompact(PlayerPresentationMode presentation)
        => presentation == PlayerPresentationMode.CompactOverlay ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>内嵌小窗的标题区位于顶部，互动视频提示下移到标题区下方，避免被遮住.</summary>
    internal static Thickness GetEmbeddedNoticeMargin(bool isEmbedded, PlayerPresentationMode presentation)
        => isEmbedded && presentation == PlayerPresentationMode.CompactOverlay ? new Thickness(0, 48, 0, 0) : new Thickness(0);

    internal static string GetEmbeddedCompactTip(PlayerPresentationMode presentation)
        => BiliCopilot.UI.Toolkits.ResourceToolkit.GetLocalizedString(presentation == PlayerPresentationMode.CompactOverlay
            ? BiliCopilot.UI.Models.Constants.StringNames.ExitCompactOverlay
            : BiliCopilot.UI.Models.Constants.StringNames.EnterCompactOverlay);

    internal static FluentIcons.Common.Symbol GetEmbeddedCompactSymbol(PlayerPresentationMode presentation)
        => presentation == PlayerPresentationMode.CompactOverlay ? FluentIcons.Common.Symbol.ContractDownLeft : FluentIcons.Common.Symbol.ContractUpRight;

    internal static string GetEmbeddedFullScreenTip(PlayerPresentationMode presentation)
        => BiliCopilot.UI.Toolkits.ResourceToolkit.GetLocalizedString(presentation == PlayerPresentationMode.FullScreen
            ? BiliCopilot.UI.Models.Constants.StringNames.ExitFullScreen
            : BiliCopilot.UI.Models.Constants.StringNames.EnterFullScreen);

    internal static FluentIcons.Common.Symbol GetEmbeddedFullScreenSymbol(PlayerPresentationMode presentation)
        => presentation == PlayerPresentationMode.FullScreen ? FluentIcons.Common.Symbol.FullScreenMinimize : FluentIcons.Common.Symbol.FullScreenMaximize;

    private void OnLocalLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _lastPresentation = ViewModel?.Presentation ?? PlayerPresentationMode.Normal;
        SubscribeViewModel(ViewModel);
        if (IslandTransport is not null)
        {
            IslandTransport.InteractionStateChanged += OnTransportInteractionChanged;
        }

        if (TransportControls is not null)
        {
            TransportControls.InteractionStateChanged += OnTransportInteractionChanged;
        }

        if (ViewModel?.IsControlsVisible == true && !ViewModel.IsWindowDeactivated)
        {
            RestartControlTimer();
        }
        RefreshEmbeddedInteractionNotice();
        // Reapply the latest header width dropped by a previous unload (idempotent visual state only).
        QueueHeaderWidthUpdate();
        // Re-associate SponsorBlock views registered through BindSponsorViews (view state only).
        ApplySponsorViews();
        RequestHostChromeRegionUpdate();
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        RequestHostChromeRegionUpdate();
        CancelTransportRelocation();
        CancelPendingInput();
        // Drop SponsorBlock view subscriptions; the registration is kept for a later reload.
        DetachSponsorViews();
        if (IslandTransport is not null)
        {
            IslandTransport.InteractionStateChanged -= OnTransportInteractionChanged;
        }

        if (TransportControls is not null)
        {
            TransportControls.InteractionStateChanged -= OnTransportInteractionChanged;
        }

        _isTransportInteracting = false;
        SubscribeViewModel(null);
        _controlTimer.Stop();
        _cursorTimer.Stop();
        // Keep timer handlers and x:Bind-owned ItemsSource intact for reload.
        ClearEmbeddedInteractionNotice();
        _headerWidthTicket = null;
        ClearTips(_ => true);
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        // SponsorBlock views belong to the old player's attachment; the composer rebinds for the new one.
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue)) UnbindSponsorViews();
        ClearEmbeddedInteractionNotice();
        ClearTips(tip => !ReferenceEquals(tip.Owner, newValue));
        CancelTransportRelocation();
        _lastPresentation = newValue?.Presentation ?? PlayerPresentationMode.Normal;
        if (!ReferenceEquals(ViewModel, newValue)) return;
        CancelPendingInput();
        if (!ReferenceEquals(ViewModel, newValue)) return;
        SubscribeViewModel(_loaded ? newValue : null);
        RefreshEmbeddedInteractionNotice();
        // Retire a width callback captured for the old VM; requeue only the latest width for the new one.
        if (_headerWidthTicket is not null) QueueHeaderWidthUpdate();
        RequestHostChromeRegionUpdate();
    }

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.WarningOccurred -= OnWarningOccurred;
            _subscribedViewModel.RequestShowNextTip -= OnRequestShowNextTip;
            _subscribedViewModel.RequestHideNextTip -= OnRequestHideNextTip;
        }

        _subscribedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.WarningOccurred += OnWarningOccurred;
            viewModel.RequestShowNextTip += OnRequestShowNextTip;
            viewModel.RequestHideNextTip += OnRequestHideNextTip;
        }
    }

    /// <summary>Notify on each accepted player key, including repeat activity.</summary>
    public void ShowControlsForInput(bool isTouch = false)
    {
        if (!_loaded || ViewModel is null || ViewModel.IsPlaybackClosing || ViewModel.IsWindowDeactivated)
        {
            return;
        }

        _isTouch = isTouch;
        // Keyboard activity gets the full inactivity timeout even when the
        // physical mouse happens to be outside this window.
        _cursorTimer.Stop();
        ViewModel.IsTouchControlsVisible = isTouch;
        ViewModel.IsControlsVisible = true;
        TestRect.Visibility = Visibility.Visible;
        if (!isTouch)
        {
            ViewModel.Window?.ShowCursor();
        }

        RestartControlTimer();
    }

    /// <summary>Window owner may call before close, deactivation or media replacement.</summary>
    public void CancelPendingInput()
    {
        CancelCompactHeaderDrag();
        ++_inputEpoch;
        _popups.Cancel();
        CancelSurfaceInput();

        // 取消两条传输条尚未触发的延迟调度/浮出；供关闭、失活、换媒体与模式切换复用.
        IslandTransport?.CancelPendingInput();
        TransportControls?.CancelPendingInput();
    }

    /// <summary>Restore physical surface input without retiring transport-owned flyouts.</summary>
    public void CancelSurfaceInput()
    {
        if (RootGrid is { } root)
        {
            foreach (var panel in root.Children.OfType<PlayerInteractivePanel>())
            {
                panel.CancelPendingInput();
            }
        }

    }

    private void RestartControlTimer()
    {
        _controlTimer.Stop();
        if (_loaded && ViewModel?.IsControlsVisible == true && !ViewModel.IsWindowDeactivated) _controlTimer.Start();
    }

    private void OnTransportInteractionChanged(object? sender, bool isInteracting)
    {
        // 任一传输条处于焦点/浮出/待处理交互时暂停自动隐藏；交互结束后恢复计时.
        _isTransportInteracting = (IslandTransport?.IsInteracting ?? false)
            || (TransportControls?.IsInteracting ?? false);
        if (!_isTransportInteracting && ViewModel?.IsControlsVisible == true)
        {
            RestartControlTimer();
        }
    }

    private void OnRequestHideNextTip(object? sender, EventArgs e)
        => NextTip?.Hide();

    private void OnRequestShowNextTip(object? sender, EventArgs e)
        => NextTip?.Show();

    private void OnWarningOccurred(object? sender, string e)
    {
        System.Diagnostics.Debug.WriteLine($"Warning: {e}");
        //var popup = new TipPopup() { Text = e };
        //RootGrid.Children.Add(popup);
        //await popup.ShowAsync(InfoType.Warning);
        //RootGrid.Children.Remove(popup);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        if (_loaded && ViewModel is { IsWindowDeactivated: false, IsPlaybackClosing: false }
            && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !e.IsGenerated)
        {
            ShowControlsForInput();
            _cursorTimer.Start();
        }
    }

    private void OnObservedPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_loaded || ViewModel is null || ViewModel.IsWindowDeactivated || ViewModel.IsPlaybackClosing || e.IsGenerated)
        {
            return;
        }

        _isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
        if (_isTouch)
        {
            _cursorTimer.Stop();
        }

        if (ViewModel?.IsControlsVisible == true)
        {
            RestartControlTimer();
        }
    }

    private void OnObservedPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_loaded && ViewModel is { IsWindowDeactivated: false, IsPlaybackClosing: false }
            && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !e.IsGenerated)
        {
            ShowControlsForInput();
            _cursorTimer.Start();
        }
    }

    private void OnObservedPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_loaded || ViewModel is null || ViewModel.IsWindowDeactivated || ViewModel.IsPlaybackClosing
            || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || e.IsGenerated)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;
        if (position.Equals(_lastPointerPosition))
        {
            // If the pointer hasn't moved, do nothing.
            return;
        }

        _lastPointerPosition = position;
        ShowControlsForInput();
        _cursorTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (e.PropertyName is null or "" or nameof(PlayerViewModel.Presentation)
            or nameof(PlayerViewModel.IsEmbedded) or nameof(PlayerViewModel.IsPlaybackClosing))
        {
            RequestHostChromeRegionUpdate();
        }
        OnSponsorHostPropertyChanged(e.PropertyName);
        if (e.PropertyName is null or "" or nameof(PlayerViewModel.Connector) or nameof(PlayerViewModel.MediaGeneration)
            or nameof(PlayerViewModel.Presentation) or nameof(PlayerViewModel.IsEmbedded) or nameof(PlayerViewModel.Player)
            or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.IsPlaybackClosing) or nameof(PlayerViewModel.ErrorMessage)
            or nameof(PlayerViewModel.IsPopupVisible) or nameof(PlayerViewModel.IsExtraPanelVisible) or nameof(PlayerViewModel.IsSubtitleSettingsVisible))
        {
            RefreshEmbeddedInteractionNotice();
            QueueHeaderWidthUpdate();
            if (!ReferenceEquals(sender, _subscribedViewModel) || !ReferenceEquals(sender, ViewModel)) return;
        }
        if (e.PropertyName == nameof(ViewModel.IsControlsVisible))
        {
            RequestHostChromeRegionUpdate();
            if (ViewModel.IsControlsVisible)
            {
                RestartControlTimer();
            }
            else
            {
                CancelPendingInput();
                ViewModel.IsTouchControlsVisible = false;
                _controlTimer.Stop();
            }
        }
        else if (e.PropertyName == nameof(ViewModel.CurrentVolume))
        {
            var thumbHeight = VolumeContainer.ActualHeight * (ViewModel.CurrentVolume / ViewModel.MaxVolume);
            VolumeThumb?.Height = Math.Max(0, thumbHeight);
        }
        else if (e.PropertyName == nameof(ViewModel.IsWindowDeactivated) && ViewModel.IsWindowDeactivated)
        {
            CancelPendingInput();
            _controlTimer.Stop();
            _cursorTimer.Stop();
        }
        else if (e.PropertyName == nameof(ViewModel.Presentation))
        {
            // 先在旧布局仍有效时记录播放按钮位置，再执行模式切换的取消逻辑；动画只作用于视觉位移，不延迟宿主提交.
            var previous = _lastPresentation;
            _lastPresentation = ViewModel.Presentation;
            BeginTransportRelocation(previous, ViewModel.Presentation);

            // 模式切换：取消旧传输条的待处理调度与浮出，并按新可见性恢复计时.
            CancelPendingInput();
            if (ViewModel.IsControlsVisible)
            {
                RestartControlTimer();
            }
        }
        else if (e.PropertyName == nameof(ViewModel.MediaGeneration)
            || (e.PropertyName == nameof(ViewModel.IsPlaybackClosing) && ViewModel.IsPlaybackClosing)
            || (e.PropertyName == nameof(ViewModel.IsConnecting) && ViewModel.IsConnecting))
        {
            CancelTransportRelocation();
            CancelPendingInput();
            var current = ViewModel;
            ClearTips(tip => !ReferenceEquals(tip.Owner, current)
                || (current is not null && (current.MediaGeneration != tip.Generation || current.IsPlaybackClosing)));
        }
    }

    private void RefreshEmbeddedInteractionNotice()
    {
        var vm = ViewModel;
        var connector = vm?.Connector as VideoConnectorViewModel;
        if (!_loaded || vm is null || !vm.IsEmbedded || vm.IsPlaybackClosing || connector is null)
        {
            ClearEmbeddedInteractionNotice();
            return;
        }

        var generation = vm.MediaGeneration;
        if (_dismissedInteractionNoticeViewModel is { } dismissed
            && (!dismissed.TryGetTarget(out var previous) || !ReferenceEquals(previous, vm)
                || _dismissedInteractionNoticeGeneration != generation)) _dismissedInteractionNoticeViewModel = null;

        var owner = _embeddedInteractionNotice;
        if (owner is null || !IsCurrentEmbeddedInteractionNotice(owner))
        {
            ClearEmbeddedInteractionNotice();
            // Closing the previous InfoBar can synchronously replace the VM/notice.
            if (!_loaded || !ReferenceEquals(vm, ViewModel) || !ReferenceEquals(connector, vm.Connector)
                || generation != vm.MediaGeneration || !vm.IsEmbedded || vm.IsPlaybackClosing
                || _embeddedInteractionNotice is not null) return;
            owner = new EmbeddedInteractionNoticeOwner(vm, connector, generation, _embeddedInteractionNoticeEpoch);
            _embeddedInteractionNotice = owner;
            owner.ConnectorChanged = (sender, args) =>
            {
                if (ReferenceEquals(sender, owner.Connector) && IsCurrentEmbeddedInteractionNotice(owner)
                    && args.PropertyName is null or "" or nameof(VideoConnectorViewModel.IsInteractionVideo))
                    RefreshEmbeddedInteractionNotice();
            };
            connector.PropertyChanged += owner.ConnectorChanged;
        }

        if (!CanShowEmbeddedInteractionNotice(owner))
        {
            HideEmbeddedInteractionNotice(owner);
            return;
        }
        if (owner.Bar is not null) return;

        // A fresh local instance for each opening prevents late Closed from an old bar
        // being attributed to a newer opening, including the same media after a mode change.
        var bar = (InfoBar)((DataTemplate)Resources["EmbeddedInteractionNoticeTemplate"]).LoadContent();
        if (!IsCurrentEmbeddedInteractionNotice(owner) || !CanShowEmbeddedInteractionNotice(owner)) return;
        if (bar.ActionButton is not HyperlinkButton action) return;
        var sourceCommand = owner.Connector.OpenInBrowserCommand;
        owner.Bar = bar;
        owner.Action = action;
        owner.Closed = (sender, args) => OnEmbeddedInteractionNoticeClosed(owner, sender, args);
        bar.Closed += owner.Closed;
        var command = new AsyncRelayCommand(async () =>
        {
            try
            {
                if (!CanInvokeEmbeddedInteractionNotice(owner, bar)) return;
                var canExecute = sourceCommand.CanExecute(null);
                if (!canExecute || !CanInvokeEmbeddedInteractionNotice(owner, bar)) return;
                await sourceCommand.ExecuteAsync(null);
            }
            catch (Exception) { System.Diagnostics.Debug.WriteLine("Embedded interaction browser action failed."); }
        }, () => CanInvokeEmbeddedInteractionNotice(owner, bar));
        action.Command = command;
        if (!IsCurrentEmbeddedInteractionNotice(owner) || !ReferenceEquals(owner.Bar, bar)) return;
        EmbeddedInteractionNoticeHost.Content = bar;
        if (!IsCurrentEmbeddedInteractionNotice(owner) || !ReferenceEquals(owner.Bar, bar)) return;
        bar.IsOpen = true;
        if (CanInvokeEmbeddedInteractionNotice(owner, bar)) command.NotifyCanExecuteChanged();
    }

    private bool IsCurrentEmbeddedInteractionNotice(EmbeddedInteractionNoticeOwner owner)
        => _loaded && ReferenceEquals(_embeddedInteractionNotice, owner) && owner.Epoch == _embeddedInteractionNoticeEpoch
            && ReferenceEquals(ViewModel, owner.ViewModel) && ReferenceEquals(owner.ViewModel.Connector, owner.Connector)
            && owner.Generation == owner.ViewModel.MediaGeneration && owner.ViewModel.IsEmbedded && !owner.ViewModel.IsPlaybackClosing;

    private bool CanShowEmbeddedInteractionNotice(EmbeddedInteractionNoticeOwner owner)
        => IsCurrentEmbeddedInteractionNotice(owner) && owner.Connector.IsInteractionVideo
            && owner.ViewModel.Presentation is PlayerPresentationMode.Normal or PlayerPresentationMode.FullWindow or PlayerPresentationMode.CompactOverlay
            && !owner.ViewModel.IsConnecting && string.IsNullOrEmpty(owner.ViewModel.ErrorMessage)
            && !owner.ViewModel.IsPopupVisible && !owner.ViewModel.IsExtraPanelVisible && !owner.ViewModel.IsSubtitleSettingsVisible
            && !(_dismissedInteractionNoticeViewModel?.TryGetTarget(out var dismissed) == true
                && ReferenceEquals(dismissed, owner.ViewModel) && _dismissedInteractionNoticeGeneration == owner.Generation);

    private bool CanInvokeEmbeddedInteractionNotice(EmbeddedInteractionNoticeOwner owner, InfoBar bar)
        => CanShowEmbeddedInteractionNotice(owner) && !owner.ViewModel.IsWindowDeactivated
            && ReferenceEquals(owner.Bar, bar) && bar.IsOpen;

    private void OnEmbeddedInteractionNoticeClosed(EmbeddedInteractionNoticeOwner owner, InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!IsCurrentEmbeddedInteractionNotice(owner) || !ReferenceEquals(owner.Bar, sender)) return;
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            _dismissedInteractionNoticeViewModel = new WeakReference<PlayerViewModel>(owner.ViewModel);
            _dismissedInteractionNoticeGeneration = owner.Generation;
        }
        HideEmbeddedInteractionNotice(owner);
    }

    private void ClearEmbeddedInteractionNotice()
    {
        var owner = _embeddedInteractionNotice;
        _embeddedInteractionNotice = null;
        ++_embeddedInteractionNoticeEpoch;
        if (owner is null) return;
        owner.Connector.PropertyChanged -= owner.ConnectorChanged;
        owner.ConnectorChanged = null;
        HideEmbeddedInteractionNotice(owner);
        // Keep only the weak per-media dismissal memory across temporary unload/mode changes.
    }

    private void HideEmbeddedInteractionNotice(EmbeddedInteractionNoticeOwner owner)
    {
        var bar = owner.Bar;
        var action = owner.Action;
        var closed = owner.Closed;
        owner.Bar = null;
        owner.Action = null;
        owner.Closed = null;
        if (bar is null) return;
        bar.Closed -= closed;
        if (action is not null) action.Command = null;
        if (ReferenceEquals(EmbeddedInteractionNoticeHost.Content, bar)) EmbeddedInteractionNoticeHost.Content = null;
        bar.ActionButton = null;
        bar.Content = null;
        // Last operation: a synchronous close callback must not let old cleanup erase new B.
        bar.IsOpen = false;
    }

    private void OnControlTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_loaded || ViewModel is null || ViewModel.IsWindowDeactivated || ViewModel.IsPlaybackClosing)
        {
            _controlTimer.Stop();
            return;
        }

        if (_isTransportInteracting || ViewModel.IsPopupVisible || ViewModel.IsExtraPanelVisible || ViewModel.IsSubtitleSettingsVisible
            || ViewModel.IsPreviewProgressChanging || ViewModel.IsHoldingSpeedChanging)
        {
            return;
        }

        _controlTimer.Stop();
        ViewModel.IsControlsVisible = false;
        ViewModel.IsTouchControlsVisible = false;
        TestRect.Visibility = Visibility.Collapsed;
        if (!_isTouch)
        {
            ViewModel.Window?.HideCursor();
        }
    }

    private void OnCursorTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_loaded || ViewModel is null || ViewModel.IsWindowDeactivated || ViewModel.IsPlaybackClosing)
        {
            _cursorTimer.Stop();
            return;
        }

        if (_isTransportInteracting || ViewModel.IsPopupVisible || ViewModel.IsExtraPanelVisible
            || ViewModel.IsPreviewProgressChanging || ViewModel.IsHoldingSpeedChanging)
        {
            return;
        }

        if (!IsCursorInWindow() && !_isTouch)
        {
            ViewModel.IsControlsVisible = false;
            _cursorTimer.Stop();
        }
    }

    private void OnCloseTipButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.ErrorMessage = default;

    private void OnBackRequested(object sender, EventArgs e)
        => ViewModel.BackCommand.Execute(default);

    private void OnSubtitleSettingItemClick(object sender, RoutedEventArgs e)
    {
        // 只读取已加载的字段（不调用 FindName）：独立窗口指向 AppTitleBar 内的设置按钮，内嵌指向纯视觉标题区的设置按钮.
        SubtitleSettingsTip.Target = ViewModel.IsEmbedded ? EmbeddedSettingButton : HeaderSettingButton;
        ViewModel.IsSubtitleSettingsVisible = false;
        ViewModel.IsSubtitleSettingsVisible = true;
    }

    private void OnSubtitlePositionChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.Player?.IsPlaybackInitialized != true || ViewModel.SubtitlePosition == e.NewValue)
        {
            return;
        }

        ViewModel.ChangeSubtitlePositionCommand.Execute(e.NewValue);
    }

    private void OnHeaderBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == 0)
        {
            return;
        }

        // Coalesce: keep only the latest width; at most one Low callback is outstanding per view.
        _headerWidth = e.NewSize.Width;
        if (_headerWidthTicket is null) QueueHeaderWidthUpdate();
    }

    private void OnEmbeddedHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RequestHostChromeRegionUpdate();
        if (e.NewSize.Width == 0)
        {
            return;
        }

        _embeddedHeaderWidth = e.NewSize.Width;
        if (_headerWidthTicket is null) QueueHeaderWidthUpdate();
    }

    private void OnEmbeddedCompactHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CancelCompactHeaderDrag();
        if (e.Handled || e.IsGenerated || !_loaded || ViewModel is not { } owner || owner.IsPlaybackClosing
            || owner.IsPopupVisible || owner.Presentation != PlayerPresentationMode.CompactOverlay
            || owner.Window is not MainPlayerHost host || host.IsClosed || host.IsPresenterChanging
            || EmbeddedHeader is not { } header || host.XamlRoot is null) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        // Never turn a header button, selector or other interactive child into a drag surface.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, header); node = VisualTreeHelper.GetParent(node))
        {
            if (node is Control) return;
        }

        if (!PInvoke.GetCursorPos(out var screen)) return;
        var dpi = HwndExtensions.GetDpiForWindow(host.OwnerHwnd);
        var thresholdX = Math.Max(1, PInvoke.GetSystemMetricsForDpi(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXDRAG, dpi) / 2d);
        var thresholdY = Math.Max(1, PInvoke.GetSystemMetricsForDpi(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYDRAG, dpi) / 2d);
        _compactHeaderDrag = new CompactHeaderDrag(owner, host, header, e.Pointer,
            new Point(screen.X, screen.Y), host.GetWindow().Position, thresholdX, thresholdY);
        if (!header.CapturePointer(e.Pointer)) CancelCompactHeaderDrag();
        e.Handled = true;
    }

    private void OnEmbeddedCompactHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_compactHeaderDrag is not { } drag || drag.Pointer.PointerId != e.Pointer.PointerId) return;
        if (e.IsGenerated) return; // Moving the HWND can synthesize pointer updates.
        if (!_loaded || !ReferenceEquals(ViewModel, drag.Owner)
            || !e.GetCurrentPoint(drag.Surface).Properties.IsLeftButtonPressed || !PInvoke.GetCursorPos(out var screen))
        {
            CancelCompactHeaderDrag();
            return;
        }

        var dx = screen.X - drag.ScreenOrigin.X;
        var dy = screen.Y - drag.ScreenOrigin.Y;
        if (!drag.Started && Math.Abs(dx) < drag.ThresholdX && Math.Abs(dy) < drag.ThresholdY) return;
        drag.Started = true;
        e.Handled = true;
        if (!drag.Host.TryMoveCompactWindow(drag.Owner, new Windows.Graphics.PointInt32(
            drag.WindowOrigin.X + (int)Math.Round(dx), drag.WindowOrigin.Y + (int)Math.Round(dy))))
            CancelCompactHeaderDrag();
    }

    private void OnEmbeddedCompactHeaderPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_compactHeaderDrag is not { } drag || drag.Pointer.PointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        CancelCompactHeaderDrag();
    }

    internal void CancelCompactHeaderDrag()
    {
        var drag = _compactHeaderDrag;
        _compactHeaderDrag = null; // ReleasePointerCapture can synchronously deliver CaptureLost.
        if (drag?.Host.XamlRoot is not null) drag.Surface.ReleasePointerCapture(drag.Pointer);
    }

    private void OnEmbeddedRightButtonPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RequestHostChromeRegionUpdate();
    }

    private void QueueHeaderWidthUpdate()
    {
        _headerWidthTicket = null;
        if (!_loaded || (_headerWidth <= 0 && _embeddedHeaderWidth <= 0)) return;
        var ticket = new object();
        var owner = ViewModel;
        _headerWidthTicket = ticket;
        try
        {
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // A callback queued for an earlier attachment/VM, or superseded by unload/VM change, must not touch B.
                if (!ReferenceEquals(_headerWidthTicket, ticket)) return;
                _headerWidthTicket = null;
                if (!_loaded || !ReferenceEquals(owner, ViewModel)) return;

                // 只读取已生成的字段：未加载（x:Load=false）的标题区为 null，此处不会触发实例化.
                if (HeaderBar is { } headerBar && _headerWidth > 0)
                {
                    var width = _headerWidth;
                    var narrow = width < 700;
                    if (FooterContainer is { } footer) footer.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                    headerBar.TitleMaxWidth = narrow ? (width > 200 ? width - 200 : 120) : width - 520;
                }

                if (EmbeddedHeader is not null && _embeddedHeaderWidth > 0)
                {
                    var layout = PlayerHeaderLayout.ForEmbeddedWidth(_embeddedHeaderWidth);
                    var isCompact = ViewModel?.Presentation == PlayerPresentationMode.CompactOverlay;
                    if (EmbeddedStatusPanel is { } status) status.Visibility = (!isCompact && layout.ShowStatus) ? Visibility.Visible : Visibility.Collapsed;
                    if (EmbeddedTitle is { } title) title.Visibility = layout.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
                }
            })) return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in QueueHeaderWidthUpdate: {ex.Message}");
        }

        if (ReferenceEquals(_headerWidthTicket, ticket)) _headerWidthTicket = null;
    }

    private EmbeddedHeaderRequest? CaptureEmbeddedHeaderRequest()
        => _loaded && ViewModel is { } owner && IsEmbeddedHeaderOwner(owner) ? new(owner, _inputEpoch) : null;

    private static bool IsEmbeddedHeaderOwner(PlayerViewModel owner)
        => owner.IsEmbedded && !owner.IsPlaybackClosing && UsesOverlayTransport(owner.Presentation);

    /// <summary>点击开始后若发生卸载、换 VM、模式切换（输入纪元递增）或关闭，旧点击不得作用于新的所有者.</summary>
    private bool IsCurrentHeaderRequest(EmbeddedHeaderRequest request)
        => _loaded && ReferenceEquals(ViewModel, request.Owner) && request.Epoch == _inputEpoch
            && IsEmbeddedHeaderOwner(request.Owner);

    private async Task RunEmbeddedHeaderCommandAsync(Func<PlayerViewModel, ICommand> select)
    {
        if (CaptureEmbeddedHeaderRequest() is not { } request) return;
        try
        {
            await PlayerHeaderCommandGate.RunAsync(select(request.Owner), () => IsCurrentHeaderRequest(request));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Embedded header command failed: {ex.Message}");
            if (IsCurrentHeaderRequest(request)) ShowTip(ex.Message);
        }
    }

    // 返回：恢复标准视图（BackToDefaultModeCommand），不是关闭播放的 BackCommand.
    private async void OnEmbeddedBackClick(object sender, RoutedEventArgs e)
        => await RunEmbeddedHeaderCommandAsync(owner => owner.BackToDefaultModeCommand);

    private async void OnEmbeddedCompactClick(object sender, RoutedEventArgs e)
        => await RunEmbeddedHeaderCommandAsync(owner => owner.ToggleCompactOverlayCommand);

    private async void OnEmbeddedFullScreenClick(object sender, RoutedEventArgs e)
        => await RunEmbeddedHeaderCommandAsync(owner => owner.ToggleFullScreenCommand);

    /// <summary>关闭窗口：把点击时捕获的 VM 交给宿主走主窗口原有的关闭流程，不直接销毁窗口或只关播放器.</summary>
    private void OnEmbeddedCloseClick(object sender, RoutedEventArgs e)
    {
        if (CaptureEmbeddedHeaderRequest() is not { } request || request.Owner.Window is not MainPlayerHost host) return;
        host.RequestOwnerClose(request.Owner);
    }

    private void RequestHostChromeRegionUpdate()
    {
        if (ViewModel?.Window is MainPlayerHost host)
        {
            host.RequestChromeRegionUpdate();
        }
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private FrameworkElement? GetPlayPauseTarget(PlayerPresentationMode mode)
        => UsesOverlayTransport(mode) ? TransportControls?.PlayPauseTarget : IslandTransport?.PlayPauseTarget;

    /// <summary>
    /// 记录旧播放按钮中心在浮层中的相对位置；新布局完成后，新按钮从该位置一次性平移回原位并淡入.
    /// 不使用计时器；系统关闭动画效果时直接跳过.
    /// </summary>
    private void BeginTransportRelocation(PlayerPresentationMode from, PlayerPresentationMode to)
    {
        CancelTransportRelocation();
        if (!_loaded || ViewModel is not { } owner || from == to || !owner.IsControlsVisible
            || owner.IsPlaybackClosing || !AreAnimationsEnabled()
            || GetPlayPauseTarget(from) is not { } oldTarget
            || oldTarget.ActualWidth <= 0 || oldTarget.ActualHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Point oldCenter;
        try
        {
            oldCenter = oldTarget.TransformToVisual(this).TransformPoint(new Point(oldTarget.ActualWidth / 2, oldTarget.ActualHeight / 2));
        }
        catch (Exception)
        {
            return;
        }

        var fraction = PlayerTransportRelocationMath.ToFraction(oldCenter, new Size(ActualWidth, ActualHeight));
        if (fraction is not { } oldFraction) return;
        var request = new TransportRelocation(owner, ++_relocationEpoch, to, oldFraction,
            PlayerTransportRelocationMath.GetParentEntryOffsetY(UsesOverlayTransport(from), UsesOverlayTransport(to)),
            PlayerTransportRelocationMath.GetDurationMilliseconds(from, to));
        _relocation = request;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => StartTransportRelocation(request, allowLayoutRetry: true)))
        {
            CancelTransportRelocation();
        }
    }

    private bool IsCurrentRelocation(TransportRelocation request)
        => _loaded && ReferenceEquals(_relocation, request) && request.Epoch == _relocationEpoch
            && ReferenceEquals(ViewModel, request.Owner) && request.Owner.Presentation == request.Target
            && !request.Owner.IsPlaybackClosing;

    private void StartTransportRelocation(TransportRelocation request, bool allowLayoutRetry)
    {
        if (!IsCurrentRelocation(request)) return;
        DetachRelocationLayoutWaiter(request);
        if (GetPlayPauseTarget(request.Target) is not { } target) { CancelTransportRelocation(); return; }
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            if (!allowLayoutRetry) { CancelTransportRelocation(); return; }

            // 新传输条尚未完成首次布局：只等待一次布局更新，不轮询.
            request.LayoutWaiter = target;
            request.LayoutHandler = (_, _) => StartTransportRelocation(request, allowLayoutRetry: false);
            target.LayoutUpdated += request.LayoutHandler;
            return;
        }

        Point newCenter;
        try
        {
            newCenter = target.TransformToVisual(this).TransformPoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        }
        catch (Exception)
        {
            CancelTransportRelocation();
            return;
        }

        var offset = PlayerTransportRelocationMath.ComputeStartOffset(request.OldFraction, new Size(ActualWidth, ActualHeight),
            newCenter, request.ParentEntryOffsetY);
        if (offset is not { } start) { CancelTransportRelocation(); return; }

        var compositor = ElementCompositionPreview.GetElementVisual(target).Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Target = "Translation";
        translation.Duration = TimeSpan.FromMilliseconds(request.DurationMilliseconds);
        translation.InsertKeyFrame(0f, new Vector3(start, 0f));
        translation.InsertKeyFrame(1f, Vector3.Zero, easing);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = TimeSpan.FromMilliseconds(PlayerTransportRelocationMath.FadeMilliseconds);
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);

        // 静态终值即正确布局位置：无论动画被打断还是完成，元素都停在 Translation=0、Opacity=1.
        target.Translation = Vector3.Zero;
        target.Opacity = 1;
        request.Element = target;
        request.Translation = translation;
        request.Opacity = opacity;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        target.StartAnimation(translation);
        target.StartAnimation(opacity);
        batch.End();
        batch.Completed += (_, _) =>
        {
            // 只有仍是当前请求时才清除；旧请求 A 的完成回调不会清理新请求 B.
            if (ReferenceEquals(_relocation, request)) _relocation = null;
        };
    }

    private static void DetachRelocationLayoutWaiter(TransportRelocation request)
    {
        if (request.LayoutWaiter is { } waiter && request.LayoutHandler is { } handler) waiter.LayoutUpdated -= handler;
        request.LayoutWaiter = null;
        request.LayoutHandler = null;
    }

    /// <summary>停止当前一次性动画并恢复终值（Translation=0、Opacity=1）；可重复调用.</summary>
    private void CancelTransportRelocation()
    {
        var request = _relocation;
        _relocation = null;
        if (request is null) return;
        DetachRelocationLayoutWaiter(request);
        if (request.Element is not { } element) return;
        if (request.Translation is { } translation) element.StopAnimation(translation);
        if (request.Opacity is { } opacity) element.StopAnimation(opacity);
        element.Translation = Vector3.Zero;
        element.Opacity = 1;
    }

    private bool IsCursorInWindow()
    {
        var isSuccess = PInvoke.GetCursorPos(out var point);
        if (!isSuccess)
        {
            return false;
        }

        var window = ViewModel.Window?.GetWindow();
        if (window?.IsVisible != true)
        {
            return false;
        }

        var handle = ViewModel.Window!.Handle;
        isSuccess = PInvoke.GetClientRect(new(handle), out var clientRect);
        if (!isSuccess)
        {
            return false;
        }

        var lt = new System.Drawing.Point(clientRect.X, clientRect.Y);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref lt);
        if (!isSuccess)
        {
            return false;
        }

        var rb = new System.Drawing.Point(clientRect.X + clientRect.Width, clientRect.Y + clientRect.Height);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref rb);
        if (!isSuccess)
        {
            return false;
        }

        return point.X >= lt.X && point.X <= rb.X && point.Y >= lt.Y && point.Y <= rb.Y;
    }

    private void OnSubtitleFontSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel?.Player == null || ViewModel.Player?.IsPlaybackInitialized != true || ViewModel.SubtitleFontSize == e.NewValue)
        {
            return;
        }

        ViewModel.ChangeSubtitleFontSizeCommand.Execute(e.NewValue);
    }

#pragma warning disable CA1822
    private string GetTimeText(double seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private string GetVolumeText(double volume)
        => Math.Round(volume).ToString();

    private string GetSpeedText(double speed)
        => $"{Math.Round(speed, 2)}x";
#pragma warning restore CA1822

    private void OnSubtitleFontChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = e.AddedItems.FirstOrDefault() as SystemFont;
        if (ViewModel?.Player == null || ViewModel.Player?.IsPlaybackInitialized != true || item == null || ViewModel.SubtitleFontFamily == item.LocalName)
        {
            return;
        }

        ViewModel.ChangeSubtitleFontFamilyCommand.Execute(item.LocalName);
    }

    private void OnSubtitleDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        var v = args.NewValue;
        if (v == ViewModel.SubtitleDelaySeconds)
        {
            return;
        }

        ViewModel.SetSubtitleDelaySecondsCommand.Execute(v);
    }

    internal static bool IsPaused(MpvPlayerState state)
    {
        return state is MpvPlayerState.Paused;
    }

    internal static bool IsLoading(MpvPlayerState state, bool isLoading)
    {
        return (state is MpvPlayerState.Seeking or MpvPlayerState.Buffering) || isLoading;
    }

    internal static Visibility IsVolumeIndicatorVisible(bool isControlVisible, bool isVolumeChanging)
    {
        return !isControlVisible && isVolumeChanging ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static Visibility IsBottomProgressShouldDisplay(bool isAot, bool isFullScreen, bool isLoading, MpvPlayerState state)
    {
        if ((isAot || isFullScreen) && !IsLoading(state, isLoading))
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    /// <summary>
    /// 全屏与小窗（CompactOverlay）使用新版 <see cref="OverlayTransportControls"/>；常规呈现保留 Store 岛式控件.
    /// 两者始终互斥，仍共用同一 VM/播放器/宿主.
    /// </summary>
    private static bool UsesOverlayTransport(PlayerPresentationMode mode)
        => mode is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay;

    internal static bool IsFullScreenActive(bool visible, bool deactivated, bool connecting, bool closing, PlayerPresentationMode mode)
        => visible && !deactivated && !connecting && !closing && UsesOverlayTransport(mode);

    internal static bool IsIslandActive(bool visible, bool deactivated, bool connecting, bool closing, PlayerPresentationMode mode)
        => visible && !deactivated && !connecting && !closing && !UsesOverlayTransport(mode);

    internal static Visibility IsFullScreenTransportVisible(bool isControlsVisible, PlayerPresentationMode mode)
        => isControlsVisible && UsesOverlayTransport(mode) ? Visibility.Visible : Visibility.Collapsed;

    internal static Visibility IsIslandVisible(bool isControlsVisible, PlayerPresentationMode mode)
        => isControlsVisible && !UsesOverlayTransport(mode) ? Visibility.Visible : Visibility.Collapsed;

    internal static Visibility IsOverlayHeaderVisible(bool isControlsVisible, bool isEmbedded, PlayerPresentationMode mode)
    {
        // 内嵌主窗口：常规呈现由页面自身标题承担；全屏/小窗时主标题栏整行折叠，由浮层的纯视觉标题区承担.
        var duplicatedByShell = isEmbedded && !UsesOverlayTransport(mode);
        return isControlsVisible && !duplicatedByShell ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>两个标题区各有一个清晰度浮出；只认当前已登记且属于当前 VM/输入纪元的那一个.</summary>
    private Flyout? GetCurrentSourceFlyout()
    {
        foreach (var flyout in new[] { SourceFlyout, EmbeddedSourceFlyout })
        {
            if (flyout is not null && _popups.IsCurrent(_popups.Get(flyout), ViewModel, _inputEpoch)) return flyout;
        }

        return null;
    }

    private void OnSourceItemClick(object sender, RoutedEventArgs e)
    {
        var selectedSource = (sender as FrameworkElement)?.DataContext as SourceItemViewModel;
        if (!_loaded || ViewModel?.IsControlsVisible != true || ViewModel.IsWindowDeactivated
            || GetCurrentSourceFlyout() is not { } flyout
            || selectedSource?.IsEnabled != true
            || !ViewModel.Sources.Any(item => ReferenceEquals(item, selectedSource))) return;
        selectedSource?.ActiveCommand.Execute(default);
        flyout.Hide();
    }

    private void OnOverlayFlyoutOpened(object? sender, object e)
    {
        if (sender is null) return;
        if (!_loaded || ViewModel?.IsControlsVisible != true || ViewModel.IsWindowDeactivated
            || ViewModel.IsPlaybackClosing || ViewModel.IsConnecting)
        {
            IslandPopupClaims.Hide(sender);
            return;
        }

        _popups.Open(sender, ViewModel, _inputEpoch);
    }

    private void OnSubtitleFontComboLoaded(object sender, RoutedEventArgs e)
    {
        var font = ViewModel.SubtitleFontFamily;
        var source = ViewModel.Fonts.FirstOrDefault(p => p.LocalName == font);
        if (source != null)
        {
            SubtitleFontComboBox.SelectedItem = source;
        }
    }
}

/// <summary>内嵌全屏/小窗标题区的响应式取舍（纯计算，便于验证）.</summary>
internal readonly record struct PlayerHeaderLayout(bool ShowStatus, bool ShowTitle)
{
    /// <summary>低于此宽度时隐藏缓存速度/清晰度/设置，保留返回、置顶、退出小窗/全屏与关闭.</summary>
    internal const double StatusMinWidth = 560;

    /// <summary>低于此宽度时隐藏标题文字，240 像素宽的小窗仍可放下全部直接操作按钮.</summary>
    internal const double TitleMinWidth = 320;

    internal static PlayerHeaderLayout ForEmbeddedWidth(double width)
        => new(width >= StatusMinWidth, width >= TitleMinWidth);
}

/// <summary>
/// 标题区命令门：执行前、CanExecute 回调（可能重入）之后各确认一次点击时捕获的所有者仍是当前所有者.
/// </summary>
internal static class PlayerHeaderCommandGate
{
    internal static async Task<bool> RunAsync(ICommand command, Func<bool> isCurrent)
    {
        if (!isCurrent() || !command.CanExecute(null) || !isCurrent()) return false;
        if (command is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            command.Execute(null);
        }

        return true;
    }
}

/// <summary>呈现切换时播放按钮连续位移的几何与时长（纯计算，便于验证）.</summary>
internal static class PlayerTransportRelocationMath
{
    internal const double EnterMilliseconds = 250;
    internal const double ExitMilliseconds = 200;
    internal const double FadeMilliseconds = 150;

    /// <summary>与 PlayerOverlay.xaml 中 TransportControls 的隐式显示动画起点（0,80）一致.</summary>
    internal const double OverlayTransportEntryOffsetY = 80;

    internal static Point? ToFraction(Point center, Size host)
    {
        if (!(host.Width > 0) || !(host.Height > 0) || !double.IsFinite(center.X) || !double.IsFinite(center.Y)) return null;
        return new Point(center.X / host.Width, center.Y / host.Height);
    }

    /// <summary>进入全屏或从常规进入小窗用 250ms；回到常规或从全屏回到小窗用 200ms.</summary>
    internal static double GetDurationMilliseconds(PlayerPresentationMode from, PlayerPresentationMode to)
        => to == PlayerPresentationMode.FullScreen
            || (to == PlayerPresentationMode.CompactOverlay && from != PlayerPresentationMode.FullScreen)
            ? EnterMilliseconds
            : ExitMilliseconds;

    /// <summary>新传输条刚从隐藏变为显示时，其自身隐式动画从下方 80 起步；按钮起点需扣除这段，避免二次下沉.</summary>
    internal static double GetParentEntryOffsetY(bool fromOverlayTransport, bool toOverlayTransport)
        => toOverlayTransport && !fromOverlayTransport ? OverlayTransportEntryOffsetY : 0;

    /// <summary>旧相对位置映射到新尺寸后与新按钮中心的差值；位移不足 1 像素时不做动画.</summary>
    internal static Vector2? ComputeStartOffset(Point oldFraction, Size newHost, Point newCenter, double parentEntryOffsetY)
    {
        if (!(newHost.Width > 0) || !(newHost.Height > 0) || !double.IsFinite(oldFraction.X) || !double.IsFinite(oldFraction.Y)
            || !double.IsFinite(newCenter.X) || !double.IsFinite(newCenter.Y)) return null;
        var dx = (oldFraction.X * newHost.Width) - newCenter.X;
        var dy = (oldFraction.Y * newHost.Height) - newCenter.Y - parentEntryOffsetY;
        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) return null;
        return new Vector2((float)dx, (float)dy);
    }
}
