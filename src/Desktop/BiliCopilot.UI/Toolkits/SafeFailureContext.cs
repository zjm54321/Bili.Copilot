using System.Diagnostics;
using System.Globalization;
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

[Flags]
internal enum FailureCaptureStatus { Complete = 0, ExceptionMetadataUnavailable = 1, NoFrames = 2, FrameMetadataUnavailable = 4 }

internal readonly record struct SafeExceptionContext(string ExceptionType, string HResult,
    string CodeMethods, string InnerTypes, FailureCaptureStatus CaptureStatus);

/// <summary>Bounded code metadata only, reused at existing ILogger boundaries. No new sink or work.</summary>
internal static class SafeFailureContext
{
    internal static SafeExceptionContext Capture(Exception exception)
    {
        var type = "ExceptionTypeUnavailable";
        var hresult = "HResultUnavailable";
        var methods = new List<string>(8);
        var innerTypes = new List<string>(3);
        var status = FailureCaptureStatus.Complete;
        try
        {
            type = TypeName(exception.GetType());
            if (type == "CodeSymbolUnavailable") status |= FailureCaptureStatus.ExceptionMetadataUnavailable;
            hresult = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture);
            var inner = exception.InnerException;
            for (var i = 0; i < 3 && inner is not null; i++, inner = inner.InnerException)
            {
                var innerType = TypeName(inner.GetType());
                if (innerType == "CodeSymbolUnavailable") status |= FailureCaptureStatus.ExceptionMetadataUnavailable;
                innerTypes.Add(innerType);
            }
        }
        catch { status |= FailureCaptureStatus.ExceptionMetadataUnavailable; }
        try
        {
            var trace = new StackTrace(exception, false); // Never request source filenames/lines/locals.
            if (trace.FrameCount == 0) status |= FailureCaptureStatus.NoFrames;
            for (var i = 0; i < Math.Min(8, trace.FrameCount); i++)
            {
                try
                {
                    // .NET 9's diagnostic metadata API, not MethodBase/ToString or parameter reflection.
                    var method = trace.GetFrame(i) is { } frame ? DiagnosticMethodInfo.Create(frame) : null;
                    if (method is not null)
                    {
                        var declaringType = Symbol(method.DeclaringTypeName);
                        var name = Symbol(method.Name);
                        if (declaringType == "CodeSymbolUnavailable" || name == "CodeSymbolUnavailable")
                            status |= FailureCaptureStatus.FrameMetadataUnavailable;
                        methods.Add(declaringType + "." + name);
                    }
                    else status |= FailureCaptureStatus.FrameMetadataUnavailable;
                }
                catch { status |= FailureCaptureStatus.FrameMetadataUnavailable; }
            }
        }
        catch { status |= FailureCaptureStatus.FrameMetadataUnavailable; }
        return new(type, hresult, string.Join(" | ", methods), string.Join(" | ", innerTypes), status);
    }

    internal static bool TryWrite(FailureStage stage, Exception exception, Func<ILogger> resolveLogger)
    {
        try
        {
            var context = Capture(exception);
            // No Exception object is passed to ILogger: sinks cannot serialize Message/Data/ToString.
            resolveLogger().LogWarning(
                "FailureContext Stage={Stage} ExceptionType={ExceptionType} HResult={HResult} CodeMethods={CodeMethods} InnerTypes={InnerTypes} CaptureStatus={CaptureStatus}",
                stage, context.ExceptionType, context.HResult, context.CodeMethods, context.InnerTypes, context.CaptureStatus);
            return true; // The sink call returned, not proof of file flush or application recovery.
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

    private static string TypeName(Type type) => Symbol(type.Namespace + "." + type.Name);

    private static string Symbol(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256) return "CodeSymbolUnavailable";
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '+' or '`' or '<' or '>' or '[' or ']' or ','))
                return "CodeSymbolUnavailable";
        return value;
    }
}
