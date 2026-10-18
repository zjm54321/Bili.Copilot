// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;
using Microsoft.UI.Dispatching;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using WinUIEx;

namespace BiliCopilot.UI.SponsorBlock.Platform;

/// <summary>
/// Window-scoped, explicit-user-choice transport for opaque, secret-free backup bytes.
/// Construct on a live owner's UI thread before its Closed event can occur. The host
/// must await every operation and CloseAsync before shutting down the owner's queue.
/// This class neither registers itself nor owns/closes the window.
/// </summary>
internal sealed class WindowsSponsorBackupFilePort : ISponsorBackupFilePort, IAsyncDisposable
{
    internal const int MaximumBackupBytes = SponsorBackupStreamIO.MaximumBackupBytes;
    private static readonly string[] JsonFileExtensions = [".json"];

    private readonly Window _owner;
    private readonly DispatcherQueue _queue;
    private readonly nint _ownerHwnd;

    // Admission and event subscription are confined to _queue. The stored task is
    // the actual operation runner, not a cancelable waiter or an async-void callback.
    private Task? _activeOperation;
    private bool _stopping;
    private bool _detached;

    internal WindowsSponsorBackupFilePort(Window owner)
    {
        _owner = owner;
        _queue = owner.DispatcherQueue;
        EnsureOwnerThread();
        _ownerHwnd = owner.GetWindowHandle();
        if (_ownerHwnd == 0)
        {
            throw new ArgumentException("A live owning window is required.", nameof(owner));
        }

        _owner.Closed += OnOwnerClosed;
    }

    public Task<bool> SaveBackupAsync(byte[] secretFreeBackup, string suggestedFileName)
    {
        EnsureAdmission();
        if (secretFreeBackup.Length > MaximumBackupBytes)
        {
            throw new InvalidDataException("The backup exceeds the 4 MiB limit.");
        }

        // Snapshot only after admission succeeds, before returning to the caller.
        // No JSON parsing, identity discovery, or export policy belongs in this port.
        var snapshot = (byte[])secretFreeBackup.Clone();
        var safeName = CleanSuggestedFileName(suggestedFileName);
        return StartOperationAsync(() => SaveCoreAsync(snapshot, safeName));
    }

    public Task<byte[]?> PickBackupAsync()
    {
        EnsureAdmission();
        return StartOperationAsync(PickCoreAsync);
    }

    /// <summary>
    /// Call on the owner's UI thread. Stops admission immediately and returns the
    /// retained actual operation task (including picker, I/O, flush and commit).
    /// A pending OS picker is NOT canceled; this task can remain pending indefinitely.
    /// Failures are not converted into successful shutdown or successful saves.
    /// Repeated calls return the same drain task. Keep the UI queue pumping while awaiting.
    /// </summary>
    internal Task CloseAsync()
    {
        EnsureOwnerThread();
        _stopping = true;
        if (!_detached)
        {
            _owner.Closed -= OnOwnerClosed;
            _detached = true;
        }

        return _activeOperation ?? Task.CompletedTask;
    }

    internal ValueTask DisposeAsync() => new(CloseAsync());

    ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync();

    private void OnOwnerClosed(object sender, WindowEventArgs args)
    {
        // No async disposal from an event, no invented picker cancellation.
        _stopping = true;
    }

    private void EnsureOwnerThread()
    {
        if (!_queue.HasThreadAccess)
        {
            throw new InvalidOperationException("Backup file operations require the owning UI thread.");
        }
    }

