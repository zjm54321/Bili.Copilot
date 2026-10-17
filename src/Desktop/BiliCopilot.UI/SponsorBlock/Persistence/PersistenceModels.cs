using System.Security.Cryptography;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

public enum PersistenceStatus
{
    Success, Missing, Corrupt, UnsupportedSchema, InvalidData, TooLarge, ForeignScope,
    Conflict, Busy, IoFailure, Cancelled, ExplicitIntentRequired, UnresolvedDrafts,
    ProtectionUnavailable, DecryptionFailed, AlreadyExists, UnresolvedMutations, HistoryConflict, CapacityReached,
}

// Never include exception messages, paths, raw JSON or secret values in results/loggable records.
public sealed record PersistenceResult<T>(PersistenceStatus Status, T? Value = default)
{
    public bool Success => Status == PersistenceStatus.Success;
}

/// <summary>Use the package/channel's app-local data root, never a shared machine directory.
/// Construction performs no IO. Scope identifiers are non-secret application metadata, not Sponsor user IDs.</summary>
public sealed class SponsorPersistenceScope
{
    public SponsorPersistenceScope(string appLocalRoot, string applicationId, string channel)
    {
        if (!Path.IsPathFullyQualified(appLocalRoot) || !SponsorLocalSettings.ValidText(applicationId, 256)
            || !SponsorLocalSettings.ValidText(channel, 128)) throw new ArgumentException("Invalid persistence scope");
        AppLocalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appLocalRoot));
        ScopeId = Hash(applicationId + "\n" + channel);
        DirectoryPath = Path.Combine(AppLocalRoot, "SponsorBlock", ScopeId);
        // Identity blobs cannot silently travel between separate supplied roots or app channels.
        var rootBinding = OperatingSystem.IsWindows() ? AppLocalRoot.ToUpperInvariant() : AppLocalRoot;
        ProtectionPurpose = "BiliCopilot.SponsorIdentity.v1:" + ScopeId + ":" + Hash(rootBinding);
    }

    public string AppLocalRoot { get; }
    public string ScopeId { get; }
    public string DirectoryPath { get; }
    internal string ProtectionPurpose { get; }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Opaque immutable Q4 archive. Capture on the queue's single owner before awaiting IO.
/// Capturing does not replace that live queue or lose its pending-operation dictionary.</summary>
public sealed class SponsorDraftArchive
{
    private readonly byte[] bytes;
    private SponsorDraftArchive(byte[] bytes) => this.bytes = bytes;
    internal byte[] CopyBytes() => (byte[])bytes.Clone();

    public static PersistenceResult<SponsorDraftArchive> Capture(DraftQueue queue)
    {
        if (!queue.TryExport(out var bytes, out _)) return new(PersistenceStatus.TooLarge);
        return FromBytes(bytes!);
    }

    internal static PersistenceResult<SponsorDraftArchive> FromBytes(byte[] bytes)
    {
        if (bytes.Length > DraftQueue.MaximumJsonBytes) return new(PersistenceStatus.TooLarge);
        var queue = new DraftQueue();
        if (!queue.Import(bytes).Success) return new(PersistenceStatus.InvalidData);
        // Retain exact exported revisions/history. Q4 performs InFlight -> Unknown only on Restore.
        return new(PersistenceStatus.Success, new((byte[])bytes.Clone()));
    }

    /// <summary>Explicit startup/reload only, after all live submissions/capture/preview have been quiesced.
    /// Q4 clears transient undo/capture/preview, never resumes pending operations, and retains ambiguous history.</summary>
    public DraftQueue Restore()
    {
        var queue = new DraftQueue();
        _ = queue.Import(bytes);
        return queue;
    }
}

public sealed record SponsorLocalSnapshot(SponsorLocalSettings Settings, SponsorDraftArchive Drafts,
    string Version, long Revision)
{
    public int SchemaVersion { get; init; } = 2;
    public SponsorAuxiliarySnapshot Auxiliary { get; init; } = SponsorAuxiliarySnapshot.Empty;
}

/// <summary>Explicit destructive import consent, not a network intent. Caller must first quiesce the live Q4 owner.</summary>
public enum BackupReplaceIntent { None, ReplaceAfterQuiescingLocalOwner }

internal sealed record StateDocument(int SchemaVersion, string ScopeId, long Revision,
    SponsorLocalSettings Settings, byte[] Drafts);
internal sealed record BackupDocument(int SchemaVersion, string ScopeId, SponsorLocalSettings Settings, byte[] Drafts);
internal sealed record IdentityDocument(int SchemaVersion, string Purpose, string PrivateId);
