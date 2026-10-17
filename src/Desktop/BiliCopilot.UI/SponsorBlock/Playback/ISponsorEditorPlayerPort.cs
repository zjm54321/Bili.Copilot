using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal enum SponsorEditorOutcome
{
    Unavailable, Stale, Busy, Accepted, Acknowledged, Released, NeedsReconciliation
}

internal enum SponsorPreviewPhase
{
    Starting, AwaitingEntry, Playing, AtEnd, Restoring, AwaitingReturn,
    NeedsReconciliation, Ended, Interrupted
}

internal enum SponsorPreviewEndMode { Restore, KeepCurrent }
internal sealed record SponsorPreviewToken(PlaybackOwner Owner, long Id);
internal sealed record SponsorPreviewStatus(
    SponsorPreviewToken Token, PreviewInterval Interval, SponsorPreviewPhase Phase,
    double? SavedPosition, bool? SavedPaused, string? Reason = null);
internal sealed record SponsorEditorReply(
    SponsorEditorOutcome Outcome, SponsorControllerResult? Snapshot,
    SponsorOperation? Command = null, string? Reason = null);

internal interface ISponsorEditorPlayerPort
{
    Task<SponsorEditorReply> RequestSponsorFrameAsync(PlaybackStamp expected, FrameDirection direction);
    Task<SponsorEditorReply> RequestSponsorPreviewAsync(PlaybackStamp expected, PreviewInterval preview);
    Task<SponsorEditorReply> EndSponsorPreviewAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode);
    Task<SponsorEditorReply> RequestSponsorMuteReleaseAsync(PlaybackStamp expected, string leaseToken);
}
