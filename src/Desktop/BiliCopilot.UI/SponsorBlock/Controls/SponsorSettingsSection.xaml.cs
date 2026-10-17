// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 设置页中的“空降助手”分组：总开关、联网/观看上报许可、11 个分类、跳过方式、更多选项、身份统计与备份。
/// 宿主负责在界面线程上调用 <c>SponsorSettingsViewModel.Bind</c>，并在离开设置页时 <c>Unbind</c>。
/// </summary>
public sealed partial class SponsorSettingsSection : UserControl
{
    /// <summary>ViewModel 依赖属性.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SponsorSettingsViewModel), typeof(SponsorSettingsSection), new PropertyMetadata(default(SponsorSettingsViewModel)));

    /// <summary>Initializes a new instance of the <see cref="SponsorSettingsSection"/> class.</summary>
    public SponsorSettingsSection() => InitializeComponent();

    /// <summary>设置视图模型.</summary>
    public SponsorSettingsViewModel? ViewModel
    {
        get => (SponsorSettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
