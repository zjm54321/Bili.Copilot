using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

/// <summary>Narrow N4 coordinator handoff, not a service/controller. No global instance, watcher, refresh,
/// scheduler, HTTP, submission, telemetry, statistics or receipt derivation. One store per active scope.</summary>
public sealed partial class SponsorPersistenceStore
{
    private readonly IAtomicSponsorFiles files;
    private readonly ISponsorIdentityProtector protector;
    private readonly SemaphoreSlim serial = new(1, 1);

    public SponsorPersistenceStore(IAtomicSponsorFiles files, ISponsorIdentityProtector protector)
    {
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>Missing returns explicit opt-out defaults with a missing-version token, without writing.
    /// Every other failure returns no snapshot: retain the caller's existing settings and the original file.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> LoadAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(ReadStateAsync, cancellationToken);

    /// <summary>Capture Q4 on its single owner first. Version is the exact last loaded/committed byte revision.
    /// This persists, but does not replace or reload, the caller's live queue/policy/controller.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> SaveAsync(string expectedVersion, SponsorLocalSettings settings,
        SponsorDraftArchive drafts, CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        return await CommitAsync(current.Value.Revision, settings, drafts, current.Value.Auxiliary, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Serial read-modify-write for independent settings updates. Callback must be synchronous,
    /// side-effect-free, and must not reenter this store. Draft bytes are kept exactly, including pending history.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> UpdateSettingsAsync(Func<SponsorLocalSettings, SponsorLocalSettings> update,
        CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        return await CommitAsync(current.Value!.Revision, update(current.Value.Settings), current.Value.Drafts, current.Value.Auxiliary, token)
            .ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Exports persisted settings, Q4's neutral archive and auxiliary preferences/statistics/journal.
    /// Includes public provenance, never private identity. Does not access identity.bin or transient effects.</summary>
    public Task<PersistenceResult<byte[]>> ExportBackupAsync(CancellationToken cancellationToken = default) =>
        LockedAsync<byte[]>(async token =>
        {
            var current = await ReadStateAsync(token).ConfigureAwait(false);
            if (!Usable(current)) return new(current.Status);
            var snapshot = current.Value!;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new BackupDocumentV2(2, files.Scope.ScopeId,
                snapshot.Settings, snapshot.Drafts.CopyBytes(), snapshot.Auxiliary), PersistenceJsonContext.Default.BackupDocumentV2);
            return bytes.Length <= PersistenceJson.MaximumDocumentBytes ? new(PersistenceStatus.Success, bytes)
                : new(PersistenceStatus.TooLarge);
        }, cancellationToken);

    /// <summary>Whole-document replacement only; never merges scope/identity data. Requires exact current
    /// version and explicit consent. Unresolved current submissions/mutations block replacement even with consent.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> ReplaceBackupAsync(ReadOnlyMemory<byte> backup,
        string expectedVersion, BackupReplaceIntent intent, CancellationToken cancellationToken = default)
    {
        if (intent != BackupReplaceIntent.ReplaceAfterQuiescingLocalOwner)
            return Task.FromResult(new PersistenceResult<SponsorLocalSnapshot>(PersistenceStatus.ExplicitIntentRequired));
        if (backup.Length > PersistenceJson.MaximumDocumentBytes)
            return Task.FromResult(new PersistenceResult<SponsorLocalSnapshot>(PersistenceStatus.TooLarge));
        // Snapshot caller-owned memory before the first await; callers cannot change validated bytes mid-import.
        var copy = backup.ToArray();
        return LockedAsync(async token =>
        {
            var candidate = Decode(copy, isBackup: true);
            if (!candidate.Success) return candidate;
            var current = await ReadStateAsync(token).ConfigureAwait(false);
            if (!Usable(current)) return current;
            if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
            if (current.Value.Drafts.Restore().Entries.Any(e => e.State is DraftState.SubmissionInFlight
                or DraftState.Unknown or DraftState.AcceptedUnconfirmed)) return new(PersistenceStatus.UnresolvedDrafts);
            if (current.Value.Auxiliary.Journal.Any(AuxiliaryValidation.Unresolved)) return new(PersistenceStatus.UnresolvedMutations);
            return await CommitAsync(current.Value.Revision, candidate.Value!.Settings, candidate.Value.Drafts,
                candidate.Value.Auxiliary, token)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<PersistenceResult<SponsorLocalSnapshot>> ReadStateAsync(CancellationToken token)
    {
        var bytes = await files.ReadAsync(SponsorLocalFile.State, PersistenceJson.MaximumDocumentBytes, token).ConfigureAwait(false);
        if (bytes is null) return new(PersistenceStatus.Missing, new(new(), SponsorDraftArchive.Capture(new DraftQueue()).Value!, "missing", 0));
        return Decode(bytes, isBackup: false);
    }

    private PersistenceResult<SponsorLocalSnapshot> Decode(byte[] bytes, bool isBackup)
    {
        if (bytes.Length > PersistenceJson.MaximumDocumentBytes) return new(PersistenceStatus.TooLarge);
        try
        {
            using var raw = PersistenceJson.Parse(bytes);
            var root = raw.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var schema))
                return new(PersistenceStatus.Corrupt);
            if (schema is not (1 or 2)) return new(PersistenceStatus.UnsupportedSchema);
            if (schema == 1 && bytes.Length > PersistenceJson.MaximumLegacyDocumentBytes) return new(PersistenceStatus.TooLarge);
            var fields = schema == 1
                ? isBackup ? new[] { "SchemaVersion", "ScopeId", "Settings", "Drafts" }
                    : new[] { "SchemaVersion", "ScopeId", "Revision", "Settings", "Drafts" }
                : isBackup ? new[] { "SchemaVersion", "ScopeId", "Settings", "Drafts", "Auxiliary" }
                    : new[] { "SchemaVersion", "ScopeId", "Revision", "Settings", "Drafts", "Auxiliary" };
            if (!PersistenceJson.HasFields(root, fields)
                || !PersistenceJson.SettingsShape(root.GetProperty("Settings"))
                || schema == 2 && !PersistenceJson.AuxiliaryShape(root.GetProperty("Auxiliary"))) return new(PersistenceStatus.Corrupt);
            SponsorLocalSettings settings;
            byte[] draftBytes;
            string scope;
            long revision;
            var auxiliary = SponsorAuxiliarySnapshot.Empty;
            if (schema == 2 && isBackup)
            {
                var doc = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.BackupDocumentV2)!;
                settings = doc.Settings; draftBytes = doc.Drafts; scope = doc.ScopeId; revision = 0; auxiliary = doc.Auxiliary;
            }
            else if (schema == 2)
            {
                var doc = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.StateDocumentV2)!;
                settings = doc.Settings; draftBytes = doc.Drafts; scope = doc.ScopeId; revision = doc.Revision; auxiliary = doc.Auxiliary;
            }
            else if (isBackup)
            {
                var doc = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.BackupDocument)!;
                settings = doc.Settings; draftBytes = doc.Drafts; scope = doc.ScopeId; revision = 0;
            }
            else
            {
                var doc = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.StateDocument)!;
                settings = doc.Settings; draftBytes = doc.Drafts; scope = doc.ScopeId; revision = doc.Revision;
            }
            if (!isBackup && revision <= 0) return new(PersistenceStatus.InvalidData);
            if (scope != files.Scope.ScopeId) return new(PersistenceStatus.ForeignScope);
            var validation = PersistenceJson.ValidateSettings(settings);
            if (validation != PersistenceStatus.Success) return new(validation);
            var auxiliaryValidation = AuxiliaryValidation.Validate(auxiliary);
            if (auxiliaryValidation != PersistenceStatus.Success) return new(auxiliaryValidation);
            // Preserve phase/outcome exactly. A recovered Prepared/Unknown fact is not a replay queue.
            auxiliary = auxiliary with { Journal = auxiliary.Journal.Select(e => e with { RecoveredOnLoad = true }).ToImmutableArray() };
            if (draftBytes is null) return new(PersistenceStatus.InvalidData);
            var drafts = SponsorDraftArchive.FromBytes(draftBytes);
            return drafts.Success ? new(PersistenceStatus.Success, new(settings, drafts.Value!, Version(bytes), revision)
                { Auxiliary = auxiliary, SchemaVersion = schema })
                : new(drafts.Status);
        }
        catch (JsonException) { return new(PersistenceStatus.Corrupt); }
    }

    private async Task<PersistenceResult<SponsorLocalSnapshot>> CommitAsync(long revision, SponsorLocalSettings settings,
        SponsorDraftArchive drafts, SponsorAuxiliarySnapshot auxiliary, CancellationToken token)
    {
        var validation = PersistenceJson.ValidateSettings(settings);
        if (validation != PersistenceStatus.Success) return new(validation);
        var auxiliaryValidation = AuxiliaryValidation.Validate(auxiliary);
        if (auxiliaryValidation != PersistenceStatus.Success) return new(auxiliaryValidation);
        if (drafts is null || revision == long.MaxValue) return new(PersistenceStatus.InvalidData);
        // Schema1 is read-only on load. Only an explicit mutation/upgrade commits the v2 envelope.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateDocumentV2(2, files.Scope.ScopeId, revision + 1,
            settings, drafts.CopyBytes(), auxiliary), PersistenceJsonContext.Default.StateDocumentV2);
        if (bytes.Length > PersistenceJson.MaximumDocumentBytes) return new(PersistenceStatus.TooLarge);
        await files.ReplaceAsync(SponsorLocalFile.State, bytes, token).ConfigureAwait(false);
        return new(PersistenceStatus.Success, new(settings, drafts, Version(bytes), revision + 1) { Auxiliary = auxiliary });
    }

    private static string Version(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Usable(PersistenceResult<SponsorLocalSnapshot> result) =>
        (result.Status is PersistenceStatus.Success or PersistenceStatus.Missing) && result.Value is not null;

    private async Task<PersistenceResult<T>> LockedAsync<T>(Func<CancellationToken, Task<PersistenceResult<T>>> action,
        CancellationToken token)
    {
        var entered = false;
        IDisposable? lease = null;
        try
        {
            await serial.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lease = await files.AcquireAsync(token).ConfigureAwait(false);
            return await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(PersistenceStatus.Cancelled); }
        catch (SponsorFileException exception) { return new(exception.Status); }
        catch (UnauthorizedAccessException) { return new(PersistenceStatus.IoFailure); }
        catch (IOException) { return new(PersistenceStatus.IoFailure); }
        catch (NotSupportedException) { return new(PersistenceStatus.IoFailure); }
        finally
        {
            // Closing the read/write lock handle must not relabel an already committed write as failed.
            // The lock file carries no buffered data. Subsequent contention remains an explicit Busy result.
            try { lease?.Dispose(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { if (entered) serial.Release(); }
        }
    }
}
