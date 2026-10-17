// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态条目视图模型：空降助手推广标记（只影响本机显示）.
/// </summary>
public sealed partial class MomentItemViewModel
{
    private SponsorContentCardState? _sponsorContent;

    /// <summary>
    /// 推广标记状态。随条目存在；判断由卡片上的 SponsorContentNotice 在可见时发起。
    /// “是否在 UP 主个人空间”只来自宿主页面/容器的显式声明（SponsorContentPlace），未声明时为 null；
    /// 从不根据卡片样式或作者推断。
    /// </summary>
    public SponsorContentCardState SponsorContent
        => _sponsorContent ??= new SponsorContentCardState(
            (surface, revision, authorSpace, body, replies) => surface == ContentSurface.Dynamic
                ? SponsorSdkContentAdapter.FromMoment(Data, revision, authorSpace, body, replies)
                : throw new ArgumentOutOfRangeException(nameof(surface)));
}
