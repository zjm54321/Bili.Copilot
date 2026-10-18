// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Content;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// The owner (UI thread) of the evaluation of one item for one visible card control.
/// </summary>
/// <remarks>
/// <para>Uses only the app's registered <see cref="SponsorContentProjectionService"/>; it never creates a second
/// service. Attachment (which item, surface and author-space context this owner currently covers) is separate
/// from evaluation (the latest computation the owner started).</para>
/// <para>A result writes effects only when ALL of these hold: the evaluation is still the latest one, its token
/// is not cancelled, the state's owner is still this object, the item stamp (surface/id/local revision,
/// including the context marker) is exactly the one sent, the configuration revision equals the CURRENT one,
/// and the manual reveal state is unchanged.</para>
/// <para>A result computed under an older configuration (StaleConfiguration, or a revision that is no longer
/// current) has no authority. It does not clear anything by itself. Instead the owner performs a fresh
/// current-configuration invalidation: effects applied under an older configuration are cleared, and a new
/// evaluation starts. Other non-Evaluated outcomes computed under the CURRENT configuration (disabled, busy,
/// unknown, unsupported, regex failed, failed, shutdown) clear effects so the original content shows. None of
/// these retry automatically; only a real configuration change, reveal, or reattachment starts new work.</para>
/// <para>Configuration invalidation is coalesced through one synchronized pending flag that is independent
/// of the evaluation generation. The service may raise <c>ConfigurationChanged</c> from any thread, even
/// concurrently. The flag is cleared on the UI thread BEFORE the drain evaluates, so a change that arrives
/// during or after the drain always queues another one. No invalidation is lost when a reveal or context
/// change starts an evaluation in between.</para>
/// </remarks>
internal sealed class SponsorContentItemProjection
{
    private readonly DispatcherQueue _queue;
    private readonly SponsorContentProjectionService _service;
    private SponsorContentCardState? _state;
    private ContentSurface _surface;
    private bool? _authorSpace;
    private CancellationTokenSource? _work;
    private long _generation;

    // 0 = no configuration drain queued, 1 = queued. The only field touched off the UI thread.
    private int _configurationPending;

    internal SponsorContentItemProjection(DispatcherQueue queue, SponsorContentProjectionService service)
    {
        _queue = queue;
        _service = service;
    }

    /// <summary>
    /// Starts covering this item (UI thread). Subscribes before the first evaluation, so configuration changes
    /// in between are not missed. <paramref name="authorSpace"/> is the host's explicit context (null = unknown).
    /// </summary>
    internal void Attach(SponsorContentCardState state, ContentSurface surface, bool? authorSpace)
    {
        Detach();
        _state = state;
        _surface = surface;
        _authorSpace = authorSpace;
        state.Owner = this;
        state.RevealChanged += OnRevealChanged;
        _service.ConfigurationChanged += OnConfigurationChanged;

        // When taking over, effects computed under another configuration or another context are cleared first.
        if (!state.HasEffectsFor(_service.CurrentConfigurationRevision, authorSpace))
        {
            state.Clear();
        }

        Evaluate();
    }

    /// <summary>Stops covering (unload, item change, surface/context change). Cancels only its own evaluation and releases only its own ownership.</summary>
    internal void Detach()
    {
        _generation++;
        _service.ConfigurationChanged -= OnConfigurationChanged;
        Cancel();
        if (_state is { } state)
        {
            state.RevealChanged -= OnRevealChanged;
            if (ReferenceEquals(state.Owner, this))
            {
                state.Owner = null;
            }
        }

        _state = null;
    }

    private void OnRevealChanged(SponsorContentCardState state)
    {
        if (ReferenceEquals(state, _state) && ReferenceEquals(state.Owner, this))
        {
            Evaluate();
        }
    }

    // May run on any thread, concurrently. Only touches the synchronized flag and the dispatcher queue.
    private void OnConfigurationChanged(long revision)
    {
        if (Interlocked.Exchange(ref _configurationPending, 1) != 0)
        {
            return; // A drain is already queued; it will read the CURRENT revision when it runs.
        }

        if (!_queue.TryEnqueue(DrainConfiguration))
        {
            Volatile.Write(ref _configurationPending, 0); // Dispatcher shutting down: nothing to update.
        }
    }

