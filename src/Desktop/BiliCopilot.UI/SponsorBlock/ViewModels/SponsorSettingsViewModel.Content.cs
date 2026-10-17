// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 动态和评论中的推广内容偏好（本机保存的一整组选项）。
/// 表单是草稿：只有点“保存”才写入，写入时在点按时读取当前修订号，过期只提示不重放。
/// 关键词表达式在保存或检查时于后台线程用有界匹配器验证，不在界面线程执行，不记录表达式内容。
/// 弹幕时间辅助选项保持原值一起写回（成对保存），在播放器能提供弹幕出现时间与点击信息前只读显示。
/// </summary>
public sealed partial class SponsorSettingsViewModel
{
    private const int MaximumSampleLength = 2000;
    private static readonly IReadOnlyList<ContentCategoryAction> ContentActionChoices =
        (ContentCategoryAction[])[ContentCategoryAction.Disabled, ContentCategoryAction.Mark, ContentCategoryAction.Hide];

    private SponsorContentPreferences _contentBaseline = SponsorContentPreferences.Default;
    private bool _projectingContent;
    private long _contentEpoch;

    [ObservableProperty]
    private bool _isContentReady;

    [ObservableProperty]
    private bool _contentFeatureEnabled;

    [ObservableProperty]
    private bool _contentDynamicEnabled;

    [ObservableProperty]
    private bool _contentAuthorSpaceEnabled;

    [ObservableProperty]
    private bool _contentCommentEnabled;

    [ObservableProperty]
    private bool _contentBlockReplies;

    [ObservableProperty]
    private bool _contentIgnoreWhitelist;

    [ObservableProperty]
    private int _goodsActionIndex;

    [ObservableProperty]
    private int _forwardedGoodsActionIndex;

    [ObservableProperty]
    private int _suspicionActionIndex;

    [ObservableProperty]
    private double _suspicionThreshold = 1;

    [ObservableProperty]
    private string _dynamicRegex = string.Empty;

    [ObservableProperty]
    private double _regexPatternLength = 4096;

    [ObservableProperty]
    private double _regexInputLength = 32768;

    [ObservableProperty]
    private double _regexMatches = 256;

    [ObservableProperty]
    private double _regexTimeout = 25;

    [ObservableProperty]
    private string _regexSample = string.Empty;

    [ObservableProperty]
    private string _regexCheckText = string.Empty;

    [ObservableProperty]
    private bool _isContentDirty;

    [ObservableProperty]
    private bool _isContentBusy;

    [ObservableProperty]
    private bool _hasContentError;

    [ObservableProperty]
    private string _contentErrorText = string.Empty;

    [ObservableProperty]
    private string _contentFeedbackText = string.Empty;

    [ObservableProperty]
    private string _contentSummaryText = "默认关闭";

    [ObservableProperty]
    private bool _isContentResetConfirmVisible;

    [ObservableProperty]
    private bool _danmakuAidSkip;

    [ObservableProperty]
    private bool _danmakuAidAuto;

    [ObservableProperty]
    private bool _danmakuAidTimestamp;

    [ObservableProperty]
    private bool _danmakuAidSubmissionMenu;

    [ObservableProperty]
    private bool _danmakuAidCheckNearby;

    [ObservableProperty]
    private bool _danmakuAidDisableSkipping;

    [ObservableProperty]
    private bool _isDanmakuAidAvailable;

    [ObservableProperty]
    private string _danmakuAidNote = string.Empty;

    /// <summary>推广内容的处理方式选项.</summary>
    public IReadOnlyList<string> ContentActionLabels { get; } = (string[])["不处理", "只标记", "折叠"];

    /// <summary>可以保存：有修改、没有正在保存、本机存储就绪.</summary>
    public bool CanSaveContent => IsContentReady && IsContentDirty && !IsContentBusy;

    private void BindContent()
    {
        _contentEpoch++;
        var (available, reason) = SponsorUiWork.DanmakuAid(this);
        IsDanmakuAidAvailable = available;
        DanmakuAidNote = available
            ? "播放器已支持弹幕时间辅助；这里暂时只显示已保存的选项。"
            : "弹幕时间辅助暂不可用：" + SponsorUiText.Availability(new SponsorAvailability(SponsorFeature.DanmakuProjection, false, reason), "播放器尚未提供所需信息")
                + "。下面显示的是已保存的选项，会原样保留。";
    }

    private void ResetContent()
    {
        _contentEpoch++;
        IsContentReady = false;
        IsContentBusy = false;
        IsContentDirty = false;
        HasContentError = false;
        ContentErrorText = string.Empty;
        ContentFeedbackText = string.Empty;
        RegexCheckText = string.Empty;
        IsContentResetConfirmVisible = false;
        ProjectContent(SponsorContentPreferences.Default);
    }

