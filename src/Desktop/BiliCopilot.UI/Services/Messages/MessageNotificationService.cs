// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Http;
using System.Globalization;

namespace BiliCopilot.UI.Services.Messages;

/// <summary>App-owned protocol adapter using the existing authenticated SDK stack.</summary>
public sealed class MessageNotificationService(
    BiliHttpClient client,
    BiliAuthenticator authenticator,
    IAuthenticationService authentication) : IMessageNotificationService
{
    public async Task<UnreadMessageSnapshot> GetUnreadAsync(CancellationToken cancellationToken)
    {
        // Match the SDK unread endpoint's existing token/authentication semantics.
        await authentication.EnsureTokenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var notifications = GetNotificationUnreadAsync(cancellationToken);
        var personal = GetPersonalUnreadAsync(cancellationToken);
        // Drain both requests, including on failure. Never publish a partial snapshot.
        await Task.WhenAll(notifications, personal);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateUnreadSnapshot(await notifications, await personal);
    }

    private async Task<UnreadResponse?> GetNotificationUnreadAsync(CancellationToken cancellationToken)
    {
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://api.bilibili.com/x/msgfeed/unread"));
        authenticator.AuthorizeRestRequest(request);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BiliHttpClient.ParseAsync(response, MessageNotificationJsonContext.Default.UnreadResponse);
    }

    private async Task<PersonalUnreadResponse?> GetPersonalUnreadAsync(CancellationToken cancellationToken)
    {
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://api.vc.bilibili.com/session_svr/v1/session_svr/single_unread"));
        authenticator.AuthorizeRestRequest(request, new Dictionary<string, string>
        {
            ["build"] = "0",
            ["mobi_app"] = "web",
            ["unread_type"] = "0",
            ["web_location"] = "333.1365",
            ["show_unfollow_list"] = "1",
            ["show_dustbin"] = "0",
        }, CookieSettings());
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BiliHttpClient.ParseAsync(response, MessageNotificationJsonContext.Default.PersonalUnreadResponse);
    }

    internal static UnreadMessageSnapshot CreateUnreadSnapshot(UnreadResponse? notifications, PersonalUnreadResponse? personal)
    {
        EnsureSuccess(notifications?.Code);
        EnsureSuccess(personal?.Code);
        var data = notifications?.Data;
        if (data?.At is not int at || data.Reply is not int reply || data.Like is not int like
            || data.System is not int system)
        {
            throw new MessageNotificationException("未读消息响应格式无效，请重试。");
        }

        var personalData = personal?.Data;
        if (personalData?.FollowUnread is not int follow || personalData.UnfollowUnread is not int unfollow)
        {
            throw new MessageNotificationException("私信未读响应格式无效，请重试。");
        }

        // msgfeed.chat is not a personal-message count. Ignore it and all push,
        // dustbin, business and customer-service counters from single_unread.
        var chat = (int)Math.Min(int.MaxValue, (long)Math.Max(0, follow) + Math.Max(0, unfollow));
        return new UnreadMessageSnapshot(at, reply, like, chat, system);
    }

    public async Task<IReadOnlyList<SystemMessageRow>> GetSystemMessagesAsync(long? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = new Dictionary<string, string>
        {
            ["page_size"] = "20",
            ["mobi_app"] = "web",
            ["build"] = "0",
            ["web_location"] = "333.40164",
        };
        if (cursor.HasValue)
        {
            parameters["cursor"] = cursor.Value.ToString(CultureInfo.InvariantCulture);
        }

        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://message.bilibili.com/x/sys-msg/query_notify_list"));
        authenticator.AuthorizeRestRequest(request, parameters, CookieSettings());
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await BiliHttpClient.ParseAsync(response, MessageNotificationJsonContext.Default.SystemMessagesResponse);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result?.Code);
        if (result?.Data is null || result.Data.Any(item => item is null))
        {
            throw new MessageNotificationException("系统通知响应格式无效，请重试。");
        }

        return result.Data.Select(item => new SystemMessageRow(item!.Id, item.Cursor, item.Title, item.Content, item.TimeAt)).ToArray();
    }

    public async Task MarkReadAsync(long cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://message.bilibili.com/x/sys-msg/update_cursor"));
        authenticator.AuthorizeRestRequest(request,
            new Dictionary<string, string> { ["cursor"] = cursor.ToString(CultureInfo.InvariantCulture) }, CookieSettings(markRead: true));

        // Inspect only the SDK-authorized query. Never obtain credentials from a resolver,
        // synthesize a CSRF value, or expose this URL/value to a log or display property.
        if (!HasAuthorizedCsrf(request.RequestUri))
        {
            throw new MessageNotificationException("无法标记已读：登录凭据不完整，请重新登录。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await BiliHttpClient.ParseAsync(response, MessageNotificationJsonContext.Default.MarkReadResponse);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result?.Code);
    }

    private static BiliAuthorizeExecutionSettings CookieSettings(bool markRead = false) => new()
    {
        ForceNoToken = true,
        RequireCookie = true,
        NeedCSRF = markRead,
    };

    internal static bool HasAuthorizedCsrf(Uri? uri)
    {
        if (uri is null) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == "csrf")
            {
                return !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            }
        }

        return false;
    }

    private static void EnsureSuccess(int? code)
    {
        if (code is null) throw new MessageNotificationException("消息响应格式无效，请重试。");
        if (code != 0) throw new MessageNotificationException($"消息请求失败（代码 {code}），请重试。");
    }
}
