// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 片段标注视图模型：开始/结束标记、精彩时刻、整片标签、编辑/删除/撤销、预览准备、提交与历史。
/// 所有时间都由门面从播放器实际位置读取；界面不估算位置，也不模拟按键或逐帧。
/// </summary>
public sealed partial class SponsorEditorViewModel : ObservableObject
{
    private const int MaximumDrafts = 64;
    private const int MaximumHistory = 20;
    private static readonly IReadOnlyList<SponsorCategory> CaptureCategories =
        SponsorRules.Categories.Where(c => c.AllowedActions.Any(a => a is "skip" or "mute")).ToList();

    private static readonly IReadOnlyList<SponsorCategory> FullCategories =
        SponsorRules.Categories.Where(c => c.AllowedActions.Contains("full")).ToList();

    private SponsorApplicationCoordinator? _app;
    private SponsorViewFeed? _feed;
    private Guid _attachment;
    private long _epoch;
    private PlaybackOwner? _owner;
    private SponsorPlayerView? _player;
    private SponsorLocalSettings _settings = new();
    private ImmutableArray<SponsorAvailability> _availability = [];
    private IReadOnlyList<string> _captureActionIds = [];
    private ImmutableHashSet<string> _locked = [];

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _statusText = "播放视频后可以标注片段";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _captureCategoryIndex;

    [ObservableProperty]
    private IReadOnlyList<string> _captureActionLabels = [];

    [ObservableProperty]
    private int _captureActionIndex;

    [ObservableProperty]
    private int _fullCategoryIndex;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _captureHint = "播放到片段开头时点击“标记开始”，到结尾时点击“标记结束”。";

    [ObservableProperty]
    private bool _hasDrafts;

    [ObservableProperty]
    private string _otherDraftsText = string.Empty;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private string _frameUnavailableText = string.Empty;

    [ObservableProperty]
    private string _previewPlaybackUnavailableText = string.Empty;

    [ObservableProperty]
    private bool _hasFeedback;

    [ObservableProperty]
    private string _feedbackText = string.Empty;

    [ObservableProperty]
    private bool _hasDraftSaveError;

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private string _draftSaveText = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="SponsorEditorViewModel"/> class.</summary>
    public SponsorEditorViewModel()
    {
        CaptureCategoryLabels = CaptureCategories.Select(c => SponsorUiText.CategoryTitle(c.Id)).ToList();
        FullCategoryLabels = FullCategories.Select(c => SponsorUiText.CategoryTitle(c.Id)).ToList();
        UpdateCaptureActions();
    }

    /// <summary>可用于开始/结束标记的分类.</summary>
    public IReadOnlyList<string> CaptureCategoryLabels { get; }

    /// <summary>可作为整片标签的分类.</summary>
    public IReadOnlyList<string> FullCategoryLabels { get; }

    /// <summary>当前视频的草稿（最多 64 条）.</summary>
    public ObservableCollection<SponsorDraftItem> Drafts { get; } = [];

    /// <summary>最近的远程操作结果（最多 20 条）.</summary>
    public ObservableCollection<SponsorHistoryItem> History { get; } = [];

    /// <summary>
    /// 在播放器窗口的界面线程上绑定到附加 ID。只读取本机身份视图（不创建、不联网）。
    /// 不启动、停止或释放门面/播放器。
    /// </summary>
    internal void Bind(SponsorApplicationCoordinator app, Guid attachment)
    {
        var queue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("SponsorEditorBindRequiresUiThread");
        Unbind();
        _epoch++;
        _app = app;
        _attachment = attachment;
        _availability = app.Availability;
        ResetNative();
        ApplyContributeAvailability();
        _feed = new SponsorViewFeed(app, queue, Apply);
        _feed.Start();
        _ = LoadIdentityAsync();
    }

    /// <summary>解除绑定，丢弃迟到的视图与异步结果。不改动草稿。</summary>
    internal void Unbind()
    {
        _epoch++;
        _feed?.Stop();
        _feed = null;
        _app = null;
        _owner = null;
        _player = null;
        _locked = [];
        ResetContribute();
        ResetNative();
        IsActive = false;
        Drafts.Clear();
        History.Clear();
        HasHistory = false;
        HasDrafts = false;
        HasPreview = false;
        HasFeedback = false;
    }

    private SponsorAvailability? Feature(SponsorFeature feature) => _availability.FirstOrDefault(a => a.Feature == feature);

