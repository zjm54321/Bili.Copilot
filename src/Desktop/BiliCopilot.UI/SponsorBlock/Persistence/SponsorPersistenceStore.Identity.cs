using System.Security.Cryptography;
using System.Text.Json;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

public enum SponsorIdentityCreateIntent { None, CreateNewLocalIdentity }

/// <summary>Deliberately not a record/serializable DTO. Private ID is exposed only through an explicitly named
/// request handoff, never through ToString, settings, backup, a filename or diagnostics. Managed strings cannot
/// be reliably zeroed; caller must keep the lifetime narrow and never log or serialize this object.</summary>
public sealed class SponsorLocalIdentity
{
    private readonly string privateId;
    internal SponsorLocalIdentity(string privateId, string publicId) { this.privateId = privateId; PublicId = publicId; }
    public string PublicId { get; }
    public string PrivateIdForExplicitRequest() => privateId;
    public override string ToString() => "SponsorLocalIdentity (redacted)";
}

public sealed partial class SponsorPersistenceStore
{
    /// <summary>Load only. Missing/unavailable/corrupt never generates or replaces an identity.</summary>
    public Task<PersistenceResult<SponsorLocalIdentity>> LoadIdentityAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(ReadIdentityAsync, cancellationToken);

    /// <summary>Only this explicit operation generates an ID (32 cryptographic bytes encoded as lower hex).
    /// Existing bytes, including unreadable blobs, block creation. No reset/rotate/import-secret shortcut exists.</summary>
    public Task<PersistenceResult<SponsorLocalIdentity>> CreateIdentityAsync(SponsorIdentityCreateIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent != SponsorIdentityCreateIntent.CreateNewLocalIdentity)
            return Task.FromResult(new PersistenceResult<SponsorLocalIdentity>(PersistenceStatus.ExplicitIntentRequired));
        return LockedAsync<SponsorLocalIdentity>(async token =>
        {
            var existing = await files.ReadAsync(SponsorLocalFile.Identity, PersistenceJson.MaximumIdentityBytes, token).ConfigureAwait(false);
            if (existing is not null) return new(PersistenceStatus.AlreadyExists);
            if (!protector.IsSupported) return new(PersistenceStatus.ProtectionUnavailable);
            token.ThrowIfCancellationRequested();
            var random = RandomNumberGenerator.GetBytes(32);
            string privateId;
            try { privateId = Convert.ToHexStringLower(random); }
            finally { CryptographicOperations.ZeroMemory(random); }
            // Finish cancellable derivation before the atomic write. Success cannot later be relabelled Cancelled.
            var publicId = await MediaIdentity.DerivePublicUserIdAsync(privateId, token).ConfigureAwait(false);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new IdentityDocument(1, files.Scope.ProtectionPurpose, privateId),
                PersistenceJsonContext.Default.IdentityDocument);
            IdentityProtectionResult protectedResult;
            try { protectedResult = await protector.ProtectAsync(plain, files.Scope.ProtectionPurpose, token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var cipher = protectedResult.Bytes;
            try
            {
                if (protectedResult.Status != PersistenceStatus.Success) return new(protectedResult.Status);
                if (cipher is null || cipher.Length == 0 || cipher.Length > PersistenceJson.MaximumIdentityBytes)
                    return new(PersistenceStatus.ProtectionUnavailable);
                await files.ReplaceAsync(SponsorLocalFile.Identity, cipher, token).ConfigureAwait(false);
                return new(PersistenceStatus.Success, new(privateId, publicId));
            }
            finally { if (cipher is not null) CryptographicOperations.ZeroMemory(cipher); }
        }, cancellationToken);
    }

    private async Task<PersistenceResult<SponsorLocalIdentity>> ReadIdentityAsync(CancellationToken token)
    {
        var cipher = await files.ReadAsync(SponsorLocalFile.Identity, PersistenceJson.MaximumIdentityBytes, token).ConfigureAwait(false);
        if (cipher is null) return new(PersistenceStatus.Missing);
        if (!protector.IsSupported) return new(PersistenceStatus.ProtectionUnavailable);
        var unprotected = await protector.UnprotectAsync(cipher, files.Scope.ProtectionPurpose, token).ConfigureAwait(false);
        var plain = unprotected.Bytes;
        try
        {
            if (unprotected.Status != PersistenceStatus.Success) return new(unprotected.Status);
            if (plain is null || plain.Length == 0 || plain.Length > PersistenceJson.MaximumIdentityBytes)
                return new(PersistenceStatus.DecryptionFailed);
            using var raw = PersistenceJson.Parse(plain);
            var header = PersistenceJson.Header(raw.RootElement);
            if (header != PersistenceStatus.Success) return new(header);
            if (!PersistenceJson.HasFields(raw.RootElement, "SchemaVersion", "Purpose", "PrivateId"))
                return new(PersistenceStatus.DecryptionFailed);
            var doc = JsonSerializer.Deserialize(plain, PersistenceJsonContext.Default.IdentityDocument)!;
            if (doc.Purpose != files.Scope.ProtectionPurpose) return new(PersistenceStatus.ForeignScope);
            if (!ValidPrivateId(doc.PrivateId)) return new(PersistenceStatus.DecryptionFailed);
            var publicId = await MediaIdentity.DerivePublicUserIdAsync(doc.PrivateId, token).ConfigureAwait(false);
            return new(PersistenceStatus.Success, new(doc.PrivateId, publicId));
        }
        catch (JsonException) { return new(PersistenceStatus.DecryptionFailed); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool ValidPrivateId(string? id)
    {
        if (id is null || id.Length != 64) return false;
        foreach (var character in id) if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
