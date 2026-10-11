// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.MpvKernel.Core.Enums;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Core;

public sealed partial class OverlayTransportControls : PlayerControlBase
{
    public static readonly DependencyProperty LeftElementProperty =
        DependencyProperty.Register(nameof(LeftElement), typeof(object), typeof(OverlayTransportControls), new PropertyMetadata(default));

    public static readonly DependencyProperty ProgressBarVisibleProperty =
        DependencyProperty.Register(nameof(ProgressBarVisible), typeof(bool), typeof(OverlayTransportControls), new PropertyMetadata(true));

    public static readonly DependencyProperty IsSkipButtonsVisibleProperty =
        DependencyProperty.Register(nameof(IsSkipButtonsVisible), typeof(bool), typeof(OverlayTransportControls), new PropertyMetadata(true));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(OverlayTransportControls),
            new PropertyMetadata(false, OnIsActiveChanged));

    private readonly record struct InputOwner(PlayerViewModel ViewModel, long Generation, long Epoch);
    private readonly DispatcherTimer _speedChangeTimer;
    private readonly DispatcherTimer _progressChangeTimer;
    private readonly DispatcherTimer _speedRefreshTimer;
    private readonly DispatcherTimer _volumeCorrectionTimer;
    private InputOwner? _pendingSpeedOwner;
    private InputOwner? _pendingProgressOwner;
    private InputOwner? _speedRefreshOwner;
    private InputOwner? _volumeCorrectionOwner;
    private InputOwner? _danmakuOptionsOwner;
    private double _pendingSpeed;
    private double _pendingProgress;
    private long _inputEpoch;
    private long _speedRevision;
    private long _popupRevision;
    private bool _loaded;
    private bool _synchronizingSliders;
    private bool _hasFocus;
    private bool _cancelingInput;
    private PlayerViewModel? _subscribedViewModel;
    private readonly HashSet<uint> _contacts = [];
    private readonly IslandPopupClaims _popups = new();
    private bool _isFirstChangeVolume = true;

    public OverlayTransportControls()
    {
        _speedChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _progressChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _speedRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _volumeCorrectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        // Self-owned timers keep one handler for the control lifetime. Unload stops, not detaches.
        _speedChangeTimer.Tick += OnSpeedChangeTick;
        _progressChangeTimer.Tick += OnProgressChangeTick;
        _speedRefreshTimer.Tick += OnSpeedRefreshTick;
        _volumeCorrectionTimer.Tick += OnVolumeCorrectionTick;
        InitializeComponent();
        DanmakuOptionsTip.RegisterPropertyChangedCallback(TeachingTip.IsOpenProperty, OnDanmakuOptionsOpenChanged);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnContactStarted), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnContactEnded), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnContactEnded), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnContactEnded), true);
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    public object LeftElement
    {
        get => (object)GetValue(LeftElementProperty);
        set => SetValue(LeftElementProperty, value);
    }

    public bool ProgressBarVisible
    {
        get => (bool)GetValue(ProgressBarVisibleProperty);
        set => SetValue(ProgressBarVisibleProperty, value);
    }

    public bool IsSkipButtonsVisible
    {
        get => (bool)GetValue(IsSkipButtonsVisibleProperty);
        set => SetValue(IsSkipButtonsVisibleProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsInteracting { get; private set; }
    public event EventHandler<bool>? InteractionStateChanged;

    private static void OnIsActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (OverlayTransportControls)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        else if (control._loaded) control.SynchronizeControls();
        control.IsEnabled = control.CaptureInput().HasValue;
        control.UpdateInteractionState();
    }

    private void OnLocalLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        SubscribeViewModel(ViewModel);
        SynchronizeControls();
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        _isFirstChangeVolume = true;
        SubscribeViewModel(_loaded ? newValue : null);
        if (_loaded) SynchronizeControls();
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        SubscribeViewModel(null);
        SubtitleRepeater?.ItemsSource = null;
        IsEnabled = false;
    }

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;
        if (_subscribedViewModel is { } old)
        {
            old.ProgressChanged -= OnViewModelProgressChanged;
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _subscribedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.ProgressChanged += OnViewModelProgressChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName == nameof(PlayerViewModel.MediaGeneration)
            || (args.PropertyName == nameof(PlayerViewModel.IsConnecting) && _subscribedViewModel!.IsConnecting))
        {
            CancelPendingInput();
            _isFirstChangeVolume = true;
        }
        else if (args.PropertyName == nameof(PlayerViewModel.IsConnecting)) SynchronizeControls();
        else if (args.PropertyName == nameof(PlayerViewModel.IsWindowDeactivated))
        {
            if (_subscribedViewModel!.IsWindowDeactivated) CancelPendingInput();
            else SynchronizeControls();
        }
        IsEnabled = CaptureInput().HasValue;
    }

    private InputOwner? CaptureInput()
        => _loaded && IsActive && !_cancelingInput && ViewModel is { } vm
            && ReferenceEquals(vm, _subscribedViewModel) && !vm.IsPlaybackClosing
            && !vm.IsConnecting && !vm.IsWindowDeactivated && vm.Player?.IsPlaybackInitialized == true
            ? new InputOwner(vm, vm.MediaGeneration, _inputEpoch) : null;

    private bool IsCurrent(InputOwner owner)
        => CaptureInput() is { } current && ReferenceEquals(current.ViewModel, owner.ViewModel)
            && current.Generation == owner.Generation && current.Epoch == owner.Epoch;

    private void SynchronizeControls()
    {
        IsEnabled = CaptureInput().HasValue;
        if (!_loaded || !IsActive) return;
        _synchronizingSliders = true;
        try
        {
            Bindings.Update();
            SubtitleRepeater.ItemsSource = ViewModel?.Subtitles;
            if (ViewModel?.Player is { } player)
            {
                ProgressSlider.Maximum = player.Duration;
                ProgressSlider.Value = player.Position;
            }
        }
        finally { _synchronizingSliders = false; }
        if (_isFirstChangeVolume && CaptureInput() is { } owner && owner.ViewModel.Player.Volume > 100
            && FullVolumeContainer.Children.OfType<Slider>().Any(slider => slider.Value == 100))
            ScheduleVolumeCorrection(owner);
        UpdateLayoutState(RootGrid.ActualWidth);
        CheckSpeedButtonState();
        UpdateInteractionState();
    }

    public void CancelPendingInput()
    {
        if (_cancelingInput) return;
        _cancelingInput = true;
        try
        {
            ++_inputEpoch;
            ++_speedRevision;
            ++_popupRevision;
            _speedChangeTimer?.Stop();
            _progressChangeTimer?.Stop();
            _speedRefreshTimer?.Stop();
            _volumeCorrectionTimer?.Stop();
            if (_volumeCorrectionOwner.HasValue) _isFirstChangeVolume = true;
            _pendingSpeedOwner = _pendingProgressOwner = _speedRefreshOwner = _volumeCorrectionOwner = null;
            _contacts.Clear();
            ResetProgressPreview();
            var danmakuOwner = _danmakuOptionsOwner;
            _danmakuOptionsOwner = null;
            _popups.Cancel(() =>
            {
                // This binding may itself close the tip: release leases/flags first.
                if (danmakuOwner is { } owner) owner.ViewModel.IsDanmakuOptionsVisible = false;
            });
        }
        finally
        {
            _cancelingInput = false;
            UpdateInteractionState();
        }
    }

    private void ResetProgressPreview()
    {
        if (ChangingTimeBlock is null) return;
        ChangingTimeBlock.Text = string.Empty;
        ChangingTimeBlock.Visibility = Visibility.Collapsed;
        CommonPositionContainer.Visibility = Visibility.Visible;
    }

    private void OnControlGotFocus(object sender, RoutedEventArgs args) => UpdateInteractionState();
    private void OnControlLostFocus(object sender, RoutedEventArgs args) => UpdateInteractionState();

    private bool ContainsFocus()
    {
        if (XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, this)) return true;
        return false;
    }

    private void OnContactStarted(object sender, PointerRoutedEventArgs args)
    {
        if (!_loaded || !IsActive) return;
        _contacts.Add(args.Pointer.PointerId);
        UpdateInteractionState();
    }

    private void OnContactEnded(object sender, PointerRoutedEventArgs args)
    {
        _contacts.Remove(args.Pointer.PointerId);
        UpdateInteractionState();
    }

    private void UpdateInteractionState()
    {
        _hasFocus = _loaded && IsActive && ContainsFocus();
        var interacting = _loaded && IsActive && (_hasFocus || _contacts.Count > 0 || _popups.Count > 0
            || _danmakuOptionsOwner.HasValue || _pendingSpeedOwner.HasValue || _pendingProgressOwner.HasValue);
        if (interacting == IsInteracting) return;
        IsInteracting = interacting;
        InteractionStateChanged?.Invoke(this, interacting);
    }

    private async Task<bool> ExecuteInputAsync(InputOwner owner, Func<Task> operation)
    {
        if (!IsCurrent(owner)) return false;
        try { await operation(); return IsCurrent(owner); }
        catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested || !IsCurrent(owner))
        {
            return false;
        }
        catch (Exception ex)
        {
            this.Get<ILogger<OverlayTransportControls>>().LogError(ex, "Fullscreen transport input failed.");
            if (IsCurrent(owner)) owner.ViewModel.ErrorMessage = ex.Message;
            return false;
        }
    }

