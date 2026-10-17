#nullable enable

using BiliCopilot.UI.Classic;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Live.Hosting;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Live.Application;

/// <summary>One DI singleton, one launcher; AppViewModel resolves it lazily after its own construction.</summary>
internal sealed class LiveApplicationBridge : ILivePlaybackAdmission, ILiveWindowRegistry
{
    private readonly AppViewModel _app;
    private readonly AccountViewModel _account;
    private readonly IBiliTokenResolver _tokens;
    private readonly DispatcherQueue _dispatcher;
    private readonly LiveLauncher _launcher;
    private readonly LiveRouteCoordinator _routes;
    private readonly LiveChatFactory _chatFactory = new();
    private readonly HashSet<LiveWindow> _owners = [];
    private LiveLaunchAccount? _openingAccount;
    private Task? _shutdown;
    private Task? _shutdownWorker;

    public LiveApplicationBridge(AppViewModel app, IPlayerService player, IBiliCookiesResolver cookies,
        IBiliTokenResolver tokens, AccountViewModel account, DispatcherQueue dispatcher)
    {
        _app = app;
        _account = account;
        _tokens = tokens;
        _dispatcher = dispatcher;
        _launcher = new LiveLauncher(player, cookies, tokens, account, dispatcher, this, this, stamp =>
        {
            CheckAccount(stamp);
            return _chatFactory.Create(cookies, tokens, CaptureAccount, dispatcher);
        }, GetMainWindow);
        _routes = new LiveRouteCoordinator(CheckAccess, CloseLiveOwnerAsync, app.CloseEmbeddedPlayerAsync);
    }

    internal bool HasWork => _owners.Count != 0 || _launcher.HasWork || _routes.HasWork
        || _chatFactory.HasWork || _shutdown is { IsCompleted: false };

    private MainWindow GetMainWindow()
    {
        CheckAccess();
        if (_app.IsPlaybackStopping) throw Failure();
        // Borrow the existing registered main host, never a new window or an arbitrary active one.
        return _app.Windows.OfType<MainWindow>().SingleOrDefault() ?? throw Failure();
    }

    internal LiveAccountStamp CaptureAccount()
    {
        CheckAccess();
        return new(_account.AccountId, _account.AccountGeneration, _account.AccountCancellationToken);
    }

    internal LiveLaunchAccount CaptureLaunchAccount()
        => new(CaptureAccount(), _tokens.GetToken() is not null, _account.MyProfile?.IsVip == true);

    internal async Task OpenLiveAsync(LiveInformation information, ClassicDecodePreference decode,
        LiveLaunchAccount capturedAccount, CancellationToken token)
    {
        CheckAccess();
        CheckLaunchAccount(capturedAccount);
        if (_shutdown is not null) throw Failure();
        using var accountScope = CancellationTokenSource.CreateLinkedTokenSource(token, capturedAccount.Account.Lifetime);
        await _routes.OpenLiveAsync(async routeToken =>
        {
            CheckLaunchAccount(capturedAccount);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(routeToken, capturedAccount.Account.Lifetime);
            _openingAccount = capturedAccount;
            try { await _launcher.OpenAsync(information, decode, linked.Token); }
            finally { _openingAccount = null; }
        }, accountScope.Token);
    }

    internal Task OpenVideoAsync(Func<CancellationToken, Task> open, CancellationToken token)
    {
        CheckAccess();
        if (_shutdown is not null) throw Failure();
        return _routes.OpenVideoAsync(open, token);
    }

    private async Task CloseLiveOwnerAsync()
    {
        // Starts launcher cancellation immediately, with no route lease held/acquired here.
        await _launcher.CloseCurrentAsync();
        // A completed release notification can fail before reaching the registry. Reconcile only
        // independently confirmed owners; never turn a failed/unconfirmed close into success.
        foreach (var owner in _owners.ToArray())
            if (owner.ReleaseConfirmed) Release(owner);
        _chatFactory.ForgetReleased();
        if (_owners.Count != 0 || _launcher.HasWork || _chatFactory.HasWork) throw Failure();
    }

