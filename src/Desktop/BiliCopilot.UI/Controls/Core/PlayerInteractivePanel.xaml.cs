// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Core;

public sealed partial class PlayerInteractivePanel : PlayerControlBase
{
    private readonly DispatcherQueueTimer _tapTimer;
    private readonly DispatcherQueueTimer _holdTimer;
    private readonly DispatcherQueueTimer _tempControlTimer;
    private readonly PlayerSurfaceGestureState _gesture = new();
    private PlayerViewModel? _subscribedViewModel;
    private uint? _pointerId;
    private double _startPosition;
    private double _startVolume;
    private bool _pendingTouchTap;
    private bool _isSecondTouchTap;
    private bool _isTouch;
    private bool _isRightButton;
    private bool _isHolding;

    public PlayerInteractivePanel()
    {
        InitializeComponent();
        _tapTimer = DispatcherQueue.CreateTimer();
        _tapTimer.Interval = TimeSpan.FromMilliseconds(200);
        _tapTimer.Tick += OnTapTimerTick;
        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(500);
        _holdTimer.Tick += OnHoldTimerTick;
        _tempControlTimer = DispatcherQueue.CreateTimer();
        _tempControlTimer.Interval = TimeSpan.FromMilliseconds(500);
        _tempControlTimer.Tick += OnTempControlTimerTick;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal static string GetPanscanText(bool usingPanscan)
        => usingPanscan ? ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.CancelCutBlackArea) : ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.CutBlackArea);

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // Windows reports precision-touchpad scroll through the same wheel axes
        // as mouse wheels. Do not infer hardware from the delta or its magnitude.
        if (!CanControlPlayback() || !IsSurfaceSource(e.OriginalSource)
            || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || e.IsGenerated
            || !AppToolkit.NotModifierKeyPressed())
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        var distance = properties.MouseWheelDelta;
        if (distance == 0)
        {
            return;
        }

        if (properties.IsHorizontalMouseWheel)
        {
            if (!CanSeek())
            {
                return;
            }

            if (distance > 0)
            {
                ViewModel.SkipForwardCommand.Execute(default);
            }
            else
            {
                ViewModel.SkipBackwardCommand.Execute(default);
            }
        }
        else if (distance > 0)
        {
            ViewModel.IncreaseVolumeCommand.Execute(default);
        }
        else
        {
            ViewModel.DecreaseVolumeCommand.Execute(default);
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!CanControlPlayback() || !IsSurfaceSource(e.OriginalSource) || e.IsGenerated)
        {
            return;
        }

        var device = e.Pointer.PointerDeviceType;
        if (device is not (PointerDeviceType.Touch or PointerDeviceType.Mouse))
        {
            return;
        }

        if (device == PointerDeviceType.Mouse && !AppToolkit.NotModifierKeyPressed())
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (_pointerId.HasValue || (device == PointerDeviceType.Touch && !point.Properties.IsPrimary))
        {
            // Multi-contact touchscreen input is not a touchpad wheel gesture.
            CleanupInteractionState();
            e.Handled = true;
            return;
        }

        if (device == PointerDeviceType.Mouse
            && !point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        // Only an accepted surface press moves focus; descendants retain their keys.
        ViewModel.FocusPlayerInput();
        _isTouch = device == PointerDeviceType.Touch;
        _isRightButton = !_isTouch && point.Properties.IsRightButtonPressed;
        _isSecondTouchTap = _isTouch && _pendingTouchTap;
        _pendingTouchTap = false;
        _tapTimer.Stop();
        _gesture.Begin(point.Position.X, point.Position.Y, _isTouch);
        _pointerId = e.Pointer.PointerId;
        _startPosition = ViewModel.Player.Position;
        _startVolume = ViewModel.Player.Volume;
        if (!CapturePointer(e.Pointer))
        {
            CleanupInteractionState();
            return;
        }

        if (_isTouch)
        {
            _holdTimer.Start();
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        if (!CanControlPlayback())
        {
            CleanupInteractionState();
            return;
        }

        UpdateGesture(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        if (!CanControlPlayback())
        {
            CleanupInteractionState();
            return;
        }

        UpdateGesture(e.GetCurrentPoint(this).Position);
        _holdTimer.Stop();
        if (_isHolding)
        {
            EndTouchHold(ViewModel);
        }
        else if (_gesture.Phase == PlayerSurfaceGesture.Horizontal)
        {
            var position = GetNewPosition();
            if (position.HasValue)
            {
                // Seeking does not change the previous playing/paused state.
                CommitPosition(position.Value);
            }
        }
        else if (_gesture.Phase == PlayerSurfaceGesture.Pressed)
        {
            if (_isRightButton)
            {
                FlyoutBase.GetAttachedFlyout(this).ShowAt(this, new FlyoutShowOptions
                {
                    Position = e.GetCurrentPoint(this).Position,
                });
            }
            else if (!_isTouch || _isSecondTouchTap)
            {
                ViewModel.PlayPauseCommand.Execute(default);
            }
            else
            {
                _pendingTouchTap = true;
                _tapTimer.Start();
            }
        }

        ViewModel.IsPreviewProgressChanging = false;
        // Clear ownership before ReleasePointerCapture raises CaptureLost.
        _pointerId = null;
        _isSecondTouchTap = false;
        _isRightButton = false;
        _gesture.Reset();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            CleanupInteractionState();
        }
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            CleanupInteractionState();
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
        => CleanupInteractionState();

    protected override void OnControlLoaded()
    {
        SubscribeViewModel(ViewModel);
        _tempControlTimer.Start();
    }

    protected override void OnControlUnloaded()
    {
        CleanupInteractionState();
        _tempControlTimer.Stop();
        SubscribeViewModel(null);
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CleanupInteractionState(oldValue);
        SubscribeViewModel(IsLoaded ? newValue : null);
    }

    internal void CancelPendingInput()
        => CleanupInteractionState();

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(ViewModel.IsWindowDeactivated) && ViewModel.IsWindowDeactivated)
            || (e.PropertyName == nameof(ViewModel.IsConnecting) && ViewModel.IsConnecting)
            || e.PropertyName is nameof(ViewModel.Player) or nameof(ViewModel.Connector) or nameof(ViewModel.SelectedSource))
        {
            CleanupInteractionState();
        }
    }

    private void OnTempControlTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (ViewModel == null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (ViewModel.IsVolumeChanging && now - ViewModel.LastVolumeChangingTime > TimeSpan.FromSeconds(2))
        {
            ViewModel.IsVolumeChanging = false;
        }

        if (ViewModel.IsProgressChanging && now - ViewModel.LastProgressChangingTime > TimeSpan.FromSeconds(2))
        {
            ViewModel.IsProgressChanging = false;
        }

        if (ViewModel.IsSpeedChanging && now - ViewModel.LastSpeedChangingTime > TimeSpan.FromSeconds(2))
        {
            ViewModel.IsSpeedChanging = false;
        }
    }

    private void OnHoldTimerTick(object sender, object e)
    {
        _holdTimer.Stop();
        if (!_pointerId.HasValue || !CanControlPlayback() || !_gesture.TryHold())
        {
            return;
        }

        _isSecondTouchTap = false;
        if (!ViewModel.Player.IsPlaying)
        {
            return;
        }

        _isHolding = true;
        ViewModel.IsTouching = true;
        ViewModel.TripleSpeedCommand.Execute(default);
    }

    private void OnTapTimerTick(object? sender, object? e)
    {
        _tapTimer.Stop();
        var toggleControls = _pendingTouchTap && !_pointerId.HasValue && CanControlPlayback();
        _pendingTouchTap = false;
        if (toggleControls)
        {
            ViewModel.IsTouchControlsVisible = !ViewModel.IsControlsVisible;
            ViewModel.IsControlsVisible = !ViewModel.IsControlsVisible;
        }
    }

    private void UpdateGesture(Point position)
    {
        _gesture.Move(position.X, position.Y);
        if (_gesture.Phase != PlayerSurfaceGesture.Pressed)
        {
            _holdTimer.Stop();
            _isSecondTouchTap = false;
        }

        if (_gesture.Phase == PlayerSurfaceGesture.Horizontal)
        {
            var newPosition = GetNewPosition();
            if (newPosition.HasValue)
            {
                ViewModel.IsPreviewProgressChanging = true;
                ViewModel.IsTouchControlsVisible = true;
                ViewModel.IsControlsVisible = true;
                ViewModel.PreviewPosition = newPosition.Value;
            }
        }
        else if (_gesture.Phase == PlayerSurfaceGesture.Vertical)
        {
            // Retain the existing touch volume scale, now over the whole surface.
            var volume = Math.Clamp(_startVolume - (_gesture.DeltaY / 10), 0, ViewModel.MaxVolume);
            ApplyVolume(volume);
        }
    }

    private async void CommitPosition(double position)
    {
        try
        {
            await ViewModel.ChangePositionAsync(position);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Player input seek failed: {ex.Message}");
        }
    }

    private async void ApplyVolume(double volume)
    {
        try
        {
            await ViewModel.ChangeVolumeAsync(volume);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Player input volume failed: {ex.Message}");
        }
    }

    private double? GetNewPosition()
    {
        if (!CanSeek() || ActualWidth <= 0)
        {
            return null;
        }

        var duration = ViewModel.Player.Duration;
        return Math.Clamp(_startPosition + (_gesture.DeltaX / ActualWidth / 2 * duration), 0, Math.Max(0, duration - 0.1));
    }

    private bool CanControlPlayback()
        => ViewModel?.Client?.IsDisposed == false && ViewModel.Player?.IsPlaybackInitialized == true
            && !ViewModel.IsPlaybackClosing && !ViewModel.IsConnecting && !ViewModel.IsWindowDeactivated
            && !ViewModel.IsPopupVisible && !ViewModel.IsExtraPanelVisible && !ViewModel.IsSubtitleSettingsVisible;

    private bool CanSeek()
        => CanControlPlayback() && double.IsFinite(ViewModel.Player.Duration) && ViewModel.Player.Duration > 0;

    private bool IsSurfaceSource(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }

            if (current is Control or Microsoft.UI.Xaml.Documents.Hyperlink)
            {
                return false;
            }
        }

        return false;
    }

    private void EndTouchHold(PlayerViewModel? owner)
    {
        if (_isHolding)
        {
            _isHolding = false;
            if (owner is not null)
            {
                owner.IsTouching = false;
                owner.RestoreSpeedCommand.Execute(default);
            }
        }
    }

    private void CleanupInteractionState(PlayerViewModel? owner = null)
    {
        _holdTimer.Stop();
        _tapTimer.Stop();
        owner ??= ViewModel;
        EndTouchHold(owner);
        if (owner is not null)
        {
            owner.IsPreviewProgressChanging = false;
        }

        _pendingTouchTap = false;
        _isSecondTouchTap = false;
        _pointerId = null;
        _isRightButton = false;
        _isTouch = false;
        _gesture.Reset();
        ReleasePointerCaptures();
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        ViewModel.IsPopupVisible = true;
        foreach (var item in Anime4KItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag.ToString();
            if (Enum.TryParse<Anime4KMode>(tag, out var mode))
            {
                item.IsChecked = ViewModel.Anime4KMode == mode;
            }
        }

        foreach (var item in ArtCNNItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag.ToString();
            if (Enum.TryParse<ArtCNNMode>(tag, out var mode))
            {
                item.IsChecked = ViewModel.ArtCNNMode == mode;
            }
        }

        foreach (var item in NNEDI3Item.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag.ToString();
            if (Enum.TryParse<Nnedi3Mode>(tag, out var mode))
            {
                item.IsChecked = ViewModel.Nnedi3Mode == mode;
            }
        }

        foreach (var item in RavuItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag.ToString();
            if (Enum.TryParse<RavuMode>(tag, out var mode))
            {
                item.IsChecked = ViewModel.RavuMode == mode;
            }
        }

        foreach (var item in NvidiaVsrItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag.ToString();
            if (Enum.TryParse<VsrScale>(tag, out var mode))
            {
                item.IsChecked = ViewModel.VsrScale == mode;
            }
        }
    }

    private void OnRowBarButtonClick(object sender, EventArgs e)
    {
        ContextMenu.Hide();
    }

    private void OnFlyoutClosed(object sender, object e)
    {
        ViewModel.IsPopupVisible = false;
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        var btn = sender as FrameworkElement;
        var point = btn!.TransformToVisual(this).TransformPoint(new Point(0, 0));
        FlyoutBase.GetAttachedFlyout(this).ShowAt(this, new FlyoutShowOptions
        {
            Position = point,
            Placement = FlyoutPlacementMode.Right,
        });
    }

    private void OnAnime4KItemClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)!.Tag.ToString();
        if (Enum.TryParse<Anime4KMode>(tag, out var mode))
        {
            ViewModel.ChangeAnime4KModeCommand.Execute(mode);
        }
    }

    private void OnArtCNNItemClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)!.Tag.ToString();
        if (Enum.TryParse<ArtCNNMode>(tag, out var mode))
        {
            ViewModel.ChangeArtCNNModeCommand.Execute(mode);
        }
    }

    private void OnNvidiaVsrItemClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)!.Tag.ToString();
        if (Enum.TryParse<VsrScale>(tag, out var mode))
        {
            ViewModel.ChangeNvidiaVsrModeCommand.Execute(mode);
        }
    }

    private void OnNNEDI3ItemClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)!.Tag.ToString();
        if (Enum.TryParse<Nnedi3Mode>(tag, out var mode))
        {
            ViewModel.ChangeNnedi3ModeCommand.Execute(mode);
        }
    }

    private void OnRavuItemClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)!.Tag.ToString();
        if (Enum.TryParse<RavuMode>(tag, out var mode))
        {
            ViewModel.ChangeRavuModeCommand.Execute(mode);
        }
    }
}