    // UI thread. Clears the flag first, so any later change queues a new drain.
    private void DrainConfiguration()
    {
        Volatile.Write(ref _configurationPending, 0);
        InvalidateForCurrentConfiguration();
    }

    // Fresh invalidation under the CURRENT attachment and CURRENT configuration (not tied to any earlier
    // evaluation generation).
    private void InvalidateForCurrentConfiguration()
    {
        if (_state is not { } state || !ReferenceEquals(state.Owner, this))
        {
            return;
        }

        if (!state.HasEffectsFor(_service.CurrentConfigurationRevision, _authorSpace))
        {
            state.Clear();
        }

        Evaluate();
    }

    private void Evaluate()
    {
        if (_state is not { } state || !ReferenceEquals(state.Owner, this))
        {
            return;
        }

        Cancel();
        var work = new CancellationTokenSource();
        _work = work;
        var generation = ++_generation;

        ContentPromotionInput input;
        try
        {
            input = state.CreateInput(_surface, _authorSpace);
        }
        catch (ArgumentException)
        {
            // The item has no usable id or revision: it cannot be evaluated, so the original content stays.
            _work = null;
            work.Dispose();
            state.Clear();
            return;
        }

        // Ownership transfers to _work; Complete/Cancel release it, not this stack frame.
        #pragma warning disable CA2025
        _ = RunAsync(state, input, work, generation, state.LocalRevision, state.IsBodyRevealed, state.ManuallyRevealedReplies);
        #pragma warning restore CA2025
    }

    private async Task RunAsync(SponsorContentCardState state, ContentPromotionInput input, CancellationTokenSource work,
        long generation, long localRevision, bool bodyRevealed, bool repliesRevealed)
    {
        ContentProjectionResult? result;
        try
        {
            result = await _service.EvaluateAsync(input, work.Token);
        }
        catch (Exception)
        {
            result = null;
        }

        // After returning to the UI thread, check everything again; drop the result if anything differs.
        if (!_queue.HasThreadAccess)
        {
            _ = _queue.TryEnqueue(() => Complete(state, input, work, generation, localRevision, bodyRevealed, repliesRevealed, result));
            return;
        }

        Complete(state, input, work, generation, localRevision, bodyRevealed, repliesRevealed, result);
    }

    private void Complete(SponsorContentCardState state, ContentPromotionInput input, CancellationTokenSource work,
        long generation, long localRevision, bool bodyRevealed, bool repliesRevealed, ContentProjectionResult? result)
    {
        var current = generation == _generation
            && ReferenceEquals(_work, work)
            && !work.IsCancellationRequested
            && ReferenceEquals(_state, state)
            && ReferenceEquals(state.Owner, this)
            && state.LocalRevision == localRevision
            && state.IsBodyRevealed == bodyRevealed
            && state.ManuallyRevealedReplies == repliesRevealed;
        if (!current)
        {
            return; // Superseded or detached: no authority at all.
        }

        _work = null;
        work.Dispose();
        var currentConfiguration = _service.CurrentConfigurationRevision;

        // Computed under an older configuration: no authority to apply OR to clear. Perform a fresh
        // invalidation for the current configuration instead. Not a retry: it only happens because the
        // configuration really changed, and the queued drain for that change would do the same.
        if (result is not null
            && (result.Outcome == ContentProjectionOutcome.StaleConfiguration || result.ConfigurationRevision != currentConfiguration))
        {
            InvalidateForCurrentConfiguration();
            return;
        }

        if (result is { Outcome: ContentProjectionOutcome.Evaluated, Decision: { } decision }
            && Equals(result.Stamp, input.Stamp)
            && Equals(decision.Stamp, input.Stamp))
        {
            state.Apply(decision, result.ConfigurationRevision, _authorSpace);
        }
        else
        {
            // Not evaluated under the current configuration (or the call failed): show the original content.
            state.Clear();
        }
    }

    private void Cancel()
    {
        if (_work is { } work)
        {
            _work = null;
            work.Cancel();
            // Not disposed here: an evaluation that is still running may still hold the token; left to GC.
        }
    }
}
