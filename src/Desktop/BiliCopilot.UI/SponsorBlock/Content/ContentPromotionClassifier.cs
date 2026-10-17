using System.Collections.Immutable;
using System.Globalization;

namespace BiliCopilot.UI.SponsorBlock.Content;

public static class ContentPromotionClassifier
{
    /// <summary>Whitelist is the caller's existing durable canonical-UID snapshot.
    /// A default array means unavailable, while an initialized empty array means known empty.</summary>
    public static ContentPromotionDecision Evaluate(ContentPromotionInput input, ContentPromotionOptions options,
        ImmutableArray<string> whitelistedAuthorUids)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        var metadataKnowledge = ContentKnowledge.Unknown;
        ContentPromotionDecision Empty(ContentKnowledge knowledge, string reason, TextRegexStatus? regex = null) =>
            new(input.Stamp, knowledge, metadataKnowledge, CommerceHint.None, [], ContentCategoryAction.Disabled,
                ContentWhitelistState.Unknown, false, false, false, false, false,
                input.ManuallyRevealedBody, input.ManuallyRevealedReplies, reason, regex);

        if (input.Stamp is null || string.IsNullOrWhiteSpace(input.Stamp.ItemId) || input.Stamp.ItemId.Length > 256
            || string.IsNullOrWhiteSpace(input.Stamp.Revision) || input.Stamp.Revision.Length > 256)
            return Empty(ContentKnowledge.Unknown, "MissingItemIdentityOrRevision");
        var surface = input.Stamp.Surface;
        if (surface is not (ContentSurface.Dynamic or ContentSurface.Comment or ContentSurface.Reply))
            return Empty(ContentKnowledge.Unsupported, "NotDynamicOrPublicCommentSurface");

        // Admission precedes metadata, text, regex and whitelist work. Off is not a negative classification.
        ContentPromotionDecision Disabled(ContentDisabledReason reason, string detail) =>
            Empty(ContentKnowledge.Unknown, detail) with { DisabledReason = reason };
        if (!options.FeatureEnabled)
            return Disabled(ContentDisabledReason.FeatureDisabled, "FeatureOrSurfaceDisabled");
        if (surface == ContentSurface.Dynamic && !options.DynamicEnabled)
            return Disabled(ContentDisabledReason.DynamicDisabled, "FeatureOrSurfaceDisabled");
        if (surface != ContentSurface.Dynamic && !options.CommentEnabled)
            return Disabled(ContentDisabledReason.CommentDisabled, "FeatureOrSurfaceDisabled");
        if (surface == ContentSurface.Reply && !options.BlockReplies)
            return Disabled(ContentDisabledReason.ReplyBlockingDisabled, "ReplyBlockingDisabled");
        if (surface == ContentSurface.Dynamic && !options.DynamicInAuthorSpaceEnabled && input.IsAuthorSpace == true)
            return Disabled(ContentDisabledReason.AuthorSpaceDisabled, "AuthorSpaceDisabled");

        if (options.RegexLimits is null || !options.RegexLimits.IsValid
            || !KnownAction(options.GoodsAction) || !KnownAction(options.ForwardedGoodsAction) || !KnownAction(options.SuspicionAction))
            return Empty(ContentKnowledge.Unsupported, "UnsupportedContentOptions");
        if (input.Metadata is null) return Empty(ContentKnowledge.Unknown, "MissingPromotionMetadata");

        var metadata = input.Metadata;
        var hint = CommerceHint.None;
        var terms = ImmutableArray<string>.Empty;
        TextRegexStatus? regexStatus = null;
        var knowledge = ContentKnowledge.Known;
        var classificationReason = "NoKnownCommerceHint";
        if (surface is ContentSurface.Comment or ContentSurface.Reply)
        {
            metadataKnowledge = metadata.HasCommentGoodsLinks.HasValue ? ContentKnowledge.Known : ContentKnowledge.Unknown;
            if (metadata.HasCommentGoodsLinks == true) hint = CommerceHint.Goods;
            else if (metadata.HasCommentGoodsLinks is null) return Empty(ContentKnowledge.Unknown, "CommentGoodsLinksUnavailable");
        }
        else
        {
            var complete = metadata.HasGoods.HasValue && metadata.IsPromoted.HasValue && metadata.IsForwarded.HasValue
                && (metadata.IsForwarded == false || metadata.ForwardedHasGoods.HasValue && metadata.ForwardedIsPromoted.HasValue);
            metadataKnowledge = complete ? ContentKnowledge.Known : ContentKnowledge.Unknown;
            if (metadata.IsForwarded == true && metadata.ForwardedHasGoods == true) hint = CommerceHint.ForwardedGoods;
            else if (metadata.HasGoods == true && metadata.IsForwarded.HasValue
                && (metadata.IsForwarded == false || metadata.ForwardedHasGoods == false)) hint = CommerceHint.Goods;
            else if (metadata.HasGoods == true || metadata.ForwardedHasGoods == true)
                return Empty(ContentKnowledge.Unknown, "GoodsForwardContextUnavailableOrInconsistent");
            else
            {
                if (!TryBuildText(input, options.RegexLimits.MaximumInputLength, out var text, out var textReason))
                    return Empty(ContentKnowledge.Unknown, textReason);
                var match = BoundedTextRegex.Match(options.DynamicRegex, text, options.RegexLimits, allowSlashFlags: true);
                regexStatus = match.Status;
                if (match.Status != TextRegexStatus.Success)
                    return Empty(match.Status == TextRegexStatus.Unsupported ? ContentKnowledge.Unsupported : ContentKnowledge.Unknown,
                        match.Reason, match.Status);
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                var ordered = ImmutableArray.CreateBuilder<string>();
                void Add(string? value)
                {
                    if (value is not null && value.Length > 1 && distinct.Add(value)) ordered.Add(value);
                }
                foreach (var item in match.Matches)
                {
                    Add(item.Value);
                    // JS String.match without g includes captures; with g includes only whole matches.
                    if (!match.Global)
                        for (var i = 1; i < item.Groups.Length; i++) Add(item.Groups[i]);
                }
                terms = ordered.ToImmutable();
                if (!terms.IsEmpty) hint = CommerceHint.ConfiguredTextSuspicion;
                else if (!complete || !input.TextComplete)
                {
                    knowledge = ContentKnowledge.Unknown;
                    classificationReason = "IncompleteMetadataOrTextNotNegativeProof";
                }
                // A generic promoted bit has no established merchandise/paid-ad semantics.
                else if (metadata.IsPromoted == true || metadata.ForwardedIsPromoted == true)
                {
                    knowledge = ContentKnowledge.Unknown;
                    classificationReason = "PromotionFlagWithoutGoodsSemantics";
                }
            }
        }