    private void EnsureNotStopping()
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_stopping, typeof(WindowsSponsorBackupFilePort));
    }

    private void EnsureAdmission()
    {
        EnsureNotStopping();
        if (_activeOperation is { IsCompleted: false })
        {
            // No semaphore waiters or unbounded pending operations.
            throw new InvalidOperationException("A backup file operation is already active.");
        }
    }

    private Task<T> StartOperationAsync<T>(Func<Task<T>> operation)
    {
        // These callers have checked admission on the same UI thread. A start gate
        // ensures the actual runner is published before any queue/native callbacks.
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualOperation = RunOperationAsync(start.Task, operation);
        _activeOperation = actualOperation;
        start.SetResult(true);
        return actualOperation;
    }

    private async Task<T> RunOperationAsync<T>(Task start, Func<Task<T>> operation)
    {
        // T5-08: The picker start signal follows runner publication; its independent producer does not wait for this operation consumer.
        #pragma warning disable VSTHRD003
        await start.ConfigureAwait(false);
        #pragma warning restore VSTHRD003

        // Dispatch explicitly to the bound owner queue, not a guessed global queue
        // or an assumed SynchronizationContext. The callback is synchronous and only
        // hands off a task; this runner awaits that real task all the way to completion.
        var launched = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
        {
            try
            {
                EnsureNotStopping();
                launched.SetResult(operation());
            }
            catch (Exception error)
            {
                launched.SetException(error);
            }
        }))
        {
            throw new InvalidOperationException("The owning UI queue is unavailable.");
        }

        var actualTask = await launched.Task.ConfigureAwait(false);
        return await actualTask.ConfigureAwait(false);
    }

    private async Task<bool> SaveCoreAsync(byte[] snapshot, string safeName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = safeName,
            DefaultFileExtension = ".json",
        };
        picker.FileTypeChoices.Add("JSON", JsonFileExtensions);
        InitializeWithWindow.Initialize(picker, _ownerHwnd);
        EnsureNotStopping(); // Interop may reenter the owner's Closed handler.

        StorageFile? file;
        try
        {
            var pickerTask = picker.PickSaveFileAsync().AsTask();
            file = await pickerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only native picker cancellation maps to false; I/O cancellation/faults
            // below must propagate, never masquerade as a successful/no-op write.
            return false;
        }
        finally
        {
            GC.KeepAlive(picker);
        }

        if (file is null)
        {
            return false;
        }

        // Already launched choices remain owned even if the owner closes meanwhile.
        // No destination truncation, path-based replacement, or nontransactional fallback.
        var transactionTask = file.OpenTransactedWriteAsync().AsTask();
        using var transaction = await transactionTask.ConfigureAwait(false);
        var stagingStream = transaction.Stream;
        stagingStream.Size = 0;
        stagingStream.Seek(0);
        using (var writer = new DataWriter(stagingStream))
        {
            try
            {
                writer.WriteBytes(snapshot);
                var writeTask = writer.StoreAsync().AsTask();
                var written = await writeTask.ConfigureAwait(false);
                if (written != (uint)snapshot.Length)
                {
                    throw new IOException("The backup write was incomplete.");
                }
            }
            finally
            {
                // The transaction, not the writer, owns the staging stream.
                writer.DetachStream();
            }
        }

        var flushTask = stagingStream.FlushAsync().AsTask();
        if (!await flushTask.ConfigureAwait(false))
        {
            throw new IOException("The backup transaction could not be flushed.");
        }

        var commitTask = transaction.CommitAsync().AsTask();
        await commitTask.ConfigureAwait(false);
        return true;
    }

    private async Task<byte[]?> PickCoreAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, _ownerHwnd);
        EnsureNotStopping();

        StorageFile? file;
        try
        {
            var pickerTask = picker.PickSingleFileAsync().AsTask();
            file = await pickerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            GC.KeepAlive(picker);
        }

        if (file is null)
        {
            return null;
        }

        var openTask = file.OpenReadAsync().AsTask();
        using var nativeStream = await openTask.ConfigureAwait(false);
        // Disable adapter read-ahead: at most limit + one probe byte is consumed.
        using var stream = nativeStream.AsStreamForRead(bufferSize: 0);
        return await SponsorBackupStreamIO.ReadBoundedAsync(stream).ConfigureAwait(false);
    }

    internal static string CleanSuggestedFileName(string? suggestedFileName)
    {
        // String-only basename cleanup, independent of OS path APIs. No path is read.
        var name = suggestedFileName ?? string.Empty;
        var separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        name = name[(separator + 1)..].Trim(' ', '.');
        var extension = name.LastIndexOf('.');
        if (extension > 0)
        {
            name = name[..extension];
        }

        var stem = new StringBuilder(80);
        foreach (var character in name)
        {
            if (stem.Length == 80)
            {
                break;
            }

            var allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-' or '_' or ' ';
            stem.Append(allowed ? character : '_');
        }

        name = stem.ToString().Trim(' ', '_');
        if (name.Length == 0)
        {
            name = "sponsor-backup";
        }

        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4
                && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] is >= '1' and <= '9'))
        {
            name = "_" + name;
        }

        return name + ".json";
    }
}
