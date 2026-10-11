// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Comment;
using Richasy.BiliKernel.Models;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 评论主视图模型.
/// </summary>
public sealed partial class CommentMainViewModel : ViewModelBase, IPlayerSectionDetailViewModel
{
    private long _requestGeneration;
    private bool _requestsInvalidated = true;
    private CancellationToken _initializationCancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentMainViewModel"/> class.
    /// </summary>
    public CommentMainViewModel(
        ICommentService service,
        ILogger<CommentMainViewModel> logger)
    {
        _service = service;
        _logger = logger;

        Sorts = Enum.GetValues<CommentSortType>().ToList();
    }

    /// <summary>
    /// 重置评论源.
    /// </summary>
    public void Initialize(string sourceId, CommentTargetType targetType, CommentSortType sortType, CancellationToken cancellationToken = default)
    {
        Clear();
        _initializationCancellation = cancellationToken;
        _requestsInvalidated = false;
        Id = sourceId;
        _targetType = targetType;
        SortType = sortType;
        Initialized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Invalidate without waiting for network work. Invoke on the UI thread.</summary>
    public void InvalidateRequests()
    {
        _requestsInvalidated = true;
        _requestGeneration++;
        IsLoading = false;
        IsReplying = false;
    }

    private bool IsRequestCurrent(long generation, CancellationToken cancellationToken)
        => !_requestsInvalidated && generation == _requestGeneration && !cancellationToken.IsCancellationRequested;

    [RelayCommand]
    private async Task TryFirstLoadAsync()
    {
        if (Comments.Count > 0)
        {
            return;
        }

        await LoadItemsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_requestsInvalidated || _initializationCancellation.IsCancellationRequested)
        {
            return;
        }

        var targetId = Id;
        var targetType = _targetType;
        var sortType = SortType;
        Clear();
        _requestsInvalidated = false;
        Id = targetId;
        _targetType = targetType;
        SortType = sortType;
        await LoadItemsAsync();
    }

    [RelayCommand]
    private async Task LoadItemsAsync()
    {
        if (_requestsInvalidated || _initializationCancellation.IsCancellationRequested || string.IsNullOrEmpty(Id) || IsLoading || _preventLoadMore || IsEmpty)
        {
            return;
        }

        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        try
        {
            IsLoading = true;
            var view = await _service.GetCommentsAsync(Id, _targetType, SortType, _offset, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            _preventLoadMore = view.IsEnd;
            void SetCurrentReplyTarget(CommentItemViewModel item)
            {
                if (IsRequestCurrent(generation, cancellationToken))
                {
                    SetReplyTarget(item);
                }
            }

            void ShowCurrentMore(CommentDetailViewModel item)
            {
                if (IsRequestCurrent(generation, cancellationToken))
                {
                    ShowMore(item);
                }
            }

            if (view.TopComment is not null)
            {
                TopItem = new CommentDetailViewModel(new CommentItemViewModel(view.TopComment, _service, SetCurrentReplyTarget), ShowCurrentMore, _service);
            }

            foreach (var item in view.Comments)
            {
                if (!IsRequestCurrent(generation, cancellationToken))
                {
                    return;
                }

                if (!Comments.Any(p => p.Data.Data.Equals(item)))
                {
                    Comments.Add(new CommentDetailViewModel(new CommentItemViewModel(item, _service, SetCurrentReplyTarget), ShowCurrentMore, _service));
                }
            }

            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            _offset = view.NextOffset;
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Cancellation/exit is not an empty list or a failed page.
        }
        catch (Exception ex)
        {
            if (IsRequestCurrent(generation, cancellationToken))
            {
                _logger.LogError(ex, "尝试加载评论时失败");
                _preventLoadMore = true;
            }
        }
        finally
        {
            if (generation == _requestGeneration)
            {
                IsLoading = false;
                if (IsRequestCurrent(generation, cancellationToken))
                {
                    IsEmpty = TopItem is null && Comments.Count == 0;
                }
            }
        }
    }

    [RelayCommand]
    private async Task ChangeSortTypeAsync(CommentSortType type)
    {
        if (_requestsInvalidated || _initializationCancellation.IsCancellationRequested)
        {
            return;
        }

        SortType = type;
        await RefreshAsync();
    }

    [RelayCommand]
    private void BackToMain()
        => SelectedItem = default;

    [RelayCommand]
    private async Task SendReplyAsync(string content)
    {
        if (_requestsInvalidated || _initializationCancellation.IsCancellationRequested || IsReplying || string.IsNullOrEmpty(content))
        {
            return;
        }

        IsReplying = true;
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        content = content.Trim();
        var replyCommentId = _replyItem is null ? "0" : _replyItem.Data.Id;
        try
        {
            await _service.SendTextCommentAsync(content, Id, _targetType, "0", replyCommentId, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            if (SortType == CommentSortType.Time)
            {
                await Task.Delay(500, cancellationToken);
                if (IsRequestCurrent(generation, cancellationToken))
                {
                    await RefreshAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsRequestCurrent(generation, cancellationToken))
            {
                _logger.LogError(ex, "回复评论时出错");
            }
        }
        finally
        {
            if (generation == _requestGeneration)
            {
                IsReplying = false;
            }
        }
    }

    [RelayCommand]
    private void ResetReplyTarget()
    {
        _replyItem = null;
        ReplyTarget = default;
    }

    private void SetReplyTarget(CommentItemViewModel item)
    {
        _replyItem = item;
        ReplyTarget = item?.Data.User.User.Name;
    }

    private void ShowMore(CommentDetailViewModel vm)
        => SelectedItem = vm;

    private void Clear()
    {
        InvalidateRequests();
        TopItem?.ClearCommand.Execute(default);
        if (Comments is not null)
        {
            foreach (var item in Comments)
            {
                item.ClearCommand.Execute(default);
            }
        }

        // All entry points are UI-thread-owned. Do not queue a clear that can erase
        // a newer page (or even a synchronously completed first page of this source).
        Comments.Clear();
        _offset = 0;
        _preventLoadMore = false;
        Id = default;
        _targetType = default;
        SortType = default;
        IsEmpty = false;
        TopItem = default;
        SelectedItem = default;
        ResetReplyTarget();
    }
}
