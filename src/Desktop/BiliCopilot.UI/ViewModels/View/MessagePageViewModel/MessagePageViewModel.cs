// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Services.Messages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 消息页面视图模型.
/// </summary>
public sealed partial class MessagePageViewModel : LayoutPageViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePageViewModel"/> class.
    /// </summary>
    public MessagePageViewModel(
        IMessageService service,
        ILogger<MessagePageViewModel> logger,
        AccountViewModel account,
        IMessageNotificationService notifications,
        ILogger<SystemMessageSectionDetailViewModel> systemLogger)
    {
        _service = service;
        _logger = logger;
        _account = account;
        _notifications = notifications;
        _systemLogger = systemLogger;
        _account.AccountChanged += OnAccountChanged;
        _account.PropertyChanged += OnAccountPropertyChanged;
    }

    /// <inheritdoc/>
    protected override string GetPageKey() => nameof(MessagePage);

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_account.AccountId is null) return;
        if (Sections.Count > 0)
        {
            RestoreSelection();
            SectionInitialized?.Invoke(this, EventArgs.Empty);
            return;
        }

        var generation = _pageGeneration;
        IsLoading = true;
        Sections.Add(new NotifyMessageSectionDetailViewModel(NotifyMessageType.Reply, _service));
        Sections.Add(new NotifyMessageSectionDetailViewModel(NotifyMessageType.At, _service));
        Sections.Add(new NotifyMessageSectionDetailViewModel(NotifyMessageType.Like, _service));
        Sections.Add(new SystemMessageSectionDetailViewModel(_notifications, _account, _systemLogger));
        OnPropertyChanged(nameof(SystemSection));
        await LoadChatSessionsAsync();
        if (generation != _pageGeneration) return;
        IsLoading = false;
        RestoreSelection();
        SectionInitialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ResetSections();
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task SelectSectionAsync(IMessageSectionDetailViewModel vm)
    {
        if (vm is null || vm == CurrentSection || !Sections.Contains(vm))
        {
            return;
        }

        var sectionSettingValue = string.Empty;
        if (vm is NotifyMessageSectionDetailViewModel nfVM)
        {
            sectionSettingValue = $"nf_{nfVM.Type}";
        }
        else if (vm is ChatMessageSectionDetailViewModel chatVM)
        {
            sectionSettingValue = $"chat_{chatVM.Data.User.Id}";
        }
        else if (vm is SystemMessageSectionDetailViewModel)
        {
            sectionSettingValue = "system";
        }

        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastSelectedMessageSection, sectionSettingValue);
        (CurrentSection as SystemMessageSectionDetailViewModel)?.SetActive(false);
        CurrentSection = vm;
        (vm as SystemMessageSectionDetailViewModel)?.SetActive(_isActive);
        var generation = _pageGeneration;
        await vm.InitializeCommand.ExecuteAsync(default);
        if (generation == _pageGeneration && ReferenceEquals(CurrentSection, vm)) await UpdateUnreadCountAsync();
    }

    [RelayCommand]
    private async Task UpdateUnreadCountAsync()
    {
        var generation = _pageGeneration;
        var snapshot = await _account.UpdateUnreadSnapshotAsync();
        if (generation == _pageGeneration && snapshot is not null) ApplyUnread(snapshot);
    }

    [RelayCommand]
    private async Task LoadChatSessionsAsync()
    {
        if (IsChatSessionLoading || _preventLoadMore)
        {
            return;
        }

        var generation = _pageGeneration;
        try
        {
            IsChatSessionLoading = true;
            var (sessions, offset, hasMore) = await _service.GetChatSessionsAsync(_chatSessionOffset);
            if (generation != _pageGeneration) return;
            _preventLoadMore = !hasMore;
            _chatSessionOffset = offset;
            if (sessions is not null)
            {
                foreach (var item in sessions)
                {
                    Sections.Add(new ChatMessageSectionDetailViewModel(item));
                }

                ChatSessionsUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            if (generation != _pageGeneration) return;
            _preventLoadMore = true;
            _logger.LogError("加载聊天会话时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToLoadChatSessions), InfoType.Error));
        }
        finally
        {
            if (generation == _pageGeneration) IsChatSessionLoading = false;
        }
    }

    private void RestoreSelection()
    {
        if (Sections.Count == 0) return;
        var lastSelectedSection = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastSelectedMessageSection, string.Empty);
        UpdateUnreadCountCommand.Execute(default);
        if (string.IsNullOrEmpty(lastSelectedSection))
        {
            SelectSectionCommand.Execute(Sections.First());
            return;
        }

        var isSelected = false;
        if (lastSelectedSection == "system" && SystemSection is not null)
        {
            isSelected = true;
            SelectSectionCommand.Execute(SystemSection);
        }
        else if (lastSelectedSection.StartsWith("nf"))
        {
            var isSuccess = Enum.TryParse<NotifyMessageType>(lastSelectedSection.Replace("nf_", string.Empty), out var type);
            if (isSuccess)
            {
                var section = Sections.OfType<NotifyMessageSectionDetailViewModel>().FirstOrDefault(p => p.Type == type);
                if (section is not null)
                {
                    isSelected = true;
                    SelectSectionCommand.Execute(section);
                }
            }
        }
        else if (lastSelectedSection.StartsWith("chat"))
        {
            var id = lastSelectedSection.Replace("chat_", string.Empty);
            var section = Sections.OfType<ChatMessageSectionDetailViewModel>().FirstOrDefault(p => p.Data.User.Id == id);
            if (section is not null)
            {
                isSelected = true;
                SelectSectionCommand.Execute(section);
            }
        }

        if (!isSelected && CurrentSection is null)
        {
            SelectSectionCommand.Execute(Sections.First());
        }
    }

    /// <summary>UI must call on page Loaded/Unloaded (and equivalent navigation visibility).</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        OnPropertyChanged(nameof(IsActive));
        if (CurrentSection is SystemMessageSectionDetailViewModel system)
        {
            system.SetActive(active);
            if (active) system.InitializeCommand.Execute(default);
        }
    }

    private void ResetSections()
    {
        _pageGeneration++;
        foreach (var system in Sections.OfType<SystemMessageSectionDetailViewModel>().ToArray()) system.Dispose();
        CurrentSection = null;
        Sections.Clear();
        _chatSessionOffset = default;
        _preventLoadMore = false;
        IsChatSessionLoading = false;
        IsLoading = false;
        OnPropertyChanged(nameof(SystemSection));
    }

    private void OnAccountChanged(object? sender, EventArgs e) => ResetSections();

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.UnreadSnapshot) && _account.UnreadSnapshot is { } snapshot) ApplyUnread(snapshot);
    }

    private void ApplyUnread(UnreadMessageSnapshot unread)
    {
        foreach (var item in Sections.OfType<NotifyMessageSectionDetailViewModel>())
        {
            item.UnreadCount = item.Type switch
            {
                NotifyMessageType.Like => unread.LikeCount,
                NotifyMessageType.Reply => unread.ReplyCount,
                NotifyMessageType.At => unread.AtCount,
                _ => 0,
            };
        }

        SystemSection?.UpdateUnreadSnapshot();
    }
}
