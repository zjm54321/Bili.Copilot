using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private SponsorRuntime? _sponsorRuntime;
    private Task<ApplicationResult>? _sponsorApplicationBind;
    private Task<ApplicationResult>? _sponsorApplicationMetadata;
    private Task<ApplicationResult>? _sponsorApplicationCleanup;
    internal DispatcherQueue SponsorApplicationDispatcher => queue;
    internal ApplicationResult? SponsorApplicationResult { get; private set; }

    private void BindSponsorApplication(PlayerOverlay overlay)
    {
        _sponsorRuntime ??= this.Get<SponsorRuntime>();
        _sponsorApplicationBind ??= _sponsorRuntime.BindPlayerAsync(this, overlay);
    }

    private async Task AwaitSponsorApplicationBindingAsync()
    {
        if (_sponsorApplicationBind is { } bind) SponsorApplicationResult = await bind;
    }

    private async Task RefreshSponsorApplicationMetadataAsync()
    {
        await AwaitSponsorApplicationBindingAsync();
        if (_sponsorRuntime is null || SponsorApplicationResult?.Status != ApplicationStatus.Success) return;
        _sponsorApplicationMetadata = _sponsorRuntime.RefreshPlayerMetadataAsync(this);
        SponsorApplicationResult = await _sponsorApplicationMetadata;
    }

    private void StartSponsorApplicationCleanup()
    {
        if (_sponsorRuntime is not null)
            _sponsorApplicationCleanup ??= _sponsorRuntime.DetachPlayerAsync(this);
    }

    private async Task AwaitSponsorApplicationCleanupAsync()
    {
        if (_sponsorRuntime is null) return; // Genuinely never admitted/created an application binding.
        await AwaitSponsorApplicationBindingAsync();
        _sponsorApplicationCleanup ??= _sponsorRuntime.DetachPlayerAsync(this);
        SponsorApplicationResult = await _sponsorApplicationCleanup;
        if (SponsorApplicationResult.Status != ApplicationStatus.Success)
        {
            // Retain the real close worker, native binding, UI and dispatcher while explicit cleanup
            // is needed. The wait is never cancelled or converted into successful teardown.
            _sponsorApplicationCleanup = _sponsorRuntime.WaitForPlayerCleanupAsync(this);
            SponsorApplicationResult = await _sponsorApplicationCleanup;
        }
        if (SponsorApplicationResult.Status != ApplicationStatus.Success)
            throw new InvalidOperationException("Sponsor cleanup retained player ownership.");
    }

    internal SponsorApplicationMetadata CaptureSponsorApplicationMetadata()
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("Sponsor metadata requires its player owner.");
        var snapshot = SponsorSnapshot;
        var owner = snapshot?.Owner;
        string? uid = null;
        if (owner is not null && owner.Media == GetSelectedSponsorIdentity()
            && Connector is VideoConnectorViewModel video
            && string.Equals(video._view?.Information?.BvId, owner.Media.BvId, StringComparison.Ordinal))
        {
            // Actual selected VideoPlayerView publisher: also used by existing follow/unfollow UI.
            var candidate = video._view?.Information?.Publisher?.User?.Id;
            if (SponsorUpUid.IsValid(candidate)) uid = candidate;
        }
        // No title/BVID-derived author; PGC, missing metadata and unknown owners remain unverified.
        return new(owner, uid, uid is null ? SponsorChannelVerification.Unknown : SponsorChannelVerification.Verified)
            { Stamp = snapshot?.Stamp };
    }
}

internal sealed record SponsorApplicationMetadata(PlaybackOwner? Owner, string? VerifiedUpUid,
    SponsorChannelVerification Verification)
{
    internal PlaybackStamp? Stamp { get; init; }
}
