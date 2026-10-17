using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal enum SponsorFeature
{
    Settings, LocalDraftEditor, Submit, VoteUpDown, ChangeServerCategory, BindPort, VotePort, UpdatePort,
    ViewedTelemetry, SetUsername, ServerStatisticsRead, ServerStatisticsWrite, DeleteServerSegment,
    SplitMerge, FrameStep, PreviewPreparation, PreviewPlayback, LocalHide, LocalDownvote,
    PersistentChannelWhitelist, CurrentChannelWhitelistFact, ContentProjection, DanmakuProjection,
    DurableStatistics, StatisticsSnapshot,
    PreviewEnd, PreviewKeepCurrent, ReleaseMute,
}
internal sealed record SponsorAvailability(SponsorFeature Feature, bool Available, string? Reason = null);

internal sealed partial class SponsorApplicationCoordinator
{
    internal static ImmutableArray<SponsorCategory> CategoryChoices => SponsorRules.Categories;
    internal static ImmutableArray<CategoryPolicy> CategoryPolicyChoices { get; } = [CategoryPolicy.Disabled, CategoryPolicy.ShowOverlay, CategoryPolicy.ManualSkip, CategoryPolicy.AutoSkip];
    internal static ImmutableArray<SpeedUpFallback> SpeedFallbackChoices { get; } = [SpeedUpFallback.None, SpeedUpFallback.SeekShortOrNearEnd];
    internal ImmutableArray<SponsorAvailability> Availability => Enum.GetValues<SponsorFeature>().Select(feature => feature switch
    {
        SponsorFeature.SplitMerge => new SponsorAvailability(feature, false, "Q4HasNoAtomicSplitMerge"),
        SponsorFeature.FrameStep or SponsorFeature.PreviewPlayback or SponsorFeature.PreviewEnd
            or SponsorFeature.PreviewKeepCurrent or SponsorFeature.ReleaseMute => new(feature,
                Snapshot.Players.Any(p => p.EditorCapabilities.Any(c => c.Feature == feature && c.Available)),
                "SeeExactPlayerEditorCapabilities"),
        SponsorFeature.DeleteServerSegment or SponsorFeature.ServerStatisticsWrite => new(feature, false, "NoS7Endpoint"),
        SponsorFeature.PersistentChannelWhitelist => new(feature, version is not null, "ActualVerifiedUpUidRequiredForCurrentChannel"),
        SponsorFeature.DurableStatistics => new(feature, version is not null && !ledger.CapacityReached,
            ledger.CapacityReached ? "ReceiptWatermarkCapacityReached" : statisticsDirty ? "ObservedStatisticsNotYetDurable" : "ActualTerminalReceiptsOnly"),
        SponsorFeature.VoteUpDown or SponsorFeature.ChangeServerCategory or SponsorFeature.BindPort or SponsorFeature.VotePort
            or SponsorFeature.UpdatePort or SponsorFeature.ViewedTelemetry or SponsorFeature.SetUsername =>
            new(feature, version is not null && auxiliary.Journal.Length < 512 && !auxiliaryCapacityReached,
                auxiliary.Journal.Length >= 512 ? "MutationJournalCapacityReached" : auxiliaryCapacityReached
                    ? "AuxiliaryStorageCapacityReached" : "ExplicitIntentAndOptInsRequired"),
        SponsorFeature.ContentProjection => new(feature, false, "VideoListAdapterRequiresExplicitVisiblePassUiBinding"),
        SponsorFeature.DanmakuProjection => new(feature, true, "CurrentPlayerReadIntervalsOnlyUnknownPassThroughNoFetch"),
        _ => new(feature, true),
    }).ToImmutableArray();

