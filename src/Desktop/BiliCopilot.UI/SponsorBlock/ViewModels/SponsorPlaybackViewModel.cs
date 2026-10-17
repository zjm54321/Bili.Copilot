// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 一个已附加播放器的空降助手视图模型：状态、提示、最近效果、恢复项、片段列表和时间轴标记。
/// 所有内容都来自门面发布的 N3 实际结果；成功提交请求只表示“已接纳”，效果以后续回执为准。
/// 由宿主在 <c>AttachAsync</c> 成功后用返回的附加 ID 调用 <see cref="Bind"/>；播放器回收前调用 <see cref="Unbind"/>。
/// </summary>
public sealed partial class SponsorPlaybackViewModel : ObservableObject
{
    private const int MaximumSegments = 64;
    private const int MaximumNotices = 8;
    private const int MaximumMarkers = 128;

    private SponsorApplicationCoordinator? _app;
    private SponsorViewFeed? _feed;
    private Guid _attachment;
    private long _epoch;
    private SponsorPlayerView? _player;
    private SponsorLocalSettings _settings = new();
    private PlaybackOwner? _owner;
    private AutoFetchIntent? _autoFetch;
    private Task? _fetchTask;
    private long _fetchEpoch;
    private long _dismissedAppliedId;
    private long _dismissedRateId;
    private ImmutableArray<SponsorAvailability> _availability = [];

    private sealed class AutoFetchIntent
    {
        internal bool Pending = true;
        internal bool AdmissionRefused;
        internal int Attempts;
        internal FetchReadiness? LastReadiness;
    }

    // Use a published ticket, never position or controller sequence as a readiness proxy.
    private sealed record FetchReadiness(PlaybackStamp Stamp, ApplicationStatus DeliveryStatus);

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _statusText = "空降助手未连接";

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private bool _canFetch;

    [ObservableProperty]
    private string _fetchButtonText = "获取片段";

    [ObservableProperty]
    private bool _hasFullLabel;

    [ObservableProperty]
    private string _fullLabelText = string.Empty;

    [ObservableProperty]
    private Color _fullLabelColor;

    [ObservableProperty]
    private bool _hasApplied;

    [ObservableProperty]
    private string _appliedText = string.Empty;

    [ObservableProperty]
    private Color _appliedColor;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canReleaseMute;

    [ObservableProperty]
    private bool _canReportViewed;

    [ObservableProperty]
    private string _pendingText = string.Empty;

    [ObservableProperty]
    private bool _hasPending;

    [ObservableProperty]
    private string _feedbackText = string.Empty;

    [ObservableProperty]
    private bool _hasFeedback;

    [ObservableProperty]
    private bool _hasProblem;

    [ObservableProperty]
    private string _problemText = string.Empty;

    [ObservableProperty]
    private double _timelineDuration;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _segmentSummaryText = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="SponsorPlaybackViewModel"/> class.</summary>
    public SponsorPlaybackViewModel()
    {
    }

    /// <summary>时间轴标记或片段列表发生变化（供时间轴控件重新布局）.</summary>
    public event EventHandler? MarkersChanged;

    /// <summary>当前可执行的提示（最多 8 条）.</summary>
    public ObservableCollection<SponsorNoticeItem> Notices { get; } = [];

    /// <summary>需要用户确认的恢复项.</summary>
    public ObservableCollection<SponsorRecoveryItem> Recoveries { get; } = [];

    /// <summary>当前视频的原始片段（最多 64 条，按开始时间排序）.</summary>
    public ObservableCollection<SponsorSegmentItem> Segments { get; } = [];

    /// <summary>时间轴标记（只读快照）.</summary>
    public IReadOnlyList<SponsorTimelineMarkerItem> Markers { get; private set; } = [];

    /// <summary>浮层上优先显示的第一条提示.</summary>
    public SponsorNoticeItem? PrimaryNotice => Notices.FirstOrDefault();

    /// <summary>浮层上显示的第一条恢复项.</summary>
    public SponsorRecoveryItem? PrimaryRecovery => Recoveries.FirstOrDefault();

    /// <summary>是否有浮层需要显示的内容.</summary>
    public bool HasOverlayContent => IsActive && (Notices.Count > 0 || HasApplied || Recoveries.Count > 0 || HasFeedback);

