// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.UserSpace;
using BiliCopilot.UI.ViewModels.View;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 用户空间页面头部.
/// </summary>
public sealed partial class UserSpacePageHeader : UserSpacePageControlBase
{
    private UserSpacePageViewModel? _subscribedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSpacePageHeader"/> class.
    /// </summary>
    public UserSpacePageHeader()
    {
        InitializeComponent();
        Loaded += OnHeaderLoaded;
        Unloaded += OnHeaderUnloaded;
    }

    /// <summary>
    /// 页面传入的基础资料（昵称/头像/ID），在用户卡片返回前即可呈现.
    /// </summary>
    public static readonly DependencyProperty ProfileProperty =
        DependencyProperty.Register(nameof(Profile), typeof(UserProfile), typeof(UserSpacePageHeader), new PropertyMetadata(default(UserProfile)));

    /// <summary>
    /// 头部独立拉取的用户卡片（签名/等级/统计）.
    /// </summary>
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register(nameof(Card), typeof(UserCard), typeof(UserSpacePageHeader), new PropertyMetadata(default(UserCard)));

    /// <summary>
    /// 用户卡片是否加载失败（失败时仅隐藏骨架，不影响页面主体状态）.
    /// </summary>
    public static readonly DependencyProperty IsCardFailedProperty =
        DependencyProperty.Register(nameof(IsCardFailed), typeof(bool), typeof(UserSpacePageHeader), new PropertyMetadata(false));

    private CancellationTokenSource? _cardCancellation;

    public UserProfile? Profile
    {
        get => (UserProfile?)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public UserCard? Card
    {
        get => (UserCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public bool IsCardFailed
    {
        get => (bool)GetValue(IsCardFailedProperty);
        set => SetValue(IsCardFailedProperty, value);
    }

    /// <summary>
    /// 设置当前空间的用户并独立加载用户卡片.
    /// 不调用 ViewModel.InitializeUserInformationCommand：该命令走页面共享的 RunAsync，会改写 State/IsLoading，造成空态/错误态被覆盖.
    /// </summary>
    public void SetProfile(UserProfile profile)
    {
        _cardCancellation?.Cancel();
        _cardCancellation?.Dispose();
        _cardCancellation = new CancellationTokenSource();
        Profile = profile;
        Card = null;
        IsCardFailed = false;
        _ = LoadCardAsync(profile, _cardCancellation.Token);
    }

    private async Task LoadCardAsync(UserProfile profile, CancellationToken token)
    {
        try
        {
            var card = await this.Get<IUserService>().GetUserInformationAsync(profile.Id, token);
            if (token.IsCancellationRequested || !ReferenceEquals(Profile, profile))
            {
                return;
            }

            Card = card;
            IsCardFailed = card is null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(Profile, profile))
            {
                IsCardFailed = true;
            }
        }
    }

    public static Uri? GetAvatar(UserProfile? profile, UserCard? card)
        => card?.Profile?.User?.Avatar?.Uri ?? profile?.Avatar?.Uri;

    public static string GetDisplayName(UserProfile? profile, UserCard? card, string? userName)
        => card?.Profile?.User?.Name ?? userName ?? profile?.Name ?? string.Empty;

    public static string FormatSign(UserCard? card)
        => string.IsNullOrWhiteSpace(card?.Profile?.Introduce) ? "这个人很神秘，什么都没有写" : card!.Profile!.Introduce!.Trim();

    public static bool IsCardLoading(UserCard? card, bool isFailed)
        => card is null && !isFailed;

    public static Visibility IsCardLoadingVisible(UserCard? card, bool isFailed)
        => IsCardLoading(card, isFailed) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsUnfollowedVisible(bool? isFollowed)
        => isFollowed == false ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFollowedVisible(bool? isFollowed)
        => isFollowed == true ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFollowUnknownVisible(bool? isFollowed)
        => isFollowed.HasValue ? Visibility.Collapsed : Visibility.Visible;

    private void OnHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_subscribedViewModel, ViewModel))
        {
            DetachViewModel();
            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        SyncSelectedTab();
    }

    private void OnHeaderUnloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void DetachViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSpacePageViewModel.SelectedTab))
        {
            SyncSelectedTab();
        }
    }

    private void SyncSelectedTab()
    {
        if (ViewModel is null) return;
        var index = (int)ViewModel.SelectedTab;
        if (index >= 0 && index < Selector.Items.Count && Selector.SelectedItem != Selector.Items[index])
        {
            Selector.SelectedItem = Selector.Items[index];
        }
    }

    private void OnSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null || ViewModel is null) return;
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0 && index <= 3)
        {
            var tab = (UserSpaceTab)index;
            if (tab != ViewModel.SelectedTab)
            {
                ViewModel.SelectTabCommand.Execute(tab);
            }
        }
    }
}
