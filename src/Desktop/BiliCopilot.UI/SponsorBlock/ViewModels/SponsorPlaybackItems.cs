// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>以稳定键复用的列表项，避免频繁的播放快照导致焦点和滚动位置丢失.</summary>
public interface ISponsorKeyedItem
{
    /// <summary>稳定键.</summary>
    string Key { get; }
}

/// <summary>播放提示（来自 N3 计划中的实际提示，不是推测）.</summary>
public sealed partial class SponsorNoticeItem : ObservableObject, ISponsorKeyedItem
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private Color _categoryColor;

    [ObservableProperty]
    private string _primaryText = string.Empty;

    [ObservableProperty]
    private bool _hasPrimary;

    [ObservableProperty]
    private bool _isPrimaryEnabled;

    [ObservableProperty]
    private string _primaryToolTip = string.Empty;

    [ObservableProperty]
    private bool _hasSkipNow;

    [ObservableProperty]
    private bool _isSkipNowEnabled;

    internal SponsorNoticeItem(string key, Func<SponsorNoticeItem, bool, Task> run)
    {
        Key = key;
        PrimaryCommand = new AsyncRelayCommand(() => run(this, false));
        SkipNowCommand = new AsyncRelayCommand(() => run(this, true));
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>主操作（跳过 / 静音 / 再次跳过 / 跳转）.</summary>
    public IAsyncRelayCommand PrimaryCommand { get; }

    /// <summary>“立即跳过”（Skip now）：明确的直接跳转意图，与“跳过”区分.</summary>
    public IAsyncRelayCommand SkipNowCommand { get; }

    internal PlaybackUiAction Action { get; set; }

    internal PlaybackSegmentKey? Target { get; set; }

    internal PlaybackOwner? Owner { get; set; }
}

/// <summary>当前视频的一条原始片段记录及其本机/远程操作.</summary>
public sealed partial class SponsorSegmentItem : ObservableObject, ISponsorKeyedItem
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private Color _categoryColor;

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private string _actionText = string.Empty;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _isLocallyHidden;

    [ObservableProperty]
    private bool _isLocallyDownvoted;

    [ObservableProperty]
    private bool _isExternal;

    [ObservableProperty]
    private bool _isExternalExcluded;

    [ObservableProperty]
    private bool _canJump;

    [ObservableProperty]
    private bool _canUseRemote;

    [ObservableProperty]
    private string _remoteUnavailableText = string.Empty;

    [ObservableProperty]
    private bool _isDimmed;

    internal SponsorSegmentItem(string key, SponsorPlaybackViewModel owner)
    {
        Key = key;
        ToggleHiddenCommand = new AsyncRelayCommand(() => owner.SetExclusionAsync(this, Persistence.LocalExclusionKind.Hidden, !IsLocallyHidden));
        ToggleDownvoteCommand = new AsyncRelayCommand(() => owner.SetExclusionAsync(this, Persistence.LocalExclusionKind.Downvoted, !IsLocallyDownvoted));
        ToggleExternalCommand = new AsyncRelayCommand(() => owner.SetExclusionAsync(this, Persistence.LocalExclusionKind.ExternalSourceExcluded, !IsExternalExcluded));
        JumpCommand = new AsyncRelayCommand(() => owner.JumpAsync(this));
        VoteUpCommand = new AsyncRelayCommand(() => owner.RequestRemoteAsync(this, SponsorRemoteKind.VoteUp));
        VoteDownCommand = new AsyncRelayCommand(() => owner.RequestRemoteAsync(this, SponsorRemoteKind.VoteDown));
        ChangeCategoryCommand = new AsyncRelayCommand(() => owner.RequestRemoteAsync(this, SponsorRemoteKind.ChangeCategory));
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>本机隐藏 / 取消隐藏.</summary>
    public IAsyncRelayCommand ToggleHiddenCommand { get; }

    /// <summary>本机标记为错误 / 取消.</summary>
    public IAsyncRelayCommand ToggleDownvoteCommand { get; }

    /// <summary>排除 / 恢复此 YouTube 搬运来源.</summary>
    public IAsyncRelayCommand ToggleExternalCommand { get; }

    /// <summary>跳转到精彩时刻.</summary>
    public IAsyncRelayCommand JumpCommand { get; }

    /// <summary>向服务器投赞同票（需确认）.</summary>
    public IAsyncRelayCommand VoteUpCommand { get; }

    /// <summary>向服务器投反对票（需确认）.</summary>
    public IAsyncRelayCommand VoteDownCommand { get; }

    /// <summary>向服务器建议更改分类（需确认）.</summary>
    public IAsyncRelayCommand ChangeCategoryCommand { get; }

    /// <summary>本机隐藏菜单文字.</summary>
    public string HiddenMenuText => IsLocallyHidden ? "取消本机隐藏" : "在本机隐藏";

    /// <summary>本机标记菜单文字.</summary>
    public string DownvoteMenuText => IsLocallyDownvoted ? "取消“本机标记为错误”" : "本机标记为错误";

    /// <summary>来源排除菜单文字.</summary>
    public string ExternalMenuText => IsExternalExcluded ? "恢复使用此搬运来源" : "排除此搬运来源";

    internal PlaybackSegmentKey? SegmentKey { get; set; }

    internal SponsorSegment? Segment { get; set; }

    partial void OnIsLocallyHiddenChanged(bool value) => OnPropertyChanged(nameof(HiddenMenuText));

    partial void OnIsLocallyDownvotedChanged(bool value) => OnPropertyChanged(nameof(DownvoteMenuText));

    partial void OnIsExternalExcludedChanged(bool value) => OnPropertyChanged(nameof(ExternalMenuText));
}