    /// <summary>
    /// 在播放器所在窗口的界面线程上绑定。<paramref name="attachment"/> 必须是 <c>AttachAsync</c> 对该播放器返回的 ID。
    /// 订阅先于读取快照；不启动、停止或释放门面/播放器。
    /// </summary>
    internal void Bind(SponsorApplicationCoordinator app, Guid attachment)
    {
        ArgumentNullException.ThrowIfNull(app);
        var queue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("SponsorPlaybackBindRequiresUiThread");
        Unbind();
        _epoch++;
        _app = app;
        _attachment = attachment;
        _availability = app.Availability;
        _feed = new SponsorViewFeed(app, queue, Apply);
        _feed.Start();
    }

    /// <summary>解除绑定。之后到达的任何视图或异步结果都被丢弃。不会分离或停止播放器（宿主负责 <c>DetachAsync</c>）.</summary>
    internal void Unbind()
    {
        _epoch++;
        _feed?.Stop();
        _feed = null;
        _app = null;
        _player = null;
        _owner = null;
        ResetFetch();
        ClearRemote();
        Clear("空降助手未连接");
    }

    private void Clear(string status)
    {
        IsActive = false;
        StatusText = status;
        CanFetch = false;
        HasApplied = false;
        HasPending = false;
        HasProblem = false;
        HasFullLabel = false;
        HasFeedback = false;
        HasSegments = false;
        Notices.Clear();
        Recoveries.Clear();
        Segments.Clear();
        SetMarkers([], 0);
        ResetOwner();
        RaiseOverlay();
    }

    private void Apply(SponsorApplicationView view)
    {
        if ((_settings.Policy.Enabled && !view.Settings.Policy.Enabled)
            || (_settings.NetworkEnabled && !view.Settings.NetworkEnabled))
        {
            ResetFetch();
        }

        _settings = view.Settings;
        var player = view.Players.FirstOrDefault(p => p.Attachment == _attachment);
        if (player is null || !player.Attached || view.Closed)
        {
            // Closed/detached stops NEW ordinary admission only. Retained cleanup (preview return,
            // lease release) is still reported honestly instead of being cleared as finished.
            _player = null;
            _owner = null;
            ResetFetch();
            Clear(view.Closed ? "空降助手已关闭" : player is null ? "播放器已断开" : "播放器正在断开");
            if (player is { CleanupPending: true })
            {
                StatusText += "，正在等待播放器归还状态";
            }

            return;
        }

        _player = player;
        var controller = player.Controller;
        var owner = controller?.Owner;
        if (owner != _owner)
        {
            // New media/generation: drop all per-media UI feedback; nothing carries over.
            _owner = owner;
            ResetFetch();
            HasFeedback = false;
            ClearRemote();
        }

        if (!_settings.Policy.Enabled)
        {
            Clear("空降助手已关闭，可在设置中开启");
            return;
        }

        if (owner is null || controller is null)
        {
            Clear("当前内容暂不支持空降助手");
            return;
        }

        IsActive = true;
        ProjectStatus(player);
        ProjectNotices(controller, player);
        ProjectOwner(player);
        ProjectApplied(controller, owner);
        ProjectRecovery(controller);
        ProjectSegments(controller, player, owner);
        RaiseOverlay();
        MaybeAutoFetch(player, owner);
    }

    private static ImmutableArray<T> Safe<T>(ImmutableArray<T>? value) => value is { IsDefault: false } items ? items : [];

    private void RaiseOverlay()
    {
        OnPropertyChanged(nameof(PrimaryNotice));
        OnPropertyChanged(nameof(PrimaryRecovery));
        OnPropertyChanged(nameof(HasOverlayContent));
    }

