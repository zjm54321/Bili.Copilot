// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// SponsorBlock marker hook of the full-screen / compact transport. View association only:
/// no coordinator, player or input admission is owned here. The panel entry lives in the
/// overlay header, not in this transport.
/// </summary>
public sealed partial class OverlayTransportControls
{
    /// <summary>
    /// Shows SponsorBlock markers on the progress track, or hides them for <see langword="null"/>.
    /// Marker positions come from the view model (actual playback duration). The marker bar is a
    /// sibling drawn below both progress sliders (real and drag preview), so it is never moved,
    /// never re-parented and the thumb always covers it.
    /// </summary>
    internal void SetSponsorTimeline(SponsorPlaybackViewModel? viewModel)
    {
        if (!ReferenceEquals(SponsorTimeline.ViewModel, viewModel)) SponsorTimeline.ViewModel = viewModel;
        SponsorTimelineLayer.Visibility = viewModel is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
