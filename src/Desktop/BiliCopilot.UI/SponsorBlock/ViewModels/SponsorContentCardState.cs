// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 一张动态卡片或一条评论上的推广标记状态（只属于本机显示，不修改条目、不影响列表加载）.
/// </summary>
/// <remarks>
/// 由条目视图模型持有（随条目存在，复用的卡片控件换条目时状态不串）。效果只由
/// <see cref="SponsorContentItemProjection"/> 在确认结果仍然有效时写入；任何未确认的结果都只会
/// 清除效果（显示原内容），从不凭猜测折叠。手动“显示内容/显示回复”是本机的显示偏好，会立即生效，
/// 并让条目的本机版本号前进，使之前发出的判断全部作废。
/// </remarks>
public sealed partial class SponsorContentCardState : ObservableObject
{
    private readonly Func<ContentSurface, string, bool?, bool, bool, ContentPromotionInput> _input;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private bool _hasLabel;

    [ObservableProperty]
    private string _labelText = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyVisible), nameof(HasNotice))]
    private bool _isBodyHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBodyToggle))]
    private bool _canToggleBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreRepliesVisible), nameof(HasNotice))]
    private bool _areRepliesHidden;

    [ObservableProperty]
    private bool _canRevealReplies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyToggleText), nameof(HasBodyToggle))]
    private bool _isBodyRevealed;

    internal SponsorContentCardState(Func<ContentSurface, string, bool?, bool, bool, ContentPromotionInput> input)
        => _input = input;

    /// <summary>正文是否显示（未判断、判断失败、已手动展开时都显示）.</summary>
    public bool IsBodyVisible => !IsBodyHidden;

    /// <summary>回复入口是否显示.</summary>
    public bool AreRepliesVisible => !AreRepliesHidden;

    /// <summary>是否需要显示标记条.</summary>
    public bool HasNotice => HasLabel || IsBodyHidden || AreRepliesHidden;

    /// <summary>是否显示“显示内容/重新折叠”按钮.</summary>
    public bool HasBodyToggle => CanToggleBody || IsBodyRevealed;

    /// <summary>正文切换按钮文字.</summary>
    public string BodyToggleText => IsBodyRevealed ? "重新折叠" : "显示内容";

    /// <summary>本机版本号；手动展开/折叠时前进，旧判断随之作废.</summary>
    internal long LocalRevision { get; private set; }

    internal bool ManuallyRevealedReplies { get; private set; }

    /// <summary>当前唯一可以写入效果的投影（后加载的卡片控件接管）.</summary>
    internal object? Owner { get; set; }

    /// <summary>写入当前效果时的设置版本；与当前设置不同的效果在接管/设置变更时先清除.</summary>
    internal long? AppliedConfiguration { get; private set; }

    /// <summary>写入当前效果时宿主声明的“是否在 UP 主个人空间”（null = 未知）.</summary>
    internal bool? AppliedAuthorSpace { get; private set; }

    /// <summary>手动展开/折叠后请求重新判断（只通知当前所有者）.</summary>
    internal event Action<SponsorContentCardState>? RevealChanged;

    /// <summary>
    /// 条目本机版本：手动展开次数 + 宿主上下文标记。上下文不同的判断因此有不同的条目标识，
    /// 换了上下文的旧结果按标识比对就会被丢弃.
    /// </summary>
    internal string RevisionFor(bool? authorSpace)
        => "local-" + LocalRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (authorSpace switch { true => "-space", false => "-feed", null => "-unknown" });

    internal ContentPromotionInput CreateInput(ContentSurface surface, bool? authorSpace)
        => _input(surface, RevisionFor(authorSpace), authorSpace, IsBodyRevealed, ManuallyRevealedReplies);

    /// <summary>当前效果是否属于给定的设置版本和上下文（没有效果时视为无需清除）.</summary>
    internal bool HasEffectsFor(long configuration, bool? authorSpace)
        => AppliedConfiguration is not { } applied || (applied == configuration && AppliedAuthorSpace == authorSpace);

    /// <summary>写入已确认有效的判断结果.</summary>
    internal void Apply(ContentPromotionDecision decision, long configuration, bool? authorSpace)
    {
        AppliedConfiguration = configuration;
        AppliedAuthorSpace = authorSpace;
        LabelText = Label(decision);
        DetailText = Detail(decision);
        HasLabel = decision.ShowLabel;
        IsBodyHidden = decision.HideBody && !IsBodyRevealed;
        CanToggleBody = decision.CanToggleBody;
        AreRepliesHidden = decision.HideReplies && !ManuallyRevealedReplies;
        CanRevealReplies = decision.CanRevealReplies;
    }

    /// <summary>清除效果（显示原内容），保留手动展开偏好.</summary>
    internal void Clear()
    {
        AppliedConfiguration = null;
        AppliedAuthorSpace = null;
        HasLabel = false;
        IsBodyHidden = false;
        CanToggleBody = false;
        AreRepliesHidden = false;
        CanRevealReplies = false;
        LabelText = string.Empty;
        DetailText = string.Empty;
    }

    [RelayCommand]
    private void ToggleBody()
    {
        if (!HasBodyToggle)
        {
            return;
        }

        IsBodyRevealed = !IsBodyRevealed;
        LocalRevision++;

        // 展开立即生效（只是本机显示）；重新折叠要等新的有效判断，不凭旧结果折叠。
        if (IsBodyRevealed)
        {
            IsBodyHidden = false;
        }

        RevealChanged?.Invoke(this);
    }

    [RelayCommand]
    private void RevealReplies()
    {
        if (!AreRepliesHidden && !CanRevealReplies)
        {
            return;
        }

        ManuallyRevealedReplies = true;
        LocalRevision++;
        AreRepliesHidden = false;
        RevealChanged?.Invoke(this);
    }

    private static string Label(ContentPromotionDecision decision) => decision.Hint switch
    {
        CommerceHint.Goods => "含商品",
        CommerceHint.ForwardedGoods => "转发的内容含商品",
        CommerceHint.ConfiguredTextSuspicion => decision.MatchedTerms.IsDefaultOrEmpty
            ? "疑似推广"
            : $"疑似推广 · 命中 {decision.MatchedTerms.Length} 个关键词",
        _ => "可能含推广",
    };

    private static string Detail(ContentPromotionDecision decision)
    {
        var text = "由本机规则判断，可能误判；不代表已确认的商业推广。";
        if (decision.Whitelist == ContentWhitelistState.Overridden)
        {
            text += "此 UP 主在白名单中，按设置仍然处理。";
        }

        if (decision.HideBody || decision.HideReplies)
        {
            text += "可以随时点开查看。";
        }

        return text;
    }
}
