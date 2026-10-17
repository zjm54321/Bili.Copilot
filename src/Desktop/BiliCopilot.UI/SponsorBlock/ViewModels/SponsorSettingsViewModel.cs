// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 空降助手设置视图模型。只暴露界面原语；门面保存在私有字段中，由宿主通过 <see cref="Bind"/> 注入。
/// 修改以“增量”形式排队，基于门面最新快照逐条重放后提交，失败时保留在界面上并提供“重试/放弃”。
/// </summary>
public sealed partial class SponsorSettingsViewModel : ObservableObject
{
    private const int RebaseAttempts = 3;
    private readonly List<Func<SponsorLocalSettings, SponsorLocalSettings>> _pending = [];
    private SponsorApplicationCoordinator? _app;
    private SponsorViewFeed? _feed;
    private DispatcherQueue? _queue;
    private ISponsorBackupFilePort? _files;
    private ImmutableAvailability _availability = ImmutableAvailability.Empty;
    private long _epoch;
    private bool _projecting;
    private bool _saving;
    private byte[]? _importBytes;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _isPlaybackEnabled;

    [ObservableProperty]
    private bool _isSkippingDisabled;

    [ObservableProperty]
    private bool _isNetworkEnabled;

    [ObservableProperty]
    private bool _isViewedReportingEnabled;

    [ObservableProperty]
    private bool _includeExternalSegments;

    [ObservableProperty]
    private bool _includeDanmakuSegments;

    [ObservableProperty]
    private bool _includeMute;

    [ObservableProperty]
    private bool _includeFull;

    [ObservableProperty]
    private bool _autoSkipOnMusicVideos;

    [ObservableProperty]
    private bool _manualSkipOnFullVideo;

    [ObservableProperty]
    private bool _waitForChannelVerification;

    [ObservableProperty]
    private bool _skipOnSeekToSegment;

    [ObservableProperty]
    private bool _enableAutomaticDanmaku;

    [ObservableProperty]
    private double _minDuration;

    [ObservableProperty]
    private double _noticeLeadSeconds = double.NaN;

    [ObservableProperty]
    private int _strategyIndex;

    [ObservableProperty]
    private bool _isSpeedUp;

    [ObservableProperty]
    private double _speedRate = 2;

    [ObservableProperty]
    private int _fallbackIndex;

    [ObservableProperty]
    private string _localExclusionText = "没有在本机隐藏的片段";

    [ObservableProperty]
    private bool _hasLocalExclusions;

    [ObservableProperty]
    private bool _isClearExclusionsConfirmVisible;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _hasSaveError;

    [ObservableProperty]
    private string _saveStatusText = string.Empty;

    [ObservableProperty]
    private bool _hasLoadWarning;

    [ObservableProperty]
    private string _loadWarningText = string.Empty;

    [ObservableProperty]
    private string _whitelistNote = string.Empty;

    [ObservableProperty]
    private string _filterNote = string.Empty;

    [ObservableProperty]
    private string _usernameNote = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="SponsorSettingsViewModel"/> class.</summary>
    public SponsorSettingsViewModel()
    {
        Categories = new(SponsorApplicationCoordinator.CategoryChoices.Select(c => new SponsorCategoryPolicyItem(c, OnCategoryChanged)));
        InitializeDurable();
    }

    /// <summary>11 个分类（来自唯一分类表）.</summary>
    public ObservableCollection<SponsorCategoryPolicyItem> Categories { get; }

    /// <summary>处理方式选项.</summary>
    public IReadOnlyList<string> StrategyLabels => SponsorUiText.StrategyLabels;

    /// <summary>加速回退选项.</summary>
    public IReadOnlyList<string> FallbackLabels => SponsorUiText.FallbackLabels;