    private void ApplyContent(SponsorApplicationView view)
    {
        IsContentReady = !view.Closed && view.StoreVersion is not null;
        var effective = SponsorContentPreferences.GetEffective(view.Auxiliary);
        ProjectDanmakuAid(effective.DanmakuAid);
        if (!IsContentDirty && !IsContentBusy)
        {
            ProjectContent(effective);
        }
        else if (effective != _contentBaseline)
        {
            ContentFeedbackText = "本机保存的设置刚刚有变化；保存时会以你现在看到的内容为准。";
        }

        ContentSummaryText = !effective.Content.FeatureEnabled ? "已关闭"
            : effective.Content.DynamicEnabled && effective.Content.CommentEnabled ? "动态和评论"
            : effective.Content.DynamicEnabled ? "仅动态"
            : effective.Content.CommentEnabled ? "仅评论" : "已开启，但动态和评论都未选择";
        OnPropertyChanged(nameof(CanSaveContent));
    }

    private void ProjectContent(SponsorContentPreferences value)
    {
        _projectingContent = true;
        try
        {
            _contentBaseline = value;
            var content = value.Content;
            ContentFeatureEnabled = content.FeatureEnabled;
            ContentDynamicEnabled = content.DynamicEnabled;
            ContentAuthorSpaceEnabled = content.DynamicInAuthorSpaceEnabled;
            ContentCommentEnabled = content.CommentEnabled;
            ContentBlockReplies = content.BlockReplies;
            ContentIgnoreWhitelist = content.IgnoreWhitelist;
            GoodsActionIndex = ActionIndex(content.GoodsAction);
            ForwardedGoodsActionIndex = ActionIndex(content.ForwardedGoodsAction);
            SuspicionActionIndex = ActionIndex(content.SuspicionAction);
            SuspicionThreshold = content.SuspicionHideThreshold;
            DynamicRegex = content.DynamicRegex;
            RegexPatternLength = content.RegexLimits.MaximumPatternLength;
            RegexInputLength = content.RegexLimits.MaximumInputLength;
            RegexMatches = content.RegexLimits.MaximumMatches;
            RegexTimeout = content.RegexLimits.TimeoutMilliseconds;
            IsContentDirty = false;
            ProjectDanmakuAid(value.DanmakuAid);
        }
        finally
        {
            _projectingContent = false;
        }

        OnPropertyChanged(nameof(CanSaveContent));
    }

    private void ProjectDanmakuAid(PersistedDanmakuAidOptions value)
    {
        DanmakuAidSkip = value.EnableDanmakuSkip;
        DanmakuAidAuto = value.EnableAuto;
        DanmakuAidTimestamp = value.EnableClickableTimestamp;
        DanmakuAidSubmissionMenu = value.EnableSubmissionMenu;
        DanmakuAidCheckNearby = value.CheckNearbySegments;
        DanmakuAidDisableSkipping = value.DisableSkipping;
    }

    private static int ActionIndex(ContentCategoryAction action)
    {
        for (var i = 0; i < ContentActionChoices.Count; i++)
        {
            if (ContentActionChoices[i] == action)
            {
                return i;
            }
        }

        return 0;
    }

    private static ContentCategoryAction? ActionAt(int index)
        => index >= 0 && index < ContentActionChoices.Count ? ContentActionChoices[index] : null;

    private static int? Whole(double value, int minimum, int maximum)
        => double.IsFinite(value) && value == Math.Floor(value) && value >= minimum && value <= maximum ? (int)value : null;

    /// <summary>从表单生成内容选项；有无效值时返回 null 并给出原因.</summary>
    private PersistedContentOptions? BuildContent(out string? problem)
    {
        problem = null;
        var goods = ActionAt(GoodsActionIndex);
        var forwarded = ActionAt(ForwardedGoodsActionIndex);
        var suspicion = ActionAt(SuspicionActionIndex);
        var threshold = Whole(SuspicionThreshold, 0, int.MaxValue);
        var pattern = Whole(RegexPatternLength, 1, 4096);
        var input = Whole(RegexInputLength, 1, 32768);
        var matches = Whole(RegexMatches, 1, 256);
        var timeout = Whole(RegexTimeout, 1, 100);
        if (goods is null || forwarded is null || suspicion is null)
        {
            problem = "请选择每一类内容的处理方式";
            return null;
        }

        if (threshold is null)
        {
            problem = "“折叠所需关键词数”需要是 0 或更大的整数";
            return null;
        }

        if (pattern is null || input is null || matches is null || timeout is null)
        {
            problem = "匹配限制超出允许范围：表达式长度 1–4096、文本长度 1–32768、匹配次数 1–256、时限 1–100 毫秒";
            return null;
        }

        var regex = DynamicRegex ?? string.Empty;
        if (regex.Length > pattern.Value)
        {
            problem = $"关键词表达式超过了 {pattern.Value} 个字符";
            return null;
        }

        return new PersistedContentOptions(
            ContentFeatureEnabled, ContentDynamicEnabled, ContentAuthorSpaceEnabled, ContentCommentEnabled,
            ContentBlockReplies, ContentIgnoreWhitelist, goods.Value, forwarded.Value, suspicion.Value,
            threshold.Value, regex, new PersistedTextRegexLimits(pattern.Value, input.Value, matches.Value, timeout.Value));
    }

