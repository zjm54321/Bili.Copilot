using System.Collections.Immutable;
using System.Globalization;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.Comment;
using Richasy.BiliKernel.Models.Moment;
using SdkContentMetadata = Richasy.BiliKernel.Models.Content.ContentMetadata;

namespace BiliCopilot.UI.SponsorBlock.Content;

/// <summary>
/// Copies bounded, already available public SDK facts into the immutable pure input.
/// Requires the private sb2 metadata contract; does not fetch, classify, mutate or retain SDK objects.
/// Caller owns a stable snapshot during this synchronous copy. Run the later classifier away from
/// the UI thread and recheck the exact item/local revision before any reversible projection.
/// </summary>
public static class SponsorSdkContentAdapter
{
    // Fixed copy ceilings, not UI latency guarantees. No recursive original traversal.
    private const int MaximumMetadataEntries = 1024;
    private const int MaximumTextSpans = 1024;
    private const int MaximumTextLength = 32768;
    private const int MaximumIdentityLength = 256;

    // Pinned bilibili/app/dynamic/v2.proto: DynModuleType.module_author_forward = 13.
    // These are wire values, not references to generated Grpc types or zero/absence heuristics.
    private const int ForwardAuthorModuleKind = 13;

    /// <summary>
    /// localImmutableRevision is a caller-owned LOCAL content/reveal snapshot token, NOT a server
    /// revision, timestamp, cursor or fetched version. isAuthorSpace is actual caller context;
    /// null means unknown. Both reveal flags must belong to exactly this item and local revision.
    /// </summary>
    public static ContentPromotionInput FromMoment(MomentInformation source, string localImmutableRevision,
        bool? isAuthorSpace, bool manuallyRevealedBody, bool manuallyRevealedReplies)
    {
        ArgumentNullException.ThrowIfNull(source);
        var stamp = CreateStamp(ContentSurface.Dynamic, source.Id, localImmutableRevision);
        var metadata = source.ContentMetadata;
        var original = source.Data as MomentInformation;
        var originalMetadata = original?.ContentMetadata;
        var copiedOriginalMetadata = metadata?.Original;

        // Either separately copied original source can establish a positive; neither empty list
        // can establish a negative. Nothing from the original is merged into current HasGoods.
        var originalGoods = HasEstablishedGoods(originalMetadata) == true
            || HasEstablishedGoods(copiedOriginalMetadata) == true ? (bool?)true : null;
        var facts = new ContentPromotionMetadata(
            HasGoods: HasEstablishedGoods(metadata),
            IsPromoted: null,
            IsForwarded: ReadForwarded(source.MomentType, metadata?.CardKind, original is not null),
            ForwardedHasGoods: originalGoods,
            ForwardedIsPromoted: null,
            HasCommentGoodsLinks: null);

        return new ContentPromotionInput(stamp, CanonicalUid(source.User?.Id),
            original is not null ? CanonicalUid(original.User?.Id) : ReadForwardAuthorUid(metadata),
            facts, CopyMomentText(source, metadata), TextComplete: false, IsAuthorSpace: isAuthorSpace,
            ManuallyRevealedBody: manuallyRevealedBody, ManuallyRevealedReplies: manuallyRevealedReplies);
    }

    /// <summary>
    /// surface must be the caller's actual Comment or Reply surface, not inferred from RootId.
    /// localImmutableRevision and reveal states have the same local snapshot contract as FromMoment.
    /// Only a positive copied Content.Urls Extra.GoodsItemId establishes a comment goods link.
    /// </summary>
    public static ContentPromotionInput FromComment(CommentInformation source, string localImmutableRevision,
        ContentSurface surface, bool? isAuthorSpace, bool manuallyRevealedBody, bool manuallyRevealedReplies)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (surface is not (ContentSurface.Comment or ContentSurface.Reply))
            throw new ArgumentOutOfRangeException(nameof(surface), "Expected an actual public comment or reply surface.");

