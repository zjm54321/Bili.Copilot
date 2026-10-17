// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.WinUIKernel.Share.Base;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 收藏按钮.
/// </summary>
public sealed partial class FavoriteButton : LayoutUserControlBase
{
    /// <summary>
    /// <see cref="IsFavorited"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsFavoritedProperty =
        DependencyProperty.Register(nameof(IsFavorited), typeof(bool), typeof(FavoriteButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="Tip"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TipProperty =
        DependencyProperty.Register(nameof(FavoriteButton), typeof(string), typeof(FavoriteButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="Command"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(FavoriteButton), new PropertyMetadata(default, OnTipCommandChanged));

    /// <summary>
    /// <see cref="InitializeCommand"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty InitializeCommandProperty =
        DependencyProperty.Register(nameof(FavoriteButton), typeof(ICommand), typeof(FavoriteButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="ItemsSource"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(FavoriteButton), new PropertyMetadata(default));

    private readonly PlayerActionTip _favoriteTip;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteButton"/> class.
    /// </summary>
    public FavoriteButton()
    {
        InitializeComponent();
        _favoriteTip = new PlayerActionTip(this, FavoriteTip, () => new ICommand?[] { Command });
    }

    /// <summary>
    /// 是否已收藏.
    /// </summary>
    public bool IsFavorited
    {
        get => (bool)GetValue(IsFavoritedProperty);
        set => SetValue(IsFavoritedProperty, value);
    }

    /// <summary>
    /// 提示文本.
    /// </summary>
    public string Tip
    {
        get => (string)GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    /// <summary>
    /// 命令.
    /// </summary>
    public ICommand Command
    {
        get => (ICommand)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// 初始化命令.
    /// </summary>
    public ICommand InitializeCommand
    {
        get => (ICommand)GetValue(InitializeCommandProperty);
        set => SetValue(InitializeCommandProperty, value);
    }

    /// <summary>
    /// 收藏夹数据源.
    /// </summary>
    public object ItemsSource
    {
        get => (object)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private void OnFavoriteButtonClick(object sender, RoutedEventArgs e)
    {
        if (FavoriteTip.IsOpen) return;
        InitializeCommand?.Execute(default);
        _favoriteTip.TryOpen();
    }

    private void OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsFavorited)
        {
            Btn.IsChecked = false;
        }
    }

    private void OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (IsFavorited)
        {
            Btn.IsChecked = true;
        }
    }

    // A rebound connector replaces the command: the tip opened for the old one closes instead of running the new one.
    private static void OnTipCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FavoriteButton)d)._favoriteTip?.Close();

    private void OnConfirmButtonClick(TeachingTip sender, object args)
        => _favoriteTip.ExecuteAndClose(0, null);
}
