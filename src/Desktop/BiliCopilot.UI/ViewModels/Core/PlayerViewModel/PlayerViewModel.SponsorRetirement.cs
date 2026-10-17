using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private SponsorPlaybackController? _sponsorRetirementController;
    private PlaybackOwner? _sponsorRetirementOwner;
    private Task<SponsorNativeRetirementReceipt?>? _sponsorRetirementTask;
    internal event Action<SponsorNativeRetirementReceipt>? SponsorNativeRetirementCompleted;

    private void RetainSponsorRetirement(SponsorPlaybackController controller, PlaybackOwner owner, Task actualDrain)
    {
        _sponsorRetirementController = controller;
        _sponsorRetirementOwner = owner;
        _sponsorRetirementTask = CaptureSponsorRetirementAsync(controller, owner, actualDrain);
    }

    private async Task<SponsorNativeRetirementReceipt?> CaptureSponsorRetirementAsync(
        SponsorPlaybackController controller, PlaybackOwner owner, Task actualDrain)
    {
        try {
            // T6-05: Retirement joins only the supplied physical/controller drain, never whole VM initialization, work-set or Close.
            #pragma warning disable VSTHRD003
            await actualDrain;
            #pragma warning restore VSTHRD003
        }
        catch { return null; } // Original caller still observes the actual failure; no receipt is issued.
        var receipt = controller.CaptureNativeRetirement(owner, actualDrain);
        if (receipt is not null && SponsorNativeRetirementCompleted is { } subscribers)
            foreach (Action<SponsorNativeRetirementReceipt> subscriber in subscribers.GetInvocationList())
                try { subscriber(receipt); } catch { /* A notification cannot change completed retirement. */ }
        return receipt;
    }

    internal Task<SponsorNativeRetirementReceipt?> JoinSponsorNativeRetirementAsync(PlaybackOwner expected)
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("Sponsor retirement requires its player owner.");
        // Only already-started narrow work. Never await _initializationGate, Initialize/Replay,
        // _ownedWork, metadata delivery or the close task from inside N4's APP serial lane.
        return ReferenceEquals(_sponsorRetirementController, _sponsor) && _sponsorRetirementOwner == expected
            && _sponsor?.Snapshot.Owner == expected && _sponsorRetirementTask is { } actual
            ? actual : Task.FromResult<SponsorNativeRetirementReceipt?>(null);
    }

    internal bool IsCurrentSponsorNativeRetirement(SponsorNativeRetirementReceipt receipt)
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("Sponsor retirement requires its player owner.");
        return ReferenceEquals(_sponsorRetirementController, _sponsor)
            && _sponsor?.IsCurrentNativeRetirement(receipt) == true;
    }
}