        var action = hint switch
        {
            CommerceHint.Goods => options.GoodsAction,
            CommerceHint.ForwardedGoods => options.ForwardedGoodsAction,
            CommerceHint.ConfiguredTextSuspicion => options.SuspicionAction,
            _ => ContentCategoryAction.Disabled,
        };
        var whitelist = Whitelist(input, options, whitelistedAuthorUids);
        var result = new ContentPromotionDecision(input.Stamp, knowledge, metadataKnowledge, hint, terms, action, whitelist,
            false, false, false, false, false, input.ManuallyRevealedBody, input.ManuallyRevealedReplies,
            hint == CommerceHint.None ? classificationReason : "KnownHint", regexStatus);
        if (hint == CommerceHint.None) return result;
        if (surface == ContentSurface.Dynamic && !options.DynamicInAuthorSpaceEnabled && input.IsAuthorSpace is null)
            return result with { Reason = "AuthorSpaceContextUnknown" };
        if (whitelist is ContentWhitelistState.Listed or ContentWhitelistState.Unknown)
            return result with { Reason = whitelist == ContentWhitelistState.Listed ? "WhitelistedAuthor" : "WhitelistOrAuthorUnknown" };
        if (action == ContentCategoryAction.Disabled) return result with { Reason = "CategoryDisabled" };

        // Threshold controls HIDE only: one eligible match is enough for the label.
        if (hint == CommerceHint.ConfiguredTextSuspicion && options.SuspicionHideThreshold < 0)
            return result with { ShowLabel = true, Reason = "InvalidHideThresholdMarkOnly" };
        var hide = action == ContentCategoryAction.Hide
            && (hint != CommerceHint.ConfiguredTextSuspicion || terms.Length >= options.SuspicionHideThreshold);
        var replies = hide && surface == ContentSurface.Comment && options.BlockReplies;
        return result with
        {
            ShowLabel = true,
            HideBody = hide && !input.ManuallyRevealedBody,
            HideReplies = replies && !input.ManuallyRevealedReplies,
            CanToggleBody = hide,
            CanRevealReplies = replies,
            Reason = hide ? "ReversibleHidePolicy" : "MarkOnlyOrBelowHideThreshold",
        };
    }

    private static bool KnownAction(ContentCategoryAction action) => action is
        ContentCategoryAction.Disabled or ContentCategoryAction.Mark or ContentCategoryAction.Hide;

    private static bool TryBuildText(ContentPromotionInput input, int limit, out string text, out string reason)
    {
        text = "";
        reason = "TextSpansUnavailableOrOverLimit";
        if (input.TextSpans.IsDefault || input.TextSpans.Length > 1024) return false;
        var builder = new StringBuilder();
        // Source concatenation order, deliberately no invented separators or interaction text.
        foreach (var kind in new[] { ContentSpanKind.Title, ContentSpanKind.RichText, ContentSpanKind.OpusParagraph })
            foreach (var span in input.TextSpans)
            {
                if (span is null || span.Text is null) return false;
                if (span.Kind != kind) continue;
                if (span.Text.Length > limit - builder.Length) return false;
                builder.Append(span.Text);
            }
        text = builder.ToString();
        return true;
    }

    private static ContentWhitelistState Whitelist(ContentPromotionInput input, ContentPromotionOptions options,
        ImmutableArray<string> ids)
    {
        if (options.IgnoreWhitelist) return ContentWhitelistState.Overridden;
        if (ids.IsDefault || ids.Length > 10000) return ContentWhitelistState.Unknown;
        foreach (var id in ids) if (!CanonicalUid(id)) return ContentWhitelistState.Unknown;
        if (ids.IsEmpty) return ContentWhitelistState.NotRequired;
        foreach (var id in ids)
            if (id == input.AuthorUid || id == input.OriginalAuthorUid) return ContentWhitelistState.Listed;
        if (!CanonicalUid(input.AuthorUid)) return ContentWhitelistState.Unknown;
        if (input.Stamp.Surface == ContentSurface.Dynamic
            && (input.Metadata.IsForwarded is null || input.Metadata.IsForwarded == true && !CanonicalUid(input.OriginalAuthorUid)))
            return ContentWhitelistState.Unknown;
        return ContentWhitelistState.NotListed;
    }

    // Same boundary as the durable canonical UID contract, without a Persistence dependency.
    private static bool CanonicalUid(string? uid) => uid is not null && uid.Length is > 0 and <= 20
        && ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
        && value.ToString(CultureInfo.InvariantCulture) == uid;
}
