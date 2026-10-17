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
    /// NavigatingTo: fade in and slide up 32 pixels over 250 ms.
    /// </summary>
    Forward,

    /// <summary>
    /// BackNavigatingTo: fade in over 200 ms without an offset.
    /// </summary>
    Back,
}

/// <summary>
/// One-shot arrival motion for the newly shown cached page root of a frame.
/// Only the current target's composition opacity and translation are animated.
/// </summary>
internal sealed class PageArrivalTransition
{
    private const float TranslationOffset = 32f;
    private static readonly TimeSpan ForwardDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BackDuration = TimeSpan.FromMilliseconds(200);

    private DispatcherQueue? _dispatcherQueue;
    private UISettings? _uiSettings;
    private bool _animationsEnabled;
    private long _epoch;
    private UIElement? _target;

    /// <summary>
    /// Starts observing the system animation setting. Call from the owning frame's Loaded.
    /// </summary>
    public void Attach(DispatcherQueue dispatcherQueue)
    {
        if (_dispatcherQueue is not null)
        {
            return;
        }

        _dispatcherQueue = dispatcherQueue;
        try
        {
            _uiSettings ??= new UISettings();
            _animationsEnabled = _uiSettings.AnimationsEnabled;
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        }
        catch (Exception)
        {
            _animationsEnabled = false;
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
        }

        _dispatcherQueue = null;
        _animationsEnabled = false;
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
        Attach(target.DispatcherQueue);
        if (target.Visibility != Visibility.Visible || !_animationsEnabled)
        {
            // Reduced motion: no offset and no fade; values are already neutral.
            return;
        }

        var epoch = _epoch;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;
        var incoming = compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(0f, 1f));
        var linear = compositor.CreateLinearEasingFunction();

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = kind == PageArrivalKind.Forward ? ForwardDuration : BackDuration;
        opacity.InsertKeyFrame(0f, 0f, linear);
        opacity.InsertKeyFrame(1f, 1f, incoming);

        _target = target;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.Opacity = 0f;
        visual.StartAnimation("Opacity", opacity);

        if (kind == PageArrivalKind.Forward)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(target, true);
            visual.Properties.InsertVector3("Translation", new Vector3(0f, TranslationOffset, 0f));
            var offset = compositor.CreateScalarKeyFrameAnimation();
            offset.Duration = ForwardDuration;
            offset.InsertKeyFrame(0f, TranslationOffset, linear);
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
            _animationsEnabled = sender.AnimationsEnabled;
            if (!_animationsEnabled)
            {
                Cancel();
            }
        });
    }
}
