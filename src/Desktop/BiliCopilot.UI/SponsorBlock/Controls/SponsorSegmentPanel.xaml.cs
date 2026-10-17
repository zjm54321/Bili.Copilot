// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 片段面板：获取状态、待确认的恢复项、最近效果、当前提示，以及当前视频的片段列表
/// （本机隐藏/标记、排除搬运来源、需确认的投票与分类建议）。自带纵向滚动，适合侧栏或弹出层。
/// </summary>
public sealed partial class SponsorSegmentPanel : UserControl
{
    /// <summary>ViewModel 依赖属性.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SponsorPlaybackViewModel), typeof(SponsorSegmentPanel), new PropertyMetadata(default(SponsorPlaybackViewModel)));

    /// <summary>Initializes a new instance of the <see cref="SponsorSegmentPanel"/> class.</summary>
    public SponsorSegmentPanel() => InitializeComponent();

    /// <summary>播放视图模型.</summary>
    public SponsorPlaybackViewModel? ViewModel
    {
        get => (SponsorPlaybackViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