    private void ProjectStatus(SponsorPlayerView player)
    {
        var count = player.RawSegments.IsDefault ? 0 : player.RawSegments.Length;
        CanFetch = _settings.NetworkEnabled && _fetchTask is null && !IsFetching;
        FetchButtonText = player.ReadStatus is ReadStatus.Disabled ? "获取片段" : "刷新";
        StatusText = !_settings.NetworkEnabled
            ? "未允许联网，无法获取片段（可在设置中开启）"
            : IsFetching ? "正在获取片段…"
            : player.ReadStatus switch
            {
                ReadStatus.Success when count > 0 => $"找到 {count} 个片段",
                ReadStatus.Success or ReadStatus.Empty200 or ReadStatus.Empty404 => "这个视频还没有片段",
                ReadStatus.Disabled when _autoFetch is null or { Pending: true } => "等待播放器就绪后自动获取片段…",
                ReadStatus.Disabled when _autoFetch is { AdmissionRefused: true } => "播放器暂未就绪，可手动获取片段",
                ReadStatus.Disabled => "尚未获取片段",
                var failure => "获取失败：" + SponsorUiText.Read(failure),
            };
        HasProblem = player.DeliveryStatus is ApplicationStatus.ControllerFailure or ApplicationStatus.Stale
            || player.Controller?.Failure is not null;
        ProblemText = player.Controller?.Failure is not null
            ? "播放器的跳过控制遇到问题，自动处理已暂停。普通播放不受影响。"
            : player.DeliveryStatus == ApplicationStatus.ControllerFailure
                ? "播放器暂时没有接收空降助手的设置"
                : player.DeliveryStatus == ApplicationStatus.Stale ? "播放状态正在变化，设置稍后生效" : string.Empty;
    }

    private void ProjectNotices(SponsorControllerResult controller, SponsorPlayerView player)
    {
        var notices = Safe(controller.Plan?.OrdinaryPlan.Notices);
        var speedUp = _settings.Policy.SkipStrategy == SponsorSkipStrategy.SpeedUp;
        var canSeek = controller.Actual?.CanSeekExactly == true;
        var full = notices.FirstOrDefault(n => n.Kind == SponsorNoticeKind.FullLabel && !n.Contributors.IsDefaultOrEmpty);
        HasFullLabel = full is not null;
        if (full is not null)
        {
            var category = full.Contributors[0].Segment.Category;
            FullLabelText = "整个视频被标记为：" + SponsorUiText.CategoryTitle(category);
            FullLabelColor = SponsorUiText.CategoryColor(category);
        }

        var shown = notices.Where(n => n.Kind != SponsorNoticeKind.FullLabel && !n.Contributors.IsDefaultOrEmpty)
            .Take(MaximumNotices).ToList();
        var keys = shown.Select(NoticeKey).ToList();
        SponsorKeyedSync.Sync(Notices, keys, key => new SponsorNoticeItem(key, RunNoticeAsync), (item, i) =>
        {
            var notice = shown[i];
            var first = notice.Contributors[0];
            var segment = first.Segment;
            var more = notice.Contributors.Length > 1 ? $" 等 {notice.Contributors.Length} 个" : string.Empty;
            var mute = segment.ActionType == "mute";
            item.Owner = controller.Owner;
            item.Target = first.Key;
            item.CategoryColor = SponsorUiText.CategoryColor(segment.Category);
            item.HasSkipNow = false;
            item.PrimaryToolTip = notice.CanAct ? string.Empty : NoticeReason(notice.Reason);
            item.IsPrimaryEnabled = notice.CanAct;
            switch (notice.Kind)
            {
                case SponsorNoticeKind.ManualAction:
                case SponsorNoticeKind.AutomaticCandidate:
                    item.Title = SponsorUiText.CategoryTitle(segment.Category) + more;
                    item.Detail = SponsorUiText.Range(segment) + (notice.Kind == SponsorNoticeKind.AutomaticCandidate
                        ? (mute ? " · 将自动静音" : speedUp ? " · 将自动加速播放" : " · 将自动跳过") : string.Empty);
                    item.Action = PlaybackUiAction.Skip;
                    item.PrimaryText = mute ? "静音" : "跳过";
                    item.HasPrimary = true;
                    item.HasSkipNow = !mute && speedUp;
                    item.IsSkipNowEnabled = canSeek;
                    break;
                case SponsorNoticeKind.ReviewProtected:
                    item.Title = SponsorUiText.CategoryTitle(segment.Category) + more;
                    item.Detail = "已撤销，这次不会再自动处理";
                    item.Action = PlaybackUiAction.ReSkip;
                    item.PrimaryText = mute ? "再次静音" : "再次跳过";
                    item.HasPrimary = true;
                    break;
                case SponsorNoticeKind.PointNavigation:
                    item.Title = "精彩时刻";
                    item.Detail = SponsorUiText.Range(segment);
                    item.Action = PlaybackUiAction.JumpToPoint;
                    item.PrimaryText = "跳转";
                    item.HasPrimary = true;
                    break;
                default:
                    item.Title = "即将出现：" + SponsorUiText.CategoryTitle(segment.Category);
                    item.Detail = SponsorUiText.Range(segment);
                    item.HasPrimary = false;
                    break;
            }
        });
    }