#pragma warning disable CA1822
    private FluentIcons.Common.Symbol GetPlayPauseSymbol(MpvPlayerState state)
        => state == MpvPlayerState.Playing ? FluentIcons.Common.Symbol.Pause : FluentIcons.Common.Symbol.Play;

    private FluentIcons.Common.Symbol GetCompactOverlaySymbol(bool isCompactOverlay)
        => isCompactOverlay ? FluentIcons.Common.Symbol.ContractDownLeft : FluentIcons.Common.Symbol.ContractUpRight;

    private FluentIcons.Common.Symbol GetFullScreenSymbol(bool isFullScreen)
        => isFullScreen ? FluentIcons.Common.Symbol.FullScreenMinimize : FluentIcons.Common.Symbol.FullScreenMaximize;

    private bool IsPlayPauseEnabled(MpvPlayerState state)
        => state is MpvPlayerState.Playing or MpvPlayerState.Paused or MpvPlayerState.End;

    private string GetPlayPauseTip(bool isPlaying)
        => isPlaying ? ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Pause) : ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Play);

    private bool IsLoading(MpvPlayerState state, bool isLoading)
        => state is MpvPlayerState.Seeking or MpvPlayerState.Buffering || isLoading;

    private string GetTimeText(double seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private string GetSpeedText(double speed)
        => $"{ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.PlaybackRate)}: {Math.Round(speed, 2)}x";

    private string GetCompactOverlayTip(bool isCompactOverlay)
        => isCompactOverlay ? ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.ExitCompactOverlay) : ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.EnterCompactOverlay);

    private string GetFullScreenTip(bool isFullScreen)
        => isFullScreen ? ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.ExitFullScreen) : ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.EnterFullScreen);

    private void OnViewModelProgressChanged(object? sender, double e)
    {
        if (CaptureInput() is not { } owner || !ReferenceEquals(sender, owner.ViewModel)
            || _pendingProgressOwner.HasValue || !double.IsFinite(e)) return;
        _synchronizingSliders = true;
        try
        {
            if (ProgressSlider.Maximum != owner.ViewModel.Player.Duration)
                ProgressSlider.Maximum = owner.ViewModel.Player.Duration;
            if (ProgressSlider.Value != e) ProgressSlider.Value = e;
        }
        finally { _synchronizingSliders = false; }
    }

    private async void OnPlayPauseButtonClick(object sender, RoutedEventArgs e)
    {
        if (CaptureInput() is not { } owner) return;
        if (owner.ViewModel.Player.PlaybackState is MpvPlayerState.End)
        {
            await ExecuteInputAsync(owner, () => owner.ViewModel.ReplayAsync());
        }
        else if (owner.ViewModel.Player.PlaybackState is MpvPlayerState.Playing or MpvPlayerState.Paused)
            await ExecuteInputAsync(owner, () => owner.ViewModel.PlayPauseCommand.ExecuteAsync(null));
    }

    private void OnProgressValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var v = e.NewValue;
        if (_synchronizingSliders || CaptureInput() is not { } owner || !double.IsFinite(v)
            || ProgressSlider.Maximum != owner.ViewModel.Player.Duration
            || e.OldValue > ProgressSlider.Maximum || e.OldValue < ProgressSlider.Minimum
            || (ProgressSlider.FocusState == FocusState.Unfocused && _contacts.Count == 0)
            || Math.Abs(v - owner.ViewModel.Player.Position) < 1.5) return;

        ChangingTimeBlock.Text = GetTimeText(v);
        ChangingTimeBlock.Visibility = Visibility.Visible;
        CommonPositionContainer.Visibility = Visibility.Collapsed;
        _pendingProgress = v;
        _pendingProgressOwner = owner;
        _progressChangeTimer.Stop();
        _progressChangeTimer.Start();
        UpdateInteractionState();
    }

    private async void OnVolumeValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var newValue = e.NewValue;
        if (CaptureInput() is not { } owner || !double.IsFinite(newValue)) return;
        var player = owner.ViewModel.Player;
        if (_isFirstChangeVolume && player.Volume > 100 && newValue == 100)
        {
            ScheduleVolumeCorrection(owner);
            return;
        }
        if (_synchronizingSliders || sender is not Slider slider
            || newValue == Math.Clamp(player.Volume, slider.Minimum, slider.Maximum)
            || (slider.FocusState == FocusState.Unfocused && _contacts.Count == 0)
            || Math.Abs(newValue - player.Volume) < 1) return;
        await ExecuteInputAsync(owner, () => owner.ViewModel.ChangeVolumeAsync(newValue));
    }

    private void OnSpeedValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var newValue = e.NewValue;
        CheckSpeedButtonState();
        if (_synchronizingSliders || CaptureInput() is not { } owner || !double.IsFinite(newValue)
            || sender is not Slider slider
            || newValue == Math.Clamp(owner.ViewModel.Player.PlaybackRate, slider.Minimum, slider.Maximum)
            || (slider.FocusState == FocusState.Unfocused && _contacts.Count == 0)
            || Math.Abs(newValue - owner.ViewModel.Player.PlaybackRate) < 0.03) return;
        _pendingSpeed = newValue;
        _pendingSpeedOwner = owner;
        ++_speedRevision;
        _speedChangeTimer.Stop();
        _speedChangeTimer.Start();
        UpdateInteractionState();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_loaded || !IsActive || ViewModel is null) return;
        UpdateLayoutState(e.NewSize.Width);
    }

    private void UpdateLayoutState(double width)
    {
        if (!_loaded || !IsActive || ViewModel is null) return;
        var miniWidth = ViewModel.IsDanmakuControlVisible ? 900 : 760;
        if (width < miniWidth)
        {
            VisualStateManager.GoToState(this, nameof(NarrowState), false);
        }
        else if (width < 1100)
        {
            VisualStateManager.GoToState(this, nameof(MediumState), false);
        }
        else if (width < 1280)
        {
            VisualStateManager.GoToState(this, nameof(BelowWideState), false);
        }
        else
        {
            VisualStateManager.GoToState(this, nameof(WideState), false);
        }

        BalanceLeftRightWidth();
    }

    private void BalanceLeftRightWidth()
    {
        var width = Math.Min(LeftHolderGrid.ActualWidth, RightPanel.ActualWidth);
        if (width == 0 && RightPanel.ActualWidth > 0)
        {
            width = RightPanel.ActualWidth;
        }

        if (width < 300 && LeftHolderGrid.ActualWidth > 300)
        {
            width = 300;
        }

        LeftPresenter.Width = width;
    }

    private async void OnSpeedChangeTick(object? sender, object e)
    {
        var owner = _pendingSpeedOwner;
        var speed = _pendingSpeed;
        var revision = _speedRevision;
        _pendingSpeedOwner = null;
        _speedChangeTimer.Stop();
        UpdateInteractionState();
        if (owner is not { } request || !IsCurrent(request)) return;
        if (Math.Abs(speed - request.ViewModel.Player.PlaybackRate) > 0.1
            && await ExecuteInputAsync(request, () => request.ViewModel.ChangeSpeedAsync(speed))
            && revision == _speedRevision)
            CheckSpeedButtonState();
    }

    private async void OnProgressChangeTick(object? sender, object e)
    {
        var owner = _pendingProgressOwner;
        var position = _pendingProgress;
        _pendingProgressOwner = null;
        _progressChangeTimer.Stop();
        ResetProgressPreview();
        UpdateInteractionState();
        if (owner is not { } request || !IsCurrent(request)
            || Math.Abs(position - request.ViewModel.Player.Position) < 1.5) return;
        if (request.ViewModel.IsNextTipShown) request.ViewModel.HideNextTipCommand.Execute(default);
        if (request.ViewModel.Player.PlaybackState is MpvPlayerState.End)
            await ExecuteInputAsync(request, () => request.ViewModel.ReplayAsync(position));
        else
            await ExecuteInputAsync(request, () => request.ViewModel.ChangePositionAsync(position));
    }

    private void OnSubtitleFlyoutOpened(object sender, object e)
        => OpenPopup(sender);

    private void OnRightPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loaded && IsActive) BalanceLeftRightWidth();
    }

    private void CheckSpeedButtonState()
    {
        if (CaptureInput() is not { } owner) return;
        _speedRefreshOwner = owner;
        _speedRefreshTimer.Stop();
        _speedRefreshTimer.Start();
    }

    private void OnSpeedRefreshTick(object? sender, object e)
    {
        var owner = _speedRefreshOwner;
        _speedRefreshOwner = null;
        _speedRefreshTimer.Stop();
        if (owner is not { } request || !IsCurrent(request)) return;
        var speed = request.ViewModel.Player.PlaybackRate;
        HalfSpeedButton.IsChecked = false;
        DefaultSpeedButton.IsChecked = false;
        OneTwoFiveSpeedButton.IsChecked = false;
        OneHalfSpeedButton.IsChecked = false;
        DoubleSpeedButton.IsChecked = false;
        TripleSpeedButton.IsChecked = false;
        if (Math.Abs(speed - 0.5) < 0.01)
        {
            HalfSpeedButton.IsChecked = true;
        }
        else if (Math.Abs(speed - 1) < 0.01)
        {
            DefaultSpeedButton.IsChecked = true;
        }
        else if (Math.Abs(speed - 1.25) < 0.01)
        {
            OneTwoFiveSpeedButton.IsChecked = true;
        }
        else if (Math.Abs(speed - 1.5) < 0.01)
        {
            OneHalfSpeedButton.IsChecked = true;
        }
        else if (Math.Abs(speed - 2) < 0.01)
        {
            DoubleSpeedButton.IsChecked = true;
        }
        else if (Math.Abs(speed - 3) < 0.01)
        {
            TripleSpeedButton.IsChecked = true;
        }
    }

    private async void OnQuickSpeedButtonClick(object sender, RoutedEventArgs e)
    {
        if (CaptureInput() is not { } owner) return;
        var speed = Convert.ToDouble((sender as FrameworkElement)?.Tag);
        var revision = ++_speedRevision;
        _pendingSpeedOwner = null;
        _speedChangeTimer.Stop();
        UpdateInteractionState();
        if (Math.Abs(speed - owner.ViewModel.Player.PlaybackRate) > 0.1)
        {
            if (await ExecuteInputAsync(owner, () => owner.ViewModel.ChangeSpeedAsync(speed))
                && revision == _speedRevision) CheckSpeedButtonState();
        }
        else
        {
            CheckSpeedButtonState();
        }
    }

    private void OnDisplayFlyoutOpened(object sender, object e)
        => OpenPopup(sender);

    private void OnDanmakuOptionsButtonClick(object sender, RoutedEventArgs e)
    {
        if (_popups.IsClosing(DanmakuOptionsTip) || CaptureInput() is not { } owner) return;
        _danmakuOptionsOwner = owner;
        owner.ViewModel.IsDanmakuOptionsVisible = !owner.ViewModel.IsDanmakuOptionsVisible;
        UpdateInteractionState();
    }

    private void OnDanmakuOptionsClosed(IslandPopupClaims.Claim claim)
    {
        var request = new InputOwner(claim.ViewModel, claim.Generation, claim.Epoch);
        if (_danmakuOptionsOwner != request) return;
        _danmakuOptionsOwner = null;
        if (IsCurrent(request)) request.ViewModel.IsDanmakuOptionsVisible = false;
        UpdateInteractionState();
        var revision = _popupRevision;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (revision == _popupRevision && IsCurrent(request)) request.ViewModel.Danmaku.ResetStyle();
        });
    }

    private void ScheduleVolumeCorrection(InputOwner owner)
    {
        _isFirstChangeVolume = false;
        _volumeCorrectionOwner = owner;
        _volumeCorrectionTimer.Stop();
        _volumeCorrectionTimer.Start();
    }

    private void OnVolumeCorrectionTick(object? sender, object e)
    {
        var owner = _volumeCorrectionOwner;
        _volumeCorrectionOwner = null;
        _volumeCorrectionTimer.Stop();
        if (owner is not { } request || !IsCurrent(request)) return;
        _synchronizingSliders = true;
        try { request.ViewModel.Player.RaisePropertyChanged(nameof(request.ViewModel.Player.Volume)); }
        finally { _synchronizingSliders = false; }
    }

    private bool IsDanmakuOptionsOpen(bool active, bool requested)
        => active && _loaded && requested && ViewModel?.IsPlaybackClosing == false
            && !ViewModel.IsConnecting && !ViewModel.IsWindowDeactivated;

    private void OnDanmakuOptionsOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        if (sender is not TeachingTip { IsOpen: true }) return;
        OpenPopup(sender);
        if (_popups.Get(sender) is { } claim)
            _danmakuOptionsOwner = new InputOwner(claim.ViewModel, claim.Generation, claim.Epoch);
        UpdateInteractionState();
    }

    private void OpenPopup(object popup)
    {
        if (CaptureInput() is not { } owner)
        {
            IslandPopupClaims.Hide(popup);
            return;
        }
        ++_popupRevision;
        _popups.Open(popup, owner.ViewModel, owner.Epoch, claim =>
        {
            ++_popupRevision;
            if (ReferenceEquals(claim.Popup, DanmakuOptionsTip)) OnDanmakuOptionsClosed(claim);
            UpdateInteractionState();
        });
        UpdateInteractionState();
    }
#pragma warning restore CA1822
}
