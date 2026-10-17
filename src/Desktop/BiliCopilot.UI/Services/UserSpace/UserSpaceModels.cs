// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Services.UserSpace;

public enum UserSpaceTab { Home, Dynamics, Submissions, Collections }
public enum UserSubmissionKind { Video, Opus, Audio }
public enum UserVideoOrder { Latest, MostPlayed, MostFavorited }
public enum UserOpusFilter { All, Article, Dynamic }
public enum UserCollectionKind { Season, Series }
public enum UserSpaceLoadState { Idle, Loading, Ready, Empty, Error, Unavailable }
public enum UserSpaceFeaturedKind { None, Top, Masterpiece }

public sealed record UserSpaceVideoRow(VideoInformation Video, string? Reason)
{
    public string? FeaturedReason => string.IsNullOrWhiteSpace(Reason) ? null : Reason;
    public UserSpaceFeaturedKind FeaturedKind { get; init; }
    public string Id => Video.Identifier.Id;
    public string? Title => Video.Identifier.Title;
    public Uri? Cover => Video.Identifier.Cover?.SourceUri;
    public string? PublisherName => Video.Publisher?.User?.Name;
    public double? ViewCount => Video.CommunityInformation?.PlayCount;
    public double? FavoriteCount => Video.CommunityInformation?.FavoriteCount;
    public long? Duration => Video.Duration;
    public DateTimeOffset? PublishTime => Video.PublishTime;
}

public sealed record UserSpaceOpusRow(string Id, string? Content, Uri? Cover, Uri? OriginalUri, string? LikeText, string? ViewText)
{
    // Id 始终是 opus_id；仅从真实的 /read/cv 地址取得专栏身份，两者不能互换.
    public string? ArticleId { get; init; }
}
public sealed record UserSpaceAudioRow(string Id, Uri OriginalUri);
public sealed record UserSpaceCollection(UserCollectionKind Kind, string Id, string OwnerId, string? Name, Uri? Cover, long? Count);
public sealed record UserSpacePage<T>(IReadOnlyList<T> Items, bool HasMore, int NextPage = 1, string? NextOffset = null);

public sealed class UserSpaceDataException : Exception
{
    public UserSpaceDataException() { }
    public UserSpaceDataException(string? message) : base(message) { }
    public UserSpaceDataException(string? message, Exception? innerException) : base(message, innerException) { }
    public UserSpaceDataException(UserSpaceLoadState state, string safeMessage) : base(safeMessage) => State = state;
    public UserSpaceLoadState State { get; } = UserSpaceLoadState.Error;
}
