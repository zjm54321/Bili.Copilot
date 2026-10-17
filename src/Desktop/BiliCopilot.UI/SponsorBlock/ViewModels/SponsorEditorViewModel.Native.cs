// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 播放器端标注控制：逐帧、播放预览、结束预览/停在这里。
/// 每个按钮只看当前播放器发布的 <c>EditorCapabilities</c>；点击时重新读取票据或预览令牌。
/// “已回应”只表示播放器接收/回应了请求，不代表画面已到位、逐帧精确或计入统计。
/// 结束预览使用保留的预览令牌，即使空降助手已关闭、播放器正在断开或草稿已变化也能结束；失败不自动重试。
/// </summary>
public sealed partial class SponsorEditorViewModel
{
    private bool _canStartPreviewPlayback;
    private string _previewPlaybackHint = string.Empty;
    private long _cleanupEpoch;

    [ObservableProperty]
    private bool _canFrameStep;

    [ObservableProperty]
    private bool _canPlayPreparedPreview;

    [ObservableProperty]
    private bool _hasPreviewPlayback;

    [ObservableProperty]
    private string _previewPlaybackTitle = string.Empty;

    [ObservableProperty]
    private string _previewPlaybackDetail = string.Empty;

    [ObservableProperty]
    private bool _isPreviewPlaybackUncertain;

    [ObservableProperty]
    private bool _canEndPreviewPlayback;

    [ObservableProperty]
    private bool _canKeepCurrentPreview;

    [ObservableProperty]
    private string _keepCurrentPreviewHint = string.Empty;

    [ObservableProperty]
    private bool _isPreviewCleanupBusy;

    [ObservableProperty]
    private bool _hasEditorActivity;

    [ObservableProperty]
    private string _editorActivityText = string.Empty;

    [ObservableProperty]
    private bool _hasLastEditorReply;

    [ObservableProperty]
    private string _lastEditorReplyText = string.Empty;

    private void ResetNative()
    {
        _cleanupEpoch++;
        _canStartPreviewPlayback = false;
        _previewPlaybackHint = string.Empty;
        CanFrameStep = false;
        CanPlayPreparedPreview = false;
        FrameUnavailableText = "播放视频后可逐帧调整";
        PreviewPlaybackUnavailableText = string.Empty;
        HasPreviewPlayback = false;
        PreviewPlaybackTitle = string.Empty;
        PreviewPlaybackDetail = string.Empty;
        IsPreviewPlaybackUncertain = false;
        CanEndPreviewPlayback = false;
        CanKeepCurrentPreview = false;
        KeepCurrentPreviewHint = string.Empty;
        IsPreviewCleanupBusy = false;
        HasEditorActivity = false;
        EditorActivityText = string.Empty;
        HasLastEditorReply = false;
        LastEditorReplyText = string.Empty;
    }

