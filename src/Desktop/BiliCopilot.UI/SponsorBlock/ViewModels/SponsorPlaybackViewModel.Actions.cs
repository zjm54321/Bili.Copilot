// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>播放动作、显式恢复、本机排除与需确认的远程操作.</summary>
public sealed partial class SponsorPlaybackViewModel
{
    private (long Applied, long Rate)? _admittedBaseline;
    private RemoteRequest? _remote;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRemoteConfirmVisible;

    [ObservableProperty]
    private string _remoteConfirmTitle = string.Empty;

    [ObservableProperty]
    private string _remoteConfirmText = string.Empty;

    [ObservableProperty]
    private bool _isRemoteCategoryVisible;

    [ObservableProperty]
    private IReadOnlyList<string> _remoteCategoryLabels = [];

    [ObservableProperty]
    private int _remoteCategoryIndex = -1;

    private sealed record RemoteRequest(SponsorRemoteKind Kind, PlaybackOwner Owner, PlaybackSegmentKey Target,
        string? PublicId, IReadOnlyList<string> CategoryIds);

    private Task<ApplicationResult?> RunTicketedAsync(PlaybackOwner? expected, Func<SponsorApplicationCoordinator, PlayerTicket, Task<ApplicationResult>> action)
        => SponsorUiWork.Track(this, RunTicketedWorkAsync(expected, action));

