// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.WinUIKernel.Share.Toolkits;
using BiliCopilot.UI.Classic.Backends.External;

namespace BiliCopilot.UI.Classic;

/// <summary>Immutable preferences captured once for a subsequent session, never written back.</summary>
public sealed record ClassicSessionOptions(
    PlaybackExperience Experience,
    ClassicBackendKind Backend,
    ClassicDisplayMode DisplayMode,
    ClassicDecodePreference DecodePreference,
    string ExternalPlayerPath,
    bool UseWebPlayerWhenLive,
    bool EndWithPlaylist)
{
    /// <summary>Protocol pinned by an explicit action; null retains the ordinary Classic prompt.</summary>
    public ExternalPlayerProtocol? ExternalProtocol { get; init; }

    /// <summary>
    /// Reads the named settings without migration or engine activation. String keys match the
    /// append-only SettingNames 135–140; numeric casts would read the wrong key before integration.
    /// Unsupported backend/display/decode selections are retained for explicit rejection by their
    /// owner, not silently replaced. DisplayMode must never be cast from PlayerDisplayMode.
    /// </summary>
    public static ClassicSessionOptions Capture(ISettingsToolkit settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            ReadExperience(settings),
            settings.ReadLocalSetting("ClassicPlayerBackend", ClassicBackendKind.Island),
            ClassicDisplayModes.Normalize(settings.ReadLocalSetting("ClassicPlayerDisplayMode", ClassicDisplayMode.Default)),
            settings.ReadLocalSetting("ClassicPlayerDecodePreference", ClassicDecodePreference.Auto),
            settings.ReadLocalSetting("ClassicExternalPlayerPath", string.Empty) ?? string.Empty,
            settings.ReadLocalSetting("ClassicUseWebPlayerWhenLive", false),
            settings.ReadLocalSetting("EndWithPlaylist", true));
    }

    /// <summary>Missing or invalid experience leaves routing on New; other settings are not read.</summary>
    public static PlaybackExperience ReadExperience(ISettingsToolkit settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var value = settings.ReadLocalSetting("PlaybackExperience", PlaybackExperience.New);
            return value == PlaybackExperience.Classic ? PlaybackExperience.Classic : PlaybackExperience.New;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return PlaybackExperience.New;
        }
    }
}
