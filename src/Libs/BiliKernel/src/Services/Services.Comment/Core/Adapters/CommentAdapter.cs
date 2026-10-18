// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Bilibili.Main.Community.Reply.V1;
using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Comment;
using Richasy.BiliKernel.Models.Content;
using Richasy.BiliKernel.Models.User;

namespace Richasy.BiliKernel.Services.Comment.Core.Adapters;

internal static class CommentAdapter
{
    public static CommentView ToCommentView(this MainListReply reply, string targetId, CommentTargetType type)
    {
        var comments = reply.Replies.Select(p => p.ToCommentInformation()).ToList();
        var top = reply.UpTop ?? reply.VoteTop;
        var topComment = top?.ToCommentInformation();
        var isEnd = reply.Cursor.IsEnd;
        foreach (var item in comments)
        {
            item.CommentId = targetId;
            item.CommentType = type;
        }

        if (topComment is not null)
        {
            topComment.CommentId = targetId;
            topComment.CommentType = type;
        }

        return new CommentView(comments, targetId, topComment, isEnd, reply.Cursor.Next);
    }

    public static CommentView ToCommentView(this DetailListReply reply, string targetId, CommentTargetType type)
    {
        var comments = reply.Root.Replies.Select(r => r.ToCommentInformation()).ToList();
        var topComment = reply.Root.ToCommentInformation();
        var isEnd = reply.Cursor.IsEnd;
        foreach (var item in comments)
        {
            item.CommentId = targetId;
            item.CommentType = type;
        }

        return new CommentView(comments, targetId, topComment, isEnd, reply.Cursor.Next);
    }

    public static CommentInformation ToCommentInformation(this ReplyInfo info)
    {
        var id = info.Id.ToString();
        var rootId = info.Root.ToString();
        var isTop = info.ReplyControl.IsAdminTop || info.ReplyControl.IsUpTop;
        var publishTime = DateTimeOffset.FromUnixTimeSeconds(info.Ctime).ToLocalTime();
        var member = info.Member;
        var userProfile = UserAdapterBase.CreateUserProfile(member.Mid, member.Name, member.Face, 96d);
        var profileDetail = new UserDetailProfile(userProfile, default, Convert.ToInt32(member.Level), member.VipType > 0, member.IsSeniorMember > 0);
        var communityInfo = new CommentCommunityInformation(id, info.Like, Convert.ToInt32(info.Count), info.ReplyControl.Action == 1);
        var content = info.Content.ToEmoteText();
        return new CommentInformation(id, content, rootId, isTop, profileDetail, publishTime, communityInfo)
        {
            ContentMetadata = CopyMetadata(info.Content),
        };
    }

    private static ContentMetadata CopyMetadata(Bilibili.Main.Community.Reply.V1.Content content)
    {
        var links = new List<ContentLink>();
        foreach (var pair in content.Urls)
        {
            var url = pair.Value;
            links.Add(new ContentLink("Content.Urls", pair.Key, url.Title, url.AppUrlSchema, extraPresent: url.Extra is not null, goodsItemId: url.Extra?.GoodsItemId, originalText: url.PcUrl));
        }

        var rich = content.RichText;
        if (rich?.Note is { } note)
        {
            links.Add(new ContentLink("Content.RichText.Note", null, note.Summary, note.ClickUrl));
            foreach (var image in note.Images)
            {
                links.Add(new ContentLink("Content.RichText.Note.Images", null, null, image));
            }
        }

        if (rich?.Opus is { } opus)
        {
            links.Add(new ContentLink("Content.RichText.Opus", opus.OpusId.ToString(), opus.Oid.ToString(), null, (int)opus.OpusType));
            foreach (var module in opus.Modules)
            {
                if (module.ModuleDesc is { } desc)
                {
                    links.Add(new ContentLink("Content.RichText.Opus.ModuleDesc", null, desc.Text, desc.JumpUri));
                }
            }
        }

        return new ContentMetadata(null, null, links, null, richTextKind: rich is null ? null : (int)rich.ItemCase);
    }
}
