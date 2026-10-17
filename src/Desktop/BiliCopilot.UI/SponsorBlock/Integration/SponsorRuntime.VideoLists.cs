using BiliCopilot.UI.ViewModels.Components;
using Richasy.BiliKernel.Bili.Media;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal sealed partial class SponsorRuntime
{
    private SponsorVideoListAdapter? videoLists;
    private Task? videoListClose;

    internal SponsorVideoListAdapter VideoLists
    {
        get
        {
            dispatcher.CheckAccess();
            if (closing) throw new InvalidOperationException("Sponsor video list admission is closed.");
            return videoLists ??= new(Coordinator, this.Get<IPlayerService>(), this.Get<AccountViewModel>(), dispatcher, TrackUiWork);
        }
    }

    private void StopVideoListAdmission()
    {
        if (videoLists is not null) videoListClose ??= videoLists.CloseAsync();
    }
}
