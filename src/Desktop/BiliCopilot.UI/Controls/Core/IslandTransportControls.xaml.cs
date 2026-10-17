// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Player;
using System.ComponentModel;
using System.Diagnostics;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// 岛式浮层传输控件。由 Store 源 <c>VideoTransportControl</c> 迁移而来，仅保留播放/进度/音量/倍速/窗口模式，
/// 并适配当前 <see cref="PlayerViewModel"/> 的命令与字段。
/// 通过最小适配接口（<see cref="IsActive"/> / <see cref="IsInputBlocked"/> /
/// <see cref="InteractionStateChanged"/> / <see cref="CancelPendingInput"/>）供持久 PlayerOverlay 融合使用；
/// 控件自身不构建 Client/Player/Host，也不恢复旧 VM/引擎/AI。
/// </summary>
public sealed partial class IslandTransportControls : PlayerControlBase
{
    /// <summary>
    /// <see cref="LeftContent"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty LeftContentProperty =
        DependencyProperty.Register(nameof(LeftContent), typeof(object), typeof(IslandTransportControls), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="MiddleContent"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty MiddleContentProperty =
        DependencyProperty.Register(nameof(MiddleContent), typeof(object), typeof(IslandTransportControls), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="RightContent"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(IslandTransportControls), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="IsActive"/> 的依赖属性. 由宿主 PlayerOverlay 绑定，表示本控件是否可见并承载输入.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandTransportControls), new PropertyMetadata(false, OnIsActiveChanged));

    private readonly List<double> _speedChangeList = [];
    private readonly List<double> _progressChangeList = [];
    private readonly DispatcherTimer _speedChangeTimer;
    private readonly DispatcherTimer _progressChangeTimer;
    private PlayerViewModel? _subscribedViewModel;
    private MpvPlayer? _subscribedPlayer;
    private PropertyChangedEventHandler? _playerPropertyChanged;
    private PlayerViewModel? _pendingSeekOwner;
    private PlayerViewModel? _pendingSpeedOwner;
    private IslandPopupClaims.Claim? _pendingSpeedClaim;
    private long _pendingSeekGeneration;
    private long _pendingSpeedGeneration;
    private readonly IslandPopupClaims _popups = new();
    private long _inputEpoch;
    private bool _loaded;
    private bool _synchronizingSliders;
    private bool _hasFocus;
    private bool _isInteracting;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandTransportControls"/> class.
    /// </summary>
    public IslandTransportControls()
    {
        InitializeComponent();
        // 仅在交互后启动的短时防抖计时器，非永动计时器.
        _speedChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _progressChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _speedChangeTimer.Tick += OnSpeedChangeTick;
        _progressChangeTimer.Tick += OnProgressChangeTick;
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    /// <summary>
    /// 左侧内容.
    /// </summary>
    public object LeftContent
    {
        get => (object)GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }

    /// <summary>
    /// 中部内容.
    /// </summary>
    public object MiddleContent
    {
        get => (object)GetValue(MiddleContentProperty);
        set => SetValue(MiddleContentProperty, value);
    }

    /// <summary>
    /// 右侧内容.
    /// </summary>
    public object RightContent
    {
        get => (object)GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    /// <summary>
    /// 获取或设置本控件是否可见并承载输入. 失活时立即取消未触发的延迟调度.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>
    /// 获取当前是否禁止接受输入（未激活、关闭中、窗口失活、连接中或没有可用播放会话）.
    /// </summary>
    public bool IsInputBlocked =>
        !_loaded || !IsActive
        || ViewModel is null
        || ViewModel.IsPlaybackClosing
        || ViewModel.IsWindowDeactivated
        || ViewModel.IsConnecting
        || ViewModel.Client?.IsDisposed != false
        || ViewModel.Player?.IsPlaybackInitialized != true;

    /// <summary>
    /// 获取当前是否正在进行需阻止自动隐藏/快捷键的交互（拖动、延迟调度中或浮出已打开）.
    /// </summary>
    public bool IsInteracting => _isInteracting;

    /// <summary>呈现切换时用于连续位移动画的播放/暂停按钮（仅视觉，不改变命令或输入状态）.</summary>
    internal FrameworkElement PlayPauseTarget => PlayPauseButton;

    /// <summary>
    /// 交互状态发生变化. 参数为新值，与 <see cref="IsInteracting"/> 一致.
    /// </summary>
    public event EventHandler<bool>? InteractionStateChanged;

    /// <summary>
    /// 取消尚未触发的延迟 seek/倍速调度并复位交互状态. 宿主在关闭、失活或替换媒体前调用.
    /// </summary>
    public void CancelPendingInput()
    {
        ++_inputEpoch;
        _progressChangeList.Clear();
        _speedChangeList.Clear();
        _progressChangeTimer?.Stop();
        _speedChangeTimer?.Stop();
        _pendingSeekOwner = null;
        _pendingSpeedOwner = null;
        _pendingSpeedClaim = null;
        _hasFocus = false;
        UpdateSlots(cancel: true);
        _popups.Cancel();
        RefreshInteraction();
    }

    /// <inheritdoc/>
    private void OnLocalLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        SubscribeViewModel(ViewModel);
        SynchronizeControls();
    }

    /// <inheritdoc/>
    private void OnLocalUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        _hasFocus = false;
        RefreshInteraction();
        // 计时器归控件所有，其 Tick 处理器保留以便重载；仅解除对外部 VM 的订阅.
        SubscribeViewModel(null);
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        SubscribeViewModel(_loaded ? newValue : null);
        SynchronizeControls();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (IslandTransportControls)d;
        if (e.NewValue is false) control.CancelPendingInput();
        control.SynchronizeControls();
    }

