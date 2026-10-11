using Microsoft.UI.Windowing;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Hosting;

/// <summary>Only the embedded and independent New/mpv hosts. UI-thread owned.</summary>
public interface IPlayerHost : IAsyncDisposable
{
    bool IsEmbedded { get; }
    bool IsDisposed { get; }
    bool IsClosed { get; }
    bool IsPresenterChanging { get; }
    IntPtr OwnerHwnd { get; }
    IntPtr RenderHwnd { get; }
    // Compatibility for picker/header callers. Never use Handle for mpv wid.
    IntPtr Handle { get; }
    XamlRoot? XamlRoot { get; }
    PlayerPresentationMode Presentation { get; }
    event EventHandler<Size> SizeChanged;
    event EventHandler PresentationChanged;
    event EventHandler CloseRequested;
    Task ReadyAsync(CancellationToken cancellationToken);
    Task SetPresentationAsync(PlayerPresentationMode mode);
    AppWindow GetWindow();
    void Show(bool restorePosition = true);
    void SetUIElement(UIElement? element);
    void SetActiveAction(Action<bool>? action);
    void SetTheme(ElementTheme theme);
    void SetCaptionButtonVisibility(bool visible);
    void DelayUpdateTitleBarPadding(bool delaySec = false);
    void ResetFocus();
    void ShowCursor();
    void HideCursor();
    void ShowTip(string message, InfoType type);
    void SetTopMost(bool value);
    bool IsTopMost();
    void Close();
    void EnterFullScreen();
    void ExitFullScreen();
    void EnterCompactOverlay();
    void ExitCompactOverlay();
}
