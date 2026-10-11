// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.Messages;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>System notifications; all entry points and notifications belong to the UI thread.</summary>
public sealed partial class SystemMessageSectionDetailViewModel : ViewModelBase, IMessageSectionDetailViewModel, IDisposable
{
    private readonly IMessageNotificationService _service;
    private readonly AccountViewModel _account;
    private readonly ILogger<SystemMessageSectionDetailViewModel> _logger;
    private readonly HashSet<long> _ids = new();
    private readonly HashSet<long> _rowCursors = new();
    private readonly HashSet<long> _pageCursors = new();
    private CancellationTokenSource _cancellation;
    private Task _operation = Task.CompletedTask;
    private Task _refresh = Task.CompletedTask;
    private long _generation;
    private long _refreshGeneration = -1;
    private long _operationGeneration = -1;
    private long _accountGeneration;
    private long? _nextCursor;
    private long? _displayedFirstCursor;
    private bool _initialized;
    private bool _active;
    private bool _disposed;
    private int _unreadCount;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private bool _isMarkingRead;
    [ObservableProperty] private string? _errorMessage;

    public SystemMessageSectionDetailViewModel(IMessageNotificationService service, AccountViewModel account,
        ILogger<SystemMessageSectionDetailViewModel> logger)
    {
        _service = service;
        _account = account;
        _logger = logger;
        _accountGeneration = account.AccountGeneration;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(account.AccountCancellationToken);
        ApplyUnread();
    }

    public ObservableCollection<SystemMessageRow> Items { get; } = new();
    public int UnreadCount => _unreadCount;
    public bool IsActive => _active;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanMarkRead => IsCurrent(_generation) && _displayedFirstCursor.HasValue
        && !IsLoading && !IsMarkingRead;
    public event EventHandler? ListUpdated;

    /// <summary>Call on actual page/section activation and deactivation, not just tab creation.</summary>
    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        OnPropertyChanged(nameof(IsActive));
        if (active)
        {
            if (_accountGeneration != _account.AccountGeneration) ResetForAccount();
            _account.AccountChanged += OnAccountChanged;
            _account.PropertyChanged += OnAccountPropertyChanged;
            ApplyUnread();
        }
        else
        {
            _account.AccountChanged -= OnAccountChanged;
            _account.PropertyChanged -= OnAccountPropertyChanged;
            Invalidate();
        }

