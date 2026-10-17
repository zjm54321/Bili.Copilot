using BiliCopilot.UI.SponsorBlock.Application;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    internal SponsorDanmakuSource? CaptureSponsorDanmakuSource()
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("Sponsor category source requires its player dispatcher.");
        if (!IsCurrentPlayback(_mediaVersion) || GetSelectedSponsorIdentity() is not { } media) return null;
        // Reuse the exact selected-part/episode identity adapter. Unknown PGC BV/CID passes through.
        var owner = _sponsor?.CanUseBinding == true ? SponsorSnapshot?.Owner : null;
        return new(media, _mediaVersion, owner?.Media == media ? owner : null);
    }
}
