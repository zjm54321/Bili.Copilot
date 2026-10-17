// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.WinUIKernel.Share.Base;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 评论控件.
/// </summary>
public sealed partial class CommentItemControl : CommentItemControlBase
{
    /// <summary>
    /// <see cref="ShowMoreCommand"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ShowMoreCommandProperty =
        DependencyProperty.Register(nameof(ShowMoreCommand), typeof(ICommand), typeof(CommentItemControl), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="IsMoreEnabled"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsMoreEnabledProperty =
        DependencyProperty.Register(nameof(IsMoreEnabled), typeof(bool), typeof(CommentItemControl), new PropertyMetadata(true));

    /// <summary>
    /// <see cref="SponsorSurface"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty SponsorSurfaceProperty =
        DependencyProperty.Register(nameof(SponsorSurface), typeof(ContentSurface), typeof(CommentItemControl), new PropertyMetadata(ContentSurface.Unknown));

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentItemControl"/> class.
    /// </summary>
    public CommentItemControl()
        => InitializeComponent();

    /// <summary>
    /// 显示更多命令.
    /// </summary>
    public ICommand ShowMoreCommand
    {
        get => (ICommand)GetValue(ShowMoreCommandProperty);
        set => SetValue(ShowMoreCommandProperty, value);
    }

    /// <summary>
    /// 是否启用更多.
    /// </summary>
    public bool IsMoreEnabled
    {
        get => (bool)GetValue(IsMoreEnabledProperty);
        set => SetValue(IsMoreEnabledProperty, value);
    }

    /// <summary>
    /// 空降助手推广标记所用的内容界面（评论 / 回复），由所在面板明确设置；默认未知，不判断.
    /// </summary>
    public ContentSurface SponsorSurface
    {
        get => (ContentSurface)GetValue(SponsorSurfaceProperty);
        set => SetValue(SponsorSurfaceProperty, value);
    }
}

/// <summary>
/// Comment item control base.
/// </summary>
public abstract class CommentItemControlBase : LayoutUserControlBase<CommentItemViewModel>;