        NotifyMarkRead();
    }

    /// <summary>
    /// UI calls this only after the first item of the current first page was actually displayed.
    /// Old item instances, off-screen/background loads and later-page cursors cannot authorize an ack.
    /// </summary>
    public void ConfirmDisplayedFirstItem(SystemMessageRow item)
    {
        if (!IsCurrent(_generation) || !ReferenceEquals(Items.FirstOrDefault(), item)) return;
        _displayedFirstCursor = item.Cursor;
        NotifyMarkRead();
    }

    [RelayCommand]
    private Task InitializeAsync() => _initialized ? _operation : LoadItemsAsync();

    [RelayCommand]
    private Task LoadItemsAsync()
    {
        if (!IsCurrent(_generation)) return Task.CompletedTask;
        // Join the current generation's published owner; this caller owns no prerequisite work.
#pragma warning disable VSTHRD003
        if (!_operation.IsCompleted && _operationGeneration == _generation) return _operation;
#pragma warning restore VSTHRD003
        return HasMore ? StartLoad(refresh: false) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (!IsCurrent(_generation)) return Task.CompletedTask;
        // Reentrant refresh callers join the published owner, not a second request.
#pragma warning disable VSTHRD003
        if (!_refresh.IsCompleted && _refreshGeneration == _generation) return _refresh;
#pragma warning restore VSTHRD003
        return StartLoad(refresh: true);
    }

    private Task StartLoad(bool refresh)
    {
        var previous = _operation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancellation = refresh ? _cancellation : null;
        if (refresh)
        {
            _generation++;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
            _displayedFirstCursor = null;
        }

        var generation = _generation;
        var token = _cancellation.Token;
        _operation = completion.Task;
        _operationGeneration = generation;
        if (refresh)
        {
            _refresh = completion.Task;
            _refreshGeneration = generation;
        }

        // Publish before cancellation callbacks or property/command notifications can reenter.
        if (oldCancellation is not null) CancelAndRetire(oldCancellation, previous);
        if (IsCurrent(generation))
        {
            if (refresh) IsMarkingRead = false;
            if (IsCurrent(generation)) IsLoading = true;
            if (IsCurrent(generation)) NotifyMarkRead();
        }

        _ = CompleteLoadAsync(previous, completion, generation, token, refresh);
        return completion.Task;
    }

    private async Task CompleteLoadAsync(Task previous, TaskCompletionSource completion, long generation,
        CancellationToken token, bool refresh)
    {
        try
        {
            // This owner drains only the predecessor captured before its own slot was published.
#pragma warning disable VSTHRD003
            await previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(generation)) return;
            if (refresh) ResetList();
            ErrorMessage = null;
            var requestedCursor = _nextCursor;
            var rows = await _service.GetSystemMessagesAsync(requestedCursor, token);
            if (!IsCurrent(generation) || token.IsCancellationRequested) return;
            foreach (var row in rows)
            {
                if (!IsCurrent(generation)) return;
                if ((row.Id is long id && _ids.Contains(id)) || (row.Cursor is long cursor && _rowCursors.Contains(cursor))) continue;
                if (row.Id is long newId) _ids.Add(newId);
                if (row.Cursor is long newCursor) _rowCursors.Add(newCursor);
                Items.Add(row);
            }

            if (!IsCurrent(generation)) return;
            // No has_more/short-page inference, and no assumption about cursor ordering.
            var next = rows.LastOrDefault()?.Cursor;
            HasMore = rows.Count > 0 && next.HasValue && next != requestedCursor && _pageCursors.Add(next.Value);
            _nextCursor = next;
            _initialized = true;
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation) && !token.IsCancellationRequested) ReportError(ex, "系统通知加载失败，请重试。");
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                IsEmpty = Items.Count == 0;
                NotifyMarkRead();
            }

            completion.TrySetResult();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkRead))]
    private Task MarkReadAsync()
    {
        if (!CanMarkRead) return Task.CompletedTask;
        var previous = _operation;
        var generation = _generation;
        var token = _cancellation.Token;
        var cursor = _displayedFirstCursor!.Value;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _operationGeneration = generation;
        IsMarkingRead = true;
        NotifyMarkRead();
        _ = CompleteMarkReadAsync(previous, completion, generation, token, cursor);
        return completion.Task;
    }

    private async Task CompleteMarkReadAsync(Task previous, TaskCompletionSource completion, long generation, CancellationToken token, long cursor)
    {
        try
        {
            // The explicit ack owner waits its captured predecessor, never the replacement refresh.
#pragma warning disable VSTHRD003
            await previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(generation) || token.IsCancellationRequested) return;
            ErrorMessage = null;
            await _service.MarkReadAsync(cursor, token);
            if (!IsCurrent(generation) || token.IsCancellationRequested) return;
            _displayedFirstCursor = null;
            // No optimistic zero, and no joining an unread request sent before this ack.
            var snapshot = await _account.RefreshUnreadAfterMarkReadAsync();
            if (!IsCurrent(generation)) return;
            if (snapshot is null) ErrorMessage = _account.UnreadError ?? "已标记已读，但未读数量更新失败，请重试。";
            ApplyUnread();
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation) && !token.IsCancellationRequested) ReportError(ex, "标记已读失败，请重试。");
        }
        finally
        {
            if (generation == _generation)
            {
                IsMarkingRead = false;
                NotifyMarkRead();
            }

            completion.TrySetResult();
        }
    }

    private bool IsCurrent(long generation) => !_disposed && _active && generation == _generation
        && _accountGeneration == _account.AccountGeneration && _account.AccountId is not null;

    private void Invalidate()
    {
        var previous = _operation;
        var oldCancellation = _cancellation;
        var generation = ++_generation;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
        _displayedFirstCursor = null;
        CancelAndRetire(oldCancellation, previous);
        if (generation != _generation) return;
        IsLoading = false;
        if (generation != _generation) return;
        IsMarkingRead = false;
        if (generation == _generation) NotifyMarkRead();
    }

    private void CancelAndRetire(CancellationTokenSource cancellation, Task previous)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex)
        {
            // This retirement owns callback failures; do not mutate the newly published owner's UI state.
            _logger.LogWarning("系统通知取消失败 ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            // Synchronous Cancel has finished all callbacks, but the actual old work may still be pending.
            _ = RetireCancellationAsync(cancellation, previous);
        }
    }

    private async Task RetireCancellationAsync(CancellationTokenSource cancellation, Task previous)
    {
        try
        {
            // Retirement owns this exact old CTS; its captured work cannot depend on this observer.
#pragma warning disable VSTHRD003
            await previous;
#pragma warning restore VSTHRD003
        }
        catch (Exception ex)
        {
            _logger.LogWarning("系统通知旧操作失败 ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void ResetList()
    {
        _displayedFirstCursor = null;
        _nextCursor = null;
        _ids.Clear();
        _rowCursors.Clear();
        _pageCursors.Clear();
        _initialized = false;
        HasMore = true;
        Items.Clear();
        IsEmpty = true;
        NotifyMarkRead();
    }

    private void ResetForAccount()
    {
        Invalidate();
        _accountGeneration = _account.AccountGeneration;
        ResetList();
        ErrorMessage = null;
        ApplyUnread();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (!_disposed && _active) ResetForAccount();
    }
    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && _active && e.PropertyName == nameof(AccountViewModel.UnreadSnapshot)) ApplyUnread();
    }

    private void ApplyUnread() => SetProperty(ref _unreadCount, _account.UnreadSnapshot?.SystemCount ?? 0, nameof(UnreadCount));
    // The owning page also keeps the inactive section's tab badge current without a subscription.
    internal void UpdateUnreadSnapshot() => ApplyUnread();
    private void ReportError(Exception ex, string fallback)
    {
        ErrorMessage = ex is MessageNotificationException ? ex.Message : fallback;
        _logger.LogWarning("系统通知操作失败 ({ErrorType})", ex.GetType().Name);
    }

    private void NotifyMarkRead()
    {
        OnPropertyChanged(nameof(CanMarkRead));
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void Dispose()
    {
        if (_disposed) return;
        SetActive(false);
        _disposed = true;
        CancelAndRetire(_cancellation, _operation);
        ResetList();
    }
}
