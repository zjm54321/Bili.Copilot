// Copyright (c) Bili Copilot. All rights reserved.

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// Kind of page arrival, mirroring WinUI <c>EntranceNavigationTransitionInfo</c> triggers.
/// </summary>
internal enum PageArrivalKind
{
    /// <summary>
    /// NavigatingTo: fade in over 150 ms while Y holds at +140, then Y settles to 0 at 450 ms.
    /// </summary>
    Forward,

    /// <summary>
    /// BackNavigatingTo: opacity holds 0 until 150 ms, then fades to 1 at 450 ms. No offset.
    /// </summary>
    Back,
}

/// <summary>
/// One-shot arrival motion for the newly shown cached page root of a frame.
/// Source-equivalent recipe of WinUI 1.8.3 (de1b1d8f) <c>ThemeTransitions.cpp:3180-3215</c>;
/// it is not a native Frame transition. Only the current target is ever animated.
/// </summary>
internal sealed class PageArrivalTransition
{
    private const float TranslationOffset = 140f;
    private const float HoldFraction = 150f / 450f;
    private static readonly TimeSpan TotalDuration = TimeSpan.FromMilliseconds(450);

    private DispatcherQueue? _dispatcherQueue;
    private UISettings? _uiSettings;
    private long _epoch;
    private UIElement? _target;

    /// <summary>
    /// Starts observing the system animation setting. Call from the owning frame's Loaded.
    /// </summary>
    public void Attach(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        if (_uiSettings is not null)
        {
            return;
        }

        try
        {
            _uiSettings = new UISettings();
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        }
        catch (Exception)
        {
            _uiSettings = null;
        }
    }

    /// <summary>
    /// Stops observing and restores any running target. Call from the owning frame's Unloaded.
    /// </summary>
    public void Detach()
    {
        Cancel();
        if (_uiSettings is not null)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            _uiSettings = null;
        }
    }

    /// <summary>
    /// Cancels the current one-shot and synchronously restores its target to Y=0 / opacity=1.
    /// Must run before any new page selection, removal or clear.
    /// </summary>
    public void Cancel()
    {
        ++_epoch;
        var target = _target;
        _target = null;
        if (target is not null)
        {
            Restore(target);
        }
    }

    /// <summary>
    /// Plays the arrival one-shot on <paramref name="target"/> if system animations are enabled.
    /// </summary>
    public void Start(UIElement target, PageArrivalKind kind)
    {
        Cancel();
        if (target.Visibility != Visibility.Visible || !AreAnimationsEnabled())
        {
            // Reduced motion: no offset and no fade; values are already neutral.
            return;
        }

        var epoch = _epoch;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;
        var incoming = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var linear = compositor.CreateLinearEasingFunction();

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TotalDuration;
        opacity.InsertKeyFrame(0f, 0f, linear);
        if (kind == PageArrivalKind.Forward)
        {
            opacity.InsertKeyFrame(HoldFraction, 1f, linear);
            opacity.InsertKeyFrame(1f, 1f, linear);
        }
        else
        {
            opacity.InsertKeyFrame(HoldFraction, 0f, linear);
            opacity.InsertKeyFrame(1f, 1f, incoming);
        }

        _target = target;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.Opacity = 0f;
        visual.StartAnimation("Opacity", opacity);

        if (kind == PageArrivalKind.Forward)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(target, true);
            visual.Properties.InsertVector3("Translation", new Vector3(0f, TranslationOffset, 0f));
            var offset = compositor.CreateScalarKeyFrameAnimation();
            offset.Duration = TotalDuration;
            offset.InsertKeyFrame(0f, TranslationOffset, linear);
            offset.InsertKeyFrame(HoldFraction, TranslationOffset, linear);
            offset.InsertKeyFrame(1f, 0f, incoming);
            visual.StartAnimation("Translation.Y", offset);
        }

        batch.End();
        batch.Completed += (_, _) => OnCompleted(epoch, target);
    }

    private static void Restore(UIElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation.Y");
        visual.StopAnimation("Translation");
        visual.Opacity = 1f;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnCompleted(long epoch, UIElement target)
    {
        if (_dispatcherQueue is { HasThreadAccess: false } queue)
        {
            _ = queue.TryEnqueue(() => OnCompleted(epoch, target));
            return;
        }

        // A stale completion must never reset a newer target.
        if (epoch != _epoch || !ReferenceEquals(_target, target))
        {
            return;
        }

        _target = null;
        Restore(target);
    }

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        // Raised off the UI thread; cancel only on the owning dispatcher.
        _ = _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!AreAnimationsEnabled())
            {
                Cancel();
            }
        });
    }
}