    /// <summary>
    /// 在界面线程上绑定到唯一的应用门面。<paramref name="files"/> 为宿主提供的文件选择通道，可为空。
    /// 不触发网络；只读取本机身份视图（不会创建）。
    /// </summary>
    internal void Bind(SponsorApplicationCoordinator app, ISponsorBackupFilePort? files = null)
    {
        var queue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("SponsorSettingsBindRequiresUiThread");
        Unbind();
        _epoch++;
        _app = app;
        _queue = queue;
        _files = files;
        _availability = new(app.Availability);
        CanUseFiles = files is not null;
        // Only binding-time constant facts are read from Availability; durable state is projected from each snapshot.
        WhitelistNote = "白名单中 UP 主的视频不会自动跳过或加速（仍会提示）。只按 UP 主的数字 UID 生效："
            + "播放页确认了视频的 UP 主 UID 后才会应用；把 UID 加入列表并不代表某个视频属于这位 UP 主。";
        FilterNote = (_availability.IsAvailable(SponsorFeature.ContentProjection)
            ? "视频列表：按已确认的分类标注过滤。"
            : "视频列表过滤尚未接入，已保存的偏好暂不生效。")
            + " " + (_availability.IsAvailable(SponsorFeature.DanmakuProjection)
                ? "弹幕：保存后按当前视频已读取的分类区间过滤，未知数据保持显示；关闭后恢复，不会额外联网。"
                : "弹幕过滤尚未接入，已保存的偏好暂不生效。");
        UsernameNote = "在播放页的“标注”面板中设置。";
        BindContent();
        _feed = new SponsorViewFeed(app, queue, Apply);
        _feed.Start();
        _ = LoadIdentityAsync(); // Tracked: LoadIdentityAsync registers its actual task with the runtime.
    }

    /// <summary>解除绑定。不会关闭或释放门面；未保存的修改被丢弃（界面会随之消失）。</summary>
    internal void Unbind()
    {
        _epoch++;
        _feed?.Stop();
        _feed = null;
        _app = null;
        _files = null;
        _queue = null;
        _pending.Clear();
        _saving = false;
        _importBytes = null;
        IsReady = false;
        IsSaving = false;
        HasPendingChanges = false;
        HasSaveError = false;
        IsImportConfirmVisible = false;
        IsCreateIdentityConfirmVisible = false;
        IsClearExclusionsConfirmVisible = false;
        ResetDurable();
        ResetContent();
    }

    private void Apply(SponsorApplicationView view)
    {
        IsReady = !view.Closed && view.Persistence is not null;
        HasLoadWarning = view.Persistence is { } p && p is not (PersistenceStatus.Success or PersistenceStatus.Missing) && !HasSaveError;
        LoadWarningText = view.Persistence is { } status ? "本机设置状态：" + SponsorUiText.Persistence(status) : string.Empty;
        if (view.Closed)
        {
            LoadWarningText = "空降助手已关闭";
            HasLoadWarning = true;
        }

        Project(view.Settings);
        ApplyStatistics(view.Statistics);
        ApplyDurable(view);
        ApplyContent(view);
    }

    private void Reproject()
    {
        if (_feed?.Current is { } view)
        {
            Project(view.Settings);
        }
    }

    private void Project(SponsorLocalSettings committed)
    {
        SponsorLocalSettings shown;
        try
        {
            shown = _pending.Aggregate(committed, (s, delta) => delta(s));
        }
        catch (Exception)
        {
            shown = committed;
        }

        _projecting = true;
        try
        {
            var policy = shown.Policy;
            IsPlaybackEnabled = policy.Enabled;
            IsSkippingDisabled = policy.DisableSkipping;
            IsNetworkEnabled = shown.NetworkEnabled;
            IsViewedReportingEnabled = shown.ViewedTelemetryEnabled;
            IncludeExternalSegments = shown.IncludeExternalSegments;
            IncludeDanmakuSegments = shown.IncludeDanmakuSegments;
            IncludeMute = policy.IncludeMute;
            IncludeFull = policy.IncludeFull;
            AutoSkipOnMusicVideos = policy.AutoSkipOnMusicVideos;
            ManualSkipOnFullVideo = policy.ManualSkipOnFullVideo;
            WaitForChannelVerification = policy.WaitForChannelVerification;
            SkipOnSeekToSegment = policy.SkipOnSeekToSegment;
            EnableAutomaticDanmaku = policy.EnableAutomaticDanmaku;
            MinDuration = policy.MinDuration;
            NoticeLeadSeconds = policy.UpcomingMediaSeconds ?? double.NaN;
            StrategyIndex = policy.SkipStrategy == SponsorSkipStrategy.SpeedUp ? 1 : 0;
            IsSpeedUp = StrategyIndex == 1;
            SpeedRate = shown.Speed.ConfiguredRate;
            FallbackIndex = Math.Max(0, SponsorApplicationCoordinator.SpeedFallbackChoices.IndexOf(shown.Speed.Fallback));
            foreach (var item in Categories)
            {
                item.Project(policy.Categories.TryGetValue(item.Id, out var value) ? value : CategoryPolicy.Disabled);
            }

            var count = shown.Exclusions.IsDefault ? 0 : shown.Exclusions.Length;
            HasLocalExclusions = count > 0;
            LocalExclusionText = count > 0 ? $"已在本机隐藏或标记 {count} 个片段" : "没有在本机隐藏的片段";
        }
        finally
        {
            _projecting = false;
        }
    }

