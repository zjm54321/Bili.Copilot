using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Toolkits;

internal enum FailureStage
{
    ArticleDetailFetch, SystemNotificationLoad,
    LiveAdmission, LivePreviousClose, LiveSourceSetup, LiveWindowCreate, LiveNativeInitialize,
    LiveBackendCreate, LiveChatCreate, LiveSessionCreate, LiveWindowOpen, LiveAfterOpen, LiveOpenCleanup,
    LivePreviousStop, LiveResolve, LiveValidateSource, LivePublishBinding, LiveSetVolume, LiveBackendOpen,
    LivePublishPlaying, SponsorInitialLoad, SponsorSettingsBind, SponsorFetch,
    LiveWindowChanged, LiveOpenDispatch, ApplicationPlaybackObserve,
    IslandOpenValidate, IslandMpvCreate, IslandConfigure, IslandPlay, IslandReady, IslandInitialSample,
    EmbeddedCaptionInput, EmbeddedOpen, EmbeddedHostReady, PlaybackInitialize,
}

internal static class SafeFailureContext
{
    internal static bool TryWrite(FailureStage stage, Exception exception, Func<ILogger> resolveLogger)
    {
        try
        {
            resolveLogger().LogWarning(
                "Failure Stage={Stage} ExceptionType={ExceptionType} HResult=0x{HResult:X8}",
                stage, exception.GetType().Name, exception.HResult);
            return true;
        }
        catch { return false; } // Diagnostic failure only; no replacement exception or recursive log.
    }

    internal static bool TryWriteFacts(Func<ILogger> resolveLogger, Action<ILogger> writeAllowedFacts)
    {
        try
        {
            writeAllowedFacts(resolveLogger());
            return true;
        }
        catch { return false; }
    }
}
