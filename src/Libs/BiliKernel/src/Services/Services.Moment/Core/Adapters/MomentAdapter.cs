// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Bilibili.App.Dynamic.V2;
using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Content;
using Richasy.BiliKernel.Models.Moment;
using Richasy.BiliKernel.Models.User;

namespace Richasy.BiliKernel.Services.Moment.Core;

internal static class MomentAdapter
{
    public static MomentInformation ToMomentInformation(this DynamicItem item)
    {
        var modules = item.Modules;
        var userModule = modules.Where(p => p.ModuleType == DynModuleType.ModuleAuthor).FirstOrDefault()?.ModuleAuthor;
        var descModule = modules.Where(p => p.ModuleType == DynModuleType.ModuleDesc).FirstOrDefault()?.ModuleDesc;
        var mainModule = modules.Where(p => p.ModuleType == DynModuleType.ModuleDynamic).FirstOrDefault()?.ModuleDynamic;
        var dataModule = modules.Where(p => p.ModuleType == DynModuleType.ModuleStat).FirstOrDefault()?.ModuleStat;

        if (mainModule is null)
        {
            return default;
        }

        UserProfile user = default;
        string tip = default;
        EmoteText description = default;
        MomentCommunityInformation communityInfo = default;
        var momentId = item.Extend.DynIdStr;
        var replyType = GetReplyTypeFromDynamicType(item.CardType);
        var replyId = replyType == CommentTargetType.Moment
            ? momentId
            : mainModule.ModuleItemCase == ModuleDynamic.ModuleItemOneofCase.DynPgc
                ? mainModule.DynPgc.Aid.ToString()
                : item.Extend.BusinessId;
        var momentType = GetMomentItemType(mainModule);
        var momentData = GetMomentContent(mainModule);
        if (userModule != null)
        {
            var author = userModule.Author;
            user = UserAdapterBase.CreateUserProfile(author.Mid, author.Name, author.Face, 64d);
            tip = userModule.PtimeLabelText;
        }
        else
        {
            var forwardUserModule = modules.Where(p => p.ModuleType == DynModuleType.ModuleAuthorForward).FirstOrDefault()?.ModuleAuthorForward;
            if (forwardUserModule != null)
            {
                var name = forwardUserModule.Title.FirstOrDefault()?.Text ?? "--";
                user = UserAdapterBase.CreateUserProfile(forwardUserModule.Uid, name, forwardUserModule.FaceUrl, 48d);
                tip = forwardUserModule.PtimeLabelText;
            }
        }

        if (descModule != null)
        {
            description = descModule.ToEmoteText();
        }

        if (dataModule != null)
        {
            communityInfo = new MomentCommunityInformation(
                momentId,
                dataModule.Like,
                dataModule.Reply,
                dataModule.LikeInfo?.IsLike ?? false);
        }

        return new MomentInformation(
            momentId,
            user,
            tip,
            communityInfo,
            replyType,
            replyId,
            momentType,
            momentData,
            description)
        {
            ContentMetadata = CopyMetadata(item, momentData as MomentInformation),
        };
    }

    private static ContentMetadata CopyMetadata(DynamicItem item, MomentInformation? original)
    {
        var goods = new List<ContentGoods>();
        var links = new List<ContentLink>();
        var origins = new List<ContentOrigin>();
        foreach (var module in item.Modules)
        {
            var additional = module.ModuleAdditional;
            if (additional?.Goods is { } card)
            {
                goods.Add(new ContentGoods("ModuleAdditional.Goods", null, card.RcmdDesc, card.Uri, null, card.SourceType, card.CardType, card.AdMarkIcon, (int)card.JumpType));
                foreach (var entry in card.GoodsItems)
                {
                    goods.Add(new ContentGoods("ModuleAdditional.Goods.GoodsItems", entry.ItemId, entry.Title, entry.JumpUrl, entry.SchemaUrl, entry.SourceType, card.CardType, entry.AdMark, (int)entry.JumpType));
                }
            }

            if (module.ModuleDesc is { } desc)
            {
                links.Add(new ContentLink("ModuleDesc", null, desc.Text, desc.JumpUri));
                foreach (var entry in desc.Desc)
                {
                    links.Add(new ContentLink("ModuleDesc.Desc", entry.Rid, entry.Text, entry.Uri, (int)entry.Type, originalText: entry.OrigText));
                    if (entry.Goods is { } detail)
                    {
                        goods.Add(new ContentGoods("ModuleDesc.Desc.Goods", detail.ItemId, entry.Text, detail.JumpUrl, detail.SchemaUrl, detail.SourceType, entry.GoodsType, detail.AdMark, (int)detail.JumpType));
                    }
                    else if (entry.Type == DescType.Goods)
                    {
                        goods.Add(new ContentGoods("ModuleDesc.Desc", null, entry.Text, entry.Uri, null, null, entry.GoodsType, null, null));
                    }
                }
            }

            if (module.ModuleAuthor is { } author)
            {
                origins.Add(new ContentOrigin((int)module.ModuleType, author.Author?.Mid, author.Author?.Name, author.Uri, null));
            }

            if (module.ModuleAuthorForward is { } forward)
            {
                var titles = forward.Title.Select(p => new ContentLink("ModuleAuthorForward.Title", null, p.Text, p.Url));
                origins.Add(new ContentOrigin((int)module.ModuleType, forward.Uid, null, forward.Url, titles));
            }
        }

        return new ContentMetadata(item.Modules.Select(p => (int)p.ModuleType), goods, links, origins, (int)item.CardType, original: original?.ContentMetadata);
    }