    private ImmutableArray<SponsorAvailability> EditorAvailability(PlayerState state)
    {
        var current = state.Result;
        var actual = current?.Actual;
        var open = !Stopping && !state.Attachment.IsClosed && state.EditorWork is not { IsCompleted: false }
            && state.CleanupWork is not { IsCompleted: false } && current?.Stamp is { } stamp
            && stamp.PolicyRevision == policyRevision && state.Attachment.Latest?.Stamp == stamp;
        var editorPort = state.Attachment.HasEditorPort;
        var editorAvailable = editor is null || editor == state.Attachment.Id;
        var frame = open && editorPort && editorAvailable && actual is { CanStepFrames: true, State: SponsorPlaybackState.Paused };
        var preview = open && editorPort && editor == state.Attachment.Id && state.Editing
            && state.Preview is not null && !PreviewUnresolved(state)
            && actual is { CanSeekExactly: true, State: SponsorPlaybackState.Playing or SponsorPlaybackState.Paused };
        var end = editorPort && PreviewUnresolved(state); // Start admission/current draft are intentionally irrelevant.
        var lease = current?.MuteLease;
        var ownedMute = current?.Stamp is { } muteStamp && lease is not null
            && lease.Owner == muteStamp.Owner && lease.UserEpoch == muteStamp.MuteUserEpoch;
        var pendingMute = lease?.Phase == PlaybackLeasePhase.Pending || lease?.Restoration?.Phase == RestorationPhase.Pending;
        var unknownMute = lease?.Phase is PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown
            || lease?.Restoration?.Phase is RestorationPhase.Failed or RestorationPhase.Unknown;
        var mute = open && ownedMute && !pendingMute && !unknownMute && (editorPort || LegacyMuteReleaseAvailable(current));
        return [
            new(SponsorFeature.FrameStep, frame, frame ? "NativeTaskAcknowledgementOnly" : !editorPort
                ? "EditorPlayerPortUnavailable" : !open ? "EditorAdmissionClosedOrStale" : !editorAvailable
                    ? "AnotherPlayerOwnsEditor" : "ActualPausedFrameCapabilityRequired"),
            new(SponsorFeature.PreviewPlayback, preview, preview ? "PreparedIntervalNativePreview" : !editorPort
                ? "EditorPlayerPortUnavailable" : PreviewUnresolved(state) ? "PreviewCleanupTokenStillOwned" : "CurrentPreparedPreviewAndExactSeekRequired"),
            new(SponsorFeature.PreviewEnd, end, end ? "ConditionalReturnByCleanupToken" : "NoOwnedPreviewCleanup"),
            new(SponsorFeature.PreviewKeepCurrent, end, end ? "ExplicitRelinquishWithoutPositionRestore" : "NoOwnedPreviewCleanup"),
            new(SponsorFeature.ReleaseMute, mute, mute ? (editorPort ? "CurrentLeaseBaselineRelease" : "LegacyConfirmedMuteUndoIntentOnly")
                : !ownedMute ? "CurrentOwnedMuteLeaseRequired" : pendingMute ? "MuteWorkPending" : unknownMute
                    ? "MuteLeaseNeedsReconciliation" : !editorPort ? "LeaseReleasePortRequired" : "EditorAdmissionClosedOrStale"),
        ];
    }

    private static bool LegacyMuteReleaseAvailable(SponsorControllerResult? current) =>
        current?.Stamp is { } stamp && current.MuteLease is { Phase: PlaybackLeasePhase.Confirmed } lease
        && lease.Owner == stamp.Owner && lease.UserEpoch == stamp.MuteUserEpoch && lease.VisitRevision == stamp.VisitRevision
        && lease.Restoration?.Phase is not (RestorationPhase.Pending or RestorationPhase.Failed or RestorationPhase.Unknown)
        && current.Applied is { Kind: SponsorEffectKind.HoldMute, WasPreview: false } applied
        && current.AppliedId > 0 && applied.Owner == lease.Owner && applied.From >= lease.Coverage.Start
        && applied.From < applied.To && applied.To == lease.Coverage.End
        && applied.Contributors.SequenceEqual(lease.Coverage.Contributors);

    internal Task<ApplicationResult> SetLocalExclusionAsync(PlayerTicket ticket, PlaybackSegmentKey key, LocalExclusionKind kind, bool excluded) => OnOwner(async () =>
    {
        if (!Current(ticket, out var state) || key.Media != ticket.Stamp.Owner.Media
            || !state.Raw.Any(s => s.Uuid == key.Uuid && s.Source == key.Source) || !Enum.IsDefined(kind)) return Result(ApplicationStatus.Stale);
        var fact = new LocalSegmentExclusion(key.Media.Combined, key.Uuid, key.Source, kind);
        var exclusions = settings.Exclusions.Remove(fact);
        if (excluded) exclusions = exclusions.Add(fact);
        var candidate = settings with { Exclusions = exclusions };
        var saved = await SaveAsync(candidate);
        if (saved != PersistenceStatus.Success) { Publish(); return new(ApplicationStatus.PersistenceFailure, Persistence: saved); }
        settings = candidate; ++policyRevision; ClearEditor();
        foreach (var player in players.Values) { ++player.ReadEpoch; player.Aligned = null; }
        await PushAllAsync(); Publish(); return Result(ApplicationStatus.Success);
    });

    // Parent supplies the actual verified current-channel fact; no channel ID is guessed from titles/media.
    // This legacy transient assertion is NOT a persistent-list edit and supplies no actual UID.
    internal Task<ApplicationResult> SetCurrentWhitelistFactAsync(PlayerTicket ticket, PlaybackChannel verifiedChannel) =>
        OnOwner(async () =>
        {
            if (!Current(ticket, out var state)) return Result(ApplicationStatus.Stale);
            if (verifiedChannel.Verification != SponsorChannelVerification.Verified || verifiedChannel.IsWhitelisted is null)
                return Result(ApplicationStatus.Invalid, "VerifiedCurrentChannelRequired");
            state.VerifiedUpUid = null;
            state.Channel = verifiedChannel; await PushAsync(state); Publish(); return Result(ApplicationStatus.Success);
        });
}
