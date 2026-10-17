// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Frozen;
using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 空降助手界面文案与状态映射。只把类型化结果翻译成友好中文，
/// 从不显示异常文本、身份、令牌、地址或原始服务器内容。
/// </summary>
internal static class SponsorUiText
{
    internal const string FeatureName = "空降助手";

    private static readonly FrozenDictionary<string, (string Title, string Description, Color Color)> CategoryInfo =
        new Dictionary<string, (string, string, Color)>(StringComparer.Ordinal)
        {
            ["sponsor"] = ("赞助广告", "付费推广、商业合作等广告内容", Color.FromArgb(255, 0, 212, 0)),
            ["selfpromo"] = ("自我推广", "无偿推广自己的周边、频道、活动或其他作品", Color.FromArgb(255, 255, 214, 0)),
            ["exclusive_access"] = ("独家体验", "展示免费获得或抢先体验的产品，只作为整片标签", Color.FromArgb(255, 0, 138, 92)),
            ["interaction"] = ("互动提醒", "提醒点赞、投币、收藏、关注或三连", Color.FromArgb(255, 204, 0, 255)),
            ["poi_highlight"] = ("精彩时刻", "视频的重点位置，只提示和手动跳转", Color.FromArgb(255, 255, 22, 132)),
            ["intro"] = ("开场", "片头动画、开场白或没有实际内容的过场", Color.FromArgb(255, 0, 255, 255)),
            ["outro"] = ("片尾", "片尾鸣谢、结束画面等结尾内容", Color.FromArgb(255, 2, 2, 237)),
            ["preview"] = ("回顾与预告", "前情回顾或下期预告", Color.FromArgb(255, 0, 143, 214)),
            ["padding"] = ("空白填充", "视频首尾没有实际内容的填充画面", Color.FromArgb(255, 140, 140, 140)),
            ["filler"] = ("离题闲聊", "与视频主题无关的闲聊或玩笑", Color.FromArgb(255, 115, 0, 255)),
            ["music_offtopic"] = ("非音乐部分", "音乐视频中不是音乐的片段", Color.FromArgb(255, 255, 153, 0)),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static readonly IReadOnlyList<string> PolicyLabels = (string[])["关闭", "仅显示", "手动跳过", "自动跳过"];
    internal static readonly IReadOnlyList<string> StrategyLabels = (string[])["直接跳转", "加速播放"];
    internal static readonly IReadOnlyList<string> FallbackLabels = (string[])["不回退", "片段太短或接近结尾时改为跳转"];

    internal static string CategoryTitle(string? id)
        => id is not null && CategoryInfo.TryGetValue(id, out var info) ? info.Title : "未知分类";

    internal static string CategoryDescription(string? id)
        => id is not null && CategoryInfo.TryGetValue(id, out var info) ? info.Description : string.Empty;

    internal static Color CategoryColor(string? id)
        => id is not null && CategoryInfo.TryGetValue(id, out var info) ? info.Color : Color.FromArgb(255, 128, 128, 128);

    internal static string ActionTitle(string? action) => action switch
    {
        "skip" => "跳过",
        "mute" => "静音",
        "poi" => "精彩时刻",
        "full" => "整片标签",
        _ => "未知操作",
    };

    internal static string SourceTitle(int? source) => source switch
    {
        null => "空降助手",
        1 => "本地草稿",
        2 => "YouTube 搬运",
        3 => "弹幕空降",
        _ => "未知来源",
    };

    internal static string PolicyTitle(CategoryPolicy policy) => (int)policy is >= 0 and < 4 ? PolicyLabels[(int)policy] : "未知";

    internal static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return "--:--";
        }