    private void MarkContentChanged()
    {
        if (_projectingContent)
        {
            return;
        }

        var built = BuildContent(out _);
        IsContentDirty = built is null || built != _contentBaseline.Content;
        HasContentError = false;
        ContentFeedbackText = string.Empty;
        OnPropertyChanged(nameof(CanSaveContent));
    }

    partial void OnContentFeatureEnabledChanged(bool value) => MarkContentChanged();

    partial void OnContentDynamicEnabledChanged(bool value) => MarkContentChanged();

    partial void OnContentAuthorSpaceEnabledChanged(bool value) => MarkContentChanged();

    partial void OnContentCommentEnabledChanged(bool value) => MarkContentChanged();

    partial void OnContentBlockRepliesChanged(bool value) => MarkContentChanged();

    partial void OnContentIgnoreWhitelistChanged(bool value) => MarkContentChanged();

    partial void OnGoodsActionIndexChanged(int value) => MarkContentChanged();

    partial void OnForwardedGoodsActionIndexChanged(int value) => MarkContentChanged();

    partial void OnSuspicionActionIndexChanged(int value) => MarkContentChanged();

    partial void OnSuspicionThresholdChanged(double value) => MarkContentChanged();

    partial void OnDynamicRegexChanged(string value)
    {
        RegexCheckText = string.Empty;
        MarkContentChanged();
    }

    partial void OnRegexPatternLengthChanged(double value) => MarkContentChanged();

    partial void OnRegexInputLengthChanged(double value) => MarkContentChanged();

    partial void OnRegexMatchesChanged(double value) => MarkContentChanged();

    partial void OnRegexTimeoutChanged(double value) => MarkContentChanged();

    partial void OnIsContentReadyChanged(bool value) => OnPropertyChanged(nameof(CanSaveContent));

    partial void OnIsContentBusyChanged(bool value) => OnPropertyChanged(nameof(CanSaveContent));

    private static string RegexStatusText(TextRegexResult result) => result.Status switch
    {
        TextRegexStatus.Success => result.Matches.IsDefaultOrEmpty
            ? "表达式可以使用（示例文本中没有匹配）"
            : $"表达式可以使用，示例文本中匹配到 {result.Matches.Length} 处："
                + string.Join("、", result.Matches.Take(5).Select(m => m.Value.Length > 20 ? m.Value[..20] + "…" : m.Value)),
        TextRegexStatus.MissingInput => "请填写表达式",
        TextRegexStatus.InvalidPattern => "表达式写法有误",
        TextRegexStatus.Unsupported => "表达式用到了暂不支持的写法（支持 /表达式/标记 形式，标记仅限 g、i、m、s）",
        TextRegexStatus.LimitExceeded => "表达式或示例文本超出了匹配限制",
        TextRegexStatus.TimedOut => "匹配用时超出限制，请简化表达式",
        _ => "无法检查表达式",
    };

    private static Task<TextRegexResult> ValidateOffUiAsync(string pattern, string sample, TextRegexLimits limits)
        => Task.Run(() => BoundedTextRegex.Match(pattern, sample, limits, allowSlashFlags: true));

    /// <summary>在后台用示例文本检查关键词表达式（不保存，不联网）.</summary>
    [RelayCommand]
    private Task CheckContentRegexAsync() => SponsorUiWork.Track(this, CheckContentRegexWorkAsync());

