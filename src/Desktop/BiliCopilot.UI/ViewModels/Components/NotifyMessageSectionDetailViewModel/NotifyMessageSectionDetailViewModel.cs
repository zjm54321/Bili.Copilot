// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;
using BiliCopilot.UI.ViewModels.View;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 通知消息区块详情视图模型.
/// </summary>
public sealed partial class NotifyMessageSectionDetailViewModel : ViewModelBase, IMessageSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NotifyMessageSectionDetailViewModel"/> class.
    /// </summary>
    public NotifyMessageSectionDetailViewModel(
        NotifyMessageType type,
        IMessageService service,
        AccountViewModel account)
    {
        _service = service;
        _account = account;
        _logger = this.Get<ILogger<NotifyMessageSectionDetailViewModel>>();
        Type = type;
    }

    [RelayCommand]
    private Task RefreshAsync() => StartLoad(refresh: true);

    [RelayCommand]
    private Task InitializeAsync() => _operation is { Task.IsCompleted: false } pending && IsCurrent(pending)
        ? pending.Task : _needsRefresh ? StartLoad(refresh: true) : IsInitialized ? Task.CompletedTask : LoadItemsAsync();

    [RelayCommand]
    private Task LoadItemsAsync() => StartLoad(refresh: false);

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _activationEpoch++;
    }

    internal Task Retire()
    {
        _retired = true;
        SetActive(false);
        _generation++;
        return _operation?.Task ?? Task.CompletedTask;
    }

    internal Task InvalidateData()
    {
        _generation++;
        _needsRefresh = true;
        return _operation?.Task ?? Task.CompletedTask;
    }

    private bool IsCurrent(MessageRequestOwner owner) => !_retired
        && owner.IsCurrent(_operation, _generation, _account.AccountGeneration, _account.AccountId);

    private Task StartLoad(bool refresh)
    {
        if (_retired || _account.AccountId is null) return Task.CompletedTask;
#pragma warning disable VSTHRD003
        if (_operation is { Task.IsCompleted: false } pending && IsCurrent(pending) && (!refresh || pending.Refresh)) return pending.Task;
#pragma warning restore VSTHRD003
        if (!refresh && _preventLoadMore) return Task.CompletedTask;
        var previous = _operation?.Task ?? Task.CompletedTask;
        if (refresh) _generation++;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, previous, refresh);
        var offsetId = refresh ? 0 : _offsetId;
        var offsetTime = refresh ? 0 : _offsetTime;
        _operation = owner;
        _ = CompleteLoadAsync(owner, offsetId, offsetTime, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteLoadAsync(MessageRequestOwner owner, long requestedId, long requestedTime, long activationEpoch)
    {
        var success = false;
        try
        {
            // The SDK request cannot be canceled here; drain the exact old physical owner.
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner)) return;
            IsLoading = true;
            if (!IsCurrent(owner)) return;
            var (messages, offsetId, offsetTime, hasMore) = await _service.GetNotifyMessagesAsync(Type, requestedId, requestedTime);
            if (!IsCurrent(owner)) return;
            // Keep visible rows until a replacement request succeeds.
            if (owner.Refresh) Items.Clear();
            if (!IsCurrent(owner)) return;
            _preventLoadMore = messages is null || messages.Count == 0 || !hasMore;
            if (messages != null)
            {
                foreach (var item in messages)
                {
                    if (!IsCurrent(owner)) return;
                    Items.Add(new NotifyMessageItemViewModel(item));
                }
            }

            if (!IsCurrent(owner)) return;
            _offsetId = offsetId;
            _offsetTime = offsetTime;
            IsInitialized = true;
            _needsRefresh = false;
            if (_active && activationEpoch == _activationEpoch) UnreadCount = 0;
            if (!IsCurrent(owner)) return;
            success = true;
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (IsCurrent(owner)) _logger.LogError("加载通知消息失败 ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            if (IsCurrent(owner)) IsLoading = false;
            if (IsCurrent(owner)) IsEmpty = Items.Count == 0;
            owner.Complete(success);
        }
    }
}