    /// <summary>
    /// 将动态的描述模块 <see cref="ModuleDesc"/> 转换为表情文本.
    /// </summary>
    /// <param name="description">动态的描述模块.</param>
    /// <returns><see cref="EmoteText"/>.</returns>
    public static EmoteText ToEmoteText(this ModuleDesc description)
    {
        var text = description.Text;
        var descs = description.Desc;
        var emoteDict = new Dictionary<string, BiliImage>();

        // 判断是否有表情存在.
        if (descs.Count > 0 && descs.Any(p => p.Type == DescType.Emoji))
        {
            foreach (var item in (IEnumerable<Description>)descs.Where(p => p.Type == DescType.Emoji && !string.IsNullOrEmpty(p.Uri)))
            {
                var t = item.Text;
                if (!emoteDict.ContainsKey(t))
                {
                    emoteDict.Add(t, item.Uri.ToImage());
                }
            }
        }
        else
        {
            emoteDict = null;
        }

        return new EmoteText(text, emoteDict);
    }

    public static MomentInformation ToMomentInformation(this MdlDynForward forward)
    {
        var item = forward.Item;
        return item.ToMomentInformation();
    }

    private static CommentTargetType GetReplyTypeFromDynamicType(DynamicType type)
    {
        return type switch
        {
            DynamicType.Forward or
            DynamicType.Word or
            DynamicType.Live => CommentTargetType.Moment,
            DynamicType.Draw => CommentTargetType.Album,
            DynamicType.Av or
            DynamicType.Pgc or
            DynamicType.UgcSeason or
            DynamicType.Medialist => CommentTargetType.Video,
            DynamicType.Courses or
            DynamicType.CoursesSeason => CommentTargetType.Course,
            DynamicType.Article => CommentTargetType.Article,
            DynamicType.Music => CommentTargetType.Music,
            _ => CommentTargetType.None,
        };
    }

    private static MomentItemType GetMomentItemType(ModuleDynamic dynamic)
    {
        return dynamic == null
            ? MomentItemType.PlainText
            : dynamic.Type switch
            {
                ModuleDynamicType.MdlDynArchive => dynamic.DynArchive.IsPGC
                    ? MomentItemType.Pgc
                    : MomentItemType.Video,
                ModuleDynamicType.MdlDynPgc => MomentItemType.Pgc,
                ModuleDynamicType.MdlDynForward => MomentItemType.Forward,
                ModuleDynamicType.MdlDynDraw => MomentItemType.Image,
                ModuleDynamicType.MdlDynArticle => MomentItemType.Article,
                _ => MomentItemType.Unsupported,
            };
    }

    private static object? GetMomentContent(ModuleDynamic dynamic)
    {
        if (dynamic == null)
        {
            return null;
        }

        if (dynamic.Type == ModuleDynamicType.MdlDynPgc)
        {
            return dynamic.DynPgc.ToEpisodeInformation();
        }
        else if (dynamic.Type == ModuleDynamicType.MdlDynArchive)
        {
            return dynamic.DynArchive.IsPGC
                ? dynamic.DynArchive.ToEpisodeInformation()
                : dynamic.DynArchive.ToVideoInformation();
        }
        else if (dynamic.Type == ModuleDynamicType.MdlDynForward)
        {
            return dynamic.DynForward.ToMomentInformation();
        }
        else if (dynamic.Type == ModuleDynamicType.MdlDynDraw)
        {
            return dynamic.DynDraw.Items.Select(p => p.Src.ToImage(240d, 240d)).ToList();
        }
        else if (dynamic.Type == ModuleDynamicType.MdlDynArticle)
        {
            return dynamic.DynArticle.ToArticleInformation();
        }

        return null;
    }
}
