namespace BiliCopilot.UI.SponsorBlock.Content;

public enum ContentProjectionOutcome
{
    Evaluated, Disabled, Unavailable, Busy, Cancelled, StaleConfiguration,
    Shutdown, InvalidInput, Unknown, Unsupported, RegexFailed, Failed,
}

/// <summary>
/// Stamp is the exact input stamp, or null only when the malformed input supplied none.
/// ConfigurationRevision is the captured LOCAL configuration version, not a persisted version.
/// Only Evaluated may carry effects. All other outcomes suppress effects, not assert clean content.
/// No CanApply property: the caller must additionally prove current item/local revision,
/// configuration revision, visibility, reveal state and an uncancelled item lifetime on its owner thread.
/// </summary>
public sealed record ContentProjectionResult(ContentItemStamp? Stamp, long ConfigurationRevision,
    ContentProjectionOutcome Outcome, ContentPromotionDecision? Decision);
