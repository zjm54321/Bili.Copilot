// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条右侧的画质（清晰度）按钮。由 Store 源 <c>VideoPlayerFormatButton</c> 迁移而来，
/// 数据源映射到当前 <see cref="PlayerViewModel.Sources"/> / <see cref="PlayerViewModel.SelectedSource"/>。
/// </summary>
public sealed partial class IslandFormatButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandFormatButton),
            new PropertyMetadata(false, OnActiveChanged));

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private bool _loaded;
    private bool _synchronizing;
    private long _epoch;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, IsSourceSelectable: true }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandFormatButton"/> class.
    /// </summary>
    public IslandFormatButton()
    {
        InitializeComponent();
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    /// <inheritdoc/>
    private void OnLocalLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Subscribe(ViewModel);
        Synchronize();
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        Subscribe(null);
        Synchronize();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        Subscribe(_loaded ? newValue : null);
        Synchronize();
    }

    public void CancelPendingInput()
    {
        ++_epoch;
        _popups.Cancel();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (IslandFormatButton)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        control.Synchronize();
    }

    private void Subscribe(PlayerViewModel? vm)
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
        _subscribedViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Sources)
            || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (FormatView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            FormatView.ItemsSource = _loaded ? ViewModel?.Sources : null;
            var index = _loaded && ViewModel is { } vm
                ? vm.Sources.ToList().FindIndex(item => ReferenceEquals(item, vm.SelectedSource)) : -1;
            if (index >= 0) FormatView.Select(index);
            else FormatView.DeselectAll();
        }
        finally { _synchronizing = false; }
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        _popups.Open(sender, ViewModel, _epoch);
        Synchronize();
    }

    private async void OnFormatItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        // ItemInvoked is user activation. Select/Deselect/ItemsSource never dispatch commands.
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(_popups.Get(FormatFlyout), ViewModel, _epoch)
            || !ReferenceEquals(sender.ItemsSource, ViewModel.Sources)
            || args.InvokedItem is not SourceItemViewModel { IsEnabled: true } format
            || !ViewModel.Sources.Any(item => ReferenceEquals(item, format))
            || ReferenceEquals(format, ViewModel.SelectedSource)) return;
        try
        {
            await format.ActiveCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island format selection failed: {ex.Message}");
        }
    }
}
