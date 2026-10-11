using BiliCopilot.UI.Classic.Views;

namespace BiliCopilot.UI.Classic.Hosting;

/// <summary>Connects the final frontend's real namescope/session API without replacing its layout.</summary>
public static class ClassicFrontendAdapter
{
    public static ClassicPlayerContent Create(IClassicPlaybackSession session, UIElement surface)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(surface);
        var view = new ClassicPlayerView { IsTabStop = true };
        if (view.FindName("VideoHost") is not Border slot)
            throw new InvalidOperationException("Classic 前端缺少约定的 VideoHost 表面槽。");

        // SetSession is the only frontend data source; do not install a competing DataContext.
        try
        {
            view.SetSession(session);
            slot.Child = surface;
        }
        catch
        {
            view.ClearSession();
            throw;
        }
        Action? detachPanel = null;
        return new ClassicPlayerContent(view, slot,
            SetFullWindow: view.SetFullWindow,
            ToggleControls: view.ToggleControls,
            ShowContextMenu: view.ShowContextMenu,
            IsInputBlocked: () => view.IsInputBlocked,
            PreviewPosition: view.PreviewPosition,
            ShowControlsForInput: view.ShowControlsForInput,
            SetInteractionActive: view.SetInteractionActive,
            Detach: () =>
        {
            // Called by the host only after the session/backend disposal task has completed.
            detachPanel?.Invoke();
            view.ClearSession();
            slot.Child = null;
        }, SubscribePanelStateChanged: handler =>
        {
            if (detachPanel is not null) throw new InvalidOperationException("Classic panel events already have a host owner.");
            view.PanelStateChanged += handler;
            var subscribed = true;
            detachPanel = () =>
            {
                if (!subscribed) return;
                subscribed = false;
                view.PanelStateChanged -= handler;
                detachPanel = null;
            };
            return detachPanel;
        });
    }
}
