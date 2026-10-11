namespace BiliCopilot.UI.Classic.Hosting;

/// <summary>Frontend-owned layout; hosting attaches surfaces and input, never creates a second UI.</summary>
public sealed record ClassicPlayerContent(FrameworkElement Root, FrameworkElement InputSurface,
    Action<bool>? SetFullWindow = null, Action? ToggleControls = null,
    Action? ShowContextMenu = null, Func<bool>? IsInputBlocked = null,
    Action<double?>? PreviewPosition = null, Action? Detach = null,
    Func<EventHandler, Action>? SubscribePanelStateChanged = null,
    Action? ShowControlsForInput = null,
    Action<bool>? SetInteractionActive = null);
