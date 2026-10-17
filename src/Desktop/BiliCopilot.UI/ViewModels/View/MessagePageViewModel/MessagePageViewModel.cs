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
    private Task InitializeAsync() => StartInitialize(refresh: false);

    private Task StartInitialize(bool refresh)
    {
        // Join work published by this UI owner, not a foreign prerequisite.
#pragma warning disable VSTHRD003
        if (_initializeOwner is { Task.IsCompleted: false } pending && IsCurrent(pending, _initializeOwner)
            && (!refresh || pending.Refresh)) return pending.Task;
#pragma warning restore VSTHRD003
        if ((!refresh && _initialized) || _account.AccountId is null) return Task.CompletedTask;
        var previous = _drain;
        if (refresh)
        {
            previous = Task.WhenAll(_drain, _initializeOwner?.Task ?? Task.CompletedTask,
                _chatOwner?.Task ?? Task.CompletedTask, _selectionOwner?.Task ?? Task.CompletedTask);
            _pageGeneration++;
            _initialized = _chatInitialized = false;
            var drains = new List<Task> { previous };
            foreach (var notify in Sections.OfType<NotifyMessageSectionDetailViewModel>()) drains.Add(notify.InvalidateData());
            foreach (var chat in Sections.OfType<ChatMessageSectionDetailViewModel>()) drains.Add(chat.InvalidateData());
            previous = Task.WhenAll(drains);
        }
        var owner = new MessageRequestOwner(_pageGeneration, _account.AccountGeneration, _account.AccountId, previous, refresh);
        _initializeOwner = owner; // Before IsLoading or collection notifications can reenter.
        var refreshedSystem = refresh ? CurrentSection as SystemMessageSectionDetailViewModel : null;
        // Let the existing system owner invalidate display/ack immediately, not after session I/O.
        var systemRefresh = refreshedSystem?.RefreshCommand.ExecuteAsync(default);
        _ = CompleteInitializeAsync(owner, refreshedSystem, systemRefresh);
        return owner.Task;
    }

    private async Task CompleteInitializeAsync(MessageRequestOwner owner,
        SystemMessageSectionDetailViewModel? refreshedSystem = null, Task? systemRefresh = null)
    {
        var success = false;
        try
        {
            // This slot owns only its captured predecessors, never a replacement that joins it.
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _initializeOwner)) return;
            IsLoading = true;
            if (!IsCurrent(owner, _initializeOwner)) return;
            foreach (var type in new[] { NotifyMessageType.Reply, NotifyMessageType.At, NotifyMessageType.Like })
            {
                if (!IsCurrent(owner, _initializeOwner)) return;
                if (!Sections.OfType<NotifyMessageSectionDetailViewModel>().Any(section => section.Type == type))
                    Sections.Add(new NotifyMessageSectionDetailViewModel(type, _service, _account));
            }
            if (!IsCurrent(owner, _initializeOwner)) return;
            if (SystemSection is null)
            {
                Sections.Add(new SystemMessageSectionDetailViewModel(_notifications, _account, _systemLogger));
                if (!IsCurrent(owner, _initializeOwner)) return;
                OnPropertyChanged(nameof(SystemSection));
            }

            if (!IsCurrent(owner, _initializeOwner)) return;
            if (!_chatInitialized) await StartChatSessions(owner.Refresh);
            if (!IsCurrent(owner, _initializeOwner) || !_chatInitialized) return;
            if (owner.Refresh)
            {
                var selected = GetSavedSection();
                var sectionRefresh = ReferenceEquals(selected, refreshedSystem) && systemRefresh is not null
                    ? systemRefresh : selected.RefreshCommand.ExecuteAsync(default);
#pragma warning disable VSTHRD003
                await sectionRefresh;
#pragma warning restore VSTHRD003
                if (!IsCurrent(owner, _initializeOwner)) return;
                if (sectionRefresh is Task<bool> result)
                {
#pragma warning disable VSTHRD003
                    var refreshed = await result;
#pragma warning restore VSTHRD003
                    if (!IsCurrent(owner, _initializeOwner) || !refreshed) return;
                }
            }
            await SelectSectionAsync(GetSavedSection());
            if (!IsCurrent(owner, _initializeOwner)) return;
            while (_selectionOwner is { Task.IsCompleted: false } selectedOwner && IsCurrent(selectedOwner, _selectionOwner))
            {
#pragma warning disable VSTHRD003
                await selectedOwner.Task;
#pragma warning restore VSTHRD003
                if (!IsCurrent(owner, _initializeOwner)) return;
            }
            // A failed section request remains retryable on a later visible visit.
            success = CurrentSection switch
            {
                NotifyMessageSectionDetailViewModel notify => notify.IsInitialized,
                ChatMessageSectionDetailViewModel chat => chat.IsInitialized,
                _ => CurrentSection is not null,
            };
            _initialized = success;
            if (success) SectionInitialized?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (IsCurrent(owner, _initializeOwner)) _logger.LogError("消息初始化失败 ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            if (IsCurrent(owner, _initializeOwner)) IsLoading = false;
            owner.Complete(success);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => StartInitialize(refresh: true);

    [RelayCommand]
    private Task SelectSectionAsync(IMessageSectionDetailViewModel vm)
    {
#pragma warning disable VSTHRD003
        if (_resetting && _initializeOwner is { } resetOwner) return resetOwner.Task;
        if (vm is null || !Sections.Contains(vm) || _account.AccountId is null) return Task.CompletedTask;
        if (ReferenceEquals(vm, CurrentSection) && _selectionOwner is { Task.IsCompleted: false } pending
            && IsCurrent(pending, _selectionOwner)) return pending.Task;
#pragma warning restore VSTHRD003
        var owner = NewOwner(Task.CompletedTask);
        _selectionOwner = owner;
        _ = CompleteSelectionAsync(vm, owner, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteSelectionAsync(IMessageSectionDetailViewModel vm, MessageRequestOwner owner, long activationEpoch)
    {
        bool Current() => IsCurrent(owner, _selectionOwner) && Sections.Contains(vm);
        var success = false;
        try
        {
            if (!Current()) return;
            var sectionSettingValue = vm switch
            {
                NotifyMessageSectionDetailViewModel notify => $"nf_{notify.Type}",
                ChatMessageSectionDetailViewModel chat => $"chat_{chat.Data.User.Id}",
                SystemMessageSectionDetailViewModel => "system",
                _ => string.Empty,
            };

            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastSelectedMessageSection, sectionSettingValue);
            if (!Current()) return;
            if (!ReferenceEquals(CurrentSection, vm)) SetSectionActive(CurrentSection, false);
            if (!Current()) return;
            CurrentSection = vm;
            if (!Current() || !ReferenceEquals(CurrentSection, vm)) return;
            SetSectionActive(vm, _isActive);
            if (!Current()) return;
            await vm.InitializeCommand.ExecuteAsync(default);
            if (!Current() || !ReferenceEquals(CurrentSection, vm)) return;
            success = true;
            if (_isActive && activationEpoch == _activationEpoch) await UpdateUnreadCountAsync();
        }
        catch (Exception ex)
        {
            if (Current()) _logger.LogError("消息分区初始化失败 ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            owner.Complete(success);
        }
    }

    [RelayCommand]
    private async Task UpdateUnreadCountAsync()
    {
        var owner = NewOwner(Task.CompletedTask);
        var epoch = _activationEpoch;
        var snapshot = await _account.UpdateUnreadSnapshotAsync();
        if (owner.IsCurrent(owner, _pageGeneration, _account.AccountGeneration, _account.AccountId)
            && _isActive && epoch == _activationEpoch && snapshot is not null) ApplyUnread(snapshot);
    }

    [RelayCommand]
    private Task LoadChatSessionsAsync() => StartChatSessions(refresh: false);

    private Task StartChatSessions(bool refresh)
    {
#pragma warning disable VSTHRD003
        if (_resetting && _initializeOwner is { } resetOwner) return resetOwner.Task;
        if (_chatOwner is { Task.IsCompleted: false } pending && IsCurrent(pending, _chatOwner)) return pending.Task;
#pragma warning restore VSTHRD003
        if ((!refresh && _preventLoadMore) || _account.AccountId is null) return Task.CompletedTask;
        var owner = new MessageRequestOwner(_pageGeneration, _account.AccountGeneration, _account.AccountId,
            _chatOwner?.Task ?? Task.CompletedTask, refresh);
        var offset = refresh ? null : _chatSessionOffset;
        _chatOwner = owner;
        _ = CompleteChatSessionsAsync(owner, offset);
        return owner.Task;
    }

    private async Task CompleteChatSessionsAsync(MessageRequestOwner owner, long? requestedOffset)
    {
        var success = false;
        try
        {
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _chatOwner)) return;
            IsChatSessionLoading = true;
            if (!IsCurrent(owner, _chatOwner)) return;
            var (sessions, offset, hasMore) = await _service.GetChatSessionsAsync(requestedOffset);
            if (!IsCurrent(owner, _chatOwner)) return;
            _preventLoadMore = !hasMore;
            _chatSessionOffset = offset;
            if (owner.Refresh)
            {
                foreach (var chat in Sections.OfType<ChatMessageSectionDetailViewModel>().ToArray())
                {
                    if (!IsCurrent(owner, _chatOwner)) return;
                    await chat.Retire();
                    if (!IsCurrent(owner, _chatOwner)) return;
                    Sections.Remove(chat);
                }
            }
            if (sessions is not null)
            {
                foreach (var item in sessions)
                {
                    if (!IsCurrent(owner, _chatOwner)) return;
                    Sections.Add(new ChatMessageSectionDetailViewModel(item, _account));
                }

                if (!IsCurrent(owner, _chatOwner)) return;
                ChatSessionsUpdated?.Invoke(this, EventArgs.Empty);
            }
            success = IsCurrent(owner, _chatOwner);
            if (success) _chatInitialized = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _chatOwner)) return;
            _logger.LogError("加载聊天会话时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToLoadChatSessions), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _chatOwner)) IsChatSessionLoading = false;
            owner.Complete(success);
        }
    }

    private IMessageSectionDetailViewModel GetSavedSection()
    {
        if (CurrentSection is not null && Sections.Contains(CurrentSection)) return CurrentSection;
        var lastSelectedSection = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastSelectedMessageSection, string.Empty);
        if (lastSelectedSection == "system" && SystemSection is not null)
        {
            return SystemSection;
        }
        else if (lastSelectedSection.StartsWith("nf"))
        {
            var isSuccess = Enum.TryParse<NotifyMessageType>(lastSelectedSection.Replace("nf_", string.Empty), out var type);
            if (isSuccess)
            {
                var section = Sections.OfType<NotifyMessageSectionDetailViewModel>().FirstOrDefault(p => p.Type == type);
                if (section is not null)
                {
                    return section;
                }
            }
        }
        else if (lastSelectedSection.StartsWith("chat"))
        {
            var id = lastSelectedSection.Replace("chat_", string.Empty);
            var section = Sections.OfType<ChatMessageSectionDetailViewModel>().FirstOrDefault(p => p.Data.User.Id == id);
            if (section is not null)
            {
                return section;
            }
        }

        return Sections.First();
    }

    /// <summary>UI must call on page Loaded/Unloaded (and equivalent navigation visibility).</summary>
    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        _activationEpoch++;
        OnPropertyChanged(nameof(IsActive));
        if (_isActive != active) return;
        SetSectionActive(CurrentSection, active);
        if (active && _isActive)
        {
            _ = InitializeAsync();
            if (_initialized && CurrentSection is SystemMessageSectionDetailViewModel system) system.InitializeCommand.Execute(default);
        }
    }

    private static void SetSectionActive(IMessageSectionDetailViewModel? section, bool active)
    {
        if (section is SystemMessageSectionDetailViewModel system) system.SetActive(active);
        else if (section is NotifyMessageSectionDetailViewModel notify) notify.SetActive(active);
        else if (section is ChatMessageSectionDetailViewModel chat) chat.SetActive(active);
    }

    private MessageRequestOwner NewOwner(Task previous) => new(_pageGeneration, _account.AccountGeneration, _account.AccountId, previous);
    private bool IsCurrent(MessageRequestOwner owner, MessageRequestOwner? slot)
        => owner.IsCurrent(slot, _pageGeneration, _account.AccountGeneration, _account.AccountId);

    private void ResetSections()
    {
        _pageGeneration++;
        _initialized = _chatInitialized = false;
        var drains = new List<Task> { _drain };
        if (_initializeOwner is not null) drains.Add(_initializeOwner.Task);
        if (_chatOwner is not null) drains.Add(_chatOwner.Task);
        if (_selectionOwner is not null) drains.Add(_selectionOwner.Task);
        foreach (var notify in Sections.OfType<NotifyMessageSectionDetailViewModel>()) drains.Add(notify.Retire());
        foreach (var chat in Sections.OfType<ChatMessageSectionDetailViewModel>()) drains.Add(chat.Retire());
        _drain = Task.WhenAll(drains);
        var owner = NewOwner(_drain);
        _initializeOwner = owner; // Account/reset notifications must join this not-yet-started owner.
        _resetting = true;
        _chatOwner = _selectionOwner = null;
        _chatSessionOffset = default;
        _preventLoadMore = false;
        try
        {
            bool Current() => ReferenceEquals(owner, _initializeOwner);
            foreach (var system in Sections.OfType<SystemMessageSectionDetailViewModel>().ToArray())
            {
                system.Dispose();
                if (!Current()) return;
            }
            CurrentSection = null;
            if (!Current()) return;
            Sections.Clear();
            if (!Current()) return;
            IsChatSessionLoading = false;
            if (!Current()) return;
            IsLoading = false;
            if (!Current()) return;
            OnPropertyChanged(nameof(SystemSection));
        }
        finally
        {
            _resetting = false;
            if (_isActive && IsCurrent(owner, _initializeOwner)) _ = CompleteInitializeAsync(owner);
            else owner.Complete(false);
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        ResetSections();
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.UnreadSnapshot) && _account.UnreadSnapshot is { } snapshot) ApplyUnread(snapshot);
    }

    private void ApplyUnread(UnreadMessageSnapshot unread)
    {
        var owner = NewOwner(Task.CompletedTask);
        bool Current() => owner.IsCurrent(owner, _pageGeneration, _account.AccountGeneration, _account.AccountId)
            && ReferenceEquals(unread, _account.UnreadSnapshot);
        foreach (var item in Sections.OfType<NotifyMessageSectionDetailViewModel>().ToArray())
        {
            if (!Current()) return;
            item.UnreadCount = item.Type switch
            {
                NotifyMessageType.Like => unread.LikeCount,
                NotifyMessageType.Reply => unread.ReplyCount,
                NotifyMessageType.At => unread.AtCount,
                _ => 0,
            };
        }

        if (Current()) SystemSection?.UpdateUnreadSnapshot();
    }
}