    private async Task<ApplicationResult?> RunTicketedWorkAsync(PlaybackOwner? expected, Func<SponsorApplicationCoordinator, PlayerTicket, Task<ApplicationResult>> action)
    {
        var epoch = _epoch;
        if (_app is not { } app || expected is null || expected != _owner)
        {
            Feedback(SponsorUiText.Reason("MediaChanged", "视频已切换"));
            return null;
        }

        IsBusy = true;
        try
        {
            var result = await SponsorTickets.RunAsync(app, _attachment, expected, ticket => action(app, ticket));

            // Re-read after await: results for a recycled player or replaced media are never shown as current.
            return epoch == _epoch && expected == _owner ? result : null;
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback("操作没有完成，请稍后再试");
            }

            return null;
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }
    }

    private Task RunNoticeAsync(SponsorNoticeItem item, bool skipNow)
        => SponsorUiWork.Track(this, RunNoticeWorkAsync(item, skipNow));

    private async Task RunNoticeWorkAsync(SponsorNoticeItem item, bool skipNow)
    {
        if (item.Target is not { } target || (!skipNow && !item.IsPrimaryEnabled))
        {
            return;
        }

        // "Skip now" is its own explicit request kind; it is never a silent fallback for a failed speed-up.
        var action = skipNow ? PlaybackUiAction.SkipNow : item.Action;
        await RequestAsync(item.Owner, new PlaybackUiCommand(action, target));
    }

    private Task RequestAsync(PlaybackOwner? owner, PlaybackUiCommand command)
        => SponsorUiWork.Track(this, RequestWorkAsync(owner, command));

    private async Task RequestWorkAsync(PlaybackOwner? owner, PlaybackUiCommand command)
    {
        var baseline = _player?.Controller is { } c ? (c.AppliedId, c.RateReceiptId) : (0L, 0L);
        var result = await RunTicketedAsync(owner, (app, ticket) => app.RequestPlaybackAsync(ticket, command));
        if (result is null)
        {
            return;
        }

        if (result.Status is ApplicationStatus.Success or ApplicationStatus.Pending)
        {
            // Admitted/pending only. The applied card appears when N3 publishes an actual receipt.
            _admittedBaseline = baseline;
            Feedback("已发出请求，等待播放器确认");
        }
        else
        {
            Feedback(SponsorUiText.Result(result));
        }
    }

    [RelayCommand]
    private Task UndoAsync() => RequestAsync(_owner, new PlaybackUiCommand(PlaybackUiAction.Undo));

    [RelayCommand]
    private Task ReleaseMuteAsync() => RequestAsync(_owner, new PlaybackUiCommand(PlaybackUiAction.ReleaseMute));

    internal Task JumpAsync(SponsorSegmentItem item)
        => item.SegmentKey is { } key && item.CanJump
            ? RequestAsync(_owner, new PlaybackUiCommand(PlaybackUiAction.JumpToPoint, key))
            : Task.CompletedTask;

    private Task RunRecoveryAsync(SponsorRecoveryItem item)
        => SponsorUiWork.Track(this, RunRecoveryWorkAsync(item));

    private async Task RunRecoveryWorkAsync(SponsorRecoveryItem item)
    {
        var field = item.Field;
        var token = item.Token;
        var result = await RunTicketedAsync(_owner, (app, ticket) => field is { } f
            ? app.ReconcileFieldAsync(ticket, token, f)
            : app.ReconcileSeekAsync(ticket, token));
        if (result is null)
        {
            return;
        }

        Feedback(result.Status == ApplicationStatus.Success
            ? field is null ? "已以当前位置为准（不计为已跳过）" : "已按播放器的实际状态恢复"
            : SponsorUiText.Result(result));
    }

    internal Task SetExclusionAsync(SponsorSegmentItem item, LocalExclusionKind kind, bool excluded)
        => SponsorUiWork.Track(this, SetExclusionWorkAsync(item, kind, excluded));

    private async Task SetExclusionWorkAsync(SponsorSegmentItem item, LocalExclusionKind kind, bool excluded)
    {
        if (item.SegmentKey is not { } key)
        {
            return;
        }

        var result = await RunTicketedAsync(_owner, (app, ticket) => app.SetLocalExclusionAsync(ticket, key, kind, excluded));
        if (result is { Status: not ApplicationStatus.Success })
        {
            Feedback(SponsorUiText.Result(result));
        }
    }

    internal Task RequestRemoteAsync(SponsorSegmentItem item, SponsorRemoteKind kind)
        => SponsorUiWork.Track(this, RequestRemoteWorkAsync(item, kind));

    private async Task RequestRemoteWorkAsync(SponsorSegmentItem item, SponsorRemoteKind kind)
    {
        var epoch = _epoch;
        if (_app is not { } app || _owner is not { } owner || item.SegmentKey is not { } key || item.Segment is not { } segment)
        {
            return;
        }

        if (!item.CanUseRemote)
        {
            Feedback(item.RemoteUnavailableText);
            return;
        }

        IdentityView identity;
        try
        {
            identity = await app.LoadIdentityViewAsync();
        }
        catch (Exception)
        {
            identity = new(PersistenceStatus.IoFailure);
        }

        if (epoch != _epoch || owner != _owner)
        {
            return;
        }

        if (identity.Status != PersistenceStatus.Success || string.IsNullOrEmpty(identity.PublicId))
        {
            Feedback(identity.Status == PersistenceStatus.Missing
                ? "需要先在设置中创建贡献者身份"
                : "无法读取贡献者身份：" + SponsorUiText.Persistence(identity.Status));
            return;
        }

        var label = $"「{SponsorUiText.CategoryTitle(segment.Category)} {SponsorUiText.Range(segment)}」";
        var who = $"将使用贡献者 ID {SponsorUiText.ShortId(identity.PublicId)}，发送后不能撤回。";
        IReadOnlyList<string> categoryIds = [];
        switch (kind)
        {
            case SponsorRemoteKind.VoteUp:
                RemoteConfirmTitle = "确认投赞同票？";
                RemoteConfirmText = $"告诉空降助手 {label} 标记正确。{who}";
                break;
            case SponsorRemoteKind.VoteDown:
                RemoteConfirmTitle = "确认投反对票？";
                RemoteConfirmText = $"告诉空降助手 {label} 标记有误。{who}";
                break;
            case SponsorRemoteKind.ChangeCategory:
                categoryIds = SponsorApplicationCoordinator.CategoryChoices
                    .Where(c => c.Id != segment.Category && c.AllowedActions.Contains(segment.ActionType))
                    .Select(c => c.Id).ToList();
                if (categoryIds.Count == 0)
                {
                    Feedback("这个片段没有其他可选分类");
                    return;
                }

                RemoteConfirmTitle = "建议更改分类";
                RemoteConfirmText = $"为 {label} 选择正确的分类。{who}";
                break;
            default:
                return;
        }

        RemoteCategoryLabels = categoryIds.Select(SponsorUiText.CategoryTitle).ToList();
        RemoteCategoryIndex = categoryIds.Count > 0 ? 0 : -1;
        IsRemoteCategoryVisible = categoryIds.Count > 0;
        _remote = new(kind, owner, key, identity.PublicId, categoryIds);
        IsRemoteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelRemote() => ClearRemote();

    private void ClearRemote()
    {
        _remote = null;
        IsRemoteConfirmVisible = false;
        IsRemoteCategoryVisible = false;
    }

    [RelayCommand]
    private Task ConfirmRemoteAsync()
        => SponsorUiWork.Track(this, ConfirmRemoteWorkAsync());

    private async Task ConfirmRemoteWorkAsync()
    {
        var request = _remote;
        ClearRemote();
        if (request is null)
        {
            return;
        }

        string? text = null;
        if (request.Kind == SponsorRemoteKind.ChangeCategory)
        {
            var index = RemoteCategoryIndex;
            if (index < 0 || index >= request.CategoryIds.Count)
            {
                Feedback("请选择一个分类");
                return;
            }

            text = request.CategoryIds[index];
        }

        var operation = request.Kind == SponsorRemoteKind.ChangeCategory ? ManualOperation.ChangeCategory : ManualOperation.Vote;
        var vote = request.Kind == SponsorRemoteKind.VoteDown ? SponsorVote.Down : SponsorVote.Up;
        var result = await RunTicketedAsync(request.Owner, (app, ticket) =>
            app.MutateAsync(new MutationIntent(ticket, operation, request.Target, vote, text), request.PublicId));
        ShowWrite(result, operation);
    }

    [RelayCommand]
    private Task ReportViewedAsync()
        => SponsorUiWork.Track(this, ReportViewedWorkAsync());

    private async Task ReportViewedWorkAsync()
    {
        if (!CanReportViewed || _player?.Controller is not { } controller)
        {
            return;
        }

        var target = controller.Applied is { WasPreview: false } applied && !applied.Contributors.IsDefaultOrEmpty
            ? applied.Contributors[0].Key
            : controller.RateReceipt is { } rate && !rate.ObservedCoverage.IsDefaultOrEmpty && !rate.ObservedCoverage[0].Contributors.IsDefaultOrEmpty
                ? rate.ObservedCoverage[0].Contributors[0].Key
                : null;
        if (target is null || target.Source is not null)
        {
            Feedback("只有空降助手服务器上的片段才能上报");
            return;
        }

        CanReportViewed = false;
        var result = await RunTicketedAsync(_owner, (app, ticket) =>
            app.MutateAsync(new MutationIntent(ticket, ManualOperation.ReportViewed, target)));
        ShowWrite(result, ManualOperation.ReportViewed);
    }

    private void ShowWrite(ApplicationResult? result, ManualOperation operation)
    {
        if (result is null)
        {
            return;
        }

        var write = result.Write?.Status;
        var text = write switch
        {
            WriteStatus.Accepted => SponsorUiText.Operation(operation) + "：服务器已接受",
            WriteStatus.Rejected => SponsorUiText.Operation(operation) + "：服务器拒绝了此操作",
            WriteStatus.Unknown => SponsorUiText.Operation(operation) + "：结果未知，不会自动重发",
            WriteStatus.NotSent => SponsorUiText.Operation(operation) + "：" + SponsorUiText.Result(result),
            _ => SponsorUiText.Result(result),
        };
        if (result.Status == ApplicationStatus.PersistenceFailure)
        {
            text += "（结果未能保存到本机，不会重发）";
        }

        Feedback(text);
    }
}
