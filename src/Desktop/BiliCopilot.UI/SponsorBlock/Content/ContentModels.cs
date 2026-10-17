using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Content;

public enum ContentSurface { Unknown, Dynamic, Comment, Reply }
public enum ContentKnowledge { Known, Unknown, Unsupported }
public enum CommerceHint { None, Goods, ForwardedGoods, ConfiguredTextSuspicion }
public enum ContentCategoryAction { Disabled, Mark, Hide }
public enum ContentSpanKind { Title, RichText, OpusParagraph, Interaction, Other }
public enum ContentWhitelistState { NotRequired, NotListed, Listed, Unknown, Overridden }
public enum ContentDisabledReason { None, FeatureDisabled, DynamicDisabled, CommentDisabled, ReplyBlockingDisabled, AuthorSpaceDisabled }

/// <summary>Actual item identity and immutable content revision, never a title or list position.</summary>
public sealed record ContentItemStamp(ContentSurface Surface, string ItemId, string Revision);
public sealed record ContentTextSpan(ContentSpanKind Kind, string Text);

/// <summary>Null means unavailable. Promoted flags are retained, not assumed to mean paid advertising
/// or merchandise. HasGoods must describe actual cards/links, not a keyword inference.</summary>
public sealed record ContentPromotionMetadata(
    bool? HasGoods,
    bool? IsPromoted,
    bool? IsForwarded,
    bool? ForwardedHasGoods,
    bool? ForwardedIsPromoted,
    bool? HasCommentGoodsLinks);

/// <summary>Reveal state belongs to exactly Stamp; adapters must not reuse it after recycling/revision changes.
/// A default TextSpans array or false TextComplete means unknown coverage, not empty content.</summary>
public sealed record ContentPromotionInput(
    ContentItemStamp Stamp,
    string? AuthorUid,
    string? OriginalAuthorUid,
    ContentPromotionMetadata Metadata,
    ImmutableArray<ContentTextSpan> TextSpans,
    bool TextComplete,
    bool? IsAuthorSpace,
    bool ManuallyRevealedBody,
    bool ManuallyRevealedReplies);

public sealed record ContentPromotionOptions
{
    // The native feature gate is deliberately independent of source nested defaults.
    public bool FeatureEnabled { get; init; }
    public bool DynamicEnabled { get; init; } = true;
    public bool DynamicInAuthorSpaceEnabled { get; init; }
    public bool CommentEnabled { get; init; } = true;
    public bool BlockReplies { get; init; }
    public bool IgnoreWhitelist { get; init; }
    public ContentCategoryAction GoodsAction { get; init; } = ContentCategoryAction.Hide;
    public ContentCategoryAction ForwardedGoodsAction { get; init; } = ContentCategoryAction.Hide;
    public ContentCategoryAction SuspicionAction { get; init; } = ContentCategoryAction.Disabled;
    public int SuspicionHideThreshold { get; init; } = 1;
    public string DynamicRegex { get; init; } = ContentPromotionDefaults.DynamicRegex;
    public TextRegexLimits RegexLimits { get; init; } = new();
}

public static class ContentPromotionDefaults
{
    // Only configuration literals are reproduced from hanydd/BilibiliSponsorBlock
    // b2513f3a788c48244b30f150b9207df76695df5a src/config.ts:401-414.
    public const string DynamicRegex = "/" +
        "618|11(?!1).11(?:日)?|双(?:11|十一|12|十二)|女神节|开学季|年货节|" +
        "恰(?:个|了|到)?饭|金主|(他|它|她)(?:们)?家(?:的)?|" +
        "(?:评论区)?(?:领(?:取|张|到)?|抢|送|得|叠)(?:我的)?(?:神|优惠|红包|折扣|福利|无门槛|隐藏|秘密|专属|(?:超)?大(?:额)?|额外)+(?:券|卷|劵|q(?:uan)?)?(?:后|到手|价|使用|下单)?|(?:领|抢|得|送)(?:红包|优惠|券|福利)|(?:优惠|(?:券|卷|劵)后|到手|促销|活动|神)价|" +
        "(?:淘宝|tb|京东|jd|狗东|拼多多|pdd|天猫|tmall)搜索|" +
        "(?:随(便|时)|任意)(?:退|退货|换货)|(?:免费|无偿)(?:换(?:个)?新|替换|更换|试用)(?:商品|物品)?|" +
        "(?:点(?:击)?|戳|来|我)评论区(?:置顶)?|(?:立即|蓝链|链接|🔗)(?:购买|下单)|" +
        "(?:vx|wx|微信|软件)扫码(?:领)?(?:优惠|红包|券)?|" +
        "(?:我的)?同款(?:[的]?(?:推荐|好物|商品|入手|购买|拥有|分享|安利)?)|" +
        @"满\d+|大促|促销|折扣|特价|秒杀|广告|推广|低至|热卖|抢购|新品|豪礼|赠品|密令|" +
        "(?:饿了么|美(?:团|団)|百度外卖|蜂鸟|达达|UU跑腿|(?:淘宝)?闪购)|(?:点|订|送|吃)(?:外卖|餐)|外卖(?:节|服务|平台|app)" +
        "/gi";
}

/// <summary>Classification and visibility are separate. None never means 'verified not sponsored'.
/// HideBody/HideReplies are reversible local projections: never delete items or change feed cursors.</summary>
public sealed record ContentPromotionDecision(
    ContentItemStamp Stamp,
    ContentKnowledge Knowledge,
    ContentKnowledge MetadataKnowledge,
    CommerceHint Hint,
    ImmutableArray<string> MatchedTerms,
    ContentCategoryAction Action,
    ContentWhitelistState Whitelist,
    bool ShowLabel,
    bool HideBody,
    bool HideReplies,
    bool CanToggleBody,
    bool CanRevealReplies,
    bool ManualBodyRevealPreserved,
    bool ManualReplyRevealPreserved,
    string Reason,
    TextRegexStatus? RegexStatus)
{
    public ContentDisabledReason DisabledReason { get; init; }
    public bool IsVerifiedPaidPromotion => false;
    public bool RemoveItem => false;
    public bool ChangeFeedCursor => false;
}
