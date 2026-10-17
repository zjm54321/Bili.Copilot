// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// A frame that owns cached page roots and decides which one is currently active.
/// </summary>
internal interface IPageActivationHost
{
    /// <summary>
    /// Gets the activation scope shared by the frames of one window.
    /// </summary>
    PageActivationScope ActivationScope { get; }

    /// <summary>
    /// Whether <paramref name="page"/> is a page root directly owned by this frame.
    /// </summary>
    bool OwnsPage(UIElement page);

    /// <summary>
    /// Whether <paramref name="page"/> is the selected, visible and uncovered page of this frame.
    /// </summary>
    bool IsPageActive(UIElement page);
}

/// <summary>
/// Per-window activation signal shared by <see cref="MainFrame"/> and its sibling <see cref="OverlayFrame"/>.
/// It is owned by the main frame instance; there is no static or cross-window state.
/// </summary>
internal sealed class PageActivationScope
{
    /// <summary>
    /// Raised synchronously on the UI thread whenever any page activation input changes.
    /// Listeners must re-query <see cref="IPageActivationHost.IsPageActive(UIElement)"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    /// <summary>
    /// Gets a value indicating whether the overlay frame currently covers (or is about to cover) the main frame.
    /// </summary>
    public bool IsMainCovered { get; private set; }

    /// <summary>
    /// Notifies all registered listeners that activation inputs changed.
    /// </summary>
    public void NotifyChanged() => ActivationChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Gets a value indicating whether a one-shot "back to main" arrival is waiting to be consumed.
    /// Set only by the overlay frame when a back step removes its last page while it was showing
    /// (so the main frame was hidden); consumed by the main frame only when its selected page
    /// actually becomes active (visible and uncovered). Any forward navigation or clear withdraws it.
    /// </summary>
    public bool IsMainBackArrivalPending { get; private set; }

    /// <summary>
    /// Records a one-shot back-arrival intent for the main frame. No notification is raised here:
    /// the overlay's own switch/visibility notifications drive consumption.
    /// </summary>
    public void OfferMainBackArrival() => IsMainBackArrivalPending = true;

    /// <summary>
    /// Withdraws any pending back-arrival intent (forward navigation, clear, unload).
    /// </summary>
    public void WithdrawMainBackArrival() => IsMainBackArrivalPending = false;

    /// <summary>
    /// Consumes the pending back-arrival intent. Returns <see langword="true"/> exactly once per offer.
    /// </summary>
    public bool TryConsumeMainBackArrival()
    {
        if (!IsMainBackArrivalPending)
        {
            return false;
        }

        IsMainBackArrivalPending = false;
        return true;
    }

    /// <summary>
    /// Publishes the overlay cover and activation inputs together in one notification.
    /// </summary>
    public void SetMainCovered(bool isCovered)
    {
        IsMainCovered = isCovered;
        NotifyChanged();
    }
}

/// <summary>
/// Locates the nearest page-activation host for a descendant element.
/// </summary>
internal static class PageActivation
{
    /// <summary>
    /// Walks the visual tree upward and returns the nearest frame that owns a page root containing <paramref name="element"/>.
    /// </summary>
    public static bool TryFindHost(DependencyObject element, out IPageActivationHost? host, out UIElement? page)
    {
        host = null;
        page = null;
        DependencyObject? grandChild = null;
        DependencyObject? child = element;
        var parent = VisualTreeHelper.GetParent(element);
        while (parent is not null)
        {
            // Frame -> RootGrid -> page root.
            if (parent is IPageActivationHost candidate
                && grandChild is UIElement candidatePage
                && candidate.OwnsPage(candidatePage))
            {
                host = candidate;
                page = candidatePage;
                return true;
            }

            grandChild = child;
            child = parent;
            parent = VisualTreeHelper.GetParent(parent);
        }

        return false;
    }
}