    private void Apply(SponsorApplicationView view)
    {
        _settings = view.Settings;
        var player = view.Players.FirstOrDefault(p => p.Attachment == _attachment);
        var owner = player is { Attached: true } ? player.Controller?.Owner : null;
        if (owner != _owner)
        {
            _owner = owner;
            _locked = [];
            HasFeedback = false;
            ResetContribute();
        }

        _player = player;
        HasDraftSaveError = view.Dirty && view.Persistence is { } p && p != PersistenceStatus.Success;
        DraftSaveText = view.Persistence is { } status && HasDraftSaveError ? "草稿未能保存到本机：" + SponsorUiText.Persistence(status) : string.Empty;
        // Native preview/frame state first: the retained cleanup token stays actionable even when
        // the facade is Closed or the attachment is detaching (Closed only stops new admission).
        ProjectNative(view, player, owner);
        if (view.Closed || player is null || owner is null)
        {
            IsActive = false;
            StatusText = view.Closed ? "空降助手已关闭" : player is null ? "播放器未连接" : player.Attached ? "当前内容暂不支持标注" : "播放器正在断开";
            Drafts.Clear();
            HasDrafts = false;
            HasPreview = false;
            ProjectHistory(view, null);
            return;
        }

        IsActive = true;
        StatusText = player.Editing ? "正在标注：自动跳过已暂停" : "标注时会暂停当前视频的自动跳过";
        var media = owner.Media.Combined;
        var mine = view.Drafts.Where(d => d.Draft.MediaKey == media).Take(MaximumDrafts).ToList();
        var others = view.Drafts.Count(d => d.Draft.MediaKey != media);
        OtherDraftsText = others > 0 ? $"其他视频还有 {others} 条草稿" : string.Empty;
        HasDrafts = mine.Count > 0;
        IsCapturing = mine.Any(d => d.State == DraftState.Incomplete);
        CaptureHint = IsCapturing
            ? "已标记开始。播放到片段结尾时点击“标记结束”。"
            : "播放到片段开头时点击“标记开始”，到结尾时点击“标记结束”。";
        HasPreview = player.Preview is not null;
        PreviewText = player.Preview is { } preview
            ? $"已准备预览 {SponsorUiText.Time(preview.Start)} – {SponsorUiText.Time(preview.End)}，自动跳过已暂停"
            : string.Empty;

        SponsorKeyedSync.Sync(Drafts, mine.Select(d => d.LocalDraftId).ToList(), key => new SponsorDraftItem(key, this), (item, i) =>
        {
            var entry = mine[i];
            var segment = entry.Draft.Segment;
            item.Entry = entry;
            item.Title = SponsorUiText.CategoryTitle(segment.Category);
            item.CategoryColor = SponsorUiText.CategoryColor(segment.Category);
            item.RangeText = SponsorUiText.Range(segment);
            item.ActionText = SponsorUiText.ActionTitle(segment.ActionType);
            item.StateText = SponsorUiText.Draft(entry.State);
            item.IsEditable = entry.State is not (DraftState.Confirmed or DraftState.SubmissionInFlight);
            item.CanDelete = entry.LastSubmission is null && entry.State != DraftState.Confirmed;
            item.CanPreview = entry.State == DraftState.Ready && segment.ActionType is "skip" or "mute";
            item.IsPreviewing = player.Preview is { } pv && pv.Revision == entry.Draft.Revision;
            ApplyPreviewItem(item);
            item.CanRetry = entry.State is DraftState.Rejected or DraftState.Unknown or DraftState.AcceptedUnconfirmed;
            item.NeedsRiskAcknowledgement = entry.State is DraftState.Unknown or DraftState.AcceptedUnconfirmed;
            item.IsLockedCategory = _locked.Contains(segment.Category);
            if (!item.IsEditable)
            {
                item.IsEditing = false;
            }
        });
        CanPlayPreparedPreview = Drafts.Any(d => d.CanPlayPreview);
        ProjectContribute(view, mine);
        ProjectHistory(view, owner);
    }

    private void ProjectHistory(SponsorApplicationView view, PlaybackOwner? owner)
    {
        var items = view.Mutations.IsDefaultOrEmpty ? [] : view.Mutations
            .Where(m => owner is null || m.Media == owner.Media)
            .Reverse().Take(MaximumHistory).ToList();
        SponsorKeyedSync.Sync(History, items.Select(m => m.Id).ToList(), key => new SponsorHistoryItem(items.First(m => m.Id == key)), (_, _) => { });
        HasHistory = History.Count > 0;
    }

    private void Feedback(string text)
    {
        FeedbackText = text;
        HasFeedback = !string.IsNullOrEmpty(text);
    }

    [RelayCommand]
    private void DismissFeedback() => HasFeedback = false;

    partial void OnCaptureCategoryIndexChanged(int value) => UpdateCaptureActions();

    private void UpdateCaptureActions()
    {
        var category = CaptureCategoryIndex >= 0 && CaptureCategoryIndex < CaptureCategories.Count ? CaptureCategories[CaptureCategoryIndex] : null;
        _captureActionIds = category?.AllowedActions.Where(a => a is "skip" or "mute").ToList() ?? [];
        CaptureActionLabels = _captureActionIds.Select(SponsorUiText.ActionTitle).ToList();
        CaptureActionIndex = _captureActionIds.Count > 0 ? 0 : -1;
    }

