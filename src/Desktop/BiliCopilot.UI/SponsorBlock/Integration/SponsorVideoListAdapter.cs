using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.ViewModels.Components;
using Richasy.BiliKernel.Bili.Media;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>One app-owned visible-list adapter. Only an explicit caller pass can create read work.
/// No renderer/layout event, completion, settings notification or cache miss schedules another pass.</summary>
internal sealed partial class SponsorVideoListAdapter
{
    private readonly SponsorApplicationCoordinator _app;
    private readonly IPlayerService _player;
    private readonly AccountViewModel _account;
    private readonly SponsorDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private SponsorVideoListScope? _scope;
    private SponsorVideoListAccount? _scopeAccount;
    private CancellationTokenRegistration _scopeRegistration, _accountRegistration;
    private Task _registrationDrain = Task.CompletedTask;
    private CancellationTokenSource? _batchStop;
    private Task? _batchCancellation;
    private Task? _worker, _close;
    private Work? _pending, _last;
    private SponsorVideoListProjection? _projection;
    private long _activation, _serial, _maxPass = -1, _preferenceRevision;
    private bool _closed, _cleanupFailed;
    private int _notificationFailed;
    private bool _settingsAvailable, _networkEnabled, _filterEnabled;
    private ImmutableArray<string> _categories = [];
    private ImmutableArray<LocalSegmentExclusion> _exclusions = [];

    internal SponsorVideoListAdapter(SponsorApplicationCoordinator app, IPlayerService player,
        AccountViewModel account, SponsorDispatcher dispatcher, Func<Task, Task> track)
    {
        _app = app; _player = player; _account = account; _dispatcher = dispatcher; _track = track;
        _dispatcher.CheckAccess();
        UpdatePreferences(app.Snapshot);
        _app.Updated += UpdatePreferences;
        _account.AccountChanged += OnAccountChanged;
    }

    internal event Action? Changed;
    internal bool ObserverFailureObserved { get; private set; }
    internal SponsorVideoListProjection? Current
    {
        get
        {
            _dispatcher.CheckAccess();
            if (_projection is not { } value) return null;
            if (!ContextMatches(value.Snapshot)) return Original(value.Snapshot,
                _closed ? SponsorVideoListStatus.Closed : SponsorVideoListStatus.Inactive);
            if (value.Status == SponsorVideoListStatus.Projected && (_last is not { } latest
                || !ReferenceEquals(latest.Snapshot, value.Snapshot) || !latest.Rows.All(RowIdentityMatches)))
                return _projection = Original(value.Snapshot, SponsorVideoListStatus.Original);
            return value;
        }
    }

    internal SponsorVideoListContext ReadContext()
    {
        _dispatcher.CheckAccess();
        var account = CaptureAccount();
        return new(account, _preferenceRevision, SettingsEnabled && !account.Lifetime.IsCancellationRequested);
    }