    public async Task<IDisposable> EnterLiveAsync(CancellationToken cancellationToken)
    {
        CheckAccess();
        var stamp = _openingAccount ?? throw Failure();
        CheckLaunchAccount(stamp);
        var lease = await _routes.EnterLiveAsync(cancellationToken);
        try { CheckLaunchAccount(stamp); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public void Retain(LiveWindow window)
    {
        CheckAccess();
        // Managed retention precedes native allocation. NativeWindow is a BORROWED MainWindow;
        // its registry entry, ordinary activation handlers and lifetime belong exclusively to App.
        if (!window.ReleaseConfirmed) _owners.Add(window);
    }

    public void Release(LiveWindow window)
    {
        CheckAccess();
        if (!window.ReleaseConfirmed) { ReportSafeFailure(string.Empty); return; }
        var removed = _owners.Remove(window); // Idempotent bookkeeping, before fallible notifications.
        _chatFactory.ForgetReleased(); // Only forgets an independently confirmed handler release.
        if (!removed) return;
        try { if (!_routes.HasWork) _app.RestoreMainWindowAfterLiveRelease(); }
        catch { ReportSafeFailure(string.Empty); }
    }

    public void ReportSafeFailure(string message)
    {
        // Finished feedback failure owns no native resource and cannot poison future route release.
        try { _app.ReportLiveFailure(); }
        catch { return; } // End notification-only work; never forward source text or poison release.
    }

    internal Task ShutdownAsync()
    {
        CheckAccess();
        if (_shutdown is not null) return _shutdown;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = done.Task;
        var routeClose = LiveRouteCoordinator.Invoke(_routes.ShutdownAsync);
        _shutdownWorker = ShutdownCoreAsync(done, routeClose);
        return done.Task;
    }

    private async Task ShutdownCoreAsync(TaskCompletionSource done, Task routeClose)
    {
        await Task.Yield();
        // Start every retained partial owner before waiting for a different route/owner to finish.
        var ownerCloses = _owners.ToArray()
            .Select(owner => LiveRouteCoordinator.Invoke(() => CloseRetainedOwnerAsync(owner))).ToArray();
        var failed = false;
        try {
            // T1-05: Route and retained-owner closes are independently started; none acquires the held route lease.
            #pragma warning disable VSTHRD003
            await Task.WhenAll(ownerCloses.Append(routeClose));
            #pragma warning restore VSTHRD003
        } catch { failed = true; }
        // Pending route work has returned. Preserve the late-owner pass and cached failed closes.
        foreach (var owner in _owners.ToArray())
        {
            try { await CloseRetainedOwnerAsync(owner); } catch { failed = true; }
        }
        if (!failed)
        {
            try { await _launcher.DisposeAsync(); } catch { failed = true; }
        }
        _chatFactory.ForgetReleased();
        if (failed || _owners.Count != 0 || _launcher.HasWork || _chatFactory.HasWork)
            done.TrySetException(Failure());
        else done.TrySetResult();
    }

    private async Task CloseRetainedOwnerAsync(LiveWindow owner)
    {
        try { await owner.CloseAsync(); }
        catch when (owner.ReleaseConfirmed) { ReportSafeFailure(string.Empty); }
        if (!owner.ReleaseConfirmed) throw Failure();
        Release(owner); // Do not depend solely on a fallible Released subscriber chain.
    }

    private void CheckAccount(LiveAccountStamp stamp)
    {
        stamp.Lifetime.ThrowIfCancellationRequested();
        if (CaptureAccount() != stamp) throw new OperationCanceledException("账号已变化，未转移原直播会话。");
    }

    private void CheckLaunchAccount(LiveLaunchAccount captured)
    {
        CheckAccount(captured.Account);
        // Same actual checks as the frozen launcher. If eligibility changed while queued, do not
        // silently adopt its later value. The launcher reads these again immediately after the lease.
        if ((_tokens.GetToken() is not null) != captured.LoggedIn || (_account.MyProfile?.IsVip == true) != captured.Vip)
            throw new OperationCanceledException("账号播放资格已变化，未转移原直播请求。");
    }

    private void CheckAccess()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("直播播放操作需要应用界面线程。");
    }

    private static InvalidOperationException Failure() => new("直播关闭或窗口登记未完成，原所有者仍被保留。");
}

internal readonly record struct LiveLaunchAccount(LiveAccountStamp Account, bool LoggedIn, bool Vip);