    private void SynchronizeControls()
    {
        IsEnabled = !IsInputBlocked;
        UpdateSlots(cancel: false);
        if (!_loaded || !IsActive) return;
        _synchronizingSliders = true;
        try
        {
            Bindings.Update();
            if (ViewModel?.Player is { } player)
            {
                ProgressSlider.Maximum = player.Duration;
                ProgressSlider.Value = player.Position;
            }
        }
        finally { _synchronizingSliders = false; }
        RefreshInteraction();
    }

    private void UpdateSlots(bool cancel)
    {
        Update(LeftContent);
        Update(MiddleContent);
        Update(RightContent);

        void Update(object? content)
        {
            var active = !IsInputBlocked;
            switch (content)
            {
                case IslandDanmakuBox box:
                    box.IsActive = active;
                    if (cancel) box.CancelPendingInput();
                    break;
                case IslandFormatButton format:
                    format.IsActive = active;
                    if (cancel) format.CancelPendingInput();
                    break;
                case IslandSubtitleButton subtitle:
                    subtitle.IsActive = active;
                    if (cancel) subtitle.CancelPendingInput();
                    break;
                case IslandNextVideoButton next:
                    next.IsActive = active;
                    if (cancel) next.CancelPendingInput();
                    break;
                case Panel panel:
                    foreach (var child in panel.Children) Update(child);
                    break;
            }
        }
    }

    private static bool IsPaused(MpvPlayerState state)
        => state is MpvPlayerState.Paused;

    private static Visibility GetPlayVisibility(MpvPlayerState state)
        => IsPaused(state) ? Visibility.Visible : Visibility.Collapsed;

    private static Visibility GetPauseVisibility(MpvPlayerState state)
        => IsPaused(state) ? Visibility.Collapsed : Visibility.Visible;

    private static bool IsBuffering(MpvPlayerState state, bool isLoading)
        => state is MpvPlayerState.Seeking or MpvPlayerState.Buffering || isLoading;

