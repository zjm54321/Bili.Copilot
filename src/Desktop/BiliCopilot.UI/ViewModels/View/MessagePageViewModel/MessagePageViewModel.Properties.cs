// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.Services.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 消息页面视图模型.
/// </summary>
public sealed partial class MessagePageViewModel
{
    private readonly IMessageService _service;
    private readonly ILogger<MessagePageViewModel> _logger;
    private readonly AccountViewModel _account;
    private readonly IMessageNotificationService _notifications;
    private readonly ILogger<SystemMessageSectionDetailViewModel> _systemLogger;
    private long _pageGeneration;
    private bool _isActive;
    private long _activationEpoch;
    private bool _initialized;
    private bool _chatInitialized;
    private bool _resetting;
    private MessageRequestOwner? _initializeOwner;
    private MessageRequestOwner? _chatOwner;
    private MessageRequestOwner? _selectionOwner;
    private Task _drain = Task.CompletedTask;

    public AccountViewModel Account => _account;
    public SystemMessageSectionDetailViewModel? SystemSection => Sections.OfType<SystemMessageSectionDetailViewModel>().FirstOrDefault();
    public bool IsActive => _isActive;

    private bool _preventLoadMore;
    private long? _chatSessionOffset;

    [ObservableProperty]
    private IMessageSectionDetailViewModel? _currentSection;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isChatSessionLoading;

    /// <summary>
    /// 分区初始化完成.
    /// </summary>
    public event EventHandler SectionInitialized;

    /// <summary>
    /// 聊天会话列表更新.
    /// </summary>
    public event EventHandler ChatSessionsUpdated;

    /// <summary>
    /// 分区列表.
    /// </summary>
    public ObservableCollection<IMessageSectionDetailViewModel> Sections { get; } = new();
}
