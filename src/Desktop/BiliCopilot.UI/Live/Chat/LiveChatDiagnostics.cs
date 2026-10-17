using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Live;

internal static class LiveChatDiagnostics
{
    // Only local phase/reason strings and numeric protocol codes; never credentials or payloads.
    internal static void Write(string phase, string reason, int code = 0)
        => SafeFailureContext.TryWriteFacts(() => typeof(LiveChatDiagnostics).Get<ILogger<LiveChatClient>>(),
            logger => logger.LogWarning("LiveChat Phase={Phase} Reason={Reason} Code={Code}", phase, reason, code));
}