    private void ProjectNative(SponsorApplicationView view, SponsorPlayerView? player, PlaybackOwner? owner)
    {
        var caps = player is null ? ImmutableArray<SponsorAvailability>.Empty : player.EditorCapabilities;
        SponsorAvailability? Cap(SponsorFeature feature) => caps.IsDefault ? null : caps.FirstOrDefault(c => c.Feature == feature);

        // Ordinary (new admission) editor transport needs a current attached owner and an open facade.
        var admissible = !view.Closed && player is { Attached: true, EditorPortAvailable: true } && owner is not null
            && !player.EditorCommandPending;

        var frame = Cap(SponsorFeature.FrameStep);
        CanFrameStep = admissible && frame?.Available == true;
        FrameUnavailableText = player is null
            ? "播放视频后可逐帧调整"
            : !player.EditorPortAvailable ? SponsorUiText.Reason("EditorPlayerPortUnavailable", "逐帧控制暂不可用")
            : SponsorUiText.Availability(frame ?? Feature(SponsorFeature.FrameStep), "逐帧控制暂不可用");

        var start = Cap(SponsorFeature.PreviewPlayback);
        _canStartPreviewPlayback = admissible && start?.Available == true;
        _previewPlaybackHint = player is { EditorPortAvailable: false }
            ? SponsorUiText.Reason("EditorPlayerPortUnavailable", "预览播放暂不可用")
            : SponsorUiText.Availability(start ?? Feature(SponsorFeature.PreviewPlayback), "预览播放暂不可用");
        PreviewPlaybackUnavailableText = _canStartPreviewPlayback ? string.Empty : _previewPlaybackHint;
        CanPlayPreparedPreview = false; // Re-derived from the synced draft rows by the active Apply path.

        // Cleanup UI: retained token, independent of Closed/Attached/draft revision.
        var preview = player?.PreviewPlayback;
        var live = preview is not null && preview.Phase != SponsorPreviewPhase.Ended;
        HasPreviewPlayback = live;
        IsPreviewPlaybackUncertain = preview?.Phase is SponsorPreviewPhase.NeedsReconciliation or SponsorPreviewPhase.Interrupted;
        if (live)
        {
            var range = $"{SponsorUiText.Time(preview!.Interval.Start)} – {SponsorUiText.Time(preview.Interval.End)}";
            PreviewPlaybackTitle = preview.Phase switch
            {
                SponsorPreviewPhase.Starting => "正在开始预览…",
                SponsorPreviewPhase.AwaitingEntry => "正在跳到预览开头…",
                SponsorPreviewPhase.Playing => "正在播放预览",
                SponsorPreviewPhase.AtEnd => "预览已播放到结尾",
                SponsorPreviewPhase.Restoring => "正在返回预览前的位置…",
                SponsorPreviewPhase.AwaitingReturn => "正在等待播放器返回…",
                SponsorPreviewPhase.NeedsReconciliation => "预览状态不明确",
                SponsorPreviewPhase.Interrupted => "预览被打断",
                _ => "预览",
            };
            var back = preview.SavedPosition is { } saved && double.IsFinite(saved)
                ? $"，结束后回到 {SponsorUiText.Time(saved)}" + (preview.SavedPaused == true ? "（暂停）" : string.Empty)
                : string.Empty;
            PreviewPlaybackDetail = IsPreviewPlaybackUncertain
                ? $"预览区间 {range}。请查看当前画面，再选择返回或停在这里。"
                : $"预览区间 {range}{back}";
        }

        var end = Cap(SponsorFeature.PreviewEnd);
        var keep = Cap(SponsorFeature.PreviewKeepCurrent);
        CanEndPreviewPlayback = live && (end is null || end.Available);
        CanKeepCurrentPreview = live && (keep is null || keep.Available);
        KeepCurrentPreviewHint = SponsorUiText.Availability(keep, "停在当前位置，不返回");

        var pending = player?.EditorCommandPending == true;
        var cleanup = player?.CleanupPending == true || (view.PlayerCleanupPending && live);
        HasEditorActivity = pending || cleanup;
        EditorActivityText = cleanup ? "正在等待播放器归还预览状态…" : pending ? "播放器正在处理上一个标注操作…" : string.Empty;

        var reply = player?.LastEditorReply;
        HasLastEditorReply = reply is not null;
        LastEditorReplyText = reply is null ? string.Empty : ReplyText(reply);
    }

    private static string ReplyText(SponsorEditorReply reply)
    {
        var outcome = SponsorUiText.Reason("Editor" + reply.Outcome, "播放器的回应不明确");

        // Command is the native mutation record; it is shown as-is, never upgraded to "applied".
        var record = reply.Command?.Phase switch
        {
            SponsorOperationPhase.Pending => "，播放器记录：处理中",
            SponsorOperationPhase.SucceededUnobserved => "，播放器记录：已执行，尚未观察到结果",
            SponsorOperationPhase.Confirmed => "，播放器记录：已确认",
            SponsorOperationPhase.Failed => "，播放器记录：失败",
            SponsorOperationPhase.Unknown => "，播放器记录：结果未知",
            SponsorOperationPhase.Reconciled => "，播放器记录：已核对",
            _ => string.Empty,
        };
        return "上次播放器操作：" + outcome + record;
    }

    private void ApplyPreviewItem(SponsorDraftItem item)
    {
        item.CanPlayPreview = item.IsPreviewing && _canStartPreviewPlayback;
        item.PlayPreviewHint = item.IsPreviewing ? _previewPlaybackHint : "先点击“预览”准备区间";
    }

