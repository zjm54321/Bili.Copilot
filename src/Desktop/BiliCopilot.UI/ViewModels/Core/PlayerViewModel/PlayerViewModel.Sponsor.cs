using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Models.Media;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private SponsorPlaybackController? _sponsor;
    private PhysicalRateHandoff? _sponsorPhysicalRate;
    // Outer ordering: initialization -> physical session -> controller/native lane.
    // No native-lane job may await this gate or the VM's owned-work set.
#pragma warning disable CA2213 // Managed gate only; collected with the VM like _initializationGate.
    private readonly SemaphoreSlim _physicalRateGate = new(1, 1);
#pragma warning restore CA2213
    private readonly long _sponsorClockOrigin = Stopwatch.GetTimestamp();

    internal SponsorControllerResult? SponsorSnapshot => _sponsor?.Snapshot;
    internal event Action<SponsorControllerResult>? SponsorUpdated;
    internal bool UpdateSponsorInput(SponsorCoordinatorInput input) => _sponsor?.Update(input) == true;
    internal bool RequestSponsorAction(PlaybackRequest request) => _sponsor?.Request(request) == true;
    internal Task<SponsorControllerResult?> ObserveSponsorAsync()
        => _sponsor is null ? Task.FromResult<SponsorControllerResult?>(null) : ObserveAsync(_sponsor);
    private static async Task<SponsorControllerResult?> ObserveAsync(SponsorPlaybackController controller)
        => await controller.ObserveCurrentAsync();
    internal Task<bool> ReconcileSponsorFieldAsync(SponsorReconciliation request)
        => _sponsor?.ReconcileRestoredFieldAsync(request) ?? Task.FromResult(false);
    internal Task<bool> ReconcileSponsorSeekAsync(SponsorSeekReconciliation request)
        => _sponsor?.ReconcileSeekAsync(request) ?? Task.FromResult(false);

    // Assembly-internal adapter targets for ISponsorEditorPlayerPort. Call on the player dispatcher.
    // Reservations precede the first await; only the VM acquires/drains the physical-input gate.
    internal Task<SponsorEditorReply> RequestSponsorFrameAsync(PlaybackStamp expected, FrameDirection direction)
    {
        if (_sponsor is not { } controller) return EditorUnavailableAsync();
        var rejected = controller.ReserveEditorFrame(expected, direction, out var reservation);
        if (rejected is not null) return Task.FromResult(rejected);
        return OwnSponsorEditorWork(ApplyAsync());
        async Task<SponsorEditorReply> ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                return await controller.ExecuteEditorFrameAsync(reservation!);
            }
            catch (Exception ex) { return controller.FailEditorPreparation(reservation!.Ticket, null, ex.Message); }
        }
    }

    internal Task<SponsorEditorReply> RequestSponsorPreviewAsync(PlaybackStamp expected, PreviewInterval preview)
    {
        if (_sponsor is not { } controller) return EditorUnavailableAsync();
        var rejected = controller.ReserveEditorPreview(expected, preview, out var reservation);
        if (rejected is not null) return Task.FromResult(rejected);
        return OwnSponsorEditorWork(ApplyAsync());
        async Task<SponsorEditorReply> ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                return await controller.ExecuteEditorPreviewAsync(reservation!);
            }
            catch (Exception ex) { return controller.FailEditorPreparation(reservation!.Ticket, reservation!.Token, ex.Message); }
        }
    }

    internal Task<SponsorEditorReply> EndSponsorPreviewAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode)
        => _sponsor is not { } controller ? EditorUnavailableAsync()
            : OwnSponsorEditorWork(controller.EndEditorPreviewAsync(token, mode));

    internal Task<SponsorEditorReply> RequestSponsorMuteReleaseAsync(PlaybackStamp expected, string leaseToken)
    {
        if (_sponsor is not { } controller) return EditorUnavailableAsync();
        var rejected = controller.ReserveEditorMuteRelease(expected, leaseToken, out var reservation);
        return rejected is not null ? Task.FromResult(rejected)
            : OwnSponsorEditorWork(controller.ExecuteEditorMuteReleaseAsync(reservation!));
    }

    private static Task<SponsorEditorReply> EditorUnavailableAsync()
        => Task.FromResult(new SponsorEditorReply(SponsorEditorOutcome.Unavailable, null, Reason: "Editor native player port is unavailable."));

    private async Task<SponsorEditorReply> OwnSponsorEditorWork(Task<SponsorEditorReply> task)
    {
        // T6-03: OwnWork tracks and joins the supplied editor operation; that producer does not await this typed result wrapper.
        #pragma warning disable VSTHRD003
        await OwnWork(task); // Existing VM ownership joins the actual operation task through close.
        #pragma warning restore VSTHRD003
        // T6-04: OwnWork already joined this same operation; the second typed await only retrieves its retained result.
        #pragma warning disable VSTHRD003
        return await task;
        #pragma warning restore VSTHRD003
    }

    private void BindSponsorPlayback()
    {
        if (!IsCurrentPlayback(_mediaVersion) || Client is not { } client || Player is not { } player) return;
        var version = _mediaVersion;
        _sponsor ??= CreateSponsorController();
        _sponsor.Bind(issuer => new MpvSponsorPlaybackBackend(client, issuer),
            // Cleanup validity deliberately excludes media cancellation and _isClosing.
            () => version == _mediaVersion && ReferenceEquals(client, Client)
                && ReferenceEquals(player, Player) && !client.IsDisposed,
            GetSelectedSponsorIdentity());
    }

    private SponsorPlaybackController CreateSponsorController()
    {
        var controller = new SponsorPlaybackController(action =>
        {
            if (!queue.TryEnqueue(() => action())) throw new InvalidOperationException("Player dispatcher is unavailable for Sponsor work.");
        }, () => queue.HasThreadAccess, () => Stopwatch.GetElapsedTime(_sponsorClockOrigin).TotalSeconds);
        controller.Updated += result => SponsorUpdated?.Invoke(result);
        return controller;
    }

    private MediaIdentity? GetSelectedSponsorIdentity()
    {
        string? bvId = null;
        string? cid = null;
        if (Connector is VideoConnectorViewModel { _part: { } part } video)
        {
            bvId = video.BvId;
            cid = part.Identifier.Id;
        }
        else if (Connector is PgcConnectorViewModel { _episode: { } episode })
        {
            // Accepted sb2 extension on the SELECTED episode, never season/main/preferred episode.
            bvId = episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.BvId);
            var value = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid);
            if (value > 0) cid = value.ToString(CultureInfo.InvariantCulture);
        }
        return !string.IsNullOrEmpty(cid) && MediaIdentity.TryParse(bvId + "+" + cid, out var identity) ? identity : null;
    }

    private void RetireSponsorBinding(RetirementReason reason) => _sponsor?.BeginNativeRetirement(reason);

    private Task DrainInputAndSponsorAsync(bool seal)
    {
        var controller = _sponsor;
        var owner = controller?.Snapshot.Owner;
        var actual = DrainInputAndSponsorCoreAsync(seal);
        if (seal && controller is not null && owner is not null) RetainSponsorRetirement(controller, owner, actual);
        return actual;
    }

    private async Task DrainInputAndSponsorCoreAsync(bool seal)
    {
        var errors = new List<Exception>();
        // Always join both cleanup paths. Physical restoration calls the still-owned native lane.
        try { await CancelPlayerInputAsync(); } catch (Exception ex) { errors.Add(ex); }
        try { if (_sponsor is not null) await _sponsor.DrainAndRestoreAsync(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("Input/Sponsor cleanup failed; player ownership retained.", errors);
        if (seal) _sponsor?.SealAfterDrain();
    }

    private Task SetUserPausedAsync(bool paused)
    {
        if (_sponsor?.CanUseBinding != true) return Task.CompletedTask;
        var ticket = _sponsor.NoteUserIntent(PlayerIntentKind.Pause);
        return OwnWork(ApplyAsync());
        async Task ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                await _sponsor.SetPausedAsync(ticket, paused);
            }
            finally { _sponsor.AbandonIntent(ticket); }
        }
    }

    private Task ToggleUserMuteAsync()
    {
        if (_sponsor?.CanUseBinding != true) return Task.CompletedTask;
        var ticket = _sponsor.NoteUserIntent(PlayerIntentKind.Mute);
        return OwnWork(ApplyAsync());
        async Task ApplyAsync()
        {
            var muted = await _sponsor.ToggleMuteAsync(ticket);
            if (!IsPlaybackClosing && ticket.NativeGeneration == _sponsor.Snapshot.Actual?.NativeGeneration) IsMute = muted;
        }
    }

    private Task SeekUserAsync(double position)
    {
        if (_sponsor?.CanUseBinding != true) return Task.CompletedTask;
        var ticket = _sponsor.NoteUserIntent(PlayerIntentKind.Seek);
        return OwnWork(ApplyAsync());
        async Task ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                await _sponsor.SeekAsync(ticket, position);
            }
            finally { _sponsor.AbandonIntent(ticket); }
        }
    }

    private Task SendOpaqueUserKeyAsync(string key)
    {
        if (_sponsor?.CanUseBinding != true || Client is not { } client) return Task.CompletedTask;
        var ticket = _sponsor.NoteUserIntent(PlayerIntentKind.OpaqueControl);
        return OwnWork(ApplyAsync());
        async Task ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                await _sponsor.OpaqueControlAsync(ticket, () => client.SendKeyPressAsync(key));
            }
            finally { _sponsor.AbandonIntent(ticket); }
        }
    }

    private Task SetPersistentUserRateAsync(double speed)
    {
        if (!double.IsFinite(speed) || speed < .1 || speed > 3 || _sponsor?.CanUseBinding != true) return Task.CompletedTask;
        var controller = _sponsor;
        var ticket = controller.NoteUserIntent(PlayerIntentKind.PersistentRate); // before physical drain/first await, even same value.
        var version = _mediaVersion;
        return OwnWork(ApplyAsync());
        async Task ApplyAsync()
        {
            try
            {
                await RestorePhysicalPlayerInputAsync();
                if (!IsCurrentPlayback(version)) return;
                IsSpeedChanging = true;
                LastSpeedChangingTime = DateTimeOffset.Now;
                var confirmed = await controller.SetPersistentRateAsync(ticket, speed);
                if (IsCurrentPlayback(version) && SettingsToolkit.ReadLocalSetting(SettingNames.IsPlayerSpeedShare, true))
                    SettingsToolkit.WriteLocalSetting(SettingNames.PlayerSpeed, confirmed);
            }
            finally { controller.AbandonIntent(ticket); }
        }
    }
}
