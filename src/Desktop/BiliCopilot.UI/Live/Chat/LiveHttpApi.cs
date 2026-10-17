#nullable enable

using System.Globalization;
using System.Net;
using System.Text.Json;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Live;

internal sealed class LiveHttpApi : IDisposable
{
    private static readonly Uri SendEndpoint = new("https://api.live.bilibili.com/msg/send");
    private readonly HttpClient _client;

    internal LiveHttpApi(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static HttpMessageHandler CreateSecureHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        // Deliberately no certificate-validation callback.
    };

    internal async Task<LiveDiscovery> DiscoverAsync(LiveBinding binding, LiveCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (binding.RoomId <= 0)
            throw LiveProtocol.Invalid();
        var buvid = credentials.Buvid;
        if (string.IsNullOrEmpty(buvid))
        {
            using var document = await GetJsonAsync(new Uri(BiliApis.Account.Buvid), credentials.CookieHeader,
                16384, cancellationToken).ConfigureAwait(false);
            var data = RequireData(document.RootElement);
            if (!data.TryGetProperty("b_3", out var property) || property.ValueKind != JsonValueKind.String)
                throw Unavailable();
            buvid = property.GetString();
            if (string.IsNullOrWhiteSpace(buvid) || buvid.Length > 512 || !LiveCredentials.IsCookieValue(buvid))
                throw Unavailable();
        }

        // Newly fetched buvid is ephemeral and never written into the resolver's dictionary.
        var cookies = credentials.CookieHeader;
        if (string.IsNullOrEmpty(credentials.Buvid))
            cookies = string.IsNullOrEmpty(cookies) ? "buvid3=" + buvid : cookies + "; buvid3=" + buvid;
        using var navigation = await GetJsonAsync(new Uri("https://api.bilibili.com/x/web-interface/nav"),
            cookies, 65536, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(BiliApis.Live.WebDanmakuInformation + "?"
            + LiveWbi.DiscoveryQuery(binding.RoomId, navigation.RootElement));
        using var discovery = await GetJsonAsync(endpoint, cookies, 65536, cancellationToken).ConfigureAwait(false);
        var discoveryData = RequireData(discovery.RootElement);
        if (!discoveryData.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()) || token.GetString()!.Length > 4096
            || !discoveryData.TryGetProperty("host_list", out var hosts) || hosts.ValueKind != JsonValueKind.Array
            || hosts.GetArrayLength() is < 1 or > 32)
            throw Unavailable();
        foreach (var host in hosts.EnumerateArray())
        {
            if (host.ValueKind != JsonValueKind.Object || !host.TryGetProperty("host", out var name)
                || name.ValueKind != JsonValueKind.String || !host.TryGetProperty("wss_port", out var port)
                || port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var number))
                continue;
            if (TrySocketEndpoint(name.GetString(), number, out var uri))
            {
                return new LiveDiscovery(uri!, token.GetString()!, buvid!);
            }
        }
        throw Unavailable();
    }

    // Policy, not a claim about every legitimate Bilibili server: a proper ASCII DNS
    // subdomain of chat.bilibili.com, advertised WSS port (443 or 2245), fixed /sub route.
    // No IPs, trailing dots, IDNs, userinfo, arbitrary ports, WS, or fallback hosts.
    internal static bool TrySocketEndpoint(string? host, int port, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrEmpty(host) || host.Length > 253 || port is not (443 or 2245)
            || !host.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (var c in label)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                    return false;
        }
        endpoint = new Uri("wss://" + host.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture) + "/sub", UriKind.Absolute);
        return true;
    }

    internal static HttpRequestMessage CreateSendRequest(LiveBinding binding, string text, LiveCredentials credentials)
    {
        if (!credentials.CanSend || binding.RoomId <= 0 || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            throw Unavailable();
        var request = CreateRequest(HttpMethod.Post, SendEndpoint, credentials.CookieHeader);
        try
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roomid"] = binding.RoomId.ToString(CultureInfo.InvariantCulture),
                ["msg"] = text,
                ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["bubble"] = "0",
                ["color"] = "16777215",
                ["mode"] = "1",
                ["fontsize"] = "25",
                ["csrf"] = credentials.Csrf!,
                ["csrf_token"] = credentials.Csrf!,
            });
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    // Ownership of request transfers here. tryAdmit and SendAsync execute back-to-back
    // synchronously on the UI thread under the caller's admission lock; no await gap.
    internal async Task<LiveSendResult> SendOnceAsync(HttpRequestMessage request, LiveBinding binding,
        CancellationToken cancellationToken, Func<bool> tryAdmit)
    {
        var dispatched = false;
        using (request)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var transport = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
        {
            try
            {
                if (transport.IsCancellationRequested || !tryAdmit())
                    return NotSent(binding);
                dispatched = true;
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    transport.Token).ConfigureAwait(false);
                // If cancellation races an eventual response, try to preserve that known
                // acknowledgement. Reads still have this attempt's finite deadline and cap.
                using var document = await ReadJsonAsync(response, 16384, deadline.Token).ConfigureAwait(false);
                if (!TryApiCode(document.RootElement, out var code))
                    return Unknown(binding);
                if (code != 0)
                    return new LiveSendResult(binding, LiveSendOutcome.Rejected, code, "The server rejected the message.");
                return response.IsSuccessStatusCode
                    ? new LiveSendResult(binding, LiveSendOutcome.Accepted, 0, "The server accepted the message.")
                    : Unknown(binding);
            }
            catch (Exception)
            {
                // No raw exception, response, message text, URL, or credential leaves here.
                return dispatched ? Unknown(binding) : NotSent(binding);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri endpoint, string cookies, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = CreateRequest(HttpMethod.Get, endpoint, cookies);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new LiveChatFailure("Live chat discovery is unavailable.", status >= 500 || status is 408 or 429, true);
        }
        return await ReadJsonAsync(response, maximumBytes, deadline.Token).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string cookies)
    {
        if (endpoint.Scheme != "https" || endpoint.Port != 443 || endpoint.UserInfo.Length != 0
            || endpoint.Fragment.Length != 0
            || (endpoint.Host != "api.live.bilibili.com" && endpoint.Host != "api.bilibili.com"))
            throw Unavailable();
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        if (!string.IsNullOrEmpty(cookies))
            request.Headers.Add("Cookie", cookies);
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes || response.Content.Headers.ContentEncoding.Count != 0)
            throw LiveProtocol.Invalid();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - (int)output.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (count > remaining)
                throw LiveProtocol.Invalid();
            await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        try { return JsonDocument.Parse(output.ToArray(), LiveProtocol.JsonOptions); }
        catch (JsonException) { throw LiveProtocol.Invalid(); }
    }

    internal static bool TryApiCode(JsonElement root, out int code)
    {
        code = 0;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "code")
                continue;
            if (found || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out code))
                return false;
            found = true;
        }
        return found;
    }

    private static JsonElement RequireData(JsonElement root)
    {
        if (!TryApiCode(root, out var code) || code != 0
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw Unavailable();
        }
        return data;
    }

    internal static LiveSendResult NotSent(LiveBinding binding) =>
        new(binding, LiveSendOutcome.NotSent, null, "The message was not sent.");
    internal static LiveSendResult Unknown(LiveBinding binding) =>
        new(binding, LiveSendOutcome.Unknown, null, "Delivery is unknown. The message will not be retried.");
    private static LiveChatFailure Unavailable() => new("Live chat credentials or discovery are unavailable.", false, true);
    public void Dispose() => _client.Dispose();
}

