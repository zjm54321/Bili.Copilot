// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>Where content is displayed, as declared explicitly by its host page or container.</summary>
public enum SponsorContentPlaceKind
{
    /// <summary>Not declared: "in a UP's personal space?" is passed as unknown (null).</summary>
    Unknown,

    /// <summary>Ordinary feed, explicitly declared (not a personal space).</summary>
    Feed,

    /// <summary>A UP's personal space, explicitly declared.</summary>
    AuthorSpace,
}

/// <summary>
/// Attached context: the host page or container states once where its content is displayed. The card
/// notice reads the nearest declaration up the visual tree.
/// </summary>
/// <remarks>
/// This is the only source of the "in personal space" flag. It is never inferred from card style, author
/// UID or anything else. With no declaration in the ancestry, the value is null. The value is read when
/// the card loads or reattaches; moving or recycling a card goes through Unloaded/Loaded and reads it
/// again. Declarations are static in XAML, and changing one while loaded is not supported.
/// </remarks>
public static class SponsorContentPlace
{
    /// <summary>Kind attached property.</summary>
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.RegisterAttached("Kind", typeof(SponsorContentPlaceKind), typeof(SponsorContentPlace), new PropertyMetadata(SponsorContentPlaceKind.Unknown));

    /// <summary>Reads the declared kind.</summary>
    public static SponsorContentPlaceKind GetKind(DependencyObject element) => (SponsorContentPlaceKind)element.GetValue(KindProperty);

    /// <summary>Declares the kind.</summary>
    public static void SetKind(DependencyObject element, SponsorContentPlaceKind value) => element.SetValue(KindProperty, value);

    /// <summary>
    /// Walks up from <paramref name="start"/> to the nearest explicit declaration. Returns true for
    /// AuthorSpace, false for Feed, null if none is found.
    /// </summary>
    internal static bool? ResolveAuthorSpace(DependencyObject start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            switch (GetKind(current))
            {
                case SponsorContentPlaceKind.AuthorSpace:
                    return true;
                case SponsorContentPlaceKind.Feed:
                    return false;
            }
        }

        return null;
    }
}