    [RelayCommand]
    private Task FrameBackwardAsync() => NativeEditAsync(new EditorCommand(EditorAction.FrameBackward));

    [RelayCommand]
    private Task FrameForwardAsync() => NativeEditAsync(new EditorCommand(EditorAction.FrameForward));

    /// <summary>播放已准备的草稿区间。点击时重新读取草稿修订号与播放票据.</summary>
    internal Task PlayPreviewAsync(SponsorDraftItem item)
    {
        if (item.Entry is not { } entry || !item.IsPreviewing || !item.CanPlayPreview)
        {
            return Task.CompletedTask;
        }

        var selection = new DraftSelection(item.Key, entry.Draft.Revision);
        return NativeEditAsync(new EditorCommand(EditorAction.StartPreviewPlayback, selection));
    }

    /// <summary>播放预览卡片中已准备的那条草稿.</summary>
    [RelayCommand]
    private Task PlayPreparedPreviewAsync()
        => Drafts.FirstOrDefault(d => d.IsPreviewing) is { } item ? PlayPreviewAsync(item) : Task.CompletedTask;

    private Task NativeEditAsync(EditorCommand command)
        => SponsorUiWork.Track(this, NativeEditWorkAsync(command));

    private async Task NativeEditWorkAsync(EditorCommand command)
    {
        var epoch = _epoch;
        var owner = _owner;
        if (_app is not { } app || owner is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await SponsorTickets.RunAsync(app, _attachment, owner, ticket => app.EditAsync(ticket, command));
            if (epoch != _epoch || owner != _owner)
            {
                return;
            }

            // Pending/Acknowledged are shown by the persistent player-reply line, not as a success toast.
            if (result.Status is ApplicationStatus.Pending or ApplicationStatus.Acknowledged or ApplicationStatus.Success)
            {
                HasFeedback = false;
            }
            else
            {
                Feedback(SponsorUiText.Result(result));
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback("播放器没有完成这个操作，请查看当前画面");
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>结束预览并返回预览前的位置（如果播放器仍可返回）.</summary>
    [RelayCommand]
    private Task EndPreviewPlaybackAsync() => EndPreviewWithAsync(SponsorPreviewEndMode.Restore);

    /// <summary>显式放弃返回：停在当前画面，结束预览.</summary>
    [RelayCommand]
    private Task KeepCurrentPreviewAsync() => EndPreviewWithAsync(SponsorPreviewEndMode.KeepCurrent);

    private Task EndPreviewWithAsync(SponsorPreviewEndMode mode)
        => SponsorUiWork.Track(this, EndPreviewWithWorkAsync(mode));

    private async Task EndPreviewWithWorkAsync(SponsorPreviewEndMode mode)
    {
        if (_app is not { } app || IsPreviewCleanupBusy)
        {
            return;
        }

        // Click-time token from the latest published view; deliberately not gated on Closed/Attached.
        var token = app.Snapshot.Players.FirstOrDefault(p => p.Attachment == _attachment)?.PreviewPlayback?.Token;
        if (token is null)
        {
            Feedback(SponsorUiText.Reason("NoOwnedPreviewCleanup", "没有正在进行的预览"));
            return;
        }

        var epoch = _epoch;
        var cleanupEpoch = ++_cleanupEpoch;
        IsPreviewCleanupBusy = true;
        try
        {
            var result = await app.EndPreviewPlaybackAsync(_attachment, token, mode);
            if (epoch != _epoch || cleanupEpoch != _cleanupEpoch)
            {
                return;
            }

            Feedback(result.Status switch
            {
                ApplicationStatus.Released => mode == SponsorPreviewEndMode.KeepCurrent ? "预览已结束，停在当前位置" : "预览已结束",
                ApplicationStatus.Closed => "播放器已断开，预览随之结束",
                _ => SponsorUiText.Result(result),
            });
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback("没有收到播放器的明确结果，请查看当前画面后再决定");
            }
        }
        finally
        {
            if (epoch == _epoch && cleanupEpoch == _cleanupEpoch)
            {
                IsPreviewCleanupBusy = false;
            }
        }
    }
}