    private async Task CheckContentRegexWorkAsync()
    {
        var content = BuildContent(out var problem);
        if (content is null)
        {
            RegexCheckText = problem ?? string.Empty;
            return;
        }

        var sample = RegexSample ?? string.Empty;
        if (sample.Length > MaximumSampleLength)
        {
            sample = sample[..MaximumSampleLength];
        }

        var epoch = _contentEpoch;
        var pattern = content.DynamicRegex;
        RegexCheckText = "正在检查…";
        try
        {
            var result = await ValidateOffUiAsync(pattern, sample, content.RegexLimits.ToOptions());
            if (epoch == _contentEpoch && string.Equals(pattern, DynamicRegex, StringComparison.Ordinal))
            {
                RegexCheckText = RegexStatusText(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _contentEpoch)
            {
                RegexCheckText = "无法检查表达式";
            }
        }
    }

    /// <summary>恢复默认关键词表达式（只改表单，需保存才生效）.</summary>
    [RelayCommand]
    private void UseDefaultRegex() => DynamicRegex = ContentPromotionDefaults.DynamicRegex;

    /// <summary>放弃表单修改，回到本机已保存的内容.</summary>
    [RelayCommand]
    private void DiscardContent()
    {
        if (IsContentBusy)
        {
            return;
        }

        HasContentError = false;
        ContentFeedbackText = string.Empty;
        RegexCheckText = string.Empty;
        var view = _feed?.Current;
        ProjectContent(view is null ? _contentBaseline : SponsorContentPreferences.GetEffective(view.Auxiliary));
    }

    /// <summary>保存整组选项。先在后台验证表达式，再用点按时的修订号写入；失败不自动重试.</summary>
    [RelayCommand]
    private Task SaveContentAsync() => SponsorUiWork.Track(this, SaveContentWorkAsync());

    private async Task SaveContentWorkAsync()
    {
        if (_app is not { } app || !CanSaveContent)
        {
            return;
        }

        var content = BuildContent(out var problem);
        if (content is null)
        {
            ContentErrorText = problem ?? "设置无效";
            HasContentError = true;
            return;
        }

        var epoch = _contentEpoch;
        IsContentBusy = true;
        HasContentError = false;
        ContentFeedbackText = "正在保存…";
        try
        {
            var check = await ValidateOffUiAsync(content.DynamicRegex, string.Empty, content.RegexLimits.ToOptions());
            if (epoch != _contentEpoch)
            {
                return;
            }

            if (check.Status != TextRegexStatus.Success)
            {
                ContentFeedbackText = string.Empty;
                ContentErrorText = "没有保存：" + RegexStatusText(check);
                HasContentError = true;
                return;
            }

            // Complete pair: the time-aid half is carried from the latest saved value, never edited here.
            var snapshot = app.Snapshot;
            var current = SponsorContentPreferences.GetEffective(snapshot.Auxiliary);
            var next = current with { Content = content };
            var result = await app.SetContentPreferencesAsync(snapshot.Revision, next);
            if (epoch != _contentEpoch)
            {
                return;
            }

            if (result.Status == ApplicationStatus.Success)
            {
                ContentFeedbackText = "已保存";
                ProjectContent(next);
            }
            else
            {
                ContentFeedbackText = string.Empty;
                ContentErrorText = result.Status == ApplicationStatus.Stale
                    ? "设置刚刚有变化，没有保存。请确认后再点一次“保存”。"
                    : "没有保存：" + SponsorUiText.Result(result);
                HasContentError = true;
            }
        }
        catch (Exception)
        {
            if (epoch == _contentEpoch)
            {
                ContentFeedbackText = string.Empty;
                ContentErrorText = "没有保存，请稍后再试";
                HasContentError = true;
            }
        }
        finally
        {
            if (epoch == _contentEpoch)
            {
                IsContentBusy = false;
            }
        }
    }

    [RelayCommand]
    private void RequestResetContent() => IsContentResetConfirmVisible = IsContentReady && !IsContentBusy;

    [RelayCommand]
    private void CancelResetContent() => IsContentResetConfirmVisible = false;

    /// <summary>确认后把整组选项恢复为默认（包括弹幕时间辅助）.</summary>
    [RelayCommand]
    private Task ConfirmResetContentAsync() => SponsorUiWork.Track(this, ConfirmResetContentWorkAsync());

    private async Task ConfirmResetContentWorkAsync()
    {
        IsContentResetConfirmVisible = false;
        if (_app is not { } app || IsContentBusy || !IsContentReady)
        {
            return;
        }

        var epoch = _contentEpoch;
        IsContentBusy = true;
        HasContentError = false;
        try
        {
            var result = await app.ResetContentPreferencesAsync(app.Snapshot.Revision);
            if (epoch != _contentEpoch)
            {
                return;
            }

            if (result.Status == ApplicationStatus.Success)
            {
                ContentFeedbackText = "已恢复默认";
                ProjectContent(SponsorContentPreferences.GetEffective(app.Snapshot.Auxiliary));
            }
            else
            {
                ContentErrorText = result.Status == ApplicationStatus.Stale
                    ? "设置刚刚有变化，没有恢复。请再点一次。"
                    : "没有恢复：" + SponsorUiText.Result(result);
                HasContentError = true;
            }
        }
        catch (Exception)
        {
            if (epoch == _contentEpoch)
            {
                ContentErrorText = "没有恢复，请稍后再试";
                HasContentError = true;
            }
        }
        finally
        {
            if (epoch == _contentEpoch)
            {
                IsContentBusy = false;
            }
        }
    }
}
