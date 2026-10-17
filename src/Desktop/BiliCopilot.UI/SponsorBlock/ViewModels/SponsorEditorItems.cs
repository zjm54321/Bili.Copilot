// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>当前视频的一条本地草稿（Q4 实际条目）.</summary>
public sealed partial class SponsorDraftItem : ObservableObject, ISponsorKeyedItem
{
    private static readonly IReadOnlyList<string> CategoryIds = SponsorRules.Categories.Select(c => c.Id).ToList();
    private IReadOnlyList<string> _actionIds = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private Color _categoryColor;

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private string _actionText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private bool _canDelete;

    [ObservableProperty]
    private bool _canPreview;

    [ObservableProperty]
    private bool _isPreviewing;

    [ObservableProperty]
    private bool _canPlayPreview;

    [ObservableProperty]
    private string _playPreviewHint = string.Empty;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _needsRiskAcknowledgement;

    [ObservableProperty]
    private bool _isRiskAcknowledged;

    [ObservableProperty]
    private bool _isLockedCategory;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int _editCategoryIndex = -1;

    [ObservableProperty]
    private IReadOnlyList<string> _editActionLabels = [];

    [ObservableProperty]
    private int _editActionIndex = -1;

    [ObservableProperty]
    private double _editStart = double.NaN;

    [ObservableProperty]
    private double _editEnd = double.NaN;

    [ObservableProperty]
    private bool _isEditEndVisible = true;

    [ObservableProperty]
    private bool _isEditStartVisible = true;

    internal SponsorDraftItem(string key, SponsorEditorViewModel owner)
    {
        Key = key;
        BeginEditCommand = new RelayCommand(BeginEdit);
        CancelEditCommand = new RelayCommand(() => IsEditing = false);
        SaveEditCommand = new AsyncRelayCommand(() => owner.SaveEditAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this));
        PreviewCommand = new AsyncRelayCommand(() => owner.PreparePreviewAsync(this));
        PlayPreviewCommand = new AsyncRelayCommand(() => owner.PlayPreviewAsync(this));
        RetryCommand = new AsyncRelayCommand(() => owner.RetryAsync(this));
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>所有分类（编辑用）.</summary>
    public IReadOnlyList<string> EditCategoryLabels { get; } = SponsorRules.Categories.Select(c => SponsorUiText.CategoryTitle(c.Id)).ToList();

    /// <summary>开始编辑.</summary>
    public IRelayCommand BeginEditCommand { get; }

    /// <summary>取消编辑.</summary>
    public IRelayCommand CancelEditCommand { get; }

    /// <summary>保存修改.</summary>
    public IAsyncRelayCommand SaveEditCommand { get; }

    /// <summary>删除（可通过“撤销”恢复）.</summary>
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>准备预览.</summary>
    public IAsyncRelayCommand PreviewCommand { get; }

    /// <summary>在播放器中播放已准备好的区间（需播放器实际支持；结束时需显式“结束预览”或“停在这里”）.</summary>
    public IAsyncRelayCommand PlayPreviewCommand { get; }

    /// <summary>重新允许提交.</summary>
    public IAsyncRelayCommand RetryCommand { get; }

    internal DraftEntry? Entry { get; set; }

    /// <summary>开始编辑时看到的修订；保存时用它，草稿若已被改动则如实失败.</summary>
    internal string? EditBaseRevision { get; private set; }

    internal string? SelectedEditCategory => EditCategoryIndex >= 0 && EditCategoryIndex < CategoryIds.Count ? CategoryIds[EditCategoryIndex] : null;

    internal string? SelectedEditAction => EditActionIndex >= 0 && EditActionIndex < _actionIds.Count ? _actionIds[EditActionIndex] : null;

    private void BeginEdit()
    {
        if (Entry is not { } entry || !IsEditable)
        {
            return;
        }

        EditBaseRevision = entry.Draft.Revision;
        var segment = entry.Draft.Segment;
        EditCategoryIndex = CategoryIds.ToList().IndexOf(segment.Category);
        SetActions(segment.Category, segment.ActionType);
        EditStart = segment.Segment.IsDefaultOrEmpty ? double.NaN : segment.Segment[0];
        EditEnd = segment.Segment.Length == 2 ? segment.Segment[1] : double.NaN;
        IsEditing = true;
    }

    partial void OnEditCategoryIndexChanged(int value)
    {
        if (SelectedEditCategory is { } category)
        {
            SetActions(category, SelectedEditAction);
        }
    }

    partial void OnEditActionIndexChanged(int value)
    {
        var action = SelectedEditAction;
        IsEditStartVisible = action is not "full";
        IsEditEndVisible = action is "skip" or "mute";
    }

    private void SetActions(string category, string? preferred)
    {
        _actionIds = SponsorRules.Categories.FirstOrDefault(c => c.Id == category)?.AllowedActions.ToList() ?? [];
        EditActionLabels = _actionIds.Select(SponsorUiText.ActionTitle).ToList();
        var index = preferred is null ? -1 : _actionIds.ToList().IndexOf(preferred);
        EditActionIndex = index >= 0 ? index : _actionIds.Count > 0 ? 0 : -1;
        OnEditActionIndexChanged(EditActionIndex);
    }
}

/// <summary>最近的远程操作记录（来自门面的真实完成结果）.</summary>
public sealed class SponsorHistoryItem : ISponsorKeyedItem
{
    internal SponsorHistoryItem(MutationCompletion completion)
    {
        Key = completion.Id;
        OperationText = SponsorUiText.Operation(completion.Operation);
        var reason = SponsorUiText.Reason(completion.Reason, string.Empty);
        StatusText = SponsorUiText.Write(completion.Status) + (reason.Length > 0 ? " · " + reason : string.Empty);
        PersistedText = completion.Persisted ? string.Empty : "结果未能保存到本机";
        HasPersistWarning = !completion.Persisted;
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>操作.</summary>
    public string OperationText { get; }

    /// <summary>结果.</summary>
    public string StatusText { get; }

    /// <summary>本机保存提示.</summary>
    public string PersistedText { get; }

    /// <summary>是否未保存到本机.</summary>
    public bool HasPersistWarning { get; }
}
