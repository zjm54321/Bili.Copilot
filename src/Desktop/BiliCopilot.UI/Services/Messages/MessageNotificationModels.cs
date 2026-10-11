// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json.Serialization;

namespace BiliCopilot.UI.Services.Messages;

/// <summary>A server unread snapshot. Each category contributes exactly once.</summary>
public sealed class UnreadMessageSnapshot
{
    public UnreadMessageSnapshot(int at, int reply, int like, int chat, int system)
    {
        AtCount = Math.Max(0, at);
        ReplyCount = Math.Max(0, reply);
        LikeCount = Math.Max(0, like);
        ChatCount = Math.Max(0, chat);
        SystemCount = Math.Max(0, system);
        Total = (int)Math.Min(int.MaxValue, (long)AtCount + ReplyCount + LikeCount + ChatCount + SystemCount);
    }

    public int AtCount { get; }
    public int ReplyCount { get; }
    public int LikeCount { get; }
    public int ChatCount { get; }
    public int SystemCount { get; }
    public int Total { get; }
}

/// <summary>Untrusted display text only; no HTML, navigation or time conversion.</summary>
public sealed record SystemMessageRow(long? Id, long? Cursor, string? Title, string? Content, string? TimeAt);

public interface IMessageNotificationService
{
    Task<UnreadMessageSnapshot> GetUnreadAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SystemMessageRow>> GetSystemMessagesAsync(long? cursor, CancellationToken cancellationToken);
    Task MarkReadAsync(long cursor, CancellationToken cancellationToken);
}

internal sealed class MessageNotificationException : Exception
{
    public MessageNotificationException()
    {
    }

    public MessageNotificationException(string message) : base(message)
    {
    }

    public MessageNotificationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

internal sealed class UnreadResponse
{
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("data")] public UnreadData? Data { get; set; }
}

internal sealed class UnreadData
{
    [JsonPropertyName("at")] public int? At { get; set; }
    [JsonPropertyName("reply")] public int? Reply { get; set; }
    [JsonPropertyName("like")] public int? Like { get; set; }
    [JsonPropertyName("sys_msg")] public int? System { get; set; }
}

internal sealed class PersonalUnreadResponse
{
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("data")] public PersonalUnreadData? Data { get; set; }
}

internal sealed class PersonalUnreadData
{
    [JsonPropertyName("follow_unread")] public int? FollowUnread { get; set; }
    [JsonPropertyName("unfollow_unread")] public int? UnfollowUnread { get; set; }
}

internal sealed class SystemMessagesResponse
{
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("data")] public List<SystemMessageData?>? Data { get; set; }
}

internal sealed class SystemMessageData
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("cursor")] public long? Cursor { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("time_at")] public string? TimeAt { get; set; }
}

internal sealed class MarkReadResponse
{
    [JsonPropertyName("code")] public int? Code { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UnreadResponse))]
[JsonSerializable(typeof(PersonalUnreadResponse))]
[JsonSerializable(typeof(SystemMessagesResponse))]
[JsonSerializable(typeof(MarkReadResponse))]
internal sealed partial class MessageNotificationJsonContext : JsonSerializerContext;