    private static string NoticeKey(PlaybackNotice notice)
        => notice.Kind + "|" + string.Join(",", notice.Contributors.Select(c => c.Key.Uuid + "@" + c.Key.Source));

    private static string NoticeReason(string? reason) => reason switch
    {
        "ExactSeekUnavailable" => "当前播放器不支持精确跳转",
        "MuteUnavailable" or "MuteOwnershipUnavailable" => "当前播放器不支持静音控制",
        "TargetNotCurrentInterval" => "已不在该片段中",
        _ => "暂时无法操作",
    };

    private void ProjectApplied(SponsorControllerResult controller, PlaybackOwner owner)
    {
        var applied = controller.AppliedId > _dismissedAppliedId && controller.Applied is { } a && a.Owner == owner && !a.WasPreview ? a : null;
        var rate = applied is null && controller.RateReceiptId > _dismissedRateId && controller.RateReceipt is { } r && r.Owner == owner
            && !r.ObservedCoverage.IsDefaultOrEmpty ? r : null;
        HasApplied = applied is not null || rate is not null;
        CanUndo = applied?.Kind == SponsorEffectKind.SeekInterval;
        // Release follows the actual current lease capability, not the presence of an old HoldMute receipt.
        CanReleaseMute = _releaseMuteAvailable;
        if (applied is null && rate is null && _releaseMuteAvailable)
        {
            HasApplied = true;
            AppliedColor = SponsorUiText.CategoryColor(null);
            AppliedText = "空降助手正在保持静音";
            CanUndo = false;
        }

        CanReportViewed = _settings.ViewedTelemetryEnabled && _settings.NetworkEnabled
            && _availability.Any(f => f.Feature == SponsorFeature.ViewedTelemetry && f.Available)
            && (applied?.Kind is SponsorEffectKind.SeekInterval or SponsorEffectKind.HoldMute || rate is not null);
        if (applied is not null)
        {
            var category = applied.Contributors.IsDefaultOrEmpty ? null : applied.Contributors[0].Segment.Category;
            AppliedColor = SponsorUiText.CategoryColor(category);
            var name = SponsorUiText.CategoryTitle(category);
            AppliedText = applied.Kind switch
            {
                SponsorEffectKind.SeekInterval => $"已跳过{name}（{SponsorUiText.Time(applied.From)} – {SponsorUiText.Time(applied.To)}）",
                SponsorEffectKind.HoldMute => $"已静音{name}",
                SponsorEffectKind.SeekPoint => "已跳转到精彩时刻",
                SponsorEffectKind.SeekUndo => "已撤销跳过",
                SponsorEffectKind.ReleaseMute => "已取消静音",
                _ => "已处理",
            };
        }
        else if (rate is not null)
        {
            var category = rate.ObservedCoverage[0].Contributors.IsDefaultOrEmpty ? null : rate.ObservedCoverage[0].Contributors[0].Segment.Category;
            AppliedColor = SponsorUiText.CategoryColor(category);
            AppliedText = $"已以 {rate.Target:0.#}× 加速播放{SponsorUiText.CategoryTitle(category)}";
        }

        if (_admittedBaseline is { } baseline && (controller.AppliedId > baseline.Applied || controller.RateReceiptId > baseline.Rate))
        {
            _admittedBaseline = null;
            HasFeedback = false;
        }

        var op = controller.Operation;
        HasPending = op is { Purpose: NativeAdmissionPurpose.Sponsor, Phase: SponsorOperationPhase.Pending or SponsorOperationPhase.SucceededUnobserved };
        PendingText = op?.Phase == SponsorOperationPhase.SucceededUnobserved ? "已执行，正在确认…" : HasPending ? "正在处理…" : string.Empty;
    }

