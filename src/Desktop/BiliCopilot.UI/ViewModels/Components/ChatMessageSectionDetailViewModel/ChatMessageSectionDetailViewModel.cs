// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;
using BiliCopilot.UI.ViewModels.View;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 聊天消息区块详情视图模型.
/// </summary>
public sealed partial class ChatMessageSectionDetailViewModel : ViewModelBase<ChatSession>, IMessageSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChatMessageSectionDetailViewModel"/> class.
    /// </summary>
    public ChatMessageSectionDetailViewModel(
        ChatSession session,
        AccountViewModel account)
        : base(session)
    {
        _service = this.Get<IMessageService>();
        _account = account;
        _logger = this.Get<ILogger<ChatMessageSectionDetailViewModel>>();

        Avatar = session.User?.Avatar?.Uri;
        UserName = session.User?.Name;
        UnreadCount = session.UnreadCount;
        LastMessage = Data.LastMessage;
        LastMessageTime = Data.Time.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
    }

    [RelayCommand]
    private Task InitializeAsync() => _operation is { Task.IsCompleted: false } pending && IsCurrent(pending, _operation)
        ? pending.Task : _needsRefresh ? StartLoad(refresh: true) : IsInitialized ? Task.CompletedTask : StartLoad(refresh: false);

    [RelayCommand]
    private void ShowUserSpace()
        => this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), Data.User);

    [RelayCommand]
    private Task RefreshAsync() => StartLoad(refresh: true);

    [RelayCommand]
    private Task SendMessageAsync()
    {
#pragma warning disable VSTHRD003
        if (_sendOwner is { Task.IsCompleted: false } pending && IsCurrent(pending, _sendOwner)) return pending.Task;
#pragma warning restore VSTHRD003
        if (string.IsNullOrEmpty(InputText) || _retired || _account.AccountId is null) return Task.CompletedTask;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, _sendOwner?.Task ?? Task.CompletedTask);
        var user = Data.User;
        var input = InputText;
        _sendOwner = owner;
        _ = CompleteSendAsync(owner, user, input, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteSendAsync(MessageRequestOwner owner, Richasy.BiliKernel.Models.User.UserProfile user, string input, long epoch)
    {
        var success = false;
        try
        {
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _sendOwner)) return;
            IsSending = true;
            if (!IsCurrent(owner, _sendOwner)) return;
            var now = DateTimeOffset.Now;
            var msg = await _service.SendChatMessageAsync(input, user);
            if (!IsCurrent(owner, _sendOwner)) return;
            if (msg is not null)
            {
                Messages.Add(new ChatMessageItemViewModel(msg));
                if (!IsCurrent(owner, _sendOwner)) return;
                if (InputText == input) InputText = string.Empty;
                if (!IsCurrent(owner, _sendOwner)) return;
                LastMessage = msg.Content.Text;
                if (!IsCurrent(owner, _sendOwner)) return;
                LastMessageTime = now.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
                if (IsCurrent(owner, _sendOwner) && _active && epoch == _activationEpoch) RequestScrollToBottom?.Invoke(this, EventArgs.Empty);
            }
            success = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _sendOwner)) return;
            _logger.LogError("发送消息时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToSendMessage), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _sendOwner)) IsSending = false;
            if (IsCurrent(owner, _sendOwner)) IsEmpty = Messages.Count == 0;
            owner.Complete(success);
        }
    }

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _activationEpoch++;
    }

    internal Task Retire()
    {
        _retired = true;
        _generation++;
        SetActive(false);
        return Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask);
    }

    internal Task InvalidateData()
    {
        _generation++;
        _needsRefresh = true;
        return Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask);
    }

    private bool IsCurrent(MessageRequestOwner owner, MessageRequestOwner? slot) => !_retired
        && owner.IsCurrent(slot, _generation, _account.AccountGeneration, _account.AccountId);

    private Task StartLoad(bool refresh)
    {
        if (_retired || _account.AccountId is null) return Task.CompletedTask;
#pragma warning disable VSTHRD003
        if (_operation is { Task.IsCompleted: false } pending && IsCurrent(pending, _operation) && (!refresh || pending.Refresh)) return pending.Task;
#pragma warning restore VSTHRD003
        if (!refresh && _preventLoadMore) return Task.CompletedTask;
        var previous = Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask);
        if (refresh) _generation++;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, previous, refresh);
        var user = Data.User;
        _operation = owner;
        _ = CompleteLoadAsync(owner, user, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteLoadAsync(MessageRequestOwner owner, Richasy.BiliKernel.Models.User.UserProfile user, long epoch)
    {
        var success = false;
        try
        {
            // A replacement drains the uncancelable predecessor without admitting another request.
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _operation)) return;
            IsLoading = true;
            if (!IsCurrent(owner, _operation)) return;
            var (messages, maxCount, hasMore) = await _service.GetChatMessagesAsync(user);
            if (!IsCurrent(owner, _operation)) return;
            if (owner.Refresh) Messages = new();
            if (!IsCurrent(owner, _operation)) return;
            if (messages is not null)
            {
                foreach (var item in messages)
                {
                    if (!IsCurrent(owner, _operation)) return;
                    Messages.Add(new ChatMessageItemViewModel(item));
                }

                if (!IsCurrent(owner, _operation)) return;
                if (messages.Count > 0 && LastMessage != messages.Last().Content.Text)
                {
                    LastMessage = messages.Last().Content.Text;
                    if (!IsCurrent(owner, _operation)) return;
                    LastMessageTime = messages.Last().Time.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
                }

                if (IsCurrent(owner, _operation) && _active && epoch == _activationEpoch) RequestScrollToBottom?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _preventLoadMore = true;
            }
            if (!IsCurrent(owner, _operation)) return;
            IsInitialized = true;
            _needsRefresh = false;
            success = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _operation)) return;
            _logger.LogError("加载聊天消息时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToLoadChatMessages), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _operation)) IsLoading = false;
            if (IsCurrent(owner, _operation)) IsEmpty = Messages.Count == 0;
            owner.Complete(success);
        }
    }
}
