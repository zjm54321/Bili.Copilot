using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Content;

/// <summary>One shared, bounded CPU boundary. No SDK, store, UI, IO, regex or work at construction.</summary>
public sealed class SponsorContentProjectionService : IAsyncDisposable
{
    private sealed record Configuration(long Revision, ContentPromotionOptions? Options, ImmutableArray<string> Whitelist);
    private readonly object _gate = new();
    // D7: Shutdown closes admission and retains its task; after CancelAsync and all accepted jobs, nested finally disposes the worker semaphore.
    #pragma warning disable CA2213
    private readonly SemaphoreSlim _workers = new(2, 2);
    #pragma warning restore CA2213
    // D8: Shutdown retains the actual join; CancelAsync is followed unconditionally by accepted-job drain and nested-finally disposal of this CTS.
    #pragma warning disable CA2213
    private readonly CancellationTokenSource _stop = new();
    #pragma warning restore CA2213
    private readonly List<Task<ContentProjectionResult>> _jobs = new(32);
    private Configuration _configuration = new(0, null, default);
    private bool _closed;
    private Task? _shutdown;
    private int _configurationSubscriberFailureCount;

    /// <summary>
    /// Synchronous on the Configure caller, outside locks. Handlers must return promptly and
    /// dispatch UI work themselves. Exceptions are isolated per handler; reentry is permitted.
    /// Concurrent/reentrant notifications may arrive out of order: reread CurrentConfigurationRevision.
    /// </summary>
    public event Action<long>? ConfigurationChanged;

    public long CurrentConfigurationRevision { get { lock (_gate) return _configuration.Revision; } }

    // Fixed category, saturating count only: no subscriber, input or exception data is retained.
    internal int ConfigurationSubscriberFailureCount { get { lock (_gate) return _configurationSubscriberFailureCount; } }

    /// <summary>
    /// Sealed records (including RegexLimits), strings and ImmutableArray are deeply immutable.
    /// No defaults are enabled for null options; default whitelist differs from initialized empty.
    /// Changes are value/ordered-sequence based, not source-object identity or playback progress.
    /// After shutdown this is a no-op returning the last revision. This is not persistence.
    /// </summary>
    public long Configure(ContentPromotionOptions? options, ImmutableArray<string> canonicalWhitelist)
    {
        Action<long>? handlers;
        long revision;
        lock (_gate)
        {
            if (_closed || _configuration.Options == options && SameWhitelist(_configuration.Whitelist, canonicalWhitelist))
                return _configuration.Revision;
            revision = checked(_configuration.Revision + 1);
            _configuration = new Configuration(revision, options, canonicalWhitelist);
            handlers = ConfigurationChanged;
        }
        if (handlers is not null)
            foreach (Action<long> handler in handlers.GetInvocationList())
                try { handler(revision); }
                catch (Exception)
                {
                    lock (_gate)
                        if (_configurationSubscriberFailureCount < int.MaxValue) ++_configurationSubscriberFailureCount;
                }
        return revision;
    }

    public Task<ContentProjectionResult> EvaluateAsync(ContentPromotionInput input, CancellationToken token)
    {
        lock (_gate)
        {
            var configuration = _configuration;
            Task<ContentProjectionResult> Done(ContentProjectionOutcome outcome, ContentPromotionDecision? decision = null) =>
                Task.FromResult(Result(input?.Stamp, configuration.Revision, outcome, decision));
            if (_closed) return Done(ContentProjectionOutcome.Shutdown);
            if (token.IsCancellationRequested) return Done(ContentProjectionOutcome.Cancelled);
            if (!ValidIdentity(input)) return Done(ContentProjectionOutcome.InvalidInput);
            var options = configuration.Options;
            if (options is null) return Done(ContentProjectionOutcome.Unavailable);

            // Mirror only the pure cheap disabled admission. Never invoke the classifier/regex here.
            var disabled = DisabledReason(input, options);
            if (disabled != ContentDisabledReason.None)
            {
                var reason = disabled == ContentDisabledReason.ReplyBlockingDisabled ? "ReplyBlockingDisabled"
                    : disabled == ContentDisabledReason.AuthorSpaceDisabled ? "AuthorSpaceDisabled" : "FeatureOrSurfaceDisabled";
                var decision = new ContentPromotionDecision(input.Stamp, ContentKnowledge.Unknown, ContentKnowledge.Unknown,
                    CommerceHint.None, ImmutableArray<string>.Empty, ContentCategoryAction.Disabled,
                    ContentWhitelistState.Unknown, false, false, false, false, false,
                    input.ManuallyRevealedBody, input.ManuallyRevealedReplies, reason, null) { DisabledReason = disabled };
                return Done(token.IsCancellationRequested ? ContentProjectionOutcome.Cancelled : ContentProjectionOutcome.Disabled, decision);
            }
            if (input.Metadata is null) return Done(ContentProjectionOutcome.InvalidInput);
            // Remove only actually completed Tasks, never release admission in a worker's finally.
            _jobs.RemoveAll(static job => job.IsCompleted);
            if (_jobs.Count == 32) return Done(ContentProjectionOutcome.Busy);
            var task = Task.Run(() => RunAsync(input, configuration, token)); // No scheduling cancellation: every caller resolves.
            _jobs.Add(task);
            return task;
        }
    }

