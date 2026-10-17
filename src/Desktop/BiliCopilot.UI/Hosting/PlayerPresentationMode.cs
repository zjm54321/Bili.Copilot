namespace BiliCopilot.UI.Hosting;

/// <summary>Presentation of a New player host; never a media/backend selection.</summary>
public enum PlayerPresentationMode
{
    Normal = 0,
    Wide = 1,
    // Reserved for persisted numeric/named compatibility; never offered as a mode.
    FullWindow = 2,
    FullScreen = 3,
    CompactOverlay = 4,
}

internal static class PlayerPresentationModes
{
    internal static PlayerPresentationMode Normalize(PlayerPresentationMode mode)
        => mode == PlayerPresentationMode.FullWindow ? PlayerPresentationMode.Normal : mode;
}
