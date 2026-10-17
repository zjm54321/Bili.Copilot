using System.Security.Cryptography;

namespace BiliCopilot.UI.SponsorBlock.Models;

/// <summary>An exact selected VOD part. Prefix hashes are never identities.</summary>
public sealed record MediaIdentity
{
    private MediaIdentity(string bvId, string cid) { BvId = bvId; Cid = cid; }

    public string BvId { get; }
    public string Cid { get; }
    public string Combined => BvId + "+" + Cid;
    public string VideoPrefix => HashOnce(BvId)[..4];
    public string LockPrefix => HashOnce(Combined)[..4];

    public static bool TryParse(string? value, out MediaIdentity? identity)
    {
        identity = null;
        if (value is null || value.Length > 128) return false;
        var parts = value.Split('+');
        if (parts.Length != 2 || !parts[0].StartsWith("BV", StringComparison.Ordinal)
            || parts[0].Length <= 2 || parts[1].Length == 0) return false;
        foreach (var c in parts[0]) if (!char.IsAsciiLetterOrDigit(c)) return false;
        foreach (var c in parts[1]) if (!char.IsAsciiDigit(c)) return false;
        identity = new(parts[0], parts[1]);
        return true;
    }

    internal static string HashOnce(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Explicit secret input only; no storage or logging. CPU work never runs on the caller's UI thread.</summary>
    public static Task<string> DerivePublicUserIdAsync(string privateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateId);
        return Task.Run(() =>
        {
            var hash = privateId;
            for (var i = 0; i < 5000; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash = HashOnce(hash);
            }
            return hash;
        }, cancellationToken);
    }
}
