// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 标注面板：标记开始/结束、精彩时刻、整片标签、撤销、草稿编辑/删除/预览准备、提交与历史、用户名和搬运关联。
/// 逐帧与预览播放在能力接入前保持禁用并说明原因。自带纵向滚动，长表单收在折叠区内。
/// </summary>
public sealed partial class SponsorEditorPanel : UserControl
{
    /// <summary>ViewModel 依赖属性.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SponsorEditorViewModel), typeof(SponsorEditorPanel), new PropertyMetadata(default(SponsorEditorViewModel)));

    /// <summary>Initializes a new instance of the <see cref="SponsorEditorPanel"/> class.</summary>
    public SponsorEditorPanel() => InitializeComponent();

    /// <summary>标注视图模型.</summary>
    public SponsorEditorViewModel? ViewModel
    {
        get => (SponsorEditorViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
