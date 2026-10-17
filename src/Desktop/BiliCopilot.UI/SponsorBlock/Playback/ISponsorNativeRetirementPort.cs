using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Playback;

/// <summary>Optional closing-safe proof, separate from ordinary player/editor admission.</summary>
internal interface ISponsorNativeRetirementPort
{
    Task<SponsorNativeRetirementReceipt?> JoinSponsorNativeRetirementAsync(PlaybackOwner expected);
    bool IsCurrentSponsorNativeRetirement(SponsorNativeRetirementReceipt receipt);
    event Action<SponsorNativeRetirementReceipt>? SponsorNativeRetirementCompleted;
}

/// <summary>Managed Sponsor retirement proof, NOT a native read/effect or whole-player release.</summary>
internal sealed class SponsorNativeRetirementReceipt
{
    internal SponsorNativeRetirementReceipt(SponsorPlaybackController producer, PlaybackOwner owner,
        long generation, long sequence, Task drainTask)
    { Producer = producer; Owner = owner; Generation = generation; Sequence = sequence; DrainTask = drainTask; }

    internal SponsorPlaybackController Producer { get; }
    internal PlaybackOwner Owner { get; }
    internal long Generation { get; }
    internal long Sequence { get; }
    internal Task DrainTask { get; }
}