        var rounded = Math.Round(seconds, 1, MidpointRounding.AwayFromZero);
        var span = TimeSpan.FromSeconds(rounded);
        var tenth = (int)Math.Round((rounded - Math.Floor(rounded)) * 10) % 10;
        return span.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}", (int)span.TotalHours, span.Minutes, span.Seconds, tenth)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", (int)span.TotalMinutes, span.Seconds, tenth);
    }

    internal static string Range(SponsorSegment segment)
    {
        if (segment.Segment.IsDefaultOrEmpty)
        {
            return "时间未知";
        }

        if (segment.ActionType == "full")
        {
            return "整个视频";
        }

        if (segment.Segment.Length == 1)
        {
            return $"从 {Time(segment.Segment[0])} 开始，尚未标记结束";
        }

        return segment.ActionType == "poi" || segment.Segment[0] == segment.Segment[1]
            ? Time(segment.Segment[0])
            : $"{Time(segment.Segment[0])} – {Time(segment.Segment[1])}";
    }

    internal static string ShortId(string? publicId)
        => string.IsNullOrEmpty(publicId) ? string.Empty : publicId.Length <= 12 ? publicId : publicId[..6] + "…" + publicId[^4..];

    internal static string Draft(DraftState state) => state switch
    {
        DraftState.Ready => "待提交",
        DraftState.Incomplete => "标记中",
        DraftState.Invalid => "时间无效",
        DraftState.SubmissionInFlight => "提交中",
        DraftState.Confirmed => "已确认",
        DraftState.AcceptedUnconfirmed => "服务器已接受，条目未确认",
        DraftState.Rejected => "被拒绝",
        DraftState.Unknown => "结果未知",
        _ => "未知状态",
    };

    internal static string Write(WriteStatus status) => status switch
    {
        WriteStatus.NotSent => "未发送",
        WriteStatus.Accepted => "服务器已接受",
        WriteStatus.Rejected => "服务器已拒绝",
        WriteStatus.Unknown => "结果未知，不会自动重发",
        _ => "未知",
    };

    internal static string Operation(ManualOperation operation) => operation switch
    {
        ManualOperation.Submit => "提交片段",
        ManualOperation.Vote => "片段投票",
        ManualOperation.ChangeCategory => "更改分类",
        ManualOperation.SetUsername => "设置用户名",
        ManualOperation.BindPort => "关联搬运视频",
        ManualOperation.VotePort => "搬运关联投票",
        ManualOperation.UpdatePort => "更新搬运片段",
        ManualOperation.ReportViewed => "上报观看",
        _ => "操作",
    };

    internal static string Read(ReadStatus status) => status switch
    {
        ReadStatus.Success => "已获取",
        ReadStatus.Empty200 or ReadStatus.Empty404 => "暂无数据",
        ReadStatus.Disabled => "尚未获取",
        ReadStatus.Cancelled => "已取消",
        ReadStatus.Busy => "请求过多，请稍后再试",
        ReadStatus.HttpFailure => "服务器暂时无法响应",
        ReadStatus.TransportFailure => "网络连接失败",
        ReadStatus.SchemaFailure => "服务器数据无法识别",
        _ => "未知",
    };

    internal static string Persistence(PersistenceStatus status) => status switch
    {
        PersistenceStatus.Success => "已保存",
        PersistenceStatus.Missing => "尚无本机数据",
        PersistenceStatus.Corrupt or PersistenceStatus.InvalidData => "本机数据已损坏，未做任何覆盖",
        PersistenceStatus.UnsupportedSchema => "本机数据来自不兼容的版本",
        PersistenceStatus.TooLarge => "数据过大",
        PersistenceStatus.ForeignScope => "数据属于其他应用或渠道",
        PersistenceStatus.Conflict => "数据已在别处更改，请重新载入",
        PersistenceStatus.Busy => "正忙，请稍后再试",
        PersistenceStatus.IoFailure => "无法写入本机文件",
        PersistenceStatus.Cancelled => "已取消",
        PersistenceStatus.ExplicitIntentRequired => "需要确认后才能继续",
        PersistenceStatus.UnresolvedDrafts => "有提交结果未知的草稿，暂不能替换",
        PersistenceStatus.ProtectionUnavailable => "此设备无法安全保存贡献者身份",
        PersistenceStatus.DecryptionFailed => "无法读取已保存的身份（可能来自其他用户或设备）",
        PersistenceStatus.AlreadyExists => "已存在贡献者身份",
        PersistenceStatus.UnresolvedMutations => "有结果不确定或尚未写入本机的操作记录，暂不能替换",
        PersistenceStatus.HistoryConflict => "这样会减少或抹去已保存的统计或操作记录，已拒绝",
        PersistenceStatus.CapacityReached => "已达到数量上限",
        _ => "未知状态",
    };

    internal static string Normalization(PlaybackNormalizationReason reason) => reason switch
    {
        PlaybackNormalizationReason.InvalidMinimumDuration => "最短时长设置无效",
        PlaybackNormalizationReason.MusicStatusUnknown => "尚不确定是否为音乐视频",
        PlaybackNormalizationReason.MusicSkipOverride => "音乐视频中不自动处理",
        PlaybackNormalizationReason.FullVideoManualDemotion => "整片标签视频改为手动",
        PlaybackNormalizationReason.BelowConfiguredMinimum => "短于最短时长",
        PlaybackNormalizationReason.MuteDisabled => "未启用静音片段",
        PlaybackNormalizationReason.LocallyHidden => "已在本机隐藏",
        PlaybackNormalizationReason.LocallyDownvoted => "已在本机标记为错误",
        PlaybackNormalizationReason.ExternalSourceExcluded => "已排除此来源",
        PlaybackNormalizationReason.UnresolvedWireHiddenFlag => "服务器标记为隐藏",
        PlaybackNormalizationReason.DraftIsolation => "本地草稿不参与播放",
        PlaybackNormalizationReason.SpeedUpUnsupported => "当前播放器不支持加速",
        _ => "不会处理",
    };

    private static readonly FrozenDictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Facade / application reasons.
        ["NetworkDisabledStaleOrBusy"] = "未允许联网、播放状态刚刚变化或请求过多",
        ["StaleDisabledOrBusy"] = "播放状态刚刚变化、未允许联网或请求过多，未发送",
        ["MutationJournalRequired"] = "此操作需要本机操作记录支持，当前版本尚未接入，未发送",
        ["ExplicitCurrentIdentityRequired"] = "贡献者身份已变化或无法读取，未发送",
        ["ActualDurationRequired"] = "需要播放器提供准确的视频时长",
        ["ActualPositionDurationRequired"] = "需要播放器提供准确的播放位置和时长",
        ["ActualTargetDurationRequired"] = "需要播放器提供准确的视频时长",
        ["OwnerPolicyOrVisitChanged"] = "视频、设置或播放位置已变化，未发送",
        ["OwnerRetiredBeforeDispatch"] = "视频或设置已变化，未发送",
        ["OwnerRetiredBeforePreparation"] = "视频或设置已变化，未发送",
        ["PreparationNotDurable"] = "无法先保存到本机，因此没有发送",
        ["ClientAdmissionUnavailable"] = "网络连接暂不可用，未发送",
        ["ClientFactoryOutcomeUnknown"] = "发送结果未知，不会自动重发",
        ["ClientOutcomeUnknown"] = "发送结果未知，不会自动重发",
        ["CompletionNotDurableDoNotReplay"] = "已收到结果，但未能保存到本机；不会重新发送",
        ["Q4HasNoAtomicSplitMerge"] = "暂不支持拆分或合并草稿",
        ["N3FrameRequestHookRequired"] = "逐帧控制尚未接入播放器",
        ["BackendFrameCapabilityUnavailable"] = "当前播放器不支持逐帧",
        ["N3StampedPreviewSeekHookRequired"] = "预览播放需要播放器支持，尚未接入",
        ["AnotherPlayerOwnsEditor"] = "另一个播放窗口正在标注",
        ["DraftRevisionChanged"] = "草稿刚刚被修改，请确认后再试",
        ["DraftSelectionRequired"] = "请先选择一条草稿",
        ["CurrentConfirmedMuteReceiptRequired"] = "没有可以取消的静音",
        ["ExplicitReadbackOnly"] = "暂时无法确认播放器状态",
        ["ReconciledIsNotConfirmed"] = "暂时无法确认跳转结果",
        ["VerifiedCurrentChannelRequired"] = "需要已验证的频道信息",
        ["QuiescedLoadRequired"] = "请先关闭正在播放的视频",
        ["DetachDrainAndSaveBeforeReplace"] = "请先关闭所有播放窗口，并把草稿、统计和操作记录保存到本机",
        ["QuiescedStatisticsImportRequired"] = "请先关闭所有播放窗口，并把统计和操作记录保存到本机",
        ["LocalChangesNotDurable"] = "还有修改没有写入本机",
        ["InvalidDurableStatistics"] = "统计数据无效",
        ["CanonicalUpUidRequired"] = "UID 格式不正确：只能是不以 0 开头的数字",
        ["ActualVerifiedUpUidRequired"] = "需要播放页先确认当前视频的 UP 主 UID",
        ["InjectedJournalOwnsCompletionPersistence"] = "操作记录由其他组件保存，这里不能写入",
        ["ParentMustJoinN3Cleanup"] = "播放器没有完成清理",
        ["UnresolvedDrafts"] = "有提交中或结果未知的草稿，暂不能替换",
        ["BackupTooLarge"] = "备份文件超过 4 MiB，不能导入",
        ["CurrentExactTargetRequired"] = "该片段已不在当前视频中",
        ["ForeignPortIdentity"] = "关联信息不属于当前视频",
        ["ConfirmedReceiptRequired"] = "只有实际处理过的片段才能上报",
        ["ViewedTelemetryDisabled"] = "未允许上报观看次数",
        ["InvalidInput"] = "输入无效：最多 128 个字符，且不能包含控制字符",
        ["UseQ4Submit"] = "请使用提交草稿",
        ["PortOperationRequired"] = "操作类型无效",
        ["UnsupportedOrMissingTarget"] = "缺少操作目标，未发送",
        ["NotPrepared"] = "未能准备请求，未发送",
        ["PreparationFailed"] = "未能准备提交内容，未发送",
        ["PreviewNotAvailableForDraft"] = "此草稿不能预览（只支持跳过和静音片段）",
        ["MediaChanged"] = "视频已切换，操作已取消",
        ["NoPlayerTicket"] = "播放器尚未就绪",
        ["NoIdentity"] = "需要先创建贡献者身份",

        // Q4 draft queue outcomes.
        ["CaptureAlreadyActive"] = "已经在标记中，请先标记结束",
        ["StaleCapture"] = "标记已中断（视频或播放已变化），请在草稿中编辑时间或删除",
        ["StaleMediaGeneration"] = "视频已变化，请重新开始标记",
        ["InvalidCaptureTime"] = "当前位置无法作为标记时间",
        ["OrdinaryCaptureRequired"] = "只有跳过和静音片段可以用开始/结束标记",
        ["QueueCapacity"] = "草稿数量已达上限",
        ["InvalidContextOrCategoryAction"] = "该分类不支持此操作",
        ["InvalidDraft"] = "时间范围无效",
        ["InvalidTime"] = "时间超出视频范围",
        ["IncompleteEdit"] = "请同时填写开始和结束时间",
        ["UnsupportedCategoryAction"] = "该分类不支持此操作",
        ["ConfirmedHistoryReadOnly"] = "已确认的提交不能修改",
        ["RemoteHistoryCannotBeLocallyWithdrawn"] = "提交过的草稿不能在本机删除",
        ["NoActiveCapture"] = "没有正在进行的标记",
        ["NoLocalUndo"] = "没有可以撤销的操作",
        ["DraftNotFound"] = "草稿已不存在",
        ["InvalidSubmissionSelection"] = "没有可提交的草稿，或草稿与当前视频时长不一致",
        ["OperationCapacity"] = "进行中的提交过多",
        ["NotRetryable"] = "这条草稿不需要重新提交",
        ["DuplicateRiskAcknowledgementRequired"] = "请先确认了解可能重复提交",
        ["NewUserIntentRequired"] = "需要重新确认",
        ["SubmissionStillInFlight"] = "这条草稿仍在提交中",
        ["ForeignOrMissingDraft"] = "草稿不属于当前视频",
        ["UnknownOrCompletedOperation"] = "该提交已经结束",
        ["InvalidDuration"] = "视频时长无效",

        // Editor/native preview and lease release (stable N4 reasons; never raw native text).
        ["EditorPlayerPortUnavailable"] = "播放器还没有接入标注控制",
        ["EditorAdmissionClosedOrStale"] = "播放状态已变化，请重新操作",
        ["AnotherPlayerOwnsEditor"] = "另一个播放窗口正在标注",
        ["ActualPausedFrameCapabilityRequired"] = "请先暂停视频，并确认播放器支持逐帧",
        ["ActualEditorCapabilityUnavailable"] = "当前播放器暂不支持这个操作",
        ["PreviewCleanupTokenStillOwned"] = "上一次预览还没有结束，请先结束预览",
        ["CurrentPreparedPreviewAndExactSeekRequired"] = "请先准备这条草稿的预览，并确认播放器支持精确跳转",
        ["PrepareExactCurrentQ4PreviewFirst"] = "草稿已变化，请重新准备预览",
        ["PreviewNotAvailableForDraft"] = "这条草稿不能预览",
        ["DraftRevisionChanged"] = "草稿刚刚被修改，请再试一次",
        ["DraftSelectionRequired"] = "请先选择一条草稿",
        ["EditorIsolationNotAccepted"] = "播放器没有接受标注模式",
        ["EditorContextChangedAfterPush"] = "播放状态已变化，请重新操作",
        ["DraftPreviewChangedAfterPush"] = "草稿已变化，请重新准备预览",
        ["ActualPositionDurationRequired"] = "还读不到当前播放位置",
        ["PreviewCleanupTokenChanged"] = "这次预览已被新的预览替代",
        ["PreviewOwnershipAlreadyReleased"] = "预览已经结束",
        ["ExplicitPreviewCleanupTokenRequired"] = "请使用“结束预览”按钮结束当前预览",
        ["PreviewCleanupTokenRequired"] = "没有可以结束的预览",
        ["NoOwnedPreviewCleanup"] = "没有正在进行的预览",
        ["CurrentOwnedMuteLeaseRequired"] = "当前没有由空降助手保持的静音",
        ["MuteWorkPending"] = "静音操作还在进行中",
        ["MuteLeaseNeedsReconciliation"] = "静音状态需要先核对",
        ["LeaseReleasePortRequired"] = "播放器还没有接入取消静音",
        ["ActualVerifiedUpUidRequired"] = "还没有确认这个视频的 UP 主",
        ["CanonicalUpUidRequired"] = "UP 主 UID 无效",
        ["EditorUnavailable"] = "播放器暂不支持这个操作",
        ["EditorStale"] = "播放状态已变化，请重新操作",
        ["EditorBusy"] = "播放器正忙，请稍后再试",
        ["EditorAccepted"] = "播放器已接收，正在处理",
        ["EditorAcknowledged"] = "播放器已回应（不代表画面已到位）",
        ["EditorReleased"] = "预览已结束",
        ["EditorNeedsReconciliation"] = "播放器没有给出明确结果，请查看当前画面后再决定",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static string Result(ApplicationResult result)
    {
        if (result.Reason is { } reason && Reasons.TryGetValue(reason, out var text))
        {
            return text;
        }

        if (result.Queue?.Error is { } queueError && Reasons.TryGetValue(queueError, out var queueText))
        {
            return queueText;
        }

        if (result.Persistence is { } persistence && result.Status == ApplicationStatus.PersistenceFailure)
        {
            return "未能保存到本机：" + Persistence(persistence);
        }

        return result.Status switch
        {
            ApplicationStatus.Success => "已完成",
            ApplicationStatus.Disabled => "功能未开启或未允许联网",
            ApplicationStatus.Stale => "播放状态刚刚变化，请再试一次（暂停后操作更稳定）",
            ApplicationStatus.Busy => "正忙，请稍后再试",
            ApplicationStatus.Closed => "空降助手已关闭",
            ApplicationStatus.Unavailable => "此操作暂不可用",
            ApplicationStatus.Invalid => "操作无效",
            ApplicationStatus.PersistenceFailure => "未能保存到本机",
            ApplicationStatus.ControllerFailure => "播放器没有响应此操作",
            ApplicationStatus.Pending => "已发给播放器，等待结果",
            ApplicationStatus.Acknowledged => "播放器已回应（不代表画面已到位）",
            ApplicationStatus.Released => "已结束",
            ApplicationStatus.NeedsReconciliation => "结果不明确，请查看当前画面后再决定",
            _ => "未知结果",
        };
    }

    internal static string Reason(string? reason, string fallback)
        => reason is not null && Reasons.TryGetValue(reason, out var text) ? text : fallback;

    internal static string Availability(SponsorAvailability? availability, string fallback)
        => availability is null ? fallback : availability.Reason switch
        {
            "MutationJournalRequired" => "需要本机操作记录支持（尚未接入）",
            "StoreHasNoChannelKeyedWhitelist" => "需要频道白名单存储支持（尚未接入）",
            "StatisticsStoreJoinRequired" => "统计尚未持久保存，重启后会清零",
            "NoS7Endpoint" => "服务器没有提供此功能",
            "Q4HasNoAtomicSplitMerge" => "暂不支持拆分或合并",
            "N3StampedFrameRequestHookRequired" => "逐帧控制尚未接入播放器",
            "N3StampedPreviewSeekHookRequired" => "预览播放尚未接入播放器",
            "PureProjectionOnlyAdaptersNotWired" => "列表和弹幕过滤尚未接入",
            "ActualVerifiedUpUidRequiredForCurrentChannel" => "需要播放页确认 UP 主 UID 后才对当前视频生效",
            "ReceiptWatermarkCapacityReached" => "统计记录已满，新的跳过不再计入",
            "ObservedStatisticsNotYetDurable" => "较新的统计还没有写入本机",
            "ActualTerminalReceiptsOnly" => "只统计播放器实际确认的跳转和加速",
            "MutationJournalCapacityReached" => "操作记录已满，暂停此类操作",
            "AuxiliaryStorageCapacityReached" => "本机附加数据已达到大小上限",
            "ExplicitIntentAndOptInsRequired" => "需要你确认并开启相应许可",
            "LocalChangesNotDurable" => "还有修改没有写入本机",
            "NativeTaskAcknowledgementOnly" => "播放器只回应是否接收，不保证逐帧精确",
            "EditorPlayerPortUnavailable" => "播放器还没有接入标注控制",
            "EditorAdmissionClosedOrStale" => "播放器未就绪",
            "AnotherPlayerOwnsEditor" => "另一个播放窗口正在标注",
            "ActualPausedFrameCapabilityRequired" => "暂停视频后可逐帧调整",
            "PreparedIntervalNativePreview" => "播放已准备好的草稿区间",
            "PreviewCleanupTokenStillOwned" => "请先结束当前预览",
            "CurrentPreparedPreviewAndExactSeekRequired" => "先点击草稿的“预览”准备区间",
            "ConditionalReturnByCleanupToken" => "回到预览前的位置",
            "NoOwnedPreviewCleanup" => "没有正在进行的预览",
            "ExplicitRelinquishWithoutPositionRestore" => "停在当前位置，不返回",
            "CurrentLeaseBaselineRelease" => "恢复静音前的状态",
            "LegacyConfirmedMuteUndoIntentOnly" => "请求取消这次静音",
            "CurrentOwnedMuteLeaseRequired" => "当前没有由空降助手保持的静音",
            "MuteWorkPending" => "静音操作还在进行中",
            "MuteLeaseNeedsReconciliation" => "静音状态需要先核对",
            "LeaseReleasePortRequired" => "播放器还没有接入取消静音",
            "RendererAppearanceAndHitTestUnavailable" => "弹幕渲染器尚未提供出现时间和点击信息",
            _ => fallback,
        };
}
