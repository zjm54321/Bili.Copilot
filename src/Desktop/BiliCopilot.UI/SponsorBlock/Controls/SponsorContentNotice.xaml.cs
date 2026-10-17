// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 动态卡片 / 评论上的推广标记，同时是这条内容判断的生命周期所有者.
/// </summary>
/// <remarks>
/// 只在“已加载 + 有状态 + 界面类型明确（动态/评论/回复）”时挂接；卸载、换条目、换界面类型、
/// 换宿主上下文（<see cref="SponsorContentPlace"/>，加载/对齐时从可视树祖先读取）都会先撤销自己的判断和所有权，
/// 再按新的目标重新判断。<see cref="Surface"/> 为 <see cref="ContentSurface.Unknown"/> 时不判断，
/// 控件保持折叠——宿主没有明确上下文时不做任何推断。应用没有登记判断服务时同样保持折叠。
/// </remarks>
public sealed partial class SponsorContentNotice : UserControl
{
    /// <summary><see cref="State"/> 依赖属性.</summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(SponsorContentCardState), typeof(SponsorContentNotice), new PropertyMetadata(default(SponsorContentCardState), OnTargetChanged));

    /// <summary><see cref="Surface"/> 依赖属性.</summary>
    public static readonly DependencyProperty SurfaceProperty =
        DependencyProperty.Register(nameof(Surface), typeof(ContentSurface), typeof(SponsorContentNotice), new PropertyMetadata(ContentSurface.Unknown, OnTargetChanged));

    /// <summary><see cref="IsCoverLayout"/> 依赖属性.</summary>
    public static readonly DependencyProperty IsCoverLayoutProperty =
        DependencyProperty.Register(nameof(IsCoverLayout), typeof(bool), typeof(SponsorContentNotice), new PropertyMetadata(false));

    private SponsorContentItemProjection? _projection;
    private SponsorContentCardState? _attachedState;
    private ContentSurface _attachedSurface;
    private bool? _attachedAuthorSpace;
    private bool _loaded;

    /// <summary>Initializes a new instance of the <see cref="SponsorContentNotice"/> class.</summary>
    public SponsorContentNotice()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>条目的推广标记状态（来自条目视图模型）.</summary>
    public SponsorContentCardState? State
    {
        get => (SponsorContentCardState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>宿主明确告知的内容界面：动态 / 评论 / 回复。未知则不判断.</summary>
    public ContentSurface Surface
    {
        get => (ContentSurface)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>放在视频封面区域时为 true（角标 / 占满封面的折叠占位）.</summary>
    public bool IsCoverLayout
    {
        get => (bool)GetValue(IsCoverLayoutProperty);
        set => SetValue(IsCoverLayoutProperty, value);
    }

    /// <summary>行内标题.</summary>
    public static string InlineTitle(bool hasLabel, string? label, bool bodyHidden, bool repliesHidden)
    {
        var title = hasLabel && !string.IsNullOrEmpty(label) ? label : "可能含推广";
        return bodyHidden ? title + " · 内容已折叠"
            : repliesHidden ? title + " · 回复已收起"
            : title;
    }

    /// <summary>封面占位标题.</summary>
    public static string FoldedTitle(string? label)
        => (string.IsNullOrEmpty(label) ? "可能含推广" : label) + "，内容已折叠";

    /// <summary>根水平对齐：封面角标贴左，其余拉伸.</summary>
    public static HorizontalAlignment RootHorizontal(bool cover, bool bodyHidden)
        => cover && !bodyHidden ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;

    /// <summary>根垂直对齐：封面占位拉伸，其余贴顶.</summary>
    public static VerticalAlignment RootVertical(bool cover, bool bodyHidden)
        => cover && bodyHidden ? VerticalAlignment.Stretch : VerticalAlignment.Top;

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SponsorContentNotice)d).Reconcile();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        Reconcile();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        Reconcile();
    }

    // 只做“当前应负责什么”的对齐：目标没变就不重复判断。
    private void Reconcile()
    {
        var state = _loaded ? State : null;
        var surface = Surface;
        if (surface is not (ContentSurface.Dynamic or ContentSurface.Comment or ContentSurface.Reply))
        {
            state = null;
        }

        // 宿主的显式上下文：最近的祖先声明；没有声明为 null。只在已加载时读取（未加载时没有可信的祖先）。
        var authorSpace = state is null ? null : SponsorContentPlace.ResolveAuthorSpace(this);

        if (ReferenceEquals(state, _attachedState) && surface == _attachedSurface && authorSpace == _attachedAuthorSpace
            && (state is null || _projection is not null))
        {
            return;
        }

        _projection?.Detach();
        _attachedState = null;
        _attachedAuthorSpace = null;
        if (state is null)
        {
            return;
        }

        _projection ??= CreateProjection();
        if (_projection is null)
        {
            return;
        }

        _attachedState = state;
        _attachedSurface = surface;
        _attachedAuthorSpace = authorSpace;
        _projection.Attach(state, surface, authorSpace);
    }

    private SponsorContentItemProjection? CreateProjection()
    {
        if (DispatcherQueue is not { } queue)
        {
            return null;
        }

        try
        {
            return new SponsorContentItemProjection(queue, this.Get<SponsorContentProjectionService>());
        }
        catch (InvalidOperationException)
        {
            // 应用没有登记判断服务：不创建替代品，标记保持隐藏。
            return null;
        }
    }
}
