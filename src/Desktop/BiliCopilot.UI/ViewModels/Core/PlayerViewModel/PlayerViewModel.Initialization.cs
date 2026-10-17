// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Playback;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private volatile bool _isClosing;
    private long _mediaVersion;
    private readonly CancellationTokenSource _lifetime = new();
    // Successful DisposeAsync -> CloseAsync -> DisposeCoreAsync drains work and native/host owners
    // before disposing current/retired CTSs; failed close intentionally retains them with this VM.
#pragma warning disable CA2213
    private CancellationTokenSource? _mediaCancellation;
#pragma warning restore CA2213
    private readonly List<CancellationTokenSource> _retiredMediaCancellations = [];
    private Exception? _mediaCancellationError;
    private CancellationToken _activeMediaToken;
    // Managed WaitAsync/Release gate (no AvailableWaitHandle); late callers may still use it.
    // It is collected with the VM, not disposed on close; a failed retained VM may live until exit.
#pragma warning disable CA2213
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
#pragma warning restore CA2213
    private readonly HashSet<Task> _ownedWork = [];
    private Task? _closeTask;
    private Task? _closeWorker;
    private readonly List<Exception> _closeCancellationErrors = [];
    private bool _hostReady;
    private bool _independentInitializationCompleted;

    // A partial/failed/closing independent session still needs a reachable main owner.
    internal bool HasUsableIndependentHost => !IsEmbedded && _independentInitializationCompleted
        && _hostReady && !IsPlaybackClosing && Window?.GetWindow().IsVisible == true;

    internal bool IsPlaybackClosing => _isClosing || Window?.IsClosed == true
        || Window?.IsDisposed == true || Client?.IsDisposed == true;

    internal bool HasPublishedClose => _closeTask is not null;

    private bool IsCurrentPlayback(long version) => !IsPlaybackClosing && version == _mediaVersion
        && !_activeMediaToken.IsCancellationRequested;

    private void CheckMediaAdmission()
    {
        _activeMediaToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsPlaybackClosing, this);
    }

    private Task OwnWork(Task task)
    {
        _ownedWork.Add(task);
        return JoinAsync();
        async Task JoinAsync()
        {
            // Observe the actual admitted task before removing it; this observer owns no media gate.
#pragma warning disable VSTHRD003
            try { await task; }
#pragma warning restore VSTHRD003
            finally { _ownedWork.Remove(task); }
        }
    }

    private Task RunWithoutPlayerInputAsync(Func<Task> operation, bool nativeReplay = false)
    {
        if (IsPlaybackClosing) return Task.CompletedTask;
        // Close Sponsor admission synchronously, before any cancellation callback/await.
        if (nativeReplay) RetireSponsorBinding(RetirementReason.Replay);
        else _sponsor?.CloseAdmission(RetirementReason.Paused);
        var intent = !nativeReplay && _sponsor?.CanUseBinding == true
            ? _sponsor.NoteUserIntent(PlayerIntentKind.Pause) : null;
        return OwnWork(RunWithoutPlayerInputCoreAsync(operation, _mediaVersion, _activeMediaToken, nativeReplay, intent));
    }

    private async Task RunWithoutPlayerInputCoreAsync(Func<Task> operation, long version, CancellationToken token,
        bool nativeReplay, UserIntentTicket? intent)
    {
        var entered = false;
        try
        {
            await _initializationGate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (!IsCurrentPlayback(version)) return;
            if (nativeReplay) RetireSponsorBinding(RetirementReason.Replay);
            else if (intent is null && _sponsor is not null) return;
            ++_inputCancellations;
            try
            {
                await DrainInputAndSponsorAsync(seal: nativeReplay);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentPlayback(version)) return;
                // Cancellation cannot retract native Play/Resume already started by Replay.
                // Keep the gate until the real operation finishes; never join _ownedWork here.
                if (nativeReplay || intent is null) await operation();
                else await _sponsor!.OpaqueControlAsync(intent, operation);
                if (IsCurrentPlayback(version))
                {
                    if (nativeReplay) BindSponsorPlayback();
                    else _sponsor?.ReopenAdmission();
                }
            }
            finally { --_inputCancellations; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (intent is not null) _sponsor?.AbandonIntent(intent);
            if (entered) _initializationGate.Release();
        }
    }

    internal Task AttachHostAsync(IPlayerHost host, CancellationToken cancellationToken)
        => OwnWork(AttachHostCoreAsync(host, cancellationToken));

    private async Task AttachHostCoreAsync(IPlayerHost host, CancellationToken cancellationToken)
    {
        if (Window is not null && !ReferenceEquals(Window, host))
            throw new InvalidOperationException("A player session cannot change hosts.");
        ObjectDisposedException.ThrowIf(IsPlaybackClosing, this);
        Window = host; // Retain partial native creation on failure for real cleanup.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await host.ReadyAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        _hostReady = true;
        BindHost();
        await AwaitSponsorApplicationBindingAsync();
    }

    public void Close() => _ = ObserveCloseAsync(CloseAsync());

    private async void OnHostCloseRequested(object? sender, EventArgs args)
    {
        try { await CloseAsync(); }
        catch (Exception ex) { ReportCloseFailure(ex); }
    }

    private async Task ObserveCloseAsync(Task task)
    {
        // Close is never OwnWork; this observer cannot become a prerequisite of its own close drain.
#pragma warning disable VSTHRD003
        try { await task; }
#pragma warning restore VSTHRD003
        catch (Exception ex) { ReportCloseFailure(ex); }
    }

    private void ReportCloseFailure(Exception ex)
    {
        logger.LogError(ex, "Player cleanup failed; host ownership retained.");
        ErrorMessage = ex.Message;
        this.Get<AppViewModel>().ShowTipCommand.Execute((ex.Message, Richasy.WinUIKernel.Share.Base.InfoType.Error));
    }

    // Cache before running cleanup: recursive/parallel close callers join the actual operation.
    public Task CloseAsync()
        => _closeTask ?? CloseAsync(IsEmbedded ? CancelEmbeddedOpeningForClose : null);

    // Resolve/capture only after publication, so even a capture failure has the same real close owner.
    private void CancelEmbeddedOpeningForClose()
        => this.Get<AppViewModel>().CaptureEmbeddedOpeningCancellation(this)?.Invoke();

    internal Task CloseAsync(Action? cancelOpening)
    {
        if (_closeTask is not null) return _closeTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTask = completion.Task;
        _isClosing = true;
        StartSponsorApplicationCleanup();
        _closeWorker = CompleteCloseAsync(completion, cancelOpening);
        return _closeTask;
    }

    private void CancelOpeningForClose(Action? cancelOpening)
    {
        try { cancelOpening?.Invoke(); }
        catch (Exception ex) { _closeCancellationErrors.Add(ex); }
    }

    // These reservations represent only the active synchronous Cancel scope, not the close itself.
    // Reentrant close joins them through the existing owned-work drain; callers never await close here.
    internal TaskCompletionSource BeginSynchronousCancellationScope()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownedWork.Add(completion.Task);
        return completion;
    }

    internal void CompleteSynchronousCancellationScope(TaskCompletionSource completion)
    {
        completion.SetResult();
        _ownedWork.Remove(completion.Task);
    }

    internal void RetainOpeningCancellationFailure(Exception error)
    {
        if (_isClosing) _closeCancellationErrors.Add(error);
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion, Action? cancelOpening)
    {
        await Task.Yield(); // Publish the actual close worker before any cleanup callbacks.
        try
        {
            await AwaitSponsorApplicationCleanupAsync();
            RetireSponsorBinding(RetirementReason.Closing);
            var media = _mediaCancellation;
            CancelOpeningForClose(cancelOpening);
            // Keep synchronous callback completion, but one throwing source cannot skip the other.
#pragma warning disable VSTHRD103
            try { _lifetime.Cancel(); }
#pragma warning restore VSTHRD103
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
#pragma warning disable VSTHRD103
            try { media?.Cancel(); }
#pragma warning restore VSTHRD103
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
            try { RetainConnectorWork(); }
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
            try { Connector?.InvalidateRequests(); }
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
            try { _playerOverlay?.CancelPendingInput(); }
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
            try { _activeDanmakuDialog?.Hide(); }
            catch (Exception ex) { _closeCancellationErrors.Add(ex); }
            await DisposeCoreAsync();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            // Publish the primary failure even if reporting a secondary callback error fails.
            completion.SetException(ex);
            foreach (var cancellationError in _closeCancellationErrors)
                logger.LogWarning(cancellationError, "Cancellation/invalidation failed while retaining the player owner.");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    internal Task InitializeAsync(MediaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (IsPlaybackClosing) return Task.CompletedTask;
        // Cancel callbacks run once; a later Cancel no-op must not turn a failed transition into reuse.
        if (_mediaCancellationError is { } failure) return OwnWork(Task.FromException(failure));
        RetireSponsorBinding(RetirementReason.Replacement);
        var previous = _mediaCancellation;
        // A callback may reenter CloseAsync before Cancel reports its error. Reserve this real
        // synchronous cancellation scope in the existing drain set, so close cannot release early.
        var cancellation = BeginSynchronousCancellationScope();
        try
        {
            // On callback failure retain the exact old owner/source, without generation handoff.
#pragma warning disable VSTHRD103
            try { previous?.Cancel(); }
#pragma warning restore VSTHRD103
            catch (Exception ex)
            {
                _mediaCancellationError = ex;
                if (_isClosing) _closeCancellationErrors.Add(ex);
                try { RetainConnectorWork(); Connector?.InvalidateRequests(); }
                catch (Exception invalidationError) { logger.LogWarning(invalidationError, "Old media invalidation failed after cancellation failure; ownership retained."); }
                return OwnWork(Task.FromException(ex));
            }
            if (IsPlaybackClosing) return Task.CompletedTask;
            RetainConnectorWork();
            Connector?.InvalidateRequests();
            if (previous is not null) _retiredMediaCancellations.Add(previous);
            var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _mediaCancellation = request;
            return OwnWork(InitializeCoreAsync(snapshot, request));
        }
        finally
        {
            CompleteSynchronousCancellationScope(cancellation);
        }
    }

    private async Task InitializeCoreAsync(MediaSnapshot snapshot, CancellationTokenSource request)
    {
        var entered = false;
        try
        {
            await _initializationGate.WaitAsync(request.Token);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            IsConnecting = true;
            RetireSponsorBinding(RetirementReason.Replacement);
            await DrainInputAndSponsorAsync(seal: true);
            request.Token.ThrowIfCancellationRequested();
            _activeMediaToken = request.Token;
            Interlocked.Increment(ref _mediaVersion);
            OnPropertyChanged(nameof(MediaGeneration));
            await InitializeMediaAsync(snapshot);
            request.Token.ThrowIfCancellationRequested();
            BindSponsorPlayback();
            await RefreshSponsorApplicationMetadataAsync();
            if (!IsEmbedded) _independentInitializationCompleted = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!IsEmbedded && !_independentInitializationCompleted) throw;
        }
        catch (Exception ex)
        {
            SafeFailureContext.TryWrite(FailureStage.PlaybackInitialize, ex, () => logger);
            if (!request.IsCancellationRequested) ReportPlaybackFailure(ex);
            // The opening caller closes only after this owned operation releases its gate.
            if (!IsEmbedded && !_independentInitializationCompleted) throw;
        }
        finally
        {
            if (entered)
            {
                IsConnecting = false;
                _initializationGate.Release();
            }
            // Caller-owned tokens remain registerable while retired connector work drains.
            // All current/retired CTSs are released only by successful session close.
        }
    }

    private void ReportPlaybackFailure(Exception ex)
    {
        logger.LogError(ex, "Playback transition failed.");
        if (!IsPlaybackClosing)
        {
            ErrorMessage = ex.Message;
            CheckBlackBackgroundVisible();
        }
    }
}