    private void ProjectRecovery(SponsorControllerResult controller)
    {
        var items = new List<(string Key, string Title, string Detail, string Action, PlaybackLeaseField? Field, string Token)>();
        if (controller.MuteLease is { Phase: PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown } mute)
        {
            items.Add(("mute|" + mute.Token, "静音状态未能确认", "为避免误改你的设置，已停止自动处理。确认后会按播放器的实际状态恢复。", "检查并恢复", PlaybackLeaseField.Mute, mute.Token));
        }

        if (controller.RateLease is { Phase: PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown } rate)
        {
            items.Add(("rate|" + rate.Token, "播放速度未能确认", "为避免误改你的速度，已停止自动加速。确认后会按播放器的实际状态恢复。", "检查并恢复", PlaybackLeaseField.Rate, rate.Token));
        }

        foreach (var seek in Safe<SponsorSeekStatus>(controller.SeekClaims)
            .Where(s => s.Phase is SponsorOperationPhase.Failed or SponsorOperationPhase.Unknown).Take(4))
        {
            items.Add(("seek|" + seek.Token, "跳转结果未知", "不会自动重试。确认后以当前位置为准（不计为已跳过）。", "以当前位置为准", null, seek.Token));
        }

        SponsorKeyedSync.Sync(Recoveries, items.Select(i => i.Key).ToList(), key => new SponsorRecoveryItem(key, RunRecoveryAsync), (item, i) =>
        {
            item.Title = items[i].Title;
            item.Detail = items[i].Detail;
            item.ActionText = items[i].Action;
            item.Field = items[i].Field;
            item.Token = items[i].Token;
        });
    }

    private void ProjectSegments(SponsorControllerResult controller, SponsorPlayerView player, PlaybackOwner owner)
    {
        var raw = Safe<SponsorSegment>(player.RawSegments);
        var plan = controller.Plan?.OrdinaryPlan;
        var markers = Safe(plan?.Markers);
        var diagnostics = Safe(plan?.Diagnostics);
        var facts = _settings.FactsFor(owner.Media);
        var remote = _availability.FirstOrDefault(a => a.Feature == SponsorFeature.VoteUpDown);
        var remoteOk = remote?.Available == true && _settings.NetworkEnabled;
        var remoteText = remote?.Available != true
            ? SponsorUiText.Availability(remote, "暂不可用")
            : _settings.NetworkEnabled ? string.Empty : "需要先在设置中允许联网";
        var ordered = raw.Where(s => s.Cid == owner.Media.Cid)
            .OrderBy(s => s.ActionType == "full" ? -1 : s.Segment.IsDefaultOrEmpty ? double.MaxValue : s.Segment[0])
            .Take(MaximumSegments).ToList();
        HasSegments = ordered.Count > 0;
        SegmentSummaryText = raw.Length > MaximumSegments ? $"仅显示前 {MaximumSegments} 个，共 {raw.Length} 个" : string.Empty;
        var keys = ordered.Select(s => s.Uuid + "@" + s.Source).ToList();
        SponsorKeyedSync.Sync(Segments, keys, key => new SponsorSegmentItem(key, this), (item, i) =>
        {
            var segment = ordered[i];
            var key = new PlaybackSegmentKey(owner.Media, segment.Uuid, segment.Source);
            var marker = markers.FirstOrDefault(m => m.Contributor.Key == key);
            var diagnostic = diagnostics.FirstOrDefault(d => d.Contributor?.Key == key && d.NormalizationReason is not null);
            item.SegmentKey = key;
            item.Segment = segment;
            item.Title = SponsorUiText.CategoryTitle(segment.Category);
            item.CategoryColor = SponsorUiText.CategoryColor(segment.Category);
            item.RangeText = SponsorUiText.Range(segment);
            item.ActionText = SponsorUiText.ActionTitle(segment.ActionType);
            item.SourceText = SponsorUiText.SourceTitle(segment.Source);
            item.IsLocked = segment.Locked is > 0;
            item.IsLocallyHidden = facts.Hidden.Contains(key);
            item.IsLocallyDownvoted = facts.Downvoted.Contains(key);
            item.IsExternal = segment.Source == 2;
            item.IsExternalExcluded = facts.ExternalSourceExcluded.Contains(key);
            item.CanJump = segment.ActionType == "poi" && marker is not null;
            item.CanUseRemote = remoteOk && segment.Source is null;
            item.RemoteUnavailableText = segment.Source is null ? remoteText : "只能对空降助手服务器上的片段投票";
            item.StatusText = diagnostic?.NormalizationReason is { } reason ? SponsorUiText.Normalization(reason)
                : marker is not null ? SponsorUiText.PolicyTitle(marker.Policy)
                : segment.Source == 2 && !_settings.IncludeExternalSegments ? "未启用搬运来源"
                : segment.Source == 3 && !_settings.IncludeDanmakuSegments ? "未启用弹幕来源"
                : "不会处理";
            item.IsDimmed = marker is null;
        });

        var duration = controller.Actual?.Duration;
        var built = new List<SponsorTimelineMarkerItem>();
        if (duration is double total && double.IsFinite(total) && total > 0)
        {
            foreach (var marker in markers.Take(MaximumMarkers))
            {
                var segment = marker.Contributor.Segment;
                if (segment.Segment.IsDefaultOrEmpty || segment.Segment.Length != 2 || segment.ActionType == "full")
                {
                    continue;
                }

                var start = Math.Clamp(segment.Segment[0] / total, 0, 1);
                var end = Math.Clamp(segment.Segment[1] / total, 0, 1);
                built.Add(new(start, end, segment.ActionType == "poi", SponsorUiText.CategoryColor(segment.Category),
                    $"{SponsorUiText.CategoryTitle(segment.Category)} · {SponsorUiText.Range(segment)} · {SponsorUiText.PolicyTitle(marker.Policy)}",
                    marker.Policy == CategoryPolicy.ShowOverlay));
            }
        }

        SetMarkers(built, duration ?? 0);
    }

