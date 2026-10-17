
namespace BiliCopilot.UI.SponsorBlock.Persistence;

public sealed partial class SponsorPersistenceStore
{
    /// <summary>Single atomic envelope commit. Explicit user mutation may upgrade prepared schema1;
    /// load never upgrades/writes. Caller captures Q4 on its owner and retains its live pending map.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> SaveEnvelopeAsync(string expectedVersion,
        SponsorLocalSettings settings, SponsorDraftArchive drafts, SponsorAuxiliarySnapshot auxiliary,
        CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        var validation = AuxiliaryValidation.Validate(auxiliary);
        if (validation != PersistenceStatus.Success) return new(validation);
        if (auxiliary.ContentPreferences != current.Value.Auxiliary.ContentPreferences)
            return new(PersistenceStatus.ExplicitIntentRequired);
        if (!AuxiliaryValidation.PreservesHistory(current.Value.Auxiliary, auxiliary)) return new(PersistenceStatus.HistoryConflict);
        return await CommitAsync(current.Value.Revision, settings, drafts, auxiliary, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Journal/statistics/whitelist/filter commit without reconstructing or normalizing Q4 bytes.
    /// Same semaphore, FileShare.None lease, expected version and atomic file replacement as core saves.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> UpdateAuxiliaryAsync(string expectedVersion,
        SponsorAuxiliarySnapshot auxiliary, CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        var validation = AuxiliaryValidation.Validate(auxiliary);
        if (validation != PersistenceStatus.Success) return new(validation);
        if (auxiliary.ContentPreferences != current.Value.Auxiliary.ContentPreferences)
            return new(PersistenceStatus.ExplicitIntentRequired);
        if (!AuxiliaryValidation.PreservesHistory(current.Value.Auxiliary, auxiliary)) return new(PersistenceStatus.HistoryConflict);
        return await CommitAsync(current.Value.Revision, current.Value.Settings, current.Value.Drafts, auxiliary, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Explicit replacement of the complete versioned option pair, not a per-field patch.
    /// Uses the same lock, exact byte-version guard and atomic envelope commit as other mutations.
    /// Does not capture/restore Q4 or alter other auxiliary facts. Null is invalid, never a reset.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> SetContentPreferencesAsync(string expectedVersion,
        SponsorContentPreferences preferences, CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        if (!ContentPreferencesValidation.IsValid(preferences)) return new(PersistenceStatus.InvalidData);
        if (current.Value.Auxiliary.ContentPreferences == preferences) return new(PersistenceStatus.Success, current.Value);
        return await CommitAsync(current.Value.Revision, current.Value.Settings, current.Value.Drafts,
            current.Value.Auxiliary with { ContentPreferences = preferences }, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Explicitly remove only the optional section. Already absent is a version-checked no-op:
    /// no file creation, schema upgrade or default materialization. Backup replacement has its own consent.</summary>
    public Task<PersistenceResult<SponsorLocalSnapshot>> ResetContentPreferencesAsync(string expectedVersion,
        CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        if (current.Value.Auxiliary.ContentPreferences is null) return new(PersistenceStatus.Success, current.Value);
        return await CommitAsync(current.Value.Revision, current.Value.Settings, current.Value.Drafts,
            current.Value.Auxiliary with { ContentPreferences = null }, token).ConfigureAwait(false);
    }, cancellationToken);

    // Additive version-guarded form; legacy callback overload remains a single locked read-modify-write.
    public Task<PersistenceResult<SponsorLocalSnapshot>> UpdateSettingsAsync(string expectedVersion,
        System.Func<SponsorLocalSettings, SponsorLocalSettings> update, CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        System.ArgumentNullException.ThrowIfNull(update);
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        return await CommitAsync(current.Value.Revision, update(current.Value.Settings), current.Value.Drafts,
            current.Value.Auxiliary, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<PersistenceResult<SponsorLocalSnapshot>> UpgradeSchemaAsync(string expectedVersion,
        SponsorSchemaUpgradeIntent intent, CancellationToken cancellationToken = default) => LockedAsync(async token =>
    {
        if (intent != SponsorSchemaUpgradeIntent.UpgradePreparedSchema1) return new(PersistenceStatus.ExplicitIntentRequired);
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        if (!Usable(current)) return current;
        if (current.Value!.Version != expectedVersion) return new(PersistenceStatus.Conflict);
        if (current.Status == PersistenceStatus.Success && current.Value.SchemaVersion == 2) return new(PersistenceStatus.Success, current.Value);
        return await CommitAsync(current.Value.Revision, current.Value.Settings, current.Value.Drafts,
            current.Value.Auxiliary, token).ConfigureAwait(false);
    }, cancellationToken);
}
