using BiliCopilot.UI.Forms;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Windowing;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal sealed partial class SponsorRuntime
{
    private bool closing;
    private Task<ApplicationResult>? closeAttempt;
    private Task<ApplicationResult>? coordinatorClose;
    private MainWindow? mainWindow;
    private Func<Task<bool>?>? requestWindowExit;
    private Task<bool>? windowExit;
    private bool nativeExitAuthorized;
    private readonly TaskCompletionSource<ApplicationResult> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ApplicationResult? LastCloseResult
    {
        get => lastCloseResult;
        private set
        {
            if (lastCloseResult == value) return;
            lastCloseResult = value;
            QueueCloseStateChanged();
        }
    }
    internal bool HasPendingCleanup => closing && !closed.Task.IsCompleted
        || players.Values.Any(p => p.Closing && !p.Released.Task.IsCompleted);
    internal bool HasMainWindowExitBarrier => mainWindow is not null && !nativeExitAuthorized;

    internal void BindMainWindow(MainWindow window, Func<Task<bool>?> requestExit)
    {
        dispatcher.CheckAccess();
        if (mainWindow is not null) throw new InvalidOperationException("Sponsor main owner is already registered.");
        mainWindow = window;
        requestWindowExit = requestExit;
        window.AppWindow.Closing += OnMainWindowClosing;
        window.Closed += OnMainWindowClosed;
    }

    private void OnMainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (nativeExitAuthorized || requestWindowExit?.Invoke() is not { } actual) return;
        windowExit = actual; // The existing App.RequestExitAsync task, not another shutdown driver.
        args.Cancel = true;
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (nativeExitAuthorized || requestWindowExit?.Invoke() is not { } actual) return;
        windowExit = actual;
        args.Handled = true;
        mainWindow?.Activate(); // The old handler may have hidden it via its Classic keepalive branch.
    }

    internal void AuthorizeNativeExit()
    {
        dispatcher.CheckAccess();
        if (!closed.Task.IsCompleted) throw new InvalidOperationException("Sponsor runtime has not completed cleanup.");
        nativeExitAuthorized = true; // App calls only after the existing terminal reservation succeeds.
        if (mainWindow is { } window)
        {
            window.AppWindow.Closing -= OnMainWindowClosing;
            window.Closed -= OnMainWindowClosed;
        }
    }

    internal Task<ApplicationResult> DetachPlayerAsync(PlayerViewModel player) => dispatcher.InvokeAsync(() =>
    {
        if (!players.TryGetValue(player, out var binding)) return Task.FromResult(new ApplicationResult(ApplicationStatus.Success));
        binding.Closing = true;
        QueueCloseStateChanged();
        if (binding.DetachTask is { IsCompleted: false }) return binding.DetachTask;
        // N4 StopAdmission runs synchronously before its own first await; keep its actual task.
        binding.NativeDetachTask = binding.Attachment is { } id
            && Coordinator.Snapshot.Players.Any(p => p.Attachment == id) ? Coordinator.DetachAsync(id) : null;
        return binding.DetachTask = DetachCoreAsync(binding);
    });

    private async Task<ApplicationResult> DetachCoreAsync(PlayerBinding binding)
    {
        await Task.Yield();
        try
        {
            if (binding.BindTask is { } bind) await bind;
            var id = binding.Attachment;
            var exists = id is not null && Coordinator.Snapshot.Players.Any(p => p.Attachment == id);
            var result = binding.NativeDetachTask is { } native ? await native
                : exists ? await Coordinator.DetachAsync(id!.Value) : new(ApplicationStatus.Success);
            binding.LastResult = result;
            if (result.Status != ApplicationStatus.Success) return result;
            return await ReleaseViewsAsync(binding);
        }
        catch { return binding.LastResult = new(ApplicationStatus.ControllerFailure, "SponsorCleanupOwnershipRetained"); }
    }

    internal Task<ApplicationResult> WaitForPlayerCleanupAsync(PlayerViewModel player) => dispatcher.InvokeAsync(() =>
        players.TryGetValue(player, out var binding) ? WaitForReleaseAsync(binding)
            : Task.FromResult(new ApplicationResult(ApplicationStatus.Success)));

    private static async Task<ApplicationResult> WaitForReleaseAsync(PlayerBinding binding)
    {
        // T5-02: binding.Released is published by actual cleanup; the release producer does not await this waiter.
        #pragma warning disable VSTHRD003
        var result = await binding.Released.Task;
        #pragma warning restore VSTHRD003
        if (binding.ReleaseTask is { } actual) result = await actual;
        return result;
    }

    private Task<ApplicationResult> ReleaseViewsAsync(PlayerBinding binding)
        => binding.ReleaseTask ??= ReleaseViewsCoreAsync(binding);

    private async Task<ApplicationResult> ReleaseViewsCoreAsync(PlayerBinding binding)
    {
        await Task.Yield();
        if (binding.Attachment is { } id && Coordinator.Snapshot.Players.Any(p => p.Attachment == id))
            return new(ApplicationStatus.NeedsReconciliation, "SponsorCleanupOwnershipRetained");
        if (binding.MetadataTask is { } metadata) await metadata;
        await binding.Dispatcher.ReadAsync(() =>
        {
            if (binding.RetirementHandler is { } retired)
            {
                binding.Port.SponsorNativeRetirementCompleted -= retired;
                binding.RetirementHandler = null;
            }
            binding.Overlay.UnbindSponsorViews();
            binding.Player.Danmaku.UnbindSponsorCategories();
            binding.Playback?.Unbind();
            binding.Editor?.Unbind();
            return true;
        });
        players.Remove(binding.Player);
        var result = new ApplicationResult(ApplicationStatus.Success);
        binding.Released.TrySetResult(result);
        QueueCloseStateChanged();
        return result;
    }

    internal Task<ApplicationResult> CloseAsync() => dispatcher.InvokeAsync(() =>
    {
        if (closed.Task.IsCompleted) return closeAttempt ?? closed.Task;
        closing = true; // Stops bind/page/ordinary runtime admission before callbacks or first await.
        StopVideoListAdmission(); // Cancel list admission now; actual SDK/APP tasks remain owned.
        foreach (var binding in players.Values) binding.Closing = true;
        if (closeAttempt is { IsCompleted: false }) return closeAttempt;
        coordinatorClose = Coordinator.CloseAsync(); // Synchronous global admission closure; no second driver.
        closeAttempt = CloseCoreAsync();
        closeStateObserver = NotifyAfterCloseTaskAsync(closeAttempt);
        QueueCloseStateChanged();
        return closeAttempt;
    });

    internal async Task<ApplicationResult> WaitForCleanupAsync()
    {
        // T5-03: closed is a retained cleanup completion published after real release; this waiter is not part of the close producer.
        #pragma warning disable VSTHRD003
        var result = await closed.Task;
        #pragma warning restore VSTHRD003
        if (closeAttempt is { } actual) result = await actual;
        return result;
    }

    private async Task<ApplicationResult> CloseCoreAsync()
    {
        await Task.Yield();
        try
        {
            UpdateProjection(Coordinator.Snapshot);
            if (load is { } loading) await loading;
            foreach (var binding in players.Values.ToArray())
                if (binding.BindTask is { } bind) await bind;
            // T5-04: The coordinator close was started and retained independently; registered UI runners do not await this Runtime.Close consumer.
            #pragma warning disable VSTHRD003
            var result = await coordinatorClose!;
            #pragma warning restore VSTHRD003
            // An already-running per-player detach can remove an ID captured by N4 global Close.
            // Its exact Closed result is not success. After those tasks finish and ONLY when no
            // attachment remains, ask N4 once for the final global/durability verdict (no native retry).
            if (result.Status == ApplicationStatus.Closed && Coordinator.Snapshot.Players.IsEmpty)
            {
                foreach (var binding in players.Values.ToArray())
                    if (binding.NativeDetachTask is { } detach) await detach;
                result = await Coordinator.CloseAsync();
            }
            LastCloseResult = result;
            foreach (var binding in players.Values.ToArray())
            {
                if (binding.Attachment is { } id && Coordinator.Snapshot.Players.Any(p => p.Attachment == id)) continue;
                var released = await ReleaseViewsAsync(binding);
                if (released.Status != ApplicationStatus.Success) return LastCloseResult = released;
            }
            if (result.Status != ApplicationStatus.Success || players.Count != 0) return result;
            // Actual picker/commit and bounded CPU work must finish before any owner is discarded.
            await backups.CloseAsync();
            await DrainUiWorkAsync();
            if (videoListClose is { } listClose) await listClose; // Preserve even a completed failed adapter close verdict.
            await ContentProjection.DisposeAsync();
            if (settingsPage is { } page) UnbindSettingsPage(page);
            if (settingsBound) { settings.Unbind(); settingsBound = false; }
            Coordinator.Updated -= OnUpdated;
            closed.TrySetResult(result);
            QueueCloseStateChanged();
            return result;
        }
        catch { return LastCloseResult = new(ApplicationStatus.ControllerFailure, "SponsorRuntimeCleanupRetained"); }
    }
}