    internal void Activate(SponsorVideoListScope scope)
    {
        CheckCaller();
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Id == Guid.Empty || scope.Generation < 0) throw new ArgumentException("A real list scope is required.", nameof(scope));
        if (_closed || _cleanupFailed) return;
        var account = CaptureAccount();
        if (_scope == scope && _scopeAccount == account) return;
        RetireScope(notify: false);
        if (scope.Lifetime.IsCancellationRequested || account.Lifetime.IsCancellationRequested) { Notify(); return; }
        _scope = scope; _scopeAccount = account; _maxPass = -1;
        var activation = _activation;
        _scopeRegistration = scope.Lifetime.Register(() => QueueLifetimeInvalidation(activation));
        _accountRegistration = account.Lifetime.Register(() => QueueLifetimeInvalidation(activation));
        _projection = null;
        Notify(); // Restore the caller's own raw source. Activation itself never requests metadata.
    }

    internal Task RequestAsync(SponsorVideoListSnapshot snapshot)
    {
        CheckCaller();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ContextMatches(snapshot)) return Task.CompletedTask;
        if (snapshot.PassRevision < 0 || snapshot.Rows.IsDefault || snapshot.Rows.Length > 128
            || snapshot.Rows.Any(row => row is null || row.Original is null || row.Video is null))
        {
            InvalidatePass();
            _projection = null;
            Notify();
            throw new ArgumentException("A bounded, explicit visible-row pass is required.", nameof(snapshot));
        }
        if (snapshot.PassRevision < _maxPass) return Task.CompletedTask;
        var rows = snapshot.Rows.Select(row => new CapturedRow(row, CaptureKey(row.Video))).ToImmutableArray();
        if (_last is { } previous && snapshot.PassRevision == previous.Snapshot.PassRevision
            && snapshot.PreferenceRevision == previous.Snapshot.PreferenceRevision)
        {
            if (SameRows(previous.Rows, rows)) return _worker ?? Task.CompletedTask;
            // A layout/refill cannot reuse an already-consumed pass with a different set of rows.
            InvalidatePass();
            _projection = null;
            Notify();
            return Task.CompletedTask;
        }
        _maxPass = snapshot.PassRevision;
        CancelBatch();
        var work = new Work(snapshot, rows, ++_serial);
        _last = _pending = work; // One latest pending input, not one Task/CTS per card or pass.
        _projection = Original(snapshot, SponsorVideoListStatus.Pending);
        if (_worker is null || _worker.IsCompleted)
        {
            _worker = RunAsync(); // First action is Yield: publish/track BEFORE any SDK/APP read.
            _ = _track(_worker);
        }
        var actual = _worker;
        Notify();
        return actual;
    }

    internal void Deactivate(Guid scopeId, long generation)
    {
        CheckCaller();
        if (_scope is { } scope && scope.Id == scopeId && scope.Generation == generation) RetireScope();
    }

    internal Task CloseAsync()
    {
        CheckCaller();
        if (_close is not null) return _close;
        _closed = true;
        _close = FinishCloseAsync(); // Published before cancellation or subscriber reentrancy.
        _ = _track(_close);
        _app.Updated -= UpdatePreferences;
        _account.AccountChanged -= OnAccountChanged;
        RetireScope();
        return _close;
    }

    private async Task FinishCloseAsync()
    {
        await Task.Yield();
        try { if (_worker is { } actual) await actual; }
        finally { var registrationDrain = _registrationDrain; await registrationDrain; }
        if (_cleanupFailed) throw new InvalidOperationException("Video list cleanup is incomplete; ownership is retained.");
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        while (_pending is { } work)
        {
            _pending = null;
            if (!IsCurrent(work)) continue;
            SponsorVideoListProjection result;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(work.Snapshot.Scope.Lifetime, work.Snapshot.Account.Lifetime);
            _batchStop = stop;
            _batchCancellation = null;
            try { result = await EvaluateAsync(work, stop.Token); }
            catch (OperationCanceledException) { result = Original(work.Snapshot, SponsorVideoListStatus.Inactive); }
            catch (Exception) { result = Original(work.Snapshot, SponsorVideoListStatus.Unavailable); }
            finally
            {
                // Evaluate awaited the actual SDK task and the tokenless APP read. Cancellation
                // only ends admission; it is never used as a substitute for their completion.
                try { await (_batchCancellation ??= stop.CancelAsync()); }
                catch (Exception) { _cleanupFailed = true; }
                finally
                {
                    stop.Dispose();
                    _batchStop = null; _batchCancellation = null;
                }
            }
            if (IsCurrent(work)) { _projection = result; Notify(); }
            else if (_cleanupFailed) { RestoreProjection(); Notify(); }
        }
    }

    private bool IsCurrent(Work work) => work.Serial == _serial && ContextMatches(work.Snapshot);
    private bool SettingsEnabled => !_closed && !_cleanupFailed && Volatile.Read(ref _notificationFailed) == 0
        && _settingsAvailable && _networkEnabled && _filterEnabled && !_categories.IsDefaultOrEmpty;
    private bool ContextMatches(SponsorVideoListSnapshot snapshot) => SettingsEnabled
        && _scope == snapshot.Scope && _scopeAccount == snapshot.Account && CaptureAccount() == snapshot.Account
        && !snapshot.Scope.Lifetime.IsCancellationRequested && !snapshot.Account.Lifetime.IsCancellationRequested
        && snapshot.PreferenceRevision == _preferenceRevision;
    private SponsorVideoListAccount CaptureAccount() => new(_account.AccountId, _account.AccountGeneration, _account.AccountCancellationToken);

    private void UpdatePreferences(SponsorApplicationView view)
    {
        _dispatcher.CheckAccess();
        var available = !view.Closed && view.StoreVersion is not null
            && (view.Persistence is PersistenceStatus.Success or PersistenceStatus.Missing)
            && (view.AuxiliaryPersistence is null or PersistenceStatus.Success);
        var filters = view.Auxiliary.Filters;
        var categories = filters.ContentCategories.IsDefault ? ImmutableArray<string>.Empty : filters.ContentCategories;
        var exclusions = view.Settings.Exclusions.IsDefault ? ImmutableArray<LocalSegmentExclusion>.Empty : view.Settings.Exclusions;
        if (_settingsAvailable == available && _networkEnabled == view.Settings.NetworkEnabled
            && _filterEnabled == filters.ContentEnabled && _categories.SequenceEqual(categories, StringComparer.Ordinal)
            && _exclusions.SequenceEqual(exclusions)) return;
        _settingsAvailable = available; _networkEnabled = view.Settings.NetworkEnabled;
        _filterEnabled = filters.ContentEnabled; _categories = categories; _exclusions = exclusions;
        ++_preferenceRevision;
        InvalidatePass(); // No automatic re-read even when a saved preference enables the feature.
        RestoreProjection();
        Notify();
    }

    private void OnAccountChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _closed)) return;
        if (_dispatcher.HasAccess) RetireScope();
        else QueueLifetimeInvalidation(Volatile.Read(ref _activation));
    }
    private void QueueLifetimeInvalidation(long activation)
    {
        try { _dispatcher.Post(() => { if (!_closed && activation == _activation) RetireScope(); }); }
        catch (Exception) { Interlocked.Exchange(ref _notificationFailed, 1); }
    }
    private void RetireScope(bool notify = true)
    {
        ++_activation;
        InvalidatePass();
        _scope = null; _scopeAccount = null;
        _last = null;
        _descriptors.Clear(); _descriptorOrder.Clear();
        var oldScope = _scopeRegistration; var oldAccount = _accountRegistration;
        _scopeRegistration = _accountRegistration = default;
        _registrationDrain = DrainRegistrationsAsync(_registrationDrain, oldScope, oldAccount);
        _ = _track(_registrationDrain);
        RestoreProjection();
        if (notify) Notify();
    }
    private static async Task DrainRegistrationsAsync(Task previous,
        CancellationTokenRegistration scope, CancellationTokenRegistration account)
    {
        try { var priorDrain = previous; await priorDrain; }
        finally { try { await scope.DisposeAsync(); } finally { await account.DisposeAsync(); } }
    }
    private void InvalidatePass()
    {
        ++_serial;
        _pending = null;
        CancelBatch();
    }
    private void CancelBatch()
    {
        if (_batchStop is { } stop) _batchCancellation ??= stop.CancelAsync();
    }
    private void RestoreProjection()
    {
        if (_projection is { } value)
            _projection = Original(value.Snapshot, _closed ? SponsorVideoListStatus.Closed : SponsorVideoListStatus.Original);
    }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { ObserverFailureObserved = true; }
    }
    private void CheckCaller()
    {
        _dispatcher.CheckAccess();
        if (SynchronizationContext.Current is null) throw new InvalidOperationException("Video list work requires the APP UI context.");
    }
    private static bool SameRows(ImmutableArray<CapturedRow> a, ImmutableArray<CapturedRow> b) => a.Length == b.Length
        && a.Zip(b).All(pair => ReferenceEquals(pair.First.Row.Original, pair.Second.Row.Original)
            && ReferenceEquals(pair.First.Row.Video, pair.Second.Row.Video) && pair.First.Key == pair.Second.Key);
    private static SponsorVideoListProjection Original(SponsorVideoListSnapshot snapshot, SponsorVideoListStatus status)
        => new(snapshot, snapshot.Rows.Select(row => row.Original).ToImmutableArray(), status);
    private readonly record struct DescriptorKey(string? Id, string? BvId);
    private sealed record CapturedRow(SponsorVideoListRow Row, DescriptorKey Key);
    private sealed record Work(SponsorVideoListSnapshot Snapshot, ImmutableArray<CapturedRow> Rows, long Serial);
}
