// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 本机持久数据：UP 主白名单、过滤偏好、统计与操作记录。
/// <para>
/// 所有显示都来自门面的不可变快照（<c>Snapshot.Auxiliary</c>、<c>StoreVersion</c>、<c>StatisticsDirty</c> 等）。
/// 每次写入都在用户点按时读取当时的 <c>Snapshot.Revision</c>；遇到“已过期”只提示并提供重试按钮，
/// 不自动重放。重试同样在点按时重新读取修订号。这些写入不会改动未保存的设置表单或正在进行的标注。
/// </para>
/// <para>
/// 操作记录只读：不提供重发按钮，也不会被解析成可发送的请求。“写入本机”只保存已收到的结果。
/// </para>
/// </summary>
public sealed partial class SponsorSettingsViewModel
{
    private const int MaximumJournalRows = 50;
    private (string Uid, bool Add)? _failedWhitelist;
    private bool _projectingFilter;
    private ImmutableArray<string> _shownWhitelist = [];
    private (string Key, string Title, string Detail, string State, bool Attention)[] _shownJournal = [];

    // ---------- 共用 ----------

    [ObservableProperty]
    private bool _isDurableReady;

    [ObservableProperty]
    private string _durableStateText = string.Empty;

    // ---------- 白名单 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddWhitelist))]
    [NotifyPropertyChangedFor(nameof(WhitelistInputHint))]
    private string _whitelistInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddWhitelist))]
    private bool _isWhitelistBusy;

    [ObservableProperty]
    private string _whitelistCountText = string.Empty;

    [ObservableProperty]
    private bool _hasWhitelist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddWhitelist))]
    private bool _isWhitelistFull;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhitelistFeedbackText))]
    private string _whitelistStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhitelistFeedbackText))]
    private bool _hasWhitelistError;

    // ---------- 过滤偏好 ----------

    [ObservableProperty]
    private bool _isContentFilterEnabled;

    [ObservableProperty]
    private bool _isDanmakuFilterEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveFilters))]
    private bool _isFilterDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveFilters))]
    private bool _isFilterBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterFeedbackText))]
    private bool _hasFilterError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterFeedbackText))]
    private string _filterStatusText = string.Empty;

    [ObservableProperty]
    private string _filterSummaryText = string.Empty;

    // ---------- 统计 ----------

    [ObservableProperty]
    private string _persistedStatsText = string.Empty;

    [ObservableProperty]
    private bool _isStatisticsDirty;

    [ObservableProperty]
    private bool _isStatisticsSaving;

    [ObservableProperty]
    private bool _hasStatisticsError;

    [ObservableProperty]
    private string _statisticsErrorText = string.Empty;

    [ObservableProperty]
    private bool _isStatisticsCapacityReached;

    // ---------- 操作记录 ----------

    [ObservableProperty]
    private string _journalSummaryText = string.Empty;

    [ObservableProperty]
    private bool _hasJournal;

    [ObservableProperty]
    private bool _hasUnsavedOutcomes;

    [ObservableProperty]
    private string _unsavedOutcomesText = string.Empty;

    [ObservableProperty]
    private bool _isJournalSaving;

    [ObservableProperty]
    private bool _hasJournalError;

    [ObservableProperty]
    private string _journalErrorText = string.Empty;

    [ObservableProperty]
    private bool _hasJournalWarning;

    [ObservableProperty]
    private string _journalWarningText = string.Empty;

    /// <summary>白名单（新添加的在前）.</summary>
    public ObservableCollection<SponsorWhitelistItem> Whitelist { get; } = [];

    /// <summary>视频列表过滤的分类.</summary>
    public ObservableCollection<SponsorFilterCategoryItem> ContentFilterCategories { get; } = [];

    /// <summary>弹幕过滤的分类.</summary>
    public ObservableCollection<SponsorFilterCategoryItem> DanmakuFilterCategories { get; } = [];

    /// <summary>最近的操作记录（只读，最多显示 50 条，新的在前）.</summary>
    public ObservableCollection<SponsorJournalItem> JournalItems { get; } = [];

    /// <summary>非错误的白名单反馈（错误显示在提示条中）.</summary>
    public string WhitelistFeedbackText => HasWhitelistError ? string.Empty : WhitelistStatusText;

    /// <summary>非错误的过滤偏好反馈；无反馈时说明需要手动保存.</summary>
    public string FilterFeedbackText => HasFilterError || string.IsNullOrEmpty(FilterStatusText)
        ? "修改后点“保存”才会写入本机。"
        : FilterStatusText;

    /// <summary>是否可以保存过滤偏好.</summary>
    public bool CanSaveFilters => IsDurableReady && IsFilterDirty && !IsFilterBusy;

    /// <summary>白名单上限说明.</summary>
    public string WhitelistLimitText { get; } = $"最多 {AuxiliaryValidation.MaximumWhitelist.ToString("N0", CultureInfo.CurrentCulture)} 个";

    /// <summary>输入框下方的提示.</summary>
    public string WhitelistInputHint
    {
        get
        {
            var text = WhitelistInput.Trim();
            if (text.Length == 0)
            {
                return "填写 UP 主空间网址中的数字 UID，不是 BV 号或昵称。";
            }

            if (!SponsorUpUid.IsValid(text))
            {
                return "UID 只能是不以 0 开头的数字（最多 20 位）。";
            }

            return _shownWhitelist.Contains(text, StringComparer.Ordinal) ? "这个 UID 已在白名单中。" : "只检查格式，不会联网核对这个 UID。";
        }
    }

    /// <summary>是否可以添加当前输入.</summary>
    public bool CanAddWhitelist
    {
        get
        {
            var text = WhitelistInput.Trim();
            return IsDurableReady && !IsWhitelistBusy && !IsWhitelistFull && SponsorUpUid.IsValid(text)
                && !_shownWhitelist.Contains(text, StringComparer.Ordinal);
        }
    }

    partial void OnIsDurableReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddWhitelist));
        OnPropertyChanged(nameof(CanSaveFilters));
    }

    private void InitializeDurable()
    {
        foreach (var category in SponsorRules.Categories)
        {
            ContentFilterCategories.Add(new SponsorFilterCategoryItem(category, MarkFilterDirty));
            DanmakuFilterCategories.Add(new SponsorFilterCategoryItem(category, MarkFilterDirty));
        }
    }

    private void ResetDurable()
    {
        _failedWhitelist = null;
        _shownWhitelist = [];
        _shownJournal = [];
        Whitelist.Clear();
        JournalItems.Clear();
        WhitelistInput = string.Empty;
        IsWhitelistBusy = false;
        HasWhitelistError = false;
        WhitelistStatusText = string.Empty;
        IsFilterDirty = false;
        IsFilterBusy = false;
        HasFilterError = false;
        FilterStatusText = string.Empty;
        IsStatisticsSaving = false;
        HasStatisticsError = false;
        IsJournalSaving = false;
        HasJournalError = false;
        IsDurableReady = false;
    }

    /// <summary>从最新快照投影本机持久数据。不会覆盖用户尚未保存的过滤草稿或白名单输入。</summary>
    private void ApplyDurable(SponsorApplicationView view)
    {
        var auxiliary = view.Auxiliary;
        IsDurableReady = !view.Closed && view.StoreVersion is not null;
        DurableStateText = view.Closed ? "空降助手已关闭"
            : view.StoreVersion is null ? "本机存储尚未就绪，白名单、过滤偏好和统计暂时无法保存。" : string.Empty;

        ApplyWhitelist(auxiliary.WhitelistedUpUids.IsDefault ? [] : auxiliary.WhitelistedUpUids);
        ApplyFilters(auxiliary.Filters);
        ApplyDurableStatistics(view, auxiliary.Statistics);
        ApplyJournal(view, auxiliary.Journal.IsDefault ? [] : auxiliary.Journal,
            view.UnsavedMutationOutcomes.IsDefault ? [] : view.UnsavedMutationOutcomes);
    }

    // ---------- 白名单 ----------

    private void ApplyWhitelist(ImmutableArray<string> persisted)
    {
        IsWhitelistFull = persisted.Length >= AuxiliaryValidation.MaximumWhitelist;
        WhitelistCountText = persisted.Length == 0
            ? "白名单为空"
            : $"已保存 {persisted.Length.ToString("N0", CultureInfo.CurrentCulture)} 个 UP 主" + (IsWhitelistFull ? "（已满）" : string.Empty);
        HasWhitelist = persisted.Length > 0;
        if (persisted.SequenceEqual(_shownWhitelist, StringComparer.Ordinal))
        {
            return;
        }

        _shownWhitelist = persisted;
        var existing = Whitelist.ToDictionary(i => i.Uid, StringComparer.Ordinal);
        Whitelist.Clear();
        for (var i = persisted.Length - 1; i >= 0; i--)
        {
            var uid = persisted[i];
            Whitelist.Add(existing.TryGetValue(uid, out var item) ? item : new SponsorWhitelistItem(uid, ShowRemoveConfirm, RemoveWhitelistItem));
        }

        OnPropertyChanged(nameof(CanAddWhitelist));
        OnPropertyChanged(nameof(WhitelistInputHint));
    }

    private void ShowRemoveConfirm(SponsorWhitelistItem target)
    {
        // 一次只展开一条确认.
        foreach (var item in Whitelist)
        {
            item.IsConfirmVisible = ReferenceEquals(item, target) && IsDurableReady && !IsWhitelistBusy;
        }
    }

    private void RemoveWhitelistItem(SponsorWhitelistItem item) => _ = RunWhitelistAsync(item.Uid, add: false);

    [RelayCommand]
    private Task AddWhitelistAsync()
    {
        var uid = WhitelistInput.Trim();
        return CanAddWhitelist ? RunWhitelistAsync(uid, add: true) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RetryWhitelistAsync()
        => _failedWhitelist is { } failed && !IsWhitelistBusy ? RunWhitelistAsync(failed.Uid, failed.Add) : Task.CompletedTask;

    [RelayCommand]
    private void DismissWhitelistError()
    {
        _failedWhitelist = null;
        HasWhitelistError = false;
        WhitelistStatusText = string.Empty;
    }

    private Task RunWhitelistAsync(string uid, bool add)
        => SponsorUiWork.Track(this, RunWhitelistWorkAsync(uid, add));

    private async Task RunWhitelistWorkAsync(string uid, bool add)
    {
        var epoch = _epoch;
        if (_app is not { } app || IsWhitelistBusy || !IsDurableReady)
        {
            return;
        }

        IsWhitelistBusy = true;
        HasWhitelistError = false;
        WhitelistStatusText = add ? "正在添加…" : "正在移除…";
        try
        {
            // 修订号在用户操作时读取；过期不自动重放.
            var result = await app.SetWhitelistedUpAsync(app.Snapshot.Revision, uid, add);
            if (epoch != _epoch)
            {
                return;
            }

            if (result.Status == ApplicationStatus.Success)
            {
                _failedWhitelist = null;
                WhitelistStatusText = add ? $"已添加 {uid}" : $"已移除 {uid}";
                if (add && WhitelistInput.Trim() == uid)
                {
                    WhitelistInput = string.Empty;
                }
            }
            else
            {
                _failedWhitelist = (uid, add);
                HasWhitelistError = true;
                WhitelistStatusText = (add ? "未添加：" : "未移除：") + DurableFailureText(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                _failedWhitelist = (uid, add);
                HasWhitelistError = true;
                WhitelistStatusText = add ? "未添加，请稍后重试" : "未移除，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsWhitelistBusy = false;
            }
        }
    }

    // ---------- 过滤偏好 ----------

    private void MarkFilterDirty()
    {
        if (!_projectingFilter && _app is not null)
        {
            IsFilterDirty = true;
            UpdateFilterSummary();
        }
    }

    partial void OnIsContentFilterEnabledChanged(bool value) => MarkFilterDirty();

    partial void OnIsDanmakuFilterEnabledChanged(bool value) => MarkFilterDirty();

    private void ApplyFilters(DurableFilterPreferences committed)
    {
        // 未保存的草稿或正在保存时不覆盖用户的选择.
        if (IsFilterDirty || IsFilterBusy)
        {
            return;
        }

        _projectingFilter = true;
        try
        {
            IsContentFilterEnabled = committed.ContentEnabled;
            IsDanmakuFilterEnabled = committed.DanmakuEnabled;
            var content = committed.ContentCategories.IsDefault ? [] : committed.ContentCategories;
            var danmaku = committed.DanmakuCategories.IsDefault ? [] : committed.DanmakuCategories;
            foreach (var item in ContentFilterCategories)
            {
                item.Project(content.Contains(item.Id, StringComparer.Ordinal));
            }

            foreach (var item in DanmakuFilterCategories)
            {
                item.Project(danmaku.Contains(item.Id, StringComparer.Ordinal));
            }
        }
        finally
        {
            _projectingFilter = false;
        }

        UpdateFilterSummary();
    }

    private void UpdateFilterSummary()
    {
        static string Part(string name, bool enabled, IEnumerable<SponsorFilterCategoryItem> items)
        {
            var count = items.Count(i => i.IsChecked);
            return !enabled ? name + "：关" : count == 0 ? name + "：开，但未选择分类" : $"{name}：开，{count} 个分类";
        }

        FilterSummaryText = Part("视频列表", IsContentFilterEnabled, ContentFilterCategories) + "；"
            + Part("弹幕", IsDanmakuFilterEnabled, DanmakuFilterCategories)
            + (IsFilterDirty ? "（未保存）" : string.Empty);
    }

    private DurableFilterPreferences BuildFilterDraft()
    {
        static ImmutableArray<string> Checked(IEnumerable<SponsorFilterCategoryItem> items)
            => items.Where(i => i.IsChecked).Select(i => i.Id).ToImmutableArray();

        return new DurableFilterPreferences(IsContentFilterEnabled, Checked(ContentFilterCategories),
            IsDanmakuFilterEnabled, Checked(DanmakuFilterCategories));
    }

    [RelayCommand]
    private Task SaveFiltersAsync()
        => SponsorUiWork.Track(this, SaveFiltersWorkAsync());

    private async Task SaveFiltersWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app || IsFilterBusy || !IsDurableReady)
        {
            return;
        }

        var draft = BuildFilterDraft();
        IsFilterBusy = true;
        HasFilterError = false;
        FilterStatusText = "正在保存…";
        try
        {
            var result = await app.SaveFilterPreferencesAsync(app.Snapshot.Revision, draft);
            if (epoch != _epoch)
            {
                return;
            }

            if (result.Status == ApplicationStatus.Success)
            {
                IsFilterDirty = false;
                FilterStatusText = "已保存过滤偏好";
            }
            else
            {
                HasFilterError = true;
                FilterStatusText = "过滤偏好未保存：" + DurableFailureText(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                HasFilterError = true;
                FilterStatusText = "过滤偏好未保存，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsFilterBusy = false;
                if (!IsFilterDirty && _feed?.Current is { } view)
                {
                    ApplyFilters(view.Auxiliary.Filters);
                }
                else
                {
                    UpdateFilterSummary();
                }
            }
        }
    }

    [RelayCommand]
    private void DiscardFilters()
    {
        if (IsFilterBusy)
        {
            return;
        }

        IsFilterDirty = false;
        HasFilterError = false;
        FilterStatusText = string.Empty;
        ApplyFilters(_feed?.Current is { } view ? view.Auxiliary.Filters : DurableFilterPreferences.Off);
    }

    // ---------- 统计 ----------

    private void ApplyDurableStatistics(SponsorApplicationView view, DurableStatistics persisted)
    {
        IsStatisticsDirty = view.StatisticsDirty;
        IsStatisticsCapacityReached = persisted.CapacityReached || view.Statistics.CapacityReached;
        if (view.StoreVersion is null)
        {
            PersistedStatsText = "本机存储尚未就绪，统计只保留到本次运行结束。";
        }
        else if (view.StatisticsDirty)
        {
            PersistedStatsText = "已写入本机的部分：" + (persisted.ConfirmedSeekCount > 0
                ? $"跳过 {persisted.ConfirmedSeekCount.ToString("N0", CultureInfo.CurrentCulture)} 次，共 {Duration(persisted.ConfirmedSeekMediaSeconds)}"
                : "还没有") + "。较新的统计尚未写入，关闭应用前请保存。";
        }
        else
        {
            PersistedStatsText = "以上统计已保存在本机。";
        }

        var failure = view.StatisticsPersistence is { } status && status != PersistenceStatus.Success;
        if (!IsStatisticsSaving)
        {
            HasStatisticsError = failure;
            StatisticsErrorText = failure ? "上次保存统计失败：" + SponsorUiText.Persistence(view.StatisticsPersistence!.Value) : string.Empty;
        }

        StatsNoteText = IsStatisticsCapacityReached
            ? $"统计记录已满（最多 {AuxiliaryValidation.MaximumWatermarks.ToString("N0", CultureInfo.CurrentCulture)} 条），之后的跳过不再计入，旧记录不会被自动删除。"
            : "只统计播放器实际执行并确认的跳转和加速；提示和点击不计入。";
    }

    [RelayCommand]
    private Task SaveStatisticsAsync()
        => SponsorUiWork.Track(this, SaveStatisticsWorkAsync());

    private async Task SaveStatisticsWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app || IsStatisticsSaving || !IsDurableReady)
        {
            return;
        }

        IsStatisticsSaving = true;
        HasStatisticsError = false;
        try
        {
            var result = await app.SaveStatisticsAsync(app.Snapshot.Revision);
            if (epoch == _epoch && result.Status != ApplicationStatus.Success)
            {
                HasStatisticsError = true;
                StatisticsErrorText = "统计未保存：" + DurableFailureText(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                HasStatisticsError = true;
                StatisticsErrorText = "统计未保存，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsStatisticsSaving = false;
            }
        }
    }

    // ---------- 操作记录 ----------

    private void ApplyJournal(SponsorApplicationView view, ImmutableArray<DurableJournalEntry> journal, ImmutableArray<DurableJournalEntry> unsaved)
    {
        var unsavedById = new Dictionary<string, DurableJournalEntry>(StringComparer.Ordinal);
        foreach (var entry in unsaved)
        {
            unsavedById[entry.Request.Id] = entry;
        }

        var uncertain = journal.Count(e => !unsavedById.ContainsKey(e.Request.Id)
            && (e.Phase == DurableJournalPhase.Prepared || e.Outcome?.Status == WriteStatus.Unknown));
        HasJournal = journal.Length > 0;
        JournalSummaryText = journal.Length == 0
            ? "还没有投票、改分类、搬运关联等操作记录。"
            : $"本机共 {journal.Length.ToString("N0", CultureInfo.CurrentCulture)} 条记录（上限 {AuxiliaryValidation.MaximumJournal}）"
                + (uncertain > 0 ? $"，其中 {uncertain} 条结果不确定" : string.Empty) + "。记录不会自动删除。";

        HasUnsavedOutcomes = unsaved.Length > 0;
        UnsavedOutcomesText = unsaved.Length > 0
            ? $"有 {unsaved.Length} 条操作已收到结果，但还没写入本机。写入只保存结果，不会重新发送请求。"
            : string.Empty;

        var warnings = new List<string>();
        if (view.JournalCapacityReached || journal.Length >= AuxiliaryValidation.MaximumJournal)
        {
            warnings.Add($"操作记录已满（{AuxiliaryValidation.MaximumJournal} 条）：投票、改分类、搬运关联和设置用户名暂停使用。");
        }

        if (view.AuxiliaryCapacityReached)
        {
            warnings.Add($"本机附加数据已达到大小上限（{AuxiliaryValidation.MaximumBytes / 1024 / 1024} MiB），新的白名单、过滤偏好、统计或记录无法写入。");
        }

        if (view.AuxiliaryPersistence is { } status && status is not (PersistenceStatus.Success or PersistenceStatus.Missing))
        {
            warnings.Add("上次写入本机失败：" + SponsorUiText.Persistence(status));
        }

        HasJournalWarning = warnings.Count > 0;
        JournalWarningText = string.Join("\n", warnings);

        var rows = new List<(string Key, string Title, string Detail, string State, bool Attention)>(MaximumJournalRows);
        for (var i = journal.Length - 1; i >= 0 && rows.Count < MaximumJournalRows; i--)
        {
            rows.Add(Describe(journal[i], unsavedById.GetValueOrDefault(journal[i].Request.Id)));
        }

        var next = rows.ToArray();
        if (next.SequenceEqual(_shownJournal))
        {
            return;
        }

        _shownJournal = next;
        JournalItems.Clear();
        foreach (var row in next)
        {
            JournalItems.Add(new SponsorJournalItem(row.Key, row.Title, row.Detail, row.State, row.Attention));
        }
    }

    private static (string Key, string Title, string Detail, string State, bool Attention) Describe(DurableJournalEntry entry, DurableJournalEntry? unsaved)
    {
        var request = entry.Request;
        var segment = request.OriginalSegment;
        var category = segment is null ? null : SponsorUiText.CategoryTitle(segment.Category);
        var range = segment is null ? null : SponsorUiText.Range(segment);
        var detail = request.Operation switch
        {
            ManualOperation.Vote => (request.Vote == SponsorVote.Up ? "赞同" : "反对") + Join(category, range),
            ManualOperation.ChangeCategory => "改为“" + SponsorUiText.CategoryTitle(request.Text) + "”" + Join(category is null ? null : "原为“" + category + "”", range),
            ManualOperation.ReportViewed => (category ?? "片段") + Join(range),
            ManualOperation.VotePort => request.Vote == SponsorVote.Up ? "赞同关联" : "反对关联",
            ManualOperation.BindPort or ManualOperation.UpdatePort => "YouTube 搬运关联",
            _ => string.Empty,
        };

        var outcome = unsaved?.Outcome ?? entry.Outcome;
        (string State, bool Attention) state = unsaved?.Outcome is { } pending
            ? (SponsorUiText.Write(pending.Status) + "；结果还没写入本机", true)
            : entry.Phase == DurableJournalPhase.Prepared
                ? ("发送前已记录，但没有记录到结果；不确定是否已发出，不会自动重发", true)
                : outcome is null ? ("结果未知，不会自动重发", true)
                : (SponsorUiText.Write(outcome.Status), outcome.Status == WriteStatus.Unknown);
        return (request.Id, SponsorUiText.Operation(request.Operation), detail, state.State, state.Attention);
    }

    private static string Join(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? string.Empty : " · " + text;
    }

    [RelayCommand]
    private Task SavePendingMutationsAsync()
        => SponsorUiWork.Track(this, SavePendingMutationsWorkAsync());

    private async Task SavePendingMutationsWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app || IsJournalSaving || !HasUnsavedOutcomes)
        {
            return;
        }

        IsJournalSaving = true;
        HasJournalError = false;
        try
        {
            // 只做本机写入；不会构造或发送任何请求.
            var result = await app.SavePendingMutationsAsync(app.Snapshot.Revision);
            if (epoch == _epoch && result.Status != ApplicationStatus.Success)
            {
                HasJournalError = true;
                JournalErrorText = "未写入本机：" + DurableFailureText(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                HasJournalError = true;
                JournalErrorText = "未写入本机，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsJournalSaving = false;
            }
        }
    }

    /// <summary>本机写入失败的友好说明；“已过期”明确要求用户再点一次，不自动重放.</summary>
    private static string DurableFailureText(ApplicationResult result)
        => result.Status == ApplicationStatus.Stale && result.Reason is null
            ? "刚好有其他更新（例如正在播放的视频），这次没有保存。请再点一次重试。"
            : SponsorUiText.Result(result);
}