        var stamp = CreateStamp(surface, source.Id, localImmutableRevision);
        var metadata = source.ContentMetadata;
        var body = source.Content?.Text;
        var spans = body is not null && body.Length <= MaximumTextLength
            ? ImmutableArray.Create(new ContentTextSpan(ContentSpanKind.RichText, body))
            : ImmutableArray<ContentTextSpan>.Empty;
        var facts = new ContentPromotionMetadata(HasGoods: null, IsPromoted: null, IsForwarded: null,
            ForwardedHasGoods: null, ForwardedIsPromoted: null, HasCommentGoodsLinks: HasCommentGoods(metadata));

        // UserDetailProfile contains UserProfile; there is no CommentInformation.User.Id member.
        // The pure classifier never keyword-classifies comment/reply bodies. Coverage stays unknown:
        // SDK body text already has its upgrade placeholder removed; rich Note/Opus is not full text.
        return new ContentPromotionInput(stamp, CanonicalUid(source.User?.User?.Id), OriginalAuthorUid: null,
            Metadata: facts, TextSpans: spans, TextComplete: false, IsAuthorSpace: isAuthorSpace,
            ManuallyRevealedBody: manuallyRevealedBody, ManuallyRevealedReplies: manuallyRevealedReplies);
    }

    private static ContentItemStamp CreateStamp(ContentSurface surface, string itemId, string localImmutableRevision)
    {
        // Never trim, hash, synthesize or replace an SDK item ID or the caller's local revision.
        if (itemId is null || itemId.Length is 0 or > MaximumIdentityLength || string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("An actual bounded SDK item ID is required.", nameof(itemId));
        if (localImmutableRevision is null || localImmutableRevision.Length is 0 or > MaximumIdentityLength
            || string.IsNullOrWhiteSpace(localImmutableRevision))
            throw new ArgumentException("A bounded caller-owned local revision is required.", nameof(localImmutableRevision));
        return new ContentItemStamp(surface, itemId, localImmutableRevision);
    }

    private static string? CanonicalUid(string? uid) => uid is not null && uid.Length is > 0 and <= 20
        && ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
        && value.ToString(CultureInfo.InvariantCulture) == uid ? uid : null;

    private static bool? HasEstablishedGoods(SdkContentMetadata? metadata)
    {
        var goods = metadata?.Goods;
        if (goods is null || goods.Count > MaximumMetadataEntries) return null;
        for (var i = 0; i < goods.Count; i++)
        {
            var entry = goods[i];
            if (entry is null || entry.Origin is not ("ModuleAdditional.Goods"
                or "ModuleAdditional.Goods.GoodsItems" or "ModuleDesc.Desc.Goods" or "ModuleDesc.Desc")) continue;

            // The sb2 producer emits the last origin only for DescType.Goods (6). Source path plus
            // a nonempty record payload is a commerce hint, never paid-promotion proof. A bare
            // zero/default card, SourceType/GoodsType alone or an AdMark alone is insufficient.
            if (entry.ItemId > 0 || HasBoundedValue(entry.Title)
                || HasBoundedValue(entry.JumpUrl) || HasBoundedValue(entry.SchemaUrl)) return true;
        }

        return null;
    }

    private static bool HasBoundedValue(string? value) => value is not null
        && value.Length is > 0 and <= MaximumTextLength && !string.IsNullOrWhiteSpace(value);

    private static bool? HasCommentGoods(SdkContentMetadata? metadata)
    {
        var links = metadata?.Links;
        if (links is null || links.Count > MaximumMetadataEntries) return null;
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link is not null && link.Origin == "Content.Urls"
                && link.ExtraPresent == true && link.GoodsItemId > 0) return true;
        }

        // Missing Extra, zero/negative ID, empty maps and Note/Opus links are all unknown.
        return null;
    }

    private static bool? ReadForwarded(MomentItemType? type, int? cardKind, bool hasNestedOriginal)
    {
        // The typed model is populated by ModuleDynamicType in the pinned producer. Unsupported
        // and PlainText do not prove absence: PlainText is also its null-main fallback.
        var typedNonForward = type is MomentItemType.Video or MomentItemType.Pgc
            or MomentItemType.Article or MomentItemType.Image;
        // DynamicType: forward=1; av=2, pgc=3, word=6, draw=7, article=8.
        // Other card kinds (including wrappers), unknown numbers and zero deliberately stay unknown.
        var cardNonForward = cardKind is 2 or 3 or 6 or 7 or 8;
        var positive = hasNestedOriginal || type == MomentItemType.Forward || cardKind == 1;
        if (positive && (typedNonForward || cardNonForward)) return null; // Contradictory copied facts.
        if (positive) return true;
        return typedNonForward || cardNonForward ? false : null;
    }

    private static string? ReadForwardAuthorUid(SdkContentMetadata? metadata)
    {
        var origins = metadata?.Origins;
        if (origins is null || origins.Count > MaximumMetadataEntries) return null;
        string? result = null;
        for (var i = 0; i < origins.Count; i++)
        {
            var origin = origins[i];
            if (origin is null || origin.Kind != ForwardAuthorModuleKind) continue;
            var value = origin.UserId;
            if (!value.HasValue || value.Value <= 0) return null;
            // A raw numeric UID has no lexical leading-zero representation to normalize.
            var uid = CanonicalUid(value.Value.ToString(CultureInfo.InvariantCulture));
            if (uid is null || result is not null && result != uid) return null;
            result = uid;
        }

        return result;
    }

    private static ImmutableArray<ContentTextSpan> CopyMomentText(MomentInformation source, SdkContentMetadata? metadata)
    {
        var spans = ImmutableArray.CreateBuilder<ContentTextSpan>();
        var length = 0;
        // Only an actual typed article title is recovered. Never use author-forward title parts,
        // Tip, a card label, an extension bag, or guessed partitions of Description.Text.
        if (source.MomentType == MomentItemType.Article && source.Data is ArticleInformation article)
        {
            var title = article.Identifier.Title;
            if (title is not null && title.Length <= MaximumTextLength)
            {
                spans.Add(new ContentTextSpan(ContentSpanKind.Title, title));
                length = title.Length;
            }
        }

        var links = metadata?.Links;
        if (links is null || links.Count > MaximumMetadataEntries) return spans.ToImmutable();
        var rich = ImmutableArray.CreateBuilder<ContentTextSpan>();
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link is null) return spans.ToImmutable();
            if (link.Origin != "ModuleDesc.Desc") continue;

            // Pinned DescType has named non-interaction values 1..18; zero is None, not Text.
            // It has NO Interaction member. 12 here is Vc, NOT DynModuleType.ModuleInteraction=12.
            // Interaction is a separate module, not copied by this producer; the exact origin
            // allowlist excludes it. Do not invent a Lottery/Vote/Aite-to-Interaction remapping.
            if (link.Kind is not (>= 1 and <= 18)) return spans.ToImmutable();
            // Prefer the copied visible Text; OrigText is only a fallback when Text is missing/empty.
            // Do not append both representations, or duplicate ModuleDesc.Text and its children.
            var text = string.IsNullOrEmpty(link.Text) ? link.OriginalText : link.Text;
            if (text is null || text.Length > MaximumTextLength - length
                || spans.Count + rich.Count >= MaximumTextSpans) return spans.ToImmutable();
            rich.Add(new ContentTextSpan(ContentSpanKind.RichText, text));
            length += text.Length;
        }

        // All-or-nothing rich-text batch: dropping an unknown/oversized middle node must not
        // concatenate its neighbours into an invented keyword. Array order is never sorted/deduped.
        spans.AddRange(rich);
        // Title/opus modules, paragraph structure and original text coverage are not fully exposed.
        // FromMoment therefore always reports TextComplete=false, even for an empty known array.
        return spans.ToImmutable();
    }
}