internal sealed class LiveDiscovery
{
    internal LiveDiscovery(Uri endpoint, string key, string buvid)
    {
        Endpoint = endpoint;
        Key = key;
        Buvid = buvid;
    }
    internal Uri Endpoint { get; }
    internal string Key { get; }
    internal string Buvid { get; }
}

// Ephemeral UI-thread snapshot. Only the minimal first-party cookie allowlist is copied.
// No access/refresh token value, persistence method, or mutable resolver dictionary escapes.
internal sealed class LiveCredentials
{
    private static readonly string[] CookieNames = { "SESSDATA", "DedeUserID", "DedeUserID__ckMd5", "bili_jct", "buvid3" };

    private LiveCredentials(LiveAccountStamp stamp, long userId, Dictionary<string, string> cookies)
    {
        Stamp = stamp;
        UserId = userId;
        cookies.TryGetValue("buvid3", out var buvid);
        cookies.TryGetValue("bili_jct", out var csrf);
        Buvid = buvid;
        Csrf = csrf;
        CanSend = userId > 0 && cookies.TryGetValue("SESSDATA", out var session) && !string.IsNullOrEmpty(session)
            && !string.IsNullOrEmpty(csrf) && cookies.ContainsKey("DedeUserID");
        var pairs = new List<string>();
        foreach (var name in CookieNames)
            if (cookies.TryGetValue(name, out var value))
                pairs.Add(name + "=" + value);
        CookieHeader = string.Join("; ", pairs);
    }

    internal LiveAccountStamp Stamp { get; }
    internal long UserId { get; }
    internal string? Buvid { get; }
    internal string? Csrf { get; }
    internal string CookieHeader { get; }
    internal bool CanSend { get; }

    internal static bool Matches(LiveAccountStamp stamp, LiveBinding binding) =>
        stamp.Generation == binding.AccountGeneration && stamp.AccountId == binding.AccountId
        && !stamp.Lifetime.IsCancellationRequested;

    internal static LiveCredentials Capture(LiveBinding binding, Func<LiveAccountStamp> account,
        IBiliCookiesResolver cookies, IBiliTokenResolver tokens)
    {
        var stamp = account();
        if (!Matches(stamp, binding))
            throw Invalid();
        var uid = 0L;
        if (stamp.AccountId is not null
            && (!long.TryParse(stamp.AccountId, NumberStyles.None, CultureInfo.InvariantCulture, out uid) || uid <= 0))
            throw Invalid();
        var source = cookies.GetCookies();
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var name in CookieNames)
        {
            if (!source.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
                continue;
            if (value.Length > 8192 || (total += value.Length) > 16384 || !IsCookieValue(value))
                throw Invalid();
            copy.Add(name, value);
        }
        if (copy.TryGetValue("buvid3", out var buvid) && buvid.Length > 512)
            throw Invalid();
        if (copy.TryGetValue("DedeUserID", out var cookieUser)
            && (!long.TryParse(cookieUser, NumberStyles.None, CultureInfo.InvariantCulture, out var cookieId)
                || cookieId <= 0 || cookieId != uid))
            throw Invalid();
        if (uid == 0 && (copy.ContainsKey("SESSDATA") || copy.ContainsKey("bili_jct")))
            throw Invalid();
        if (copy.ContainsKey("SESSDATA") && !copy.ContainsKey("DedeUserID"))
            throw Invalid();
        var token = tokens.GetToken();
        if (token is not null && (token.UserId <= 0 || token.UserId != uid))
            throw Invalid();
        var current = account();
        if (current != stamp || !Matches(current, binding))
            throw Invalid();
        return new LiveCredentials(stamp, uid, copy);
    }

    internal static bool IsCookieValue(string value)
    {
        foreach (var c in value)
            if (c < 0x21 || c > 0x7e || c is '"' or ',' or ';' or '\\')
                return false;
        return true;
    }

    private static LiveChatFailure Invalid() => new("Live chat account credentials are unavailable.", false, true);
}