    private Task<ApplicationResult?> EditCoreAsync(EditorCommand command, string? successText = null)
        => SponsorUiWork.Track(this, EditCoreWorkAsync(command, successText));

    private async Task<ApplicationResult?> EditCoreWorkAsync(EditorCommand command, string? successText = null)
    {
        var epoch = _epoch;
        var owner = _owner;
        if (_app is not { } app || owner is null)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var result = await SponsorTickets.RunAsync(app, _attachment, owner, ticket => app.EditAsync(ticket, command));
            if (epoch != _epoch || owner != _owner)
            {
                return null;
            }

            if (result.Status == ApplicationStatus.Success)
            {
                Feedback(successText ?? string.Empty);
            }
            else
            {
                Feedback(SponsorUiText.Result(result));
            }

            return result;
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

    [RelayCommand]
    private Task StartCaptureAsync()
    {
        var category = CaptureCategoryIndex >= 0 && CaptureCategoryIndex < CaptureCategories.Count ? CaptureCategories[CaptureCategoryIndex].Id : null;
        var action = CaptureActionIndex >= 0 && CaptureActionIndex < _captureActionIds.Count ? _captureActionIds[CaptureActionIndex] : null;
        return category is null || action is null
            ? Task.CompletedTask
            : EditCoreAsync(new EditorCommand(EditorAction.StartCapture, Category: category, ActionType: action));
    }

    [RelayCommand]
    private Task FinishCaptureAsync() => EditCoreAsync(new EditorCommand(EditorAction.FinishCapture), "已添加草稿");

    [RelayCommand]
    private Task CancelCaptureAsync() => EditCoreAsync(new EditorCommand(EditorAction.CancelCapture));

    [RelayCommand]
    private Task MarkPointAsync() => EditCoreAsync(new EditorCommand(EditorAction.Point), "已在当前位置添加精彩时刻");

    [RelayCommand]
    private Task MarkFullAsync()
    {
        var category = FullCategoryIndex >= 0 && FullCategoryIndex < FullCategories.Count ? FullCategories[FullCategoryIndex].Id : null;
        return category is null
            ? Task.CompletedTask
            : EditCoreAsync(new EditorCommand(EditorAction.FullLabel, Category: category, ActionType: "full"), "已添加整片标签");
    }

    [RelayCommand]
    private Task UndoAsync() => EditCoreAsync(new EditorCommand(EditorAction.Undo), "已撤销");

    [RelayCommand]
    private Task EndPreviewAsync() => EditCoreAsync(new EditorCommand(EditorAction.EndPreview), "已结束标注，自动跳过恢复");

    [RelayCommand]
    private Task SaveDraftsAsync()
        => SponsorUiWork.Track(this, SaveDraftsWorkAsync());

    private async Task SaveDraftsWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app)
        {
            return;
        }

        try
        {
            // Explicit local-persistence retry only; never sends anything.
            var result = await app.SaveDraftsAsync();
            if (epoch == _epoch)
            {
                Feedback(result.Status == ApplicationStatus.Success ? "草稿已保存" : SponsorUiText.Result(result));
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback("草稿未能保存");
            }
        }
    }

    internal Task SaveEditAsync(SponsorDraftItem item)
        => SponsorUiWork.Track(this, SaveEditWorkAsync(item));

    private async Task SaveEditWorkAsync(SponsorDraftItem item)
    {
        if (item.EditBaseRevision is not { } revision || item.SelectedEditCategory is not { } category || item.SelectedEditAction is not { } action)
        {
            return;
        }

        double? start = action == "full" || double.IsNaN(item.EditStart) ? null : item.EditStart;
        double? end = action is "skip" or "mute" && !double.IsNaN(item.EditEnd) ? item.EditEnd : null;
        var result = await EditCoreAsync(new EditorCommand(EditorAction.Edit, new DraftSelection(item.Key, revision), category, action, start, end), "已保存修改");
        if (result?.Status == ApplicationStatus.Success)
        {
            item.IsEditing = false;
        }
    }

    internal Task DeleteAsync(SponsorDraftItem item)
        => item.Entry is { } entry
            ? EditCoreAsync(new EditorCommand(EditorAction.Delete, new DraftSelection(item.Key, entry.Draft.Revision)), "已删除，可点击“撤销”恢复")
            : Task.CompletedTask;

    internal Task PreparePreviewAsync(SponsorDraftItem item)
        => item.Entry is { } entry
            ? EditCoreAsync(new EditorCommand(EditorAction.PreparePreview, new DraftSelection(item.Key, entry.Draft.Revision)), "预览区间已准备；自动跳过已暂停，可点击“播放预览”")
            : Task.CompletedTask;

    internal void SetLockedCategories(ImmutableHashSet<string> locked)
    {
        _locked = locked;
        foreach (var item in Drafts)
        {
            item.IsLockedCategory = item.Entry is { } entry && locked.Contains(entry.Draft.Segment.Category);
        }
    }
}