    private void Enqueue(Func<SponsorLocalSettings, SponsorLocalSettings> delta)
    {
        if (_projecting || _app is null)
        {
            return;
        }

        _pending.Add(delta);
        HasPendingChanges = true;
        if (!_saving)
        {
            _ = SaveLoopAsync();
        }
    }

    private void EnqueuePolicy(Func<PlaybackPolicy, PlaybackPolicy> change)
        => Enqueue(s => s with { Policy = change(s.Policy) });

    private void DeferReproject()
    {
        if (_queue is { } queue)
        {
            _ = queue.TryEnqueue(Reproject);
        }
    }

    private Task SaveLoopAsync()
        => SponsorUiWork.Track(this, SaveLoopWorkAsync());

    private async Task SaveLoopWorkAsync()
    {
        var epoch = _epoch;
        var app = _app;
        if (app is null)
        {
            return;
        }

        _saving = true;
        IsSaving = true;
        HasSaveError = false;
        SaveStatusText = "正在保存…";
        try
        {
            while (_pending.Count > 0 && epoch == _epoch)
            {
                var batch = _pending.ToArray();
                ApplicationResult? result = null;
                for (var attempt = 0; attempt < RebaseAttempts; attempt++)
                {
                    // Rebase the user's explicit edits on the newest committed settings. Not a network retry.
                    var snapshot = app.Snapshot;
                    var candidate = batch.Aggregate(snapshot.Settings, (s, delta) => delta(s));
                    result = await app.SaveSettingsAsync(snapshot.Revision, candidate);
                    if (epoch != _epoch)
                    {
                        return;
                    }

                    if (result.Status != ApplicationStatus.Stale)
                    {
                        break;
                    }
                }

                if (result?.Status == ApplicationStatus.Success)
                {
                    _pending.RemoveRange(0, batch.Length);
                    continue;
                }

                HasSaveError = true;
                SaveStatusText = "设置未能保存：" + (result is null ? "未知原因" : SponsorUiText.Result(result));
                break;
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                HasSaveError = true;
                SaveStatusText = "设置未能保存，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                _saving = false;
                IsSaving = false;
                HasPendingChanges = _pending.Count > 0;
                if (!HasSaveError)
                {
                    SaveStatusText = string.Empty;
                }

                Reproject();
            }
        }
    }

    [RelayCommand]
    private Task RetrySaveAsync()
        => SponsorUiWork.Track(this, RetrySaveWorkAsync());

