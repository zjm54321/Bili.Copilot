// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.Moment;
using Richasy.BiliKernel.Models.User;
using System.Text.Json;

namespace BiliCopilot.UI.Services.UserSpace;

public sealed partial class UserSpaceService
{
    /// <summary>The SDK's gRPC adapter discards pin modules; only this Web tag establishes a pin.</summary>
    public async Task<MomentInformation?> GetPinnedDynamicAsync(string mid, CancellationToken token)
    {
        using var json = await GetAsync("/x/polymer/web-dynamic/v1/feed/space", mid,
            new() { ["host_mid"] = Id(mid) }, false, token);
        foreach (var item in ArrayItems(Required(Data(json), "items")))
        {
            if (Text(Get(Required(item, "modules"), "module_tag"), "text") == "置顶")
                return PinnedMoment(item);
        }

        return null;
    }

    private static MomentInformation PinnedMoment(JsonElement item, int depth = 0)
    {
        if (depth > 4) throw Schema();
        var id = Identifier(Required(item, "id_str"));
        var modules = Required(item, "modules");
        var author = Required(modules, "module_author");
        var avatar = ImageUrl(Text(author, "face"));
        var user = new UserProfile(Identifier(Required(author, "mid")), Text(author, "name"), avatar?.AbsoluteUri.ToImage(96));
        var dynamic = Required(modules, "module_dynamic");
        var major = Get(dynamic, "major");
        var desc = Get(dynamic, "desc");
        var content = Text(desc, "text");
        var emotes = new Dictionary<string, BiliImage>(StringComparer.Ordinal);
        if (Get(desc, "rich_text_nodes") is { ValueKind: JsonValueKind.Array } nodes)
        {
            foreach (var node in ArrayItems(nodes))
            {
                if (Text(node, "type") == "RICH_TEXT_NODE_TYPE_EMOJI" && Text(node, "text") is string name
                    && ImageUrl(Text(Get(node, "emoji"), "icon_url")) is Uri icon)
                    emotes.TryAdd(name, new BiliImage(icon));
            }
        }

        object? data = null;
        var type = MomentItemType.PlainText;
        var replyType = CommentTargetType.Moment;
        var replyId = id;
        if (Get(item, "orig") is { ValueKind: JsonValueKind.Object } original && Get(original, "modules").ValueKind == JsonValueKind.Object)
        {
            data = PinnedMoment(original, depth + 1);
            type = MomentItemType.Forward;
        }
        else if (Get(major, "archive") is { ValueKind: JsonValueKind.Object } archive)
        {
            var aid = Identifier(Required(archive, "aid"));
            var cover = ImageUrl(Text(archive, "cover")) ?? throw Schema();
            data = new VideoInformation(new MediaIdentifier(aid, Text(archive, "title"), cover.AbsoluteUri.ToVideoCover()),
                new PublisherProfile(user), Duration(Text(archive, "duration_text")) ?? throw Schema(), Text(archive, "bvid"));
            type = MomentItemType.Video;
            replyType = CommentTargetType.Video;
            replyId = aid;
        }
        else
        {
            var opus = Get(major, "opus");
            content ??= Text(Get(opus, "summary"), "text") ?? Text(Get(major, "article"), "title");
            var pictures = Get(opus, "pics");
            if (pictures.ValueKind != JsonValueKind.Array) pictures = Get(Get(major, "draw"), "items");
            if (pictures.ValueKind == JsonValueKind.Array)
            {
                var images = ArrayItems(pictures).Select(picture => ImageUrl(Text(picture, "url") ?? Text(picture, "src")))
                    .OfType<Uri>().Select(uri => new BiliImage(uri)).ToArray();
                if (images.Length > 0) { data = images; type = MomentItemType.Image; }
            }
        }

        var stat = Get(modules, "module_stat");
        var like = Get(stat, "like");
        var community = new MomentCommunityInformation(id, Number(like, "count"), Number(Get(stat, "comment"), "count"),
            Get(like, "status").ValueKind == JsonValueKind.True);
        return new MomentInformation(id, user, Text(author, "pub_time"), community, replyType, replyId, type, data,
            content is null ? null : new EmoteText(content, emotes.Count == 0 ? null : emotes));
    }
}
