// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// “片段 / 标注”两页的完整面板。宽度 300–440，适合播放页侧栏或全屏时的弹出层；
/// 两个视图模型由宿主用同一个附加 ID 绑定。
/// </summary>
public sealed partial class SponsorPlayerPanel : UserControl
{
    /// <summary>PlaybackViewModel 依赖属性.</summary>
    public static readonly DependencyProperty PlaybackViewModelProperty =
        DependencyProperty.Register(nameof(PlaybackViewModel), typeof(SponsorPlaybackViewModel), typeof(SponsorPlayerPanel), new PropertyMetadata(default(SponsorPlaybackViewModel)));

    /// <summary>EditorViewModel 依赖属性.</summary>
    public static readonly DependencyProperty EditorViewModelProperty =
        DependencyProperty.Register(nameof(EditorViewModel), typeof(SponsorEditorViewModel), typeof(SponsorPlayerPanel), new PropertyMetadata(default(SponsorEditorViewModel)));

    /// <summary>Initializes a new instance of the <see cref="SponsorPlayerPanel"/> class.</summary>
    public SponsorPlayerPanel() => InitializeComponent();

    /// <summary>播放视图模型.</summary>
    public SponsorPlaybackViewModel? PlaybackViewModel
    {
        get => (SponsorPlaybackViewModel?)GetValue(PlaybackViewModelProperty);
        set => SetValue(PlaybackViewModelProperty, value);
    }

    /// <summary>标注视图模型.</summary>
    public SponsorEditorViewModel? EditorViewModel
    {
        get => (SponsorEditorViewModel?)GetValue(EditorViewModelProperty);
        set => SetValue(EditorViewModelProperty, value);
    }

    /// <summary>切换到“标注”页（宿主快捷键或菜单可调用）.</summary>
    public void ShowEditor() => Selector.SelectedItem = EditorItem;

    /// <summary>切换到“片段”页.</summary>
    public void ShowSegments() => Selector.SelectedItem = SegmentsItem;

    private void OnSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // May fire during InitializeComponent before the panels are created.
        if (EditorPanel is null || SegmentPanel is null)
        {
            return;
        }

        var editor = sender.SelectedItem == EditorItem;
        EditorPanel.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        SegmentPanel.Visibility = editor ? Visibility.Collapsed : Visibility.Visible;
    }
}