    private void SetMarkers(IReadOnlyList<SponsorTimelineMarkerItem> markers, double duration)
    {
        var changed = markers.Count != Markers.Count || duration != TimelineDuration
            || markers.Where((m, i) => m.StartRatio != Markers[i].StartRatio || m.EndRatio != Markers[i].EndRatio
                || m.CategoryColor != Markers[i].CategoryColor || m.IsDimmed != Markers[i].IsDimmed).Any();
        if (!changed)
        {
            return;
        }

        Markers = markers;
        TimelineDuration = duration;
        OnPropertyChanged(nameof(Markers));
        MarkersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MaybeAutoFetch(SponsorPlayerView player, PlaybackOwner owner)
    {
        // Intent belongs to this attachment/bind epoch, owner and enabled consent interval.
        // A missing ticket does not spend an attempt. Admission is still owned by the coordinator.
        if (_app is not { } app || _player != player || _owner != owner
            || !_settings.Policy.Enabled || !_settings.NetworkEnabled)
        {
            return;
        }

        var intent = _autoFetch ??= new();
        var view = app.Snapshot;
        var latest = view.Players.FirstOrDefault(p => p.Attachment == _attachment);
        if (view.Closed || !view.Settings.Policy.Enabled || !view.Settings.NetworkEnabled
            || latest is not { Attached: true } || latest.Controller?.Owner != owner)
        {
            return;
        }

        if (player.ReadStatus != ReadStatus.Disabled || latest.ReadStatus != ReadStatus.Disabled)
        {
            intent.Pending = false; // A published real read (including failure) is definitive.
            return;
        }

        // A published ticket is insufficient while controller input delivery is unsettled.
        if (!intent.Pending || _fetchTask is not null || latest.DeliveryStatus != ApplicationStatus.Success
            || SponsorTickets.Read(view, _attachment) is not { } ticket
            || ticket.Stamp.Owner != owner)
        {
            return;
        }

        var readiness = new FetchReadiness(ticket.Stamp, latest.DeliveryStatus);
        if (intent.Attempts >= 2 || (intent.Attempts > 0 && readiness == intent.LastReadiness))
        {
            return;
        }

        ++intent.Attempts;
        intent.LastReadiness = readiness;
        _ = FetchCoreAsync(false, intent, ticket);
    }

    private void ResetFetch()
    {
        ++_fetchEpoch;
        _autoFetch = null;
        IsFetching = false;
        CanFetch = false;
        // Keep the actual task reserved until it drains, even across Unbind/Bind or media replacement.
    }

    private bool IsCurrentFetch(SponsorApplicationCoordinator app, Guid attachment, PlaybackOwner owner, long epoch, long fetchEpoch)
        => ReferenceEquals(_app, app) && _attachment == attachment && _owner == owner
            && _epoch == epoch && _fetchEpoch == fetchEpoch;

    [RelayCommand]
    private Task FetchAsync() => FetchCoreAsync(_player?.ReadStatus is not ReadStatus.Disabled);

    private Task FetchCoreAsync(bool refresh, AutoFetchIntent? automatic = null, PlayerTicket? ticket = null)
    {
        if (_app is not { } app || _owner is not { } owner || _fetchTask is not null)
        {
            return Task.CompletedTask;
        }

        if (automatic is null)
        {
            var intent = _autoFetch ??= new();
            intent.Pending = false; // Explicit manual work supersedes the startup intent, not vice versa.
            intent.AdmissionRefused = false;
        }

        var task = FetchCoreWorkAsync(app, _attachment, owner, _epoch, _fetchEpoch, refresh, automatic, ticket);
        _fetchTask = task;
        return SponsorUiWork.Track(this, task);
    }

    private async Task FetchCoreWorkAsync(SponsorApplicationCoordinator app, Guid attachment, PlaybackOwner owner,
        long epoch, long fetchEpoch, bool refresh, AutoFetchIntent? automatic, PlayerTicket? ticket)
    {
        // Reserve and track this actual runner before any observable-property callback can re-enter.
        await Task.Yield();
        try
        {
            if (!IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch)) return;
            IsFetching = true;
            if (!IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch)) return;
            CanFetch = false;
            if (!IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch)) return;
            StatusText = "正在获取片段…";
            if (!IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch)) return;

            // Auto owns its one additional ready-edge attempt; do not stack the helper's retry on it.
            var result = automatic is not null
                ? await app.FetchAsync(ticket!, refresh)
                : await SponsorTickets.RunAsync(app, attachment, owner, current => app.FetchAsync(current, refresh),
                    SponsorTickets.IsReadAdmissionRefusal);
            SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<SponsorPlaybackViewModel>>(), logger =>
            {
                // This is the committed published snapshot AFTER the result, not a claim about
                // the private admission instant or the settings page's pending toggle overlay.
                var observed = app.Snapshot;
                logger.LogInformation(
                    "FailureContext Stage=SponsorFetchResult Result={Result} ResultPersistence={ResultPersistence} Persistence={Persistence} StoreVersionKnown={StoreVersionKnown} CommittedNetworkEnabled={CommittedNetworkEnabled} CommittedViewedEnabled={CommittedViewedEnabled} Closed={Closed}",
                    result.Status, result.Persistence, observed.Persistence, observed.StoreVersion is not null,
                    observed.StoreVersion is not null ? (bool?)observed.Settings.NetworkEnabled : null,
                    observed.StoreVersion is not null ? (bool?)observed.Settings.ViewedTelemetryEnabled : null, observed.Closed);
            });
            if (IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch))
            {
                var refused = SponsorTickets.IsReadAdmissionRefusal(result);
                if (automatic is not null)
                {
                    automatic.AdmissionRefused = refused;
                    automatic.Pending = refused && automatic.Attempts < 2;
                }

                if (result.Status is not (ApplicationStatus.Success or ApplicationStatus.Unavailable)
                    && (automatic is null || !refused))
                {
                    Feedback(SponsorUiText.Result(result));
                }
            }
        }
        catch (Exception ex)
        {
            SafeFailureContext.TryWrite(FailureStage.SponsorFetch, ex, () => this.Get<ILogger<SponsorPlaybackViewModel>>());
            if (IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch))
            {
                if (automatic is not null)
                {
                    automatic.Pending = false;
                    automatic.AdmissionRefused = false;
                }
                Feedback("获取片段失败");
            }
        }
        finally
        {
            // The awaited physical work and its coordinator completion have drained; no cancellation wrapper.
            _fetchTask = null;
            if (IsCurrentFetch(app, attachment, owner, epoch, fetchEpoch))
            {
                IsFetching = false;
            }

            // Inspect the CURRENT binding once, not the old result. A real ticket publication while
            // awaiting must not strand its pending intent; ordinary position/sequence changes cannot create one.
            if (IsActive && _player is { } player && _owner is { } currentOwner)
            {
                ProjectStatus(player);
                MaybeAutoFetch(player, currentOwner);
            }
        }
    }

    private void Feedback(string text)
    {
        FeedbackText = text;
        HasFeedback = !string.IsNullOrEmpty(text);
    }

    [RelayCommand]
    private void DismissFeedback() => HasFeedback = false;

    partial void OnHasFeedbackChanged(bool value) => RaiseOverlay();

    [RelayCommand]
    private void DismissApplied()
    {
        if (_player?.Controller is { } controller)
        {
            _dismissedAppliedId = controller.AppliedId;
            _dismissedRateId = controller.RateReceiptId;
        }

        HasApplied = false;
        RaiseOverlay();
    }
}