/// <summary>时间轴标记（仅来自 N3 计划中的实际标记）.</summary>
public sealed class SponsorTimelineMarkerItem
{
    internal SponsorTimelineMarkerItem(double start, double end, bool isPoint, Color color, string toolTip, bool isDimmed)
    {
        StartRatio = start;
        EndRatio = end;
        IsPoint = isPoint;
        CategoryColor = color;
        ToolTip = toolTip;
        IsDimmed = isDimmed;
    }

    /// <summary>起点（0..1，相对于实际时长）.</summary>
    public double StartRatio { get; }

    /// <summary>终点（0..1）.</summary>
    public double EndRatio { get; }

    /// <summary>是否为精彩时刻点.</summary>
    public bool IsPoint { get; }

    /// <summary>颜色.</summary>
    public Color CategoryColor { get; }

    /// <summary>提示文字.</summary>
    public string ToolTip { get; }

    /// <summary>仅显示（不处理）的分类以低透明度呈现.</summary>
    public bool IsDimmed { get; }
}

/// <summary>需要用户显式确认的恢复项（失败或未知的静音/速度/跳转）。从不自动执行.</summary>
public sealed partial class SponsorRecoveryItem : ObservableObject, ISponsorKeyedItem
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _actionText = string.Empty;

    internal SponsorRecoveryItem(string key, Func<SponsorRecoveryItem, Task> run)
    {
        Key = key;
        Command = new AsyncRelayCommand(() => run(this));
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>恢复操作.</summary>
    public IAsyncRelayCommand Command { get; }

    internal PlaybackLeaseField? Field { get; set; }

    internal string Token { get; set; } = string.Empty;
}

/// <summary>远程操作类型（全部需要确认，全部受操作记录能力约束）.</summary>
internal enum SponsorRemoteKind
{
    VoteUp,
    VoteDown,
    ChangeCategory,
    ReportViewed,
}

internal static class SponsorKeyedSync
{
    /// <summary>按键同步集合：复用已有项、移除多余项、保持顺序；上限由调用方保证.</summary>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<string> keys, Func<string, T> create, Action<T, int> update)
        where T : class, ISponsorKeyedItem
    {
        var existing = target.ToDictionary(item => item.Key, StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(target[i].Key))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var item = existing.TryGetValue(keys[i], out var found) ? found : create(keys[i]);
            update(item, i);
            var index = target.IndexOf(item);
            if (index < 0)
            {
                target.Insert(Math.Min(i, target.Count), item);
            }
            else if (index != i)
            {
                target.Move(index, i);
            }
        }
    }
}
