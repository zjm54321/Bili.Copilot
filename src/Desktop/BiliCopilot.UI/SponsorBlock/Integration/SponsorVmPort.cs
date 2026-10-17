using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>The real VM seam, including the sole native-declared editor port. No controller or clock.</summary>
internal sealed class SponsorVmPort(PlayerViewModel player, SponsorDispatcher dispatcher)
    : ISponsorPlayerPort, ISponsorEditorPlayerPort, ISponsorNativeRetirementPort
{
    private bool observed;
    private Action<SponsorControllerResult>? handlers;
    public SponsorControllerResult? SponsorSnapshot
    {
        get { dispatcher.CheckAccess(); return Validate(player.SponsorSnapshot); }
    }

    private SponsorControllerResult? Validate(SponsorControllerResult? value)
    {
        if (value is null && observed) throw new InvalidOperationException("Sponsor snapshot lost after binding.");
        if (value is not null) observed = true;
        return value;
    }

    public event Action<SponsorControllerResult>? SponsorUpdated
    {
        add
        {
            dispatcher.CheckAccess();
            if (handlers is null) player.SponsorUpdated += Forward;
            handlers += value;
        }
        remove
        {
            dispatcher.CheckAccess();
            handlers -= value;
            if (handlers is null) player.SponsorUpdated -= Forward;
        }
    }

    private void Forward(SponsorControllerResult value)
    {
        dispatcher.CheckAccess();
        Validate(value);
        handlers?.Invoke(value); // N4 posts APP delivery; never wait for APP from this PLAYER callback.
    }

    public bool UpdateSponsorInput(SponsorCoordinatorInput input)
    { dispatcher.CheckAccess(); return player.UpdateSponsorInput(input); }
    public bool RequestSponsorAction(PlaybackRequest request)
    { dispatcher.CheckAccess(); return player.RequestSponsorAction(request); }
    public async Task<SponsorControllerResult?> ObserveSponsorAsync()
    { dispatcher.CheckAccess(); return Validate(await player.ObserveSponsorAsync()); }
    public Task<bool> ReconcileSponsorFieldAsync(SponsorReconciliation request)
    { dispatcher.CheckAccess(); return player.ReconcileSponsorFieldAsync(request); }
    public Task<bool> ReconcileSponsorSeekAsync(SponsorSeekReconciliation request)
    { dispatcher.CheckAccess(); return player.ReconcileSponsorSeekAsync(request); }

    // Return each exact VM task, not a success placeholder, fire-and-forget call or native replacement.
    public Task<SponsorEditorReply> RequestSponsorFrameAsync(PlaybackStamp expected, FrameDirection direction)
    { dispatcher.CheckAccess(); return player.RequestSponsorFrameAsync(expected, direction); }
    public Task<SponsorEditorReply> RequestSponsorPreviewAsync(PlaybackStamp expected, PreviewInterval preview)
    { dispatcher.CheckAccess(); return player.RequestSponsorPreviewAsync(expected, preview); }
    public Task<SponsorEditorReply> EndSponsorPreviewAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode)
    { dispatcher.CheckAccess(); return player.EndSponsorPreviewAsync(token, mode); }
    public Task<SponsorEditorReply> RequestSponsorMuteReleaseAsync(PlaybackStamp expected, string leaseToken)
    { dispatcher.CheckAccess(); return player.RequestSponsorMuteReleaseAsync(expected, leaseToken); }

    public Task<SponsorNativeRetirementReceipt?> JoinSponsorNativeRetirementAsync(PlaybackOwner expected)
    { dispatcher.CheckAccess(); return player.JoinSponsorNativeRetirementAsync(expected); }
    public bool IsCurrentSponsorNativeRetirement(SponsorNativeRetirementReceipt receipt)
    { dispatcher.CheckAccess(); return player.IsCurrentSponsorNativeRetirement(receipt); }
    public event Action<SponsorNativeRetirementReceipt>? SponsorNativeRetirementCompleted
    {
        add { dispatcher.CheckAccess(); player.SponsorNativeRetirementCompleted += value; }
        remove { dispatcher.CheckAccess(); player.SponsorNativeRetirementCompleted -= value; }
    }
}
