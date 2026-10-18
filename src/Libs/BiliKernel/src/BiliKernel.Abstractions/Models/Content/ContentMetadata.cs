// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Content;

/// <summary>Copied server evidence; absence and proto3 defaults are not negative promotion verdicts.</summary>
public sealed class ContentMetadata
{
    /// <summary>Initializes a new instance of the <see cref="ContentMetadata"/> class.</summary>
    /// <param name="moduleKinds">Raw module kinds, or null when unavailable.</param>
    /// <param name="goods">Copied goods evidence.</param>
    /// <param name="links">Copied link evidence.</param>
    /// <param name="origins">Copied author and forwarding evidence.</param>
    /// <param name="cardKind">Raw content card kind, or null when unavailable.</param>
    /// <param name="richTextKind">Raw rich-text oneof kind, or null when absent.</param>
    /// <param name="original">Metadata of the already projected forwarded original.</param>
    public ContentMetadata(IEnumerable<int>? moduleKinds, IEnumerable<ContentGoods>? goods, IEnumerable<ContentLink>? links, IEnumerable<ContentOrigin>? origins, int? cardKind = null, int? richTextKind = null, ContentMetadata? original = null)
    {
        ModuleKinds = moduleKinds?.ToList().AsReadOnly();
        Goods = (goods ?? Array.Empty<ContentGoods>()).ToList().AsReadOnly();
        Links = (links ?? Array.Empty<ContentLink>()).ToList().AsReadOnly();
        Origins = (origins ?? Array.Empty<ContentOrigin>()).ToList().AsReadOnly();
        CardKind = cardKind;
        RichTextKind = richTextKind;
        Original = original;
    }

    /// <summary>Raw module kinds; null means unavailable, not an empty confirmed response.</summary>
    public IReadOnlyList<int>? ModuleKinds { get; }
    /// <summary>Raw goods evidence, not a sponsorship classification.</summary>
    public IReadOnlyList<ContentGoods> Goods { get; }
    /// <summary>Raw text and URL evidence; no URI parsing is performed.</summary>
    public IReadOnlyList<ContentLink> Links { get; }
    /// <summary>Author and forwarding module evidence.</summary>
    public IReadOnlyList<ContentOrigin> Origins { get; }
    /// <summary>Raw card kind, including unknown numeric values.</summary>
    public int? CardKind { get; }
    /// <summary>Raw rich-text oneof kind; null means absent.</summary>
    public int? RichTextKind { get; }
    /// <summary>Evidence from the already projected original; no SDK graph is retained.</summary>
    public ContentMetadata? Original { get; }
}

/// <summary>Neutral copied goods or goods-card evidence.</summary>
public sealed class ContentGoods
{
    /// <summary>Initializes a new instance of the <see cref="ContentGoods"/> class.</summary>
    /// <param name="origin">Source field path.</param>
    /// <param name="itemId">Raw ID; zero remains scalar-default ambiguous.</param>
    /// <param name="title">Raw title.</param>
    /// <param name="jumpUrl">Raw jump URL.</param>
    /// <param name="schemaUrl">Raw schema URL.</param>
    /// <param name="sourceType">Raw source type; zero remains ambiguous.</param>
    /// <param name="goodsType">Raw goods or card type.</param>
    /// <param name="adMark">Raw ad-mark text, not a boolean verdict.</param>
    /// <param name="jumpType">Raw jump type.</param>
    public ContentGoods(string origin, long? itemId, string? title, string? jumpUrl, string? schemaUrl, int? sourceType, string? goodsType, string? adMark, int? jumpType)
    {
        Origin = origin;
        ItemId = itemId;
        Title = title;
        JumpUrl = jumpUrl;
        SchemaUrl = schemaUrl;
        SourceType = sourceType;
        GoodsType = goodsType;
        AdMark = adMark;
        JumpType = jumpType;
    }

