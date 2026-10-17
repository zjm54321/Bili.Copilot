// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.SponsorBlock.ViewModels;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// App-wide local-save recovery bar shown during quit. Placed once in RootLayout's dedicated Auto row (Row 2).
/// </summary>
/// <remarks>
/// Associates itself on Loaded with the existing DI singleton <see cref="SponsorRuntime"/> and releases only
/// its own subscription on Unloaded. It never creates a fallback runtime and never Load/Close/Wait. If the
/// app has no runtime registered, it stays collapsed. One view model per bar instance; a Loaded/Unloaded
/// cycle rebinds the same instance.
/// </remarks>
public sealed partial class SponsorCloseRecoveryBar : UserControl
{
    // Height bounds for the dedicated recovery row: at most 40% of the window, and never more than 200.
    // Content beyond that scrolls inside the bar, so the main content keeps most of a small window.
    private const double MaximumShare = 0.4;
    private const double MaximumHeight = 200;
    private const double MinimumHeight = 72;
    private XamlRoot? _root;

    /// <summary>Initializes a new instance of the <see cref="SponsorCloseRecoveryBar"/> class.</summary>
    public SponsorCloseRecoveryBar()
    {
        Visibility = Visibility.Collapsed;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The recovery view model owned by this bar.</summary>
    public SponsorCloseRecoveryViewModel ViewModel { get; } = new();

    /// <summary>Error while local content is still unsaved; otherwise informational.</summary>
    public static InfoBarSeverity SeverityOf(bool error) => error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DispatcherQueue is not { } queue)
        {
            return;
        }

        SponsorRuntime runtime;
        try
        {
            runtime = this.Get<SponsorRuntime>();
        }
        catch (InvalidOperationException)
        {
            // No runtime registered: there is nothing to recover here, so the bar stays collapsed.
            return;
        }

        _ = ViewModel.Bind(runtime, queue);
        TrackRoot(XamlRoot);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TrackRoot(null);
        ViewModel.Unbind();
    }

    private void TrackRoot(XamlRoot? root)
    {
        if (_root is { } old)
        {
            old.Changed -= OnRootChanged;
        }

        _root = root;
        if (root is not null)
        {
            root.Changed += OnRootChanged;
        }

        ApplyHeightBound();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyHeightBound();

    private void ApplyHeightBound()
    {
        var height = _root?.Size.Height ?? 0;
        BarScroll.MaxHeight = height > 0 && double.IsFinite(height)
            ? Math.Clamp(height * MaximumShare, MinimumHeight, MaximumHeight)
            : MaximumHeight;
    }
}
