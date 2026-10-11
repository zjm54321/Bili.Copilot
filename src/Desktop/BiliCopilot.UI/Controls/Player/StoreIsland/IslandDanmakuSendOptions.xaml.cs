// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Richasy.BiliKernel.Models;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 弹幕发送选项。由 Store 源 <c>Controls/Danmaku/DanmakuSendOptions</c> 迁移而来，
/// 数据源改为当前独立的 <see cref="DanmakuSendViewModel"/>。
/// </summary>
public sealed partial class IslandDanmakuSendOptions : UserControl
{
    /// <summary>
    /// <see cref="ViewModel"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DanmakuSendViewModel), typeof(IslandDanmakuSendOptions),
            new PropertyMetadata(default, OnViewModelChanged));

    private bool _loaded;
    private bool _synchronizing;
    private Func<bool>? _isInputCurrent;
    private DanmakuSendViewModel? _subscribedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandDanmakuSendOptions"/> class.
    /// </summary>
    public IslandDanmakuSendOptions()
    {
        InitializeComponent();
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
    }

    /// <summary>
    /// 弹幕发送视图模型.
    /// </summary>
    public DanmakuSendViewModel? ViewModel
    {
        get => (DanmakuSendViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    internal void Activate(Func<bool> isInputCurrent)
    {
        _isInputCurrent = isInputCurrent;
        Synchronize();
    }

    internal void CancelPendingInput()
    {
        _isInputCurrent = null;
        IsEnabled = false;
    }

    private bool CanInteract => _loaded && ViewModel is not null
        && ReferenceEquals(ViewModel, _subscribedViewModel) && _isInputCurrent?.Invoke() == true;

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Subscribe(ViewModel);
        Synchronize();
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        Subscribe(null);
        Synchronize();
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (IslandDanmakuSendOptions)sender;
        control.CancelPendingInput();
        control.Subscribe(control._loaded ? (DanmakuSendViewModel?)args.NewValue : null);
        control.Synchronize();
    }

    private void Subscribe(DanmakuSendViewModel? vm)
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
        _subscribedViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args) => Synchronize();

    private void Synchronize()
    {
        if (ColorRepeater is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            ColorRepeater.ItemsSource = _loaded ? ViewModel?.Colors : null;
            DanmakuLocationComboBox.ItemsSource = _loaded ? ViewModel?.Locations : null;
            DanmakuLocationComboBox.SelectedItem = _loaded ? ViewModel?.Location : null;
        }
        finally { _synchronizing = false; }
    }

    private void OnSizeItemClick(object sender, RoutedEventArgs e)
    {
        if (CanInteract) ViewModel!.IsStandardSize = (sender as FrameworkElement)?.Tag?.ToString() == "Standard";
    }

    private void OnColorItemTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (CanInteract && sender is FrameworkElement { DataContext: Windows.UI.Color color }
            && ViewModel!.Colors.Contains(color)) ViewModel.Color = color;
    }

    private void OnLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && CanInteract && DanmakuLocationComboBox.SelectedItem is DanmakuLocation location
            && ViewModel!.Locations.Contains(location)) ViewModel.Location = location;
    }
}
