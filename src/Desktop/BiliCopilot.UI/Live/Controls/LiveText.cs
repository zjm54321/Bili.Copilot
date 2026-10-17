#nullable enable

using System.Globalization;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Local, fixed Chinese copy for the Live window. Kept here on purpose: the Live
/// controls do not touch the shared resw files. Session/chat error strings are never
/// shown verbatim; the UI derives its wording from typed state only.
/// </summary>
internal static class LiveText
{
    public const string LiveBadge = "直播";
    public const string UntitledRoom = "直播间";

    public const string Play = "播放";
    public const string Pause = "暂停";
    public const string Refresh = "回到最新";
    public const string RefreshName = "刷新到最新直播画面";
    public const string Volume = "音量";
    public const string OverlayToggle = "弹幕层";
    public const string OverlayToggleName = "显示或隐藏画面上的弹幕";
    public const string ChatToggle = "弹幕列表";
    public const string ChatToggleName = "显示或隐藏右侧弹幕列表";

    public const string ChatHeader = "弹幕列表";
    public const string EmptyConnected = "还没有新弹幕";
    public const string JumpToLatest = "回到最新弹幕";
    public const string ComposerName = "弹幕内容";
    public const string ComposerPlaceholder = "发个弹幕…";
    public const string ComposerPlaceholderUnavailable = "现在还不能发送弹幕";
    public const string SendName = "发送弹幕";

    public const string CommandFailed = "操作没有完成，请稍后再试。";
    public const string VolumeFailed = "音量没有调整成功。";

    public const string Sending = "正在发送…";
    public const string SendAccepted = "已发送。";
    public const string SendUnavailable = "现在还不能发送弹幕，请等直播和弹幕连接就绪后再试。";
    public const string SendNotSent = "弹幕没有发出，内容已保留。请检查登录状态后再试。";
    public const string SendUnknown = "无法确认弹幕是否已发出。请先看看弹幕列表，确认没有发出后再手动发送。";
    public const string AnonymousUser = "观众";

    public static string SendRejected(int? apiCode)
        => apiCode is { } code
            ? string.Format(CultureInfo.CurrentCulture, "弹幕被拒绝（代码 {0}），内容已保留。", code)
            : "弹幕被拒绝，内容已保留。";

    public static string NewMessages(int count)
        => count <= 0
            ? JumpToLatest
            : count > 99 ? "99+ 条新弹幕" : string.Format(CultureInfo.CurrentCulture, "{0} 条新弹幕", count);

    public static string VolumeValue(double percent)
        => string.Format(CultureInfo.CurrentCulture, "{0:0}", percent);

    public static string Playback(LivePlaybackState state) => state switch
    {
        LivePlaybackState.Idle => "准备中",
        LivePlaybackState.Opening => "正在加载直播…",
        LivePlaybackState.Playing => "直播中",
        LivePlaybackState.Paused => "已暂停",
        LivePlaybackState.Ended => "直播已结束或连接中断",
        LivePlaybackState.Failed => "直播加载失败",
        LivePlaybackState.Closing => "正在关闭…",
        LivePlaybackState.Closed => "已关闭",
        _ => string.Empty,
    };

    /// <summary>Longer guidance shown on top of the video (inside the overlay island).</summary>
    public static string PlaybackHint(LivePlaybackState state) => state switch
    {
        LivePlaybackState.Idle => "正在准备直播间…",
        LivePlaybackState.Opening => "正在加载直播…",
        LivePlaybackState.Paused => "已暂停。继续播放时会回到最新画面。",
        LivePlaybackState.Ended => "直播已结束或连接中断。可以点“回到最新”重新加载。",
        LivePlaybackState.Failed => "直播没有加载出来。可以点“回到最新”再试一次。",
        LivePlaybackState.Closing => "正在关闭…",
        LivePlaybackState.Closed => "直播已关闭。",
        _ => string.Empty,
    };

    public static string Chat(LiveChatState state) => state switch
    {
        LiveChatState.Idle => "弹幕未连接",
        LiveChatState.Connecting => "正在连接弹幕…",
        LiveChatState.Authenticating => "正在验证弹幕连接…",
        LiveChatState.Connected => "弹幕已连接",
        LiveChatState.Retrying => "弹幕连接中断，正在重连…",
        LiveChatState.Unavailable => "弹幕暂时不可用",
        LiveChatState.Failed => "弹幕连接失败",
        LiveChatState.Closed => "弹幕已断开",
        _ => string.Empty,
    };
}
