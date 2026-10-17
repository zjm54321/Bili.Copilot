#nullable enable

using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Live.Application;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    private LiveApplicationBridge? _liveApplication;
    private SponsorRuntime? _sponsorApplicationRuntime;
    private ApplicationPlaybackOperations? _applicationPlayback;
    private Task<bool>? _playbackShutdown;
    private Task? _playbackShutdownWorker;
    private bool _playbackShutdownFailed;
    // Terminal reservation is separate from the permanently completed playback shutdown.
    private object? _playbackTerminationReservation;
    private Task _liveNotice = Task.CompletedTask;
    private Task? _liveNoticeWorker;
    private readonly HashSet<PlaybackObserver> _playbackObservers = [];

    // No constructor injection: Account/Navigation/App resolution must finish before this lazy join.
    private LiveApplicationBridge LiveApplication => _liveApplication ??= this.Get<LiveApplicationBridge>();
    private ApplicationPlaybackOperations ApplicationPlayback
        => _applicationPlayback ??= new ApplicationPlaybackOperations(CheckPlaybackAccess);

    public bool HasLiveWork => _liveApplication?.HasWork == true;
    internal bool IsPlaybackStopping => _playbackShutdown is not null || IsClosed;
    internal bool HasApplicationPlaybackKeepAlive => HasLiveWork || _applicationPlayback?.HasWork == true
        || _playbackObservers.Count != 0 || !_liveNotice.IsCompleted || _liveNoticeWorker is { IsCompleted: false }
        || _playbackShutdown is { IsCompleted: false } || _playbackShutdownFailed
        || _sponsorApplicationRuntime?.HasPendingCleanup == true
        || SponsorMainWindowExitEligible && _sponsorApplicationRuntime?.HasMainWindowExitBarrier == true
        || _playbackTerminationReservation is not null; // Pending/uncertain termination must keep exclusion.

    // Use the existing Classic/MainWindow keepalive seam, not another window registry. Other Live,
    // Classic, independent and pending-open owners retain their previous hide/keepalive policy.
    private bool SponsorMainWindowExitEligible => !HasEmbeddedWork && !HasLiveWork && !HasClassicOwnerWork
        && !Players.Any(player => !player.IsEmbedded) && _applicationPlayback?.HasWork != true;

    private void BindSponsorMainWindow(MainWindow window)
    {
        _sponsorApplicationRuntime ??= this.Get<SponsorRuntime>();
        _sponsorApplicationRuntime.BindMainWindow(window, () =>
            SponsorMainWindowExitEligible || IsPlaybackStopping ? App.Current.RequestExitAsync() : null);
    }

    /// <summary>Only the explicit Live user route; failures stay in-app and never open a browser.</summary>
    public Task OpenLiveAsync(LiveInformation information)
    {
        CheckPlaybackAccess();
        if (IsPlaybackStopping) return Task.CompletedTask;
        try
        {
            // Capture the old identity/generation/lifetime and real saved decode choice BEFORE any await.
            var bridge = LiveApplication;
            var account = bridge.CaptureLaunchAccount();
            var decode = ClassicSessionOptions.Capture(this.Get<ISettingsToolkit>()).DecodePreference;
            return OwnApplicationPlaybackAsync(token => bridge.OpenLiveAsync(information, decode, account, token));
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
                SafeFailureContext.TryWrite(FailureStage.LiveOpenDispatch, error, () => _logger);
            ReportLiveFailure();
            // T1-11: ReportLiveFailure publishes independently produced notice work; returning that notice does not make its producer await shutdown.
            #pragma warning disable VSTHRD003
            return _liveNotice;
            #pragma warning restore VSTHRD003
        }
    }

    private Task OwnApplicationPlaybackAsync(Func<CancellationToken, Task> action)
    {
        CheckPlaybackAccess();
        if (IsPlaybackStopping) return Task.CompletedTask;
        var actual = ApplicationPlayback.RunAsync(action);
        var observer = new PlaybackObserver();
        _playbackObservers.Add(observer); // Even error-tip continuations remain owned until shutdown.
        observer.Worker = ObservePlaybackAsync(actual, observer);
        // T1-12: PlaybackObserver.Done is published before callbacks; the identical retained completion remains owned through shutdown.
        #pragma warning disable VSTHRD003
        return observer.Done.Task;
        #pragma warning restore VSTHRD003
    }

    private async Task ObservePlaybackAsync(Task actual, PlaybackObserver observer)
    {
        try {
            // T1-13: This observer joins the independently started playback operation; the operation does not await its observer.
            #pragma warning disable VSTHRD003
            await actual;
            #pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SafeFailureContext.TryWrite(FailureStage.ApplicationPlaybackObserve, error, () => _logger);
            ReportLiveFailure();
        }
        finally
        {
            // No caller can receive a raw media/credential exception from this application join.
            _playbackObservers.Remove(observer);
            observer.Done.TrySetResult();
        }
    }

    /// <summary>Registry callback: fixed Chinese text only, one bounded actual tip task.</summary>
    internal void ReportLiveFailure()
    {
        if (!_liveNotice.IsCompleted) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _liveNotice = done.Task;
        try
        {
            var dispatcher = this.Get<DispatcherQueue>();
            if (dispatcher.HasThreadAccess) _liveNoticeWorker = ShowLiveFailureAsync(done);
            else if (!dispatcher.TryEnqueue(() => _liveNoticeWorker = ShowLiveFailureAsync(done))) done.TrySetResult();
        }
        catch { done.TrySetResult(); }
    }

    private async Task ShowLiveFailureAsync(TaskCompletionSource done)
    {
        try
        {
            const string message = "直播或播放切换未完成。请检查当前窗口；如关闭未完成，应用会保留相关资源，不会强行退出。";
            var main = Windows.OfType<MainWindow>().FirstOrDefault();
            // Failure visibility only. A successful Live launch does not hide/resize unrelated windows.
            if (main is not null)
            {
                main.Activate();
                ActivatedWindow = main;
            }
            await ShowTipCommand.ExecuteAsync((message, InfoType.Error));
        }
        catch { /* No raw diagnostics and no unsafe exit if the existing tip surface is unavailable. */ }
        finally { done.TrySetResult(); }
    }

    internal void RestoreMainWindowAfterLiveRelease()
    {
        // Match the existing Classic last-window return affordance, without changing saved policy.
        try
        {
            if (!IsPlaybackStopping && !HasClassicWork && Players.Count == 0 && !IsMainWindowVisible())
                Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
        }
        catch { ReportLiveFailure(); }
    }

    /// <summary>
    /// Exit/restart seam for later application owners. Cached false means keep dispatcher/windows alive.
    /// N4 joins inside owner close, before native teardown; terminal actions still require true.
    /// </summary>
    public Task<bool> PreparePlaybackShutdownAsync()
    {
        CheckPlaybackAccess();
        if (_playbackShutdown is not null) return _playbackShutdown;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playbackShutdown = done.Task; // Blocks every app playback entry before callbacks/first await.
        var stop = ApplicationPlayback.StopAsync(CloseApplicationPlaybackOwnersAsync);
        _playbackShutdownWorker = CompletePlaybackShutdownAsync(done, stop);
        return done.Task;
    }

    private async Task CompletePlaybackShutdownAsync(TaskCompletionSource<bool> done, Task stop)
    {
        await Task.Yield();
        var failed = false;
        try {
            // T1-14: Playback stop starts before this shutdown join; real pending opens must return before late-owner cleanup.
            #pragma warning disable VSTHRD003
            await stop;
            #pragma warning restore VSTHRD003
        } catch { failed = true; }
        // Pending launch/consent/independent opens have really returned. Close any late adopted owner.
        // Live retains cached failed release; legacy Classic has its own failed-close retry policy.
        // This existing late-owner pass does not change either owner's release contract.
        if (!failed)
        {
            try { await CloseApplicationPlaybackOwnersAsync(); } catch { failed = true; }
        }
        foreach (var observer in _playbackObservers.ToArray())
        {
            // T1-15: Shutdown joins each prepublished playback-observer completion; the observed operations do not await shutdown.
            #pragma warning disable VSTHRD003
            await observer.Done.Task;
            #pragma warning restore VSTHRD003
            if (observer.Worker is not null)
                // T1-16: Retain and join the actual observer worker after Done so error-tip continuations cannot outlive shutdown.
                #pragma warning disable VSTHRD003
                await observer.Worker;
                #pragma warning restore VSTHRD003
        }
        if (HasLiveWork || HasEmbeddedWork || Players.Count != 0 || ClassicPlayers.Count != 0
            || _classicOpenCount != 0 || HasPendingClassicExternalCleanup || _offeringExternalCleanupRetry) failed = true;
        if (failed)
        {
            _playbackShutdownFailed = true;
            ReportLiveFailure();
        }
        // T1-17: Notice production is independent of shutdown; this joins already-owned UI feedback without synchronous blocking.
        #pragma warning disable VSTHRD003
        await _liveNotice;
        #pragma warning restore VSTHRD003
        if (_liveNoticeWorker is not null)
            // T1-18: The notice worker is retained separately from its published completion and must really finish before shutdown returns.
            #pragma warning disable VSTHRD003
            await _liveNoticeWorker;
            #pragma warning restore VSTHRD003
        if (!failed) IsClosed = true;
        done.TrySetResult(!failed);
    }

    internal object? TryClaimPlaybackTermination()
    {
        CheckPlaybackAccess();
        if (!IsClosed || _playbackTerminationReservation is not null || _playbackShutdownFailed) return null;
        return _playbackTerminationReservation = new object();
    }

    internal void ReleasePlaybackTermination(object reservation)
    {
        CheckPlaybackAccess();
        // A delayed failure from an older attempt cannot release a newer terminal owner.
        if (ReferenceEquals(_playbackTerminationReservation, reservation)) _playbackTerminationReservation = null;
        // Never reset IsClosed, _playbackShutdown or playback admission, and never dispose again.
    }

    internal async Task DrainPlaybackTerminationNoticesAsync()
    {
        CheckPlaybackAccess();
        // A rejected terminal attempt may have shown a new tip AFTER the cached playback shutdown.
        // Join that owned UI work on a later explicit terminal request, without replaying owner close.
        // T1-19: A terminal retry joins the latest independently produced notice, without replaying or waiting from its producer.
        #pragma warning disable VSTHRD003
        await _liveNotice;
        #pragma warning restore VSTHRD003
        if (_liveNoticeWorker is not null)
            // T1-20: Join the retained notice worker as well as its completion; notice production never awaits this termination drain.
            #pragma warning disable VSTHRD003
            await _liveNoticeWorker;
            #pragma warning restore VSTHRD003
    }

    private async Task CloseApplicationPlaybackOwnersAsync()
    {
        // Admission is already shut. Start global durability and every independent owner before
        // joining any branch; each Player.Close still owns its required Sponsor detach/native drain.
        var players = Players.ToArray();
        var classicPlayers = ClassicPlayers.ToArray();
        var tasks = new List<Task>
        {
            LiveRouteCoordinator.Invoke(CloseSponsorApplicationAsync),
            LiveRouteCoordinator.Invoke(CloseEmbeddedPlayerAsync),
        };
        if (_liveApplication is not null) tasks.Add(LiveRouteCoordinator.Invoke(_liveApplication.ShutdownAsync));
        tasks.AddRange(players.Select(player => LiveRouteCoordinator.Invoke(() => player.CloseAsync())));
        tasks.AddRange(classicPlayers.Select(window => LiveRouteCoordinator.Invoke(window.CloseAsync)));
        // MainWindow/APP dispatcher and recovery UI survive until ALL real closes and durability finish.
        await Task.WhenAll(tasks);
    }

    private async Task CloseSponsorApplicationAsync()
    {
        // Global save gates application/dispatcher exit, not already-detached child destruction.
        _sponsorApplicationRuntime ??= this.Get<SponsorRuntime>();
        var cleanup = await _sponsorApplicationRuntime.CloseAsync();
        if (cleanup.Status != ApplicationStatus.Success)
            cleanup = await _sponsorApplicationRuntime.WaitForCleanupAsync();
        if (cleanup.Status != ApplicationStatus.Success)
            throw new InvalidOperationException("Sponsor cleanup retained application ownership.");
    }

    private void CheckPlaybackAccess()
    {
        if (!this.Get<DispatcherQueue>().HasThreadAccess)
            throw new InvalidOperationException("播放和关闭操作需要应用界面线程。");
    }

    private sealed class PlaybackObserver
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Worker;
    }
}
