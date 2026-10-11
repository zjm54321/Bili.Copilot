// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条右侧的字幕按钮。由 Store 源 <c>SubtitleButton</c> 迁移而来，
/// 数据源映射到当前 <see cref="PlayerViewModel.Subtitles"/> 与 <see cref="PlayerViewModel.IsSubtitleEnabled"/>。
/// </summary>
public sealed partial class IslandSubtitleButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandSubtitleButton),
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
        IsPlaybackClosing: false, IsConnecting: false, IsSubtitleEmpty: false }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandSubtitleButton"/> class.
    /// </summary>
    public IslandSubtitleButton()
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
        var control = (IslandSubtitleButton)sender;
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
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Subtitles)
            || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (SubtitleView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            SubtitleView.ItemsSource = _loaded ? ViewModel?.Subtitles : null;
            var index = _loaded && ViewModel is { } vm ? vm.Subtitles.ToList().FindIndex(item => item.IsSelected) : -1;
            if (index >= 0) SubtitleView.Select(index);
            else SubtitleView.DeselectAll();
        }
        finally { _synchronizing = false; }
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        _popups.Open(sender, ViewModel, _epoch);
        Synchronize();
    }

    private async void OnSubtitleItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        var claim = _popups.Get(SubtitleFlyout);
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(claim, ViewModel, _epoch)
            || !ReferenceEquals(sender.ItemsSource, ViewModel.Subtitles)
            || args.InvokedItem is not SubtitleItemViewModel meta
            || !ViewModel.Subtitles.Any(item => ReferenceEquals(item, meta))) return;
        try
        {
            // Only explicit activation toggles an already selected track off.
            await meta.ActiveCommand.ExecuteAsync(null);
            if (CanInteract && _popups.IsCurrent(claim, ViewModel, _epoch)) Synchronize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island subtitle selection failed: {ex.Message}");
        }
    }
}
