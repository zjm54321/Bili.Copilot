// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Services.Messages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 账户视图模型.
/// </summary>
public sealed partial class AccountViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AccountViewModel"/> class.
    /// </summary>
    public AccountViewModel(
        IMyProfileService myProfileService,
        IMessageNotificationService messageService,
        NavigationViewModel navService,
        ILogger<AccountViewModel> logger)
    {
        _navService = navService;
        _messageService = messageService;
        _myProfileService = myProfileService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (MyProfile is not null || IsInitializing || _isSigningOut)
        {
            return;
        }

        var generation = AccountGeneration;
        var cancellationToken = AccountCancellationToken;
        IsInitializing = true;
        try
        {
            var profile = await _myProfileService.GetMyProfileAsync(cancellationToken);
            if (generation != AccountGeneration || _isSigningOut) return;
            MyProfile = profile;
            if (!ReferenceEquals(MyProfile, profile) || _isSigningOut) return;
            generation = AccountGeneration;
            Introduce = string.IsNullOrEmpty(MyProfile.Introduce)
                ? ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.NoSelfIntroduce)
                : MyProfile.Introduce;
            this.Get<AppViewModel>().IsInitialLoading = false;
            this.Get<AppViewModel>().CheckUpateCommand.Execute(default);
            UpdateUnreadCommand.Execute(default);
        }
        catch (Exception ex)
        {
            if (generation != AccountGeneration || _isSigningOut || cancellationToken.IsCancellationRequested) return;
            if (_logger.IsEnabled(LogLevel.Critical))
            {
                _logger.LogCritical("无法获取登录用户的资料 ({ErrorType})", ex.GetType().Name);
            }

            // 用户可能更改了密码，提醒用户是否需要重新登录.
            var dialog = new ContentDialog()
            {
                Title = ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.LoginExpiredTitle),
                Content = ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.LoginExpiredDescription),
                PrimaryButtonText = ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.ReSignIn),
                CloseButtonText = ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Exit),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Get<AppViewModel>().ActivatedWindow.Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (generation != AccountGeneration || _isSigningOut) return;
            if (result == ContentDialogResult.Primary)
            {
                await SignOutAsync();
            }
            else
            {
                App.Current.Exit();
            }
        }
        finally
        {
            if (generation == AccountGeneration) IsInitializing = false;
        }
    }

    [RelayCommand]
    private async Task UpdateCommunityInformationAsync()
    {
        if (MyProfile is null)
        {
            return;
        }

        var generation = AccountGeneration;
        try
        {
            var communityInformation = await _myProfileService.GetMyCommunityInformationAsync();
            if (generation != AccountGeneration || MyProfile is null) return;
            MomentCount = communityInformation.MomentCount ?? 0;
            FollowCount = communityInformation.FollowCount ?? 0;
            FansCount = communityInformation.FansCount ?? 0;
            UpdateUnreadCommand.Execute(default);
        }
        catch (Exception ex)
        {
            if (generation != AccountGeneration) return;
            _logger.LogError("已登录用户的社区信息获取失败 ({ErrorType})", ex.GetType().Name);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        // Invalidate before the first await, including a pending profile initialization.
        _isSigningOut = true;
        if (MyProfile is null) InvalidateAccount();
        else MyProfile = null;
        var generation = AccountGeneration;
        await this.Get<IAuthenticationService>().SignOutAsync();
        if (generation == AccountGeneration) this.Get<AppViewModel>().RestartCommand.Execute(default);
    }

    [RelayCommand]
    private async Task OpenPersonalWebsiteAsync()
    {
        if (MyProfile is null)
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri($"https://space.bilibili.com/{MyProfile.User.Id}"));
    }

    [RelayCommand]
    private void ShowFans()
        => _navService.NavigateToOver(typeof(FansPage), MyProfile.User.Id);

    [RelayCommand]
    private void ShowFollows()
        => _navService.NavigateToOver(typeof(FollowsPage), MyProfile.User.Id);

    [RelayCommand]
    private void ShowMoments()
        => _navService.NavigateToOver(typeof(UserSpacePage), MyProfile.User);

    [RelayCommand]
    private void ShowViewLater()
        => _navService.NavigateToOver(typeof(ViewLaterPage));

    [RelayCommand]
    private void ShowHistory()
        => _navService.NavigateToOver(typeof(HistoryPage));

    [RelayCommand]
    private void ShowFavorites()
        => _navService.NavigateToOver(typeof(FavoritesPage));

    [RelayCommand]
    private async Task UpdateUnreadAsync() => await UpdateUnreadSnapshotAsync();

    /// <summary>UI-thread single-flight shared by the account badge and message page.</summary>
    public Task<UnreadMessageSnapshot?> UpdateUnreadSnapshotAsync()
    {
        if (AccountId is null || _isSigningOut) return Task.FromResult<UnreadMessageSnapshot?>(null);
        if (_unreadTask is { IsCompleted: false }) return _unreadTask;
        var completion = new TaskCompletionSource<UnreadMessageSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _unreadTask = completion.Task;
        _ = CompleteUnreadAsync(AccountGeneration, AccountId, AccountCancellationToken, completion);
        return completion.Task;
    }

    /// <summary>After an acknowledged mutation, never reuse a request sent before it.</summary>
    public async Task<UnreadMessageSnapshot?> RefreshUnreadAfterMarkReadAsync()
    {
        var generation = AccountGeneration;
        if (_unreadTask is { IsCompleted: false } pending) await pending;
        return generation == AccountGeneration ? await UpdateUnreadSnapshotAsync() : null;
    }

    private async Task CompleteUnreadAsync(long generation, string accountId, System.Threading.CancellationToken cancellationToken,
        TaskCompletionSource<UnreadMessageSnapshot?> completion)
    {
        UnreadMessageSnapshot? applied = null;
        try
        {
            var snapshot = await _messageService.GetUnreadAsync(cancellationToken);
            if (generation == AccountGeneration && accountId == AccountId && !cancellationToken.IsCancellationRequested)
            {
                UnreadSnapshot = snapshot;
                UnreadCount = snapshot.Total;
                UnreadError = null;
                applied = snapshot;
            }
        }
        catch (Exception ex)
        {
            if (generation == AccountGeneration && !cancellationToken.IsCancellationRequested)
            {
                UnreadError = ex is MessageNotificationException ? ex.Message : "未读消息获取失败，请重试。";
                // Exception messages can contain authenticated URLs or response bodies.
                _logger.LogWarning("未读消息获取失败 ({ErrorType})", ex.GetType().Name);
            }
        }
        finally
        {
            completion.TrySetResult(applied);
        }
    }

    partial void OnMyProfileChanged(UserDetailProfile? value)
    {
        if (value is not null) _isSigningOut = false;
        InvalidateAccount();
    }

    partial void OnUnreadCountChanged(int value)
    {
        var item = _navService.FooterItems.FirstOrDefault(p => p.PageKey == typeof(MessagePage).FullName);
        if (item is not null) item.ShowUnread = value > 0;
    }

    private void InvalidateAccount()
    {
        _accountGeneration++;
        _accountCancellation.Cancel();
        _accountCancellation.Dispose();
        _accountCancellation = new();
        _unreadTask = null;
        UnreadSnapshot = null;
        UnreadCount = 0;
        OnUnreadCountChanged(0);
        UnreadError = null;
        IsInitializing = false;
        MomentCount = FollowCount = FansCount = 0;
        Introduce = string.Empty;
        OnPropertyChanged(nameof(AccountGeneration));
        OnPropertyChanged(nameof(AccountId));
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }
}