    private async Task<ContentProjectionResult> RunAsync(ContentPromotionInput input, Configuration configuration, CancellationToken token)
    {
        ContentPromotionDecision? decision = null;
        var outcome = ContentProjectionOutcome.Failed;
        var entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            await _workers.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            ContentProjectionOutcome? interrupted;
            lock (_gate) interrupted = Interrupted(configuration.Revision, token);
            if (interrupted.HasValue) outcome = interrupted.Value;
            else
            {
                // The ONLY actual classifier call is inside Task.Run, after the two-worker gate.
                decision = ContentPromotionClassifier.Evaluate(input, configuration.Options!, configuration.Whitelist);
                outcome = decision.RegexStatus is { } status && status != TextRegexStatus.Success ? ContentProjectionOutcome.RegexFailed
                    : decision.Knowledge == ContentKnowledge.Unsupported ? ContentProjectionOutcome.Unsupported
                    : decision.Knowledge == ContentKnowledge.Unknown || decision.Whitelist == ContentWhitelistState.Unknown
                        ? ContentProjectionOutcome.Unknown : ContentProjectionOutcome.Evaluated;
            }
        }
        catch (OperationCanceledException) { outcome = ContentProjectionOutcome.Cancelled; }
        catch (Exception) { outcome = ContentProjectionOutcome.Failed; } // No raw input/pattern/UID/exception logging.
        finally { if (entered) _workers.Release(); }
        lock (_gate)
        {
            // Final service-side linearization; caller must still recheck after awaiting/dispatching.
            outcome = Interrupted(configuration.Revision, token) ?? outcome;
            return Result(input.Stamp, configuration.Revision, outcome, decision);
        }
    }

    // Called only under _gate. Shutdown takes precedence over cancellation, then stale configuration.
    private ContentProjectionOutcome? Interrupted(long revision, CancellationToken token) => _closed ? ContentProjectionOutcome.Shutdown
        : token.IsCancellationRequested ? ContentProjectionOutcome.Cancelled
        : revision != _configuration.Revision ? ContentProjectionOutcome.StaleConfiguration : null;

    private static ContentProjectionResult Result(ContentItemStamp? stamp, long revision,
        ContentProjectionOutcome outcome, ContentPromotionDecision? decision)
    {
        // Keep the pure reason/status, hint and reveal provenance, but never leak failed/stale effects.
        if (outcome != ContentProjectionOutcome.Evaluated && decision is not null)
            decision = decision with { ShowLabel = false, HideBody = false, HideReplies = false, CanToggleBody = false, CanRevealReplies = false };
        return new ContentProjectionResult(stamp, revision, outcome, decision);
    }

    private static bool ValidIdentity(ContentPromotionInput? input) => input?.Stamp is { } stamp
        && stamp.ItemId is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(stamp.ItemId)
        && stamp.Revision is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(stamp.Revision)
        && stamp.Surface is ContentSurface.Dynamic or ContentSurface.Comment or ContentSurface.Reply;

    private static ContentDisabledReason DisabledReason(ContentPromotionInput input, ContentPromotionOptions options)
    {
        if (!options.FeatureEnabled) return ContentDisabledReason.FeatureDisabled;
        if (input.Stamp.Surface == ContentSurface.Dynamic && !options.DynamicEnabled) return ContentDisabledReason.DynamicDisabled;
        if (input.Stamp.Surface != ContentSurface.Dynamic && !options.CommentEnabled) return ContentDisabledReason.CommentDisabled;
        if (input.Stamp.Surface == ContentSurface.Reply && !options.BlockReplies) return ContentDisabledReason.ReplyBlockingDisabled;
        if (input.Stamp.Surface == ContentSurface.Dynamic && !options.DynamicInAuthorSpaceEnabled && input.IsAuthorSpace == true)
            return ContentDisabledReason.AuthorSpaceDisabled;
        return ContentDisabledReason.None;
    }

    private static bool SameWhitelist(ImmutableArray<string> left, ImmutableArray<string> right) =>
        left.IsDefault || right.IsDefault ? left.IsDefault == right.IsDefault : left.AsSpan().SequenceEqual(right.AsSpan());

    /// <summary>
    /// Closes admission synchronously, cancels cooperatively, then joins every accepted actual Task.
    /// Concurrent/repeated calls share the same drain Task. No timeout-as-success, detached cleanup,
    /// or synchronous UI wait. One in-flight pure regex operation may finish before cancellation wins.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new ValueTask(_shutdown);
            _closed = true;
            var accepted = _jobs.ToArray();
            _shutdown = Task.Run(async () =>
            {
                try { await _stop.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    // Even an unexpected cancellation-callback failure cannot skip the actual join.
                    try { await Task.WhenAll(accepted).ConfigureAwait(false); }
                    finally
                    {
                        try { _workers.Dispose(); }
                        finally
                        {
                            try { _stop.Dispose(); }
                            finally { lock (_gate) { _jobs.Clear(); ConfigurationChanged = null; } }
                        }
                    }
                }
            });
            return new ValueTask(_shutdown);
        }
    }
}
