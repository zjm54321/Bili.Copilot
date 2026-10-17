#nullable enable

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiliCopilot.UI.Live;

internal static class LiveWbi
{
    private static readonly int[] Order = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];

    internal static string DiscoveryQuery(long roomId, JsonElement navigation)
    {
        // Logged-out nav returns -101 but still supplies the public signing keys.
        if (!LiveHttpApi.TryApiCode(navigation, out var code) || code is not (0 or -101)
            || !navigation.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("wbi_img", out var image) || image.ValueKind != JsonValueKind.Object)
            throw LiveProtocol.Invalid();
        var source = KeyPart(image, "img_url") + KeyPart(image, "sub_url");
        var key = new string(Order.Select(index => source[index]).ToArray());
        // All values here are decimal digits; no WBI special-character filtering is needed.
        var query = "id=" + roomId.ToString(CultureInfo.InvariantCulture) + "&type=0&wts="
            + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + key))).ToLowerInvariant();
        return query + "&w_rid=" + digest;
    }

    private static string KeyPart(JsonElement image, string name)
    {
        if (!image.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
            throw LiveProtocol.Invalid();
        var key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (key.Length != 32 || !key.All(char.IsAsciiHexDigit))
            throw LiveProtocol.Invalid();
        return key;
    }
}
