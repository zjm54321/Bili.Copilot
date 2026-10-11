// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using System.ComponentModel;

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
        DispatcherQueue.TryEnqueue(async () =>
        {
            var popup = new TipPopup() { Text = text };
            TipContainer.Visibility = Visibility.Visible;
            TipContainer.Children.Add(popup);
            await popup.ShowAsync(type);
            TipContainer.Children.Remove(popup);
            TipContainer.Visibility = Visibility.Collapsed;

        });
    }

    internal static Visibility IsTopMostButtonVisible(bool isFullScreen, bool isAot)
        => (isFullScreen || isAot) ? Visibility.Collapsed : Visibility.Visible;

    private void OnLocalLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
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
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
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
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        ClearEmbeddedInteractionNotice();
        if (!ReferenceEquals(ViewModel, newValue)) return;
        CancelPendingInput();
        if (!ReferenceEquals(ViewModel, newValue)) return;
        SubscribeViewModel(_loaded ? newValue : null);
        RefreshEmbeddedInteractionNotice();
        // Retire a width callback captured for the old VM; requeue only the latest width for the new one.
        if (_headerWidthTicket is not null) QueueHeaderWidthUpdate();
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
        if (e.PropertyName is null or "" or nameof(PlayerViewModel.Connector) or nameof(PlayerViewModel.MediaGeneration)
            or nameof(PlayerViewModel.Presentation) or nameof(PlayerViewModel.IsEmbedded) or nameof(PlayerViewModel.Player)
            or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.IsPlaybackClosing) or nameof(PlayerViewModel.ErrorMessage)
            or nameof(PlayerViewModel.IsPopupVisible) or nameof(PlayerViewModel.IsExtraPanelVisible) or nameof(PlayerViewModel.IsSubtitleSettingsVisible))
        {
            RefreshEmbeddedInteractionNotice();
            if (!ReferenceEquals(sender, _subscribedViewModel) || !ReferenceEquals(sender, ViewModel)) return;
        }
        if (e.PropertyName == nameof(ViewModel.IsControlsVisible))
        {
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
            CancelPendingInput();
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

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.Close();

    private void OnCloseTipButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.ErrorMessage = default;

    private void OnBackRequested(object sender, EventArgs e)
        => ViewModel.BackCommand.Execute(default);

    private void OnSubtitleSettingItemClick(object sender, RoutedEventArgs e)
    {
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

    private void QueueHeaderWidthUpdate()
    {
        _headerWidthTicket = null;
        if (!_loaded || _headerWidth <= 0) return;
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
                var width = _headerWidth;
                if (width < 700)
                {
                    VisualStateManager.GoToState(this, nameof(NarrowState), false);
                    HeaderBar.TitleMaxWidth = width > 200 ? width - 200 : 120;
                }
                else
                {
                    VisualStateManager.GoToState(this, nameof(DefaultState), false);
                    HeaderBar.TitleMaxWidth = width - 520;
                }
            })) return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnHeaderBarSizeChanged: {ex.Message}");
        }

        if (ReferenceEquals(_headerWidthTicket, ticket)) _headerWidthTicket = null;
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
    /// 全屏与小窗（CompactOverlay）使用新版 <see cref="OverlayTransportControls"/>；Normal/FullWindow 保留 Store 岛式控件.
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
        // 非全屏且内嵌主窗口外壳时，隐藏浮层标题以避免与外壳标题重复.
        var duplicatedByShell = isEmbedded && mode != PlayerPresentationMode.FullScreen;
        return isControlsVisible && !duplicatedByShell ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSourceItemClick(object sender, RoutedEventArgs e)
    {
        var selectedSource = (sender as FrameworkElement)?.DataContext as SourceItemViewModel;
        if (!_loaded || ViewModel?.IsControlsVisible != true || ViewModel.IsWindowDeactivated
            || !_popups.IsCurrent(_popups.Get(SourceFlyout), ViewModel, _inputEpoch)
            || selectedSource?.IsEnabled != true
            || !ViewModel.Sources.Any(item => ReferenceEquals(item, selectedSource))) return;
        selectedSource?.ActiveCommand.Execute(default);
        SourceFlyout?.Hide();
    }

    private void OnOverlayFlyoutOpened(object sender, object e)
    {
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
