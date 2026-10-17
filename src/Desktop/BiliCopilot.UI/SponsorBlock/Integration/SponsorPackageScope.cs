using BiliCopilot.UI.SponsorBlock.Persistence;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal static class SponsorPackageScope
{
    // Called only by the retained runtime load/store task, never by construction. Requiring Package
    // first rejects unpackaged startup; there is no shared-directory or other-channel fallback.
    internal static SponsorPersistenceScope Resolve()
    {
        try
        {
            var id = Windows.ApplicationModel.Package.Current.Id;
            var root = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path;
            // FamilyName and Name survive version updates; LocalTest has its own package LocalState.
            return new SponsorPersistenceScope(root, id.FamilyName, id.Name);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            throw new SponsorFileException(PersistenceStatus.IoFailure);
        }
    }
}