    private async Task RetrySaveWorkAsync()
    {
        if (_pending.Count > 0 && !_saving)
        {
            await SaveLoopAsync();
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        if (_saving)
        {
            return;
        }

        _pending.Clear();
        HasPendingChanges = false;
        HasSaveError = false;
        SaveStatusText = string.Empty;
        Reproject();
    }

    [RelayCommand]
    private void RequestClearExclusions() => IsClearExclusionsConfirmVisible = HasLocalExclusions;

    [RelayCommand]
    private void CancelClearExclusions() => IsClearExclusionsConfirmVisible = false;

    [RelayCommand]
    private void ConfirmClearExclusions()
    {
        IsClearExclusionsConfirmVisible = false;
        Enqueue(s => s with { Exclusions = [] });
        Reproject();
    }

    private void OnCategoryChanged(SponsorCategoryPolicyItem item, CategoryPolicy policy)
        => EnqueuePolicy(p => p with { Categories = p.Categories.SetItem(item.Id, policy) });

    partial void OnIsPlaybackEnabledChanged(bool value) => EnqueuePolicy(p => p with { Enabled = value });

    partial void OnIsSkippingDisabledChanged(bool value) => EnqueuePolicy(p => p with { DisableSkipping = value });

    partial void OnIsNetworkEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefreshUserStats));
        Enqueue(s => s with { NetworkEnabled = value });
    }

    partial void OnIsViewedReportingEnabledChanged(bool value) => Enqueue(s => s with { ViewedTelemetryEnabled = value });

    partial void OnIncludeExternalSegmentsChanged(bool value) => Enqueue(s => s with { IncludeExternalSegments = value });

    partial void OnIncludeDanmakuSegmentsChanged(bool value) => Enqueue(s => s with { IncludeDanmakuSegments = value });

    partial void OnIncludeMuteChanged(bool value) => EnqueuePolicy(p => p with { IncludeMute = value });

    partial void OnIncludeFullChanged(bool value) => EnqueuePolicy(p => p with { IncludeFull = value });

    partial void OnAutoSkipOnMusicVideosChanged(bool value) => EnqueuePolicy(p => p with { AutoSkipOnMusicVideos = value });

    partial void OnManualSkipOnFullVideoChanged(bool value) => EnqueuePolicy(p => p with { ManualSkipOnFullVideo = value });

    partial void OnWaitForChannelVerificationChanged(bool value) => EnqueuePolicy(p => p with { WaitForChannelVerification = value });

    partial void OnSkipOnSeekToSegmentChanged(bool value) => EnqueuePolicy(p => p with { SkipOnSeekToSegment = value });

    partial void OnEnableAutomaticDanmakuChanged(bool value) => EnqueuePolicy(p => p with { EnableAutomaticDanmaku = value });

    partial void OnMinDurationChanged(double value)
    {
        if (_projecting)
        {
            return;
        }

        if (double.IsFinite(value) && value is >= 0 and <= 3600)
        {
            EnqueuePolicy(p => p with { MinDuration = value });
        }
        else
        {
            DeferReproject();
        }
    }

    partial void OnNoticeLeadSecondsChanged(double value)
    {
        if (_projecting)
        {
            return;
        }

        if (double.IsNaN(value))
        {
            EnqueuePolicy(p => p with { UpcomingMediaSeconds = null });
        }
        else if (double.IsFinite(value) && value is > 0 and <= 60)
        {
            EnqueuePolicy(p => p with { UpcomingMediaSeconds = value });
        }
        else
        {
            DeferReproject();
        }
    }

    partial void OnStrategyIndexChanged(int value)
    {
        IsSpeedUp = value == 1;
        if (value is 0 or 1)
        {
            EnqueuePolicy(p => p with { SkipStrategy = value == 1 ? SponsorSkipStrategy.SpeedUp : SponsorSkipStrategy.Seek });
        }
    }

    partial void OnSpeedRateChanged(double value)
    {
        if (_projecting)
        {
            return;
        }

        if (double.IsFinite(value) && value is >= 1.1 and <= 16)
        {
            Enqueue(s => s with { Speed = s.Speed with { ConfiguredRate = value } });
        }
        else
        {
            DeferReproject();
        }
    }

    partial void OnFallbackIndexChanged(int value)
    {
        var choices = SponsorApplicationCoordinator.SpeedFallbackChoices;
        if (value >= 0 && value < choices.Length)
        {
            var fallback = choices[value];
            Enqueue(s => s with { Speed = s.Speed with { Fallback = fallback } });
        }
    }

    /// <summary>Snapshot of typed capabilities, read once per binding.</summary>
    private sealed class ImmutableAvailability(IEnumerable<SponsorAvailability> items)
    {
        internal static readonly ImmutableAvailability Empty = new([]);
        private readonly Dictionary<SponsorFeature, SponsorAvailability> _items = items.ToDictionary(i => i.Feature);

        internal SponsorAvailability? Get(SponsorFeature feature) => _items.GetValueOrDefault(feature);

        internal bool IsAvailable(SponsorFeature feature) => _items.TryGetValue(feature, out var item) && item.Available;
    }
}
