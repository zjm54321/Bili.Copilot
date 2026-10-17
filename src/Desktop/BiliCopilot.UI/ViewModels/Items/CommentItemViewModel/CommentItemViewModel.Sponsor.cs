// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 评论条目视图模型：空降助手推广标记（只影响本机显示）.
/// </summary>
public sealed partial class CommentItemViewModel
{
    private SponsorContentCardState? _sponsorContent;

    /// <summary>
    /// 推广标记状态。是评论还是回复由所在面板明确告知（CommentItemControl.SponsorSurface），
    /// 适配器只接受 Comment / Reply；“是否在 UP 主个人空间”只来自宿主的显式声明，未声明为 null。
    /// </summary>
    public SponsorContentCardState SponsorContent
        => _sponsorContent ??= new SponsorContentCardState(
            (surface, revision, authorSpace, body, replies) => SponsorSdkContentAdapter.FromComment(Data, revision, surface, authorSpace, body, replies));
}
