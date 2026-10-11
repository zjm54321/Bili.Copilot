namespace BiliCopilot.UI.Classic.Backends.External;

/// <summary>Only emitted after affirmative approval; failure before approval leaves old media alone.</summary>
public sealed class ExternalReplacementException : Exception
{
    public ExternalReplacementException() { }
    public ExternalReplacementException(string? message) : base(message) { }
    public ExternalReplacementException(string? message, Exception? innerException) : base(message, innerException) { }
    public ExternalReplacementException(bool previousOwnerRetained) : base(
        previousOwnerRetained ? "External replacement was approved, but previous process exit is unconfirmed; its owner is retained."
            : "External replacement failed after approval; previous playback is no longer available.")
        => PreviousOwnerRetained = previousOwnerRetained;
    public bool PreviousOwnerRetained { get; } = true;
}
