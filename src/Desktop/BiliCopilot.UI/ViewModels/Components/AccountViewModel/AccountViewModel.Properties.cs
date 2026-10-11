// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.Services.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 账户视图模型.
/// </summary>
public sealed partial class AccountViewModel
{
    private readonly IMyProfileService _myProfileService;
    private readonly IMessageNotificationService _messageService;
    private readonly NavigationViewModel _navService;
    private readonly ILogger<AccountViewModel> _logger;

    private CancellationTokenSource _accountCancellation = new();
    private long _accountGeneration;
    private bool _isSigningOut;
    private Task<UnreadMessageSnapshot?>? _unreadTask;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private UnreadMessageSnapshot? _unreadSnapshot;

    [ObservableProperty]
    private string? _unreadError;

    public long AccountGeneration => _accountGeneration;
    public string? AccountId => MyProfile?.User.Id;
    public CancellationToken AccountCancellationToken => _accountCancellation.Token;
    public event EventHandler? AccountChanged;

    [ObservableProperty]
    private UserDetailProfile? _myProfile;

    [ObservableProperty]
    private bool _isInitializing;

    [ObservableProperty]
    private int _momentCount;

    [ObservableProperty]
    private int _followCount;

    [ObservableProperty]
    private int _fansCount;

    [ObservableProperty]
    private string _introduce;

    [ObservableProperty]
    public partial bool IsFlyoutOpened { get; set; }
}
