// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// SponsorBlock marker hook of the ordinary (island) transport. View association only.
/// The panel entry lives in the page / overlay header, not in this transport.
/// </summary>
public sealed partial class IslandTransportControls
{
    /// <summary>
    /// Shows SponsorBlock markers on the progress track, or hides them for <see langword="null"/>.
    /// Marker positions come from the view model (actual playback duration). The marker bar is a
    /// sibling drawn below the progress slider, so it is never re-parented and the thumb covers it.
    /// </summary>
    internal void SetSponsorTimeline(SponsorPlaybackViewModel? viewModel)
    {
        if (!ReferenceEquals(SponsorTimeline.ViewModel, viewModel)) SponsorTimeline.ViewModel = viewModel;
        SponsorTimelineLayer.Visibility = viewModel is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
