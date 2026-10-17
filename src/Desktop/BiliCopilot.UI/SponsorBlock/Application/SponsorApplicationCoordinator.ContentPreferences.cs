using BiliCopilot.UI.SponsorBlock.Persistence;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    /// <summary>Read the existing immutable published auxiliary snapshot. Does not fill/persist absence.</summary>
    internal SponsorContentPreferences EffectiveContentPreferences => SponsorContentPreferences.GetEffective(Snapshot.Auxiliary);

    internal Task<ApplicationResult> SetContentPreferencesAsync(long expectedRevision, SponsorContentPreferences preferences) =>
        OnOwner(() => ChangeContentPreferencesCoreAsync(expectedRevision, preferences, reset: false));

    internal Task<ApplicationResult> ResetContentPreferencesAsync(long expectedRevision) =>
        OnOwner(() => ChangeContentPreferencesCoreAsync(expectedRevision, null, reset: true));

    // Already on the existing APP owner. Never creates a second actor/store, calls OnOwner recursively,
    // restores live Q4, marks unsaved drafts clean, or activates a player/content adapter.
    private async Task<ApplicationResult> ChangeContentPreferencesCoreAsync(long expectedRevision,
        SponsorContentPreferences? preferences, bool reset)
    {
        if (Stopping) return Result(ApplicationStatus.Closed);
        if (expectedRevision != revision) return Result(ApplicationStatus.Stale);
        if (version is null)
        {
            var status = SetAuxiliaryStatus(PersistenceStatus.InvalidData);
            Publish();
            return AuxiliaryResult(status);
        }

        PersistenceResult<SponsorLocalSnapshot> saved;
        try
        {
            saved = reset ? await store.ResetContentPreferencesAsync(version)
                : await store.SetContentPreferencesAsync(version, preferences!);
        }
        catch
        {
            var status = SetAuxiliaryStatus(PersistenceStatus.IoFailure);
            Publish();
            return AuxiliaryResult(status);
        }
        SetAuxiliaryStatus(saved.Status);
        if (saved.Success) AdoptAuxiliary(saved.Value!);
        Publish();
        return AuxiliaryResult(saved.Status);
    }
}