    /// <summary>Source field path.</summary>
    public string Origin { get; }
    /// <summary>Raw item ID; zero does not confirm absence.</summary>
    public long? ItemId { get; }
    /// <summary>Raw title.</summary>
    public string? Title { get; }
    /// <summary>Raw jump URL.</summary>
    public string? JumpUrl { get; }
    /// <summary>Raw schema URL.</summary>
    public string? SchemaUrl { get; }
    /// <summary>Raw source type; zero does not confirm absence.</summary>
    public int? SourceType { get; }
    /// <summary>Raw goods or card type.</summary>
    public string? GoodsType { get; }
    /// <summary>Raw ad-mark text.</summary>
    public string? AdMark { get; }
    /// <summary>Raw jump type; zero does not confirm absence.</summary>
    public int? JumpType { get; }
}

/// <summary>Neutral raw link and optional Extra evidence.</summary>
public sealed class ContentLink
{
    /// <summary>Initializes a new instance of the <see cref="ContentLink"/> class.</summary>
    /// <param name="origin">Source field path.</param>
    /// <param name="key">URL map key or raw resource ID.</param>
    /// <param name="text">Raw title or text.</param>
    /// <param name="url">Raw URL.</param>
    /// <param name="kind">Raw description kind.</param>
    /// <param name="extraPresent">Extra message presence; null means not applicable.</param>
    /// <param name="goodsItemId">Raw Extra ID; null means absent Extra, zero is ambiguous.</param>
    /// <param name="originalText">Raw original text or alternate URL.</param>
    public ContentLink(string origin, string? key, string? text, string? url, int? kind = null, bool? extraPresent = null, long? goodsItemId = null, string? originalText = null)
    {
        Origin = origin;
        Key = key;
        Text = text;
        Url = url;
        Kind = kind;
        ExtraPresent = extraPresent;
        GoodsItemId = goodsItemId;
        OriginalText = originalText;
    }

    /// <summary>Source field path.</summary>
    public string Origin { get; }
    /// <summary>URL map key or resource ID.</summary>
    public string? Key { get; }
    /// <summary>Raw title or text.</summary>
    public string? Text { get; }
    /// <summary>Raw URL; malformed values remain available.</summary>
    public string? Url { get; }
    /// <summary>Raw description kind.</summary>
    public int? Kind { get; }
    /// <summary>Extra message presence; null means not applicable.</summary>
    public bool? ExtraPresent { get; }
    /// <summary>Raw Extra ID; zero remains scalar-default ambiguous.</summary>
    public long? GoodsItemId { get; }
    /// <summary>Raw original text or alternate URL.</summary>
    public string? OriginalText { get; }
}

/// <summary>Neutral copied author module evidence.</summary>
public sealed class ContentOrigin
{
    /// <summary>Initializes a new instance of the <see cref="ContentOrigin"/> class.</summary>
    /// <param name="kind">Raw author module kind.</param>
    /// <param name="userId">Raw user ID.</param>
    /// <param name="name">Raw author name.</param>
    /// <param name="url">Raw module URL.</param>
    /// <param name="titleParts">All forwarding title parts.</param>
    public ContentOrigin(int kind, long? userId, string? name, string? url, IEnumerable<ContentLink>? titleParts)
    {
        Kind = kind;
        UserId = userId;
        Name = name;
        Url = url;
        TitleParts = (titleParts ?? Array.Empty<ContentLink>()).ToList().AsReadOnly();
    }

    /// <summary>Raw author module kind.</summary>
    public int Kind { get; }
    /// <summary>Raw user ID; zero remains ambiguous.</summary>
    public long? UserId { get; }
    /// <summary>Raw author name.</summary>
    public string? Name { get; }
    /// <summary>Raw module URL.</summary>
    public string? Url { get; }
    /// <summary>All copied forwarding title parts.</summary>
    public IReadOnlyList<ContentLink> TitleParts { get; }
}
