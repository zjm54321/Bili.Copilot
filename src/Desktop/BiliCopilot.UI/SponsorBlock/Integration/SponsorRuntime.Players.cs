using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal sealed partial class SponsorRuntime
{
    private readonly Dictionary<PlayerViewModel, PlayerBinding> players = [];

    private sealed class PlayerBinding(PlayerViewModel player, PlayerOverlay overlay, SponsorDispatcher dispatcher)
    {
        internal readonly PlayerViewModel Player = player;
        internal readonly PlayerOverlay Overlay = overlay;
        internal readonly SponsorDispatcher Dispatcher = dispatcher;
        internal readonly SponsorVmPort Port = new(player, dispatcher);
        internal readonly TaskCompletionSource<ApplicationResult> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Guid? Attachment;
        internal SponsorPlaybackViewModel? Playback;
        internal SponsorEditorViewModel? Editor;
        internal Task<ApplicationResult>? BindTask, MetadataTask, DetachTask, NativeDetachTask, ReleaseTask;
        internal Task? CompletionCheckTask;
        internal ApplicationResult? LastResult;
        internal bool Closing, CompletionSignal;
        internal PlaybackOwner? ObservedOwner;
        internal long MetadataRevision, MetadataSatisfiedRevision, MetadataReadinessRevision;
        internal MetadataReadiness? Readiness;
        internal ApplicationResult? LastMetadataResult;
        internal Action<SponsorNativeRetirementReceipt>? RetirementHandler;
        internal (SponsorPreviewToken? Token, SponsorPreviewPhase? Phase, bool Released) CompletionEdge;
    }

    private sealed record MetadataReadiness(PlaybackStamp? Stamp, ApplicationStatus Delivery, bool Attached);

    internal Task<ApplicationResult> BindPlayerAsync(PlayerViewModel player, PlayerOverlay overlay)
    {
        var owner = new SponsorDispatcher(player.SponsorApplicationDispatcher);
        owner.CheckAccess();
        return dispatcher.InvokeAsync(() =>
        {
            if (closing) return Task.FromResult(new ApplicationResult(ApplicationStatus.Closed));
            if (players.TryGetValue(player, out var existing))
                return ReferenceEquals(existing.Overlay, overlay) ? existing.BindTask!
                    : Task.FromResult(new ApplicationResult(ApplicationStatus.Invalid, "SponsorOverlayOwnerChanged"));
            if (players.Count >= 16) return Task.FromResult(new ApplicationResult(ApplicationStatus.Busy));
            var binding = new PlayerBinding(player, overlay, owner);
            players.Add(player, binding);
            return binding.BindTask = BindCoreAsync(binding);
        });
    }

    private async Task<ApplicationResult> BindCoreAsync(PlayerBinding binding)
    {
        await Task.Yield();
        try
        {
            var loaded = await InitializeAsync();
            if (loaded.Status != ApplicationStatus.Success) return binding.LastResult = loaded;
            if (closing || binding.Closing) return binding.LastResult = new(ApplicationStatus.Closed);
            binding.RetirementHandler = receipt => dispatcher.Post(() =>
            {
                if (players.TryGetValue(binding.Player, out var current) && ReferenceEquals(current, binding)
                    && (binding.Closing || closing)
                    && (binding.ObservedOwner is null || binding.ObservedOwner == receipt.Owner)) SignalCompletionCheck(binding);
            });
            await binding.Dispatcher.ReadAsync(() =>
            {
                binding.Port.SponsorNativeRetirementCompleted += binding.RetirementHandler;
                return true;
            });
            binding.Attachment = await Coordinator.AttachAsync(binding.Dispatcher, binding.Port);
            if (binding.Attachment is not { } id)
                return binding.LastResult = new(ApplicationStatus.Unavailable, "SponsorPlayerAttachmentUnavailable");
            if (closing || binding.Closing) return binding.LastResult = new(ApplicationStatus.Closed);
            await binding.Dispatcher.ReadAsync(() =>
            {
                binding.Player.Danmaku.BindSponsorCategories(Coordinator, id,
                    binding.Player.SponsorApplicationDispatcher, binding.Player.CaptureSponsorDanmakuSource);
                binding.Playback = new SponsorPlaybackViewModel();
                binding.Editor = new SponsorEditorViewModel();
                binding.Playback.Bind(Coordinator, id);
                binding.Editor.Bind(Coordinator, id);
                if (!binding.Overlay.BindSponsorViews(binding.Playback, binding.Editor))
                    throw new InvalidOperationException("Sponsor player view owner is unavailable.");
                return true;
            });
            RequestMetadata(binding);
            return binding.LastResult = new(ApplicationStatus.Success);
        }
        catch { return binding.LastResult = new(ApplicationStatus.ControllerFailure, "SponsorPlayerBindingUnavailable"); }
    }

    internal Task<ApplicationResult> RefreshPlayerMetadataAsync(PlayerViewModel player) => dispatcher.InvokeAsync(() =>
    {
        if (!players.TryGetValue(player, out var binding) || closing || binding.Closing)
            return Task.FromResult(new ApplicationResult(ApplicationStatus.Closed));
        RequestMetadata(binding);
        return binding.MetadataTask ?? Task.FromResult(new ApplicationResult(ApplicationStatus.Stale));
    });

    private void RequestMetadata(PlayerBinding binding)
    {
        if (binding.Attachment is null || binding.Closing || closing) return;
        ++binding.MetadataRevision;
        ScheduleMetadata(binding);
    }

    private void ScheduleMetadata(PlayerBinding binding)
    {
        if (!closing && !binding.Closing && binding.MetadataSatisfiedRevision < binding.MetadataRevision
            && binding.MetadataTask is not { IsCompleted: false }) binding.MetadataTask = MetadataCoreAsync(binding);
    }

    private async Task<ApplicationResult> MetadataCoreAsync(PlayerBinding binding)
    {
        await Task.Yield();
        while (true)
        {
            var revision = binding.MetadataRevision;
            var readiness = binding.MetadataReadinessRevision;
            if (closing || binding.Closing || binding.Attachment is not { } id) return new(ApplicationStatus.Closed);
            ApplicationResult result;
            try
            {
                var metadata = await binding.Dispatcher.ReadAsync(binding.Player.CaptureSponsorApplicationMetadata);
                var view = Coordinator.Snapshot;
                var player = view.Players.FirstOrDefault(p => p.Attachment == id);
                result = metadata.Owner is null || player?.Controller?.Stamp is not { } stamp
                    || stamp.Owner != metadata.Owner || metadata.Stamp != stamp
                    ? new(ApplicationStatus.Stale)
                    : await Coordinator.SetVerifiedUpUidAsync(new PlayerTicket(id, stamp, view.DraftRevision),
                        metadata.VerifiedUpUid, metadata.Verification);
            }
            catch { result = new(ApplicationStatus.ControllerFailure, "SponsorMetadataUnavailable"); }
            binding.LastMetadataResult = result;
            if (result.Status == ApplicationStatus.Success)
                binding.MetadataSatisfiedRevision = Math.Max(binding.MetadataSatisfiedRevision, revision);
            // Both verified and definitively unknown current metadata are satisfied only on actual
            // N4 acceptance. Early mismatch/Stale retains the objective and any newer request.
            if (binding.MetadataSatisfiedRevision >= binding.MetadataRevision) return result;
            if (revision == binding.MetadataRevision && readiness == binding.MetadataReadinessRevision) return result;
            // A real readiness/request edge occurred while awaiting. Coalesce it into one latest
            // attempt; never retry from position/sequence alone or our own successful publication.
        }
    }

    private void ObservePlayers(SponsorApplicationView view)
    {
        foreach (var binding in players.Values.ToArray())
        {
            if (binding.Attachment is not { } id) continue;
            var player = view.Players.FirstOrDefault(p => p.Attachment == id);
            if (player is null) continue;
            var readiness = new MetadataReadiness(player.Controller?.Stamp, player.DeliveryStatus, player.Attached);
            var readinessChanged = readiness != binding.Readiness;
            if (readinessChanged)
            {
                binding.Readiness = readiness;
                ++binding.MetadataReadinessRevision;
            }
            if (player.Controller?.Owner != binding.ObservedOwner)
            {
                binding.ObservedOwner = player.Controller?.Owner;
                RequestMetadata(binding);
            }
            else if (readinessChanged) ScheduleMetadata(binding);
            if (!binding.Closing && !closing) continue;
            var preview = player.PreviewPlayback;
            var released = player.LastEditorReply?.Outcome == SponsorEditorOutcome.Released;
            var terminal = preview?.Phase is SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted;
            var edge = (preview?.Token, terminal ? preview?.Phase : null, released);
            if ((!terminal && !released) || edge == binding.CompletionEdge) continue;
            binding.CompletionEdge = edge;
            SignalCompletionCheck(binding);
        }
    }

    private void SignalCompletionCheck(PlayerBinding binding)
    {
        binding.CompletionSignal = true;
        if (binding.CompletionCheckTask is not { IsCompleted: false })
            binding.CompletionCheckTask = CheckCompletionAsync(binding);
    }

    private async Task CheckCompletionAsync(PlayerBinding binding)
    {
        await Task.Yield(); // Never reenter the APP serial lane from its publication callback.
        try
        {
            while (binding.CompletionSignal)
            {
                binding.CompletionSignal = false;
                if (binding.DetachTask is { } detach) await detach;
                if (closeAttempt is { } close) await close;
                if (closing) await CloseAsync();
                else if (players.ContainsKey(binding.Player)) await DetachPlayerAsync(binding.Player);
            }
        }
        catch { binding.LastResult = new(ApplicationStatus.ControllerFailure, "SponsorCleanupCheckUnavailable"); }
    }
}
