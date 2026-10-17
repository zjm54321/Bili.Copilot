// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 播放器浮层上的紧凑提示条：当前提示（跳过 / 立即跳过 / 再次跳过 / 跳转）、最近实际效果（撤销 / 取消静音）、
/// 第一条需确认的恢复项。宽度上限 400，适配窗口内与全屏；没有内容时自动折叠。
/// </summary>
public sealed partial class SponsorNoticeStrip : UserControl
{
    /// <summary>ViewModel 依赖属性.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SponsorPlaybackViewModel), typeof(SponsorNoticeStrip), new PropertyMetadata(default(SponsorPlaybackViewModel)));

    /// <summary>Initializes a new instance of the <see cref="SponsorNoticeStrip"/> class.</summary>
    public SponsorNoticeStrip() => InitializeComponent();

    /// <summary>播放视图模型（由宿主绑定，同一附加 ID 与面板共用）.</summary>
    public SponsorPlaybackViewModel? ViewModel
    {
        get => (SponsorPlaybackViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
