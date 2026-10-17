using System.Globalization;
using System.Runtime.ExceptionServices;
using BiliCopilot.UI.SponsorBlock.Rules;
using FluentResults;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;

namespace BiliCopilot.UI.SponsorBlock.Playback;

/// <summary>
/// One captured client binding; the controller owns serialization, admission, drain and disposal.
/// Returned tasks cover every started SDK/native call, not just a cancellable wait for it.
/// A successful command acknowledges a request, not a displayed frame or an applied-effect receipt.
/// </summary>
internal sealed class MpvSponsorPlaybackBackend : ISponsorPlaybackBackend
{
    private readonly MpvClient client;
    private readonly NativeAdmission.Issuer issuer;

    internal MpvSponsorPlaybackBackend(MpvClient client, NativeAdmission.Issuer issuer)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
    }

    public Task<NativePlaybackSnapshot> ReadAsync(NativeAdmission operation) => Task.Run(async () =>
    {
        issuer.ValidateAndConsume(operation, NativeOperationKind.Read);
        var positionResult = await client.GetCurrentPositionAsync().ConfigureAwait(false);
        RequireSuccess(positionResult, "read time-pos");
        var position = positionResult.Value;
        if (!double.IsFinite(position) || position < 0)
            throw new InvalidOperationException("MPV returned an invalid actual position.");

        // Sequential awaits: even an early failure cannot abandon another started SDK task.
        var durationResult = await client.GetDurationAsync().ConfigureAwait(false);
        double? duration = !durationResult.IsFailed && double.IsFinite(durationResult.Value)
            && durationResult.Value > 0 ? durationResult.Value : null;
        var rateResult = await client.GetSpeedAsync().ConfigureAwait(false);
        RequireSuccess(rateResult, "read speed");
        var rate = rateResult.Value;
        if (!double.IsFinite(rate) || rate <= 0)
            throw new InvalidOperationException("MPV returned an invalid actual rate.");

        var stateResult = await client.GetPlayerStateAsync().ConfigureAwait(false);
        RequireSuccess(stateResult, "read player state");
        var muted = ReadMute();
        var seekable = ReadOptionalFlag("seekable");
        var paused = ReadOptionalFlag("pause");
        var state = MapState(stateResult.Value, paused);
        var active = state is SponsorPlaybackState.Playing or SponsorPlaybackState.Paused;
        var frames = state == SponsorPlaybackState.Paused && seekable == true
            && await HasSelectedVideoAsync().ConfigureAwait(false) && HasPausedVideoFlags();

        return new NativePlaybackSnapshot(issuer.NativeGeneration, position, duration, rate, state, muted,
            CanSeekExactly: active && seekable == true,
            CanWriteMute: muted.HasValue,
            CanWriteRate: true, // This binding just supplied a valid speed; setter failure still faults its task.
            CanStepFrames: frames);
    });

    public Task SeekExactAsync(NativeAdmission operation, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var payload = new[] { "seek", seconds.ToString("R", CultureInfo.InvariantCulture), "absolute+exact" };
        return Task.Run(() =>
        {
            issuer.ValidateAndConsume(operation, NativeOperationKind.SeekExact);
            ThrowNativeError(MpvNative.SetCommand(client.Handle, payload), "seek absolute+exact");
        });
    }

    public async Task WriteMuteAsync(NativeAdmission operation, bool muted)
    {
        issuer.ValidateAndConsume(operation, NativeOperationKind.WriteMute);
        // Pinned API is exception-bearing Task, NOT Task<Result>.
        await client.SetMuteAsync(muted).ConfigureAwait(false);
    }

    public async Task WriteRateAsync(NativeAdmission operation, double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(rate));
        issuer.ValidateAndConsume(operation, NativeOperationKind.WriteRate);
        var result = await client.SetSpeedAsync(rate).ConfigureAwait(false);
        RequireSuccess(result, "set speed");
    }

    public async Task SetPausedAsync(NativeAdmission operation, bool paused)
    {
        issuer.ValidateAndConsume(operation, NativeOperationKind.SetPaused);
        // Both are idempotent pause-property writes in the pinned SDK, not toggle/play/load.
        var result = await (paused ? client.PauseAsync() : client.ResumeAsync()).ConfigureAwait(false);
        RequireSuccess(result, paused ? "set pause" : "clear pause");
    }

    public Task StepFrameAsync(NativeAdmission operation, FrameDirection direction)
    {
        var command = direction switch
        {
            FrameDirection.Forward => "frame-step",
            FrameDirection.Backward => "frame-back-step",
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        return Task.Run(async () =>
        {
            issuer.ValidateAndConsume(operation, NativeOperationKind.StepFrame);
            var stateResult = await client.GetPlayerStateAsync().ConfigureAwait(false);
            RequireSuccess(stateResult, "read frame-step state");
            if (stateResult.Value != MpvPlayerState.Paused || !await HasSelectedVideoAsync().ConfigureAwait(false))
                throw new NotSupportedException("Frame stepping requires an actual paused, selected video track.");

            // Reread typed flags after the asynchronous track read, immediately before the command.
            // One capability covers both directions, so require seekability for forward as well as backward.
            if (!HasPausedVideoFlags())
                throw new NotSupportedException("Paused-video frame capability is unavailable or changed.");
            ThrowNativeError(MpvNative.SetCommand(client.Handle, new[] { command }), command);
        });
    }

    // Only called inside an admitted operation. No handles/nodes escape or survive its task.
    // Flag is an int in the union's scalar storage, NOT MpvNode.Format (GetProperty initializes it to zero).
    private bool? ReadMute() => ReadOptionalFlag("mute");

    private bool HasPausedVideoFlags() => ReadOptionalFlag("seekable") == true
        && ReadOptionalFlag("pause") == true && ReadOptionalFlag("seeking") == false
        && ReadOptionalFlag("paused-for-cache") == false && ReadOptionalFlag("idle-active") == false;

    private bool? ReadOptionalFlag(string property)
    {
        var error = MpvNative.GetProperty(client.Handle, property, MpvFormat.Flag, out var node);
        if (error != MpvError.Success) return null;
        return node.Flag switch { 0 => false, 1 => true, _ => null };
    }

    private async Task<bool> HasSelectedVideoAsync()
    {
        var result = await client.GetTracksAsync().ConfigureAwait(false);
        // Track capability is optional. This SDK reports native track errors as Result.Fail(string).
        if (result.IsFailed || result.Value is null) return false;
        foreach (var track in result.Value)
            if (track is not null && track.Type == MpvTrackType.Video && track.Current) return true;
        return false;
    }

    private static SponsorPlaybackState MapState(MpvPlayerState state, bool? paused) => state switch
    {
        MpvPlayerState.Idle or MpvPlayerState.End => SponsorPlaybackState.Stopped,
        MpvPlayerState.Buffering => SponsorPlaybackState.Waiting,
        MpvPlayerState.Seeking => SponsorPlaybackState.Seeking,
        // SDK Paused is a core-idle fallback, not a read of the pause property. Require agreement.
        MpvPlayerState.Paused when paused == true => SponsorPlaybackState.Paused,
        MpvPlayerState.Playing when paused == false => SponsorPlaybackState.Playing,
        _ => SponsorPlaybackState.Unknown,
    };

    private static void ThrowNativeError(MpvError error, string operation)
    {
        if (error != MpvError.Success)
            throw new MpvException("MPV Sponsor " + operation + " failed.", error);
    }

    private static void RequireSuccess(IResultBase result, string operation)
    {
        if (!result.IsFailed) return;
        var exceptions = new List<Exception>();
        foreach (var error in result.Errors) CollectExceptions(error, exceptions);
        if (exceptions.Count == 1)
        {
            // Preserve the actual MpvException/code/stack, rather than replacing it with a generic error.
            exceptions[0].Data["MpvSponsorOperation"] = operation;
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }
        if (exceptions.Count > 1)
            throw new AggregateException("MPV Sponsor " + operation + " failed.", exceptions);
        // No SDK error text/metadata is copied into logs: these operations need no URL/account/headers.
        throw new InvalidOperationException("MPV Sponsor " + operation + " failed (SDK Result without exception).");
    }

    private static void CollectExceptions(IError error, List<Exception> exceptions)
    {
        if (error is ExceptionalError exceptional) exceptions.Add(exceptional.Exception);
        foreach (var reason in error.Reasons) CollectExceptions(reason, exceptions);
    }
}