    private static Visibility GetBufferingVisibility(MpvPlayerState state, bool isLoading)
        => IsBuffering(state, isLoading) ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsNotBuffering(MpvPlayerState state, bool isLoading)
        => !IsBuffering(state, isLoading);

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        SubscribePlayer(null);
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.ProgressChanged -= OnViewModelProgressChanged;
        }

        _subscribedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.ProgressChanged += OnViewModelProgressChanged;
        }
        SubscribePlayer(viewModel);
    }

    private void SubscribePlayer(PlayerViewModel? owner)
    {
        if (_subscribedPlayer is not null && _playerPropertyChanged is not null)
        {
            _subscribedPlayer.PropertyChanged -= _playerPropertyChanged;
        }

        _subscribedPlayer = owner?.Player;
        _playerPropertyChanged = null;
        if (owner is null || _subscribedPlayer is not { } player) return;

        PropertyChangedEventHandler? handler = null;
        handler = (sender, args) =>
        {
            if (!ReferenceEquals(sender, player)
                || args.PropertyName is not (null or "" or nameof(MpvPlayer.IsPlaybackInitialized))) return;
            var generation = owner.MediaGeneration;
            var epoch = _inputEpoch;
            void SynchronizeInitialization()
            {
                // A queued notification belongs to this subscription, owner, media and input lifetime.
                if (!_loaded || !ReferenceEquals(handler, _playerPropertyChanged)
                    || !ReferenceEquals(owner, _subscribedViewModel) || !ReferenceEquals(owner, ViewModel)
                    || !ReferenceEquals(player, _subscribedPlayer) || !ReferenceEquals(player, owner.Player)
                    || generation != owner.MediaGeneration || epoch != _inputEpoch) return;
                if (!player.IsPlaybackInitialized) CancelPendingInput();
                SynchronizeControls();
            }

            if (DispatcherQueue.HasThreadAccess) SynchronizeInitialization();
            else DispatcherQueue.TryEnqueue(SynchronizeInitialization);
        };
        _playerPropertyChanged = handler;
        player.PropertyChanged += handler;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if ((e.PropertyName == nameof(ViewModel.IsWindowDeactivated) && ViewModel.IsWindowDeactivated)
            || IsInputBlocked || e.PropertyName is nameof(ViewModel.MediaGeneration)
                or nameof(ViewModel.Player) or nameof(ViewModel.Connector) or nameof(ViewModel.SelectedSource))
        {
            CancelPendingInput();
        }
        if (e.PropertyName == nameof(ViewModel.Player)) SubscribePlayer(_subscribedViewModel);
        if (e.PropertyName is nameof(ViewModel.IsWindowDeactivated) or nameof(ViewModel.IsConnecting)
            or nameof(ViewModel.IsPlaybackClosing) or nameof(ViewModel.Player)) SynchronizeControls();
    }

    private void OnViewModelProgressChanged(object? sender, double e)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel) || _progressChangeList.Count > 0 || IsInputBlocked)
        {
            return;
        }

        _synchronizingSliders = true;
        try
        {
            if (ProgressSlider.Maximum != ViewModel.Player.Duration) ProgressSlider.Maximum = ViewModel.Player.Duration;
            if (ProgressSlider.Value != e && double.IsFinite(e)) ProgressSlider.Value = e;
        }
        finally { _synchronizingSliders = false; }
    }

    private void OnProgressSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_synchronizingSliders || IsInputBlocked || !double.IsFinite(e.NewValue)
            || ProgressSlider.FocusState == FocusState.Unfocused
            || e.OldValue > ProgressSlider.Maximum || e.OldValue < ProgressSlider.Minimum)
        {
            return;
        }

        var v = e.NewValue;
        if (ProgressSlider.Maximum != ViewModel.Player.Duration || !ViewModel.Player.IsPlaybackInitialized)
        {
            return;
        }

        if (Math.Abs(v - ViewModel.Player.Position) < 1.5)
        {
            return;
        }

        _pendingSeekOwner = ViewModel;
        _pendingSeekGeneration = ViewModel.MediaGeneration;
        _progressChangeList.Clear();
        _progressChangeList.Add(v);
        _progressChangeTimer.Stop();
        _progressChangeTimer.Start();
        RefreshInteraction();
    }

    private async void OnProgressChangeTick(object? sender, object e)
    {
        try
        {
            var last = _progressChangeList.LastOrDefault();
            _progressChangeList.Clear();
            _progressChangeTimer.Stop();
            var owner = _pendingSeekOwner;
            _pendingSeekOwner = null;
            RefreshInteraction();

            // 触发时核对捕获的 VM 与媒体代次，绝不在此处读取新媒体.
            if (owner is null || !ReferenceEquals(owner, ViewModel)
                || owner.MediaGeneration != _pendingSeekGeneration || IsInputBlocked)
            {
                return;
            }

            if (Math.Abs(last - owner.Player.Position) < 1.5)
            {
                return;
            }

            if (owner.Player.PlaybackState is MpvPlayerState.End)
            {
                // 唯一业务重播入口.
                await owner.ReplayAsync(last);
            }
            else
            {
                await owner.ChangePositionAsync(last);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Island transport seek failed: {ex.Message}");
        }
    }

    private async void OnVolumeSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_synchronizingSliders || IsInputBlocked || !double.IsFinite(e.NewValue)
            || sender is not Slider slider || slider.FocusState == FocusState.Unfocused
            || !_popups.IsCurrentFlyout(_popups.Get(VolumeFlyout), VolumeFlyout, ViewModel, _inputEpoch, requireOpened: true)
            || e.NewValue == Math.Clamp(ViewModel.Player.Volume, slider.Minimum, slider.Maximum))
        {
            return;
        }

        var newValue = e.NewValue;
        if (Math.Abs(newValue - ViewModel.Player.Volume) < 1)
        {
            return;
        }

        try
        {
            await ViewModel.ChangeVolumeAsync(newValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Island transport volume failed: {ex.Message}");
        }
    }

    private void OnSpeedSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SyncQuickSpeedButtons();
        if (_synchronizingSliders || IsInputBlocked || !double.IsFinite(e.NewValue)
            || sender is not Slider slider || slider.FocusState == FocusState.Unfocused
            || !_popups.IsCurrentFlyout(_popups.Get(SpeedFlyout), SpeedFlyout, ViewModel, _inputEpoch, requireOpened: true)
            || e.NewValue == Math.Clamp(ViewModel.Player.PlaybackRate, slider.Minimum, slider.Maximum))
        {
            return;
        }

        var newValue = e.NewValue;
        if (Math.Abs(newValue - ViewModel.Player.PlaybackRate) < 0.03)
        {
            return;
        }

        _pendingSpeedOwner = ViewModel;
        _pendingSpeedClaim = _popups.Get(SpeedFlyout);
        _pendingSpeedGeneration = ViewModel.MediaGeneration;
        _speedChangeList.Clear();
        _speedChangeList.Add(newValue);
        _speedChangeTimer.Stop();
        _speedChangeTimer.Start();
        RefreshInteraction();
    }

    private async void OnSpeedChangeTick(object? sender, object e)
    {
        try
        {
            var last = _speedChangeList.LastOrDefault();
            _speedChangeList.Clear();
            _speedChangeTimer.Stop();
            var owner = _pendingSpeedOwner;
            var claim = _pendingSpeedClaim;
            _pendingSpeedOwner = null;
            _pendingSpeedClaim = null;
            RefreshInteraction();

            if (owner is null || !ReferenceEquals(owner, ViewModel)
                || owner.MediaGeneration != _pendingSpeedGeneration || IsInputBlocked
                || !_popups.IsCurrentFlyout(claim, SpeedFlyout, owner, _inputEpoch, requireOpened: true))
            {
                return;
            }

            if (Math.Abs(last - owner.Player.PlaybackRate) > 0.1)
            {
                await owner.ChangeSpeedAsync(last);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Island transport speed failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 快捷倍速：与全屏倍速浮出相同的档位与阈值，但仍经本控件已打开的 SpeedFlyout 认领准入，
    /// 并替代尚未触发的滑块防抖值（不新增独立倍速状态）.
    /// </summary>
    private async void OnQuickSpeedButtonClick(object sender, RoutedEventArgs e)
    {
        var claim = _popups.Get(SpeedFlyout);
        if (IsInputBlocked || ViewModel is not { } owner
            || !_popups.IsCurrentFlyout(claim, SpeedFlyout, owner, _inputEpoch, requireOpened: true)
            || !double.TryParse((sender as FrameworkElement)?.Tag as string, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            SyncQuickSpeedButtons();
            return;
        }

        _speedChangeList.Clear();
        _speedChangeTimer.Stop();
        _pendingSpeedOwner = null;
        _pendingSpeedClaim = null;
        RefreshInteraction();
        SyncQuickSpeedButtons(speed);
        try
        {
            if (Math.Abs(speed - owner.Player.PlaybackRate) > 0.1)
            {
                await owner.ChangeSpeedAsync(speed);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Island transport quick speed failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(owner, ViewModel)) SyncQuickSpeedButtons();
        }
    }

    /// <summary>
    /// 按当前（或刚请求的）倍速同步快捷档位的选中态；纯视觉，不触发播放器写入.
    /// </summary>
    private void SyncQuickSpeedButtons(double? requested = null)
    {
        if (HalfSpeedButton is null || TripleSpeedButton is null) return;
        var speed = requested ?? ViewModel?.Player?.PlaybackRate ?? double.NaN;
        HalfSpeedButton.IsChecked = Math.Abs(speed - 0.5) < 0.01;
        DefaultSpeedButton.IsChecked = Math.Abs(speed - 1) < 0.01;
        OneTwoFiveSpeedButton.IsChecked = Math.Abs(speed - 1.25) < 0.01;
        OneHalfSpeedButton.IsChecked = Math.Abs(speed - 1.5) < 0.01;
        DoubleSpeedButton.IsChecked = Math.Abs(speed - 2) < 0.01;
        TripleSpeedButton.IsChecked = Math.Abs(speed - 3) < 0.01;
    }

    private void OnTransportFlyoutOpening(object sender, object e)
    {
        if (sender is not Flyout popup
            || (!ReferenceEquals(popup, VolumeFlyout) && !ReferenceEquals(popup, SpeedFlyout))) return;
        if (IsInputBlocked || ViewModel is not { } owner || popup.Content is not { } content)
        {
            IslandPopupClaims.Hide(popup);
            return;
        }

        var generation = owner.MediaGeneration;
        var epoch = _inputEpoch;
        try
        {
            _popups.BeginOpening(popup, content, owner, epoch,
                () => ReferenceEquals(owner, ViewModel) && generation == owner.MediaGeneration
                    && epoch == _inputEpoch && ReferenceEquals(content, popup.Content) && !IsInputBlocked,
                _ => RefreshInteraction(), OnTransportFlyoutClosed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Island transport popup registration failed: {ex.Message}");
        }

        RefreshInteraction();
    }

    private void OnTransportFlyoutOpened(object sender, object e)
    {
        if (sender is not Flyout popup
            || (!ReferenceEquals(popup, VolumeFlyout) && !ReferenceEquals(popup, SpeedFlyout))) return;
        var claim = _popups.Get(popup);
        // XAML's handler may precede the captured per-opening handler. It never creates a claim.
        if (claim is null) IslandPopupClaims.Hide(popup);
        else if (IsInputBlocked
            || !_popups.IsCurrentFlyout(claim, popup, ViewModel, _inputEpoch, requireOpened: false))
        {
            _popups.RetireFlyout(claim);
        }
        else if (ReferenceEquals(popup, SpeedFlyout))
        {
            SyncQuickSpeedButtons();
        }

        RefreshInteraction();
    }

    private void OnTransportFlyoutClosed(IslandPopupClaims.Claim claim)
    {
        if (ReferenceEquals(_pendingSpeedClaim, claim))
        {
            _pendingSpeedClaim = null;
            _pendingSpeedOwner = null;
            _speedChangeList.Clear();
            _speedChangeTimer.Stop();
        }

        RefreshInteraction();
    }

    private void OnControlGotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = _loaded && IsActive;
        RefreshInteraction();
    }

    private void OnControlLostFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = false;
        RefreshInteraction();
    }

    private void RefreshInteraction()
    {
        var interacting = _loaded && IsActive && (_hasFocus
            || _popups.Count > 0
            || _progressChangeList.Count > 0
            || _speedChangeList.Count > 0);
        if (_isInteracting == interacting)
        {
            return;
        }

        _isInteracting = interacting;
        InteractionStateChanged?.Invoke(this, interacting);
    }
}
