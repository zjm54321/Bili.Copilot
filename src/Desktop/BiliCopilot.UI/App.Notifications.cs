// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.Notifications;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Richasy.BiliKernel.Bili.Authorization;
using System.ComponentModel;
using System.Globalization;

namespace BiliCopilot.UI;

public partial class App
{
    private readonly NotificationActivationGate _notifications = new();
    private bool _initialActivationSelected;
    private volatile bool _isPrimaryInstance;
    private Task? _activationRedirect;
    private AccountViewModel? _notificationAccount;
    private IBiliTokenResolver? _notificationTokens;
    private AppViewModel? _notificationApp;
    private object? _notificationRoot;
    private Func<bool>? _notificationRootIsCurrent;

    private void InitializeNotificationRouting()
    {
        _notificationTokens = this.Get<IBiliTokenResolver>();
        var tokenAccountId = NotificationAccountId();
        if (tokenAccountId is null)
        {
            _notifications.Reject(); // Ordinary StartupWindow/login restart, with no retained intent.
            return;
        }
        _notificationAccount = this.Get<AccountViewModel>();
        _notificationApp = this.Get<AppViewModel>();
        _notifications.SelectPrimary(tokenAccountId, _notificationAccount.AccountId, _notificationAccount.AccountGeneration);
        _notificationAccount.AccountChanged += OnNotificationAccountChanged;
        _notificationApp.PropertyChanged += OnNotificationAppChanged;
    }

    private string? NotificationAccountId()
    {
        var token = _notificationTokens?.GetToken();
        return token is null || token.UserId <= 0 ? null : token.UserId.ToString(CultureInfo.InvariantCulture);
    }

    private void CaptureActivation(AppActivationArguments arguments, bool initial = false)
    {
        if (!_isPrimaryInstance) return;
        // Keep normal redirected Launch activation. Startup payloads themselves are captured
        // immediately into the mailbox, not retained in an unbounded dispatcher backlog.
        if (!initial) _dispatcherQueue.TryEnqueue(() =>
        {
            if (GlobalDependencies.Kernel is not null) GetMainWindow()?.Activate();
        });
        if (arguments.Kind == ExtendedActivationKind.AppNotification
            && arguments.Data is AppNotificationActivatedEventArgs notification)
            CaptureNotification(notification, initial);
    }

    private void CaptureNotification(AppNotificationActivatedEventArgs arguments, bool initial = false)
    {
        // Manager callbacks need not be on UI. Before readiness, only the bounded mailbox
        // retains a target: no queue of startup payload-capturing dispatcher callbacks.
        try
        {
            var values = arguments.Arguments;
            values.TryGetValue("page", out var page);
            values.TryGetValue("args", out var json);
            var request = _notifications.Capture(page, json, initial);
            if (request is not null)
                _dispatcherQueue.TryEnqueue(() => DispatchNotification(request));
        }
        catch (Exception)
        {
            Serilog.Log.Warning("通知激活参数不可用。");
        }
    }

    private void RefreshNotificationOwnership()
    {
        if (!_isPrimaryInstance || _notificationAccount is not { } account || _notificationApp is not { } app) return;
        try
        {
            _notifications.UpdateOwnership(NotificationAccountId(), account.AccountId, account.AccountGeneration,
                account.AccountCancellationToken.IsCancellationRequested, app.IsInitialLoading,
                !app.IsClosed && _notificationRootIsCurrent?.Invoke() == true);
        }
        catch (Exception)
        {
            _notifications.Reject(); // A notification observer must not disturb account initialization/sign-out.
            Serilog.Log.Warning("通知激活上下文不可用。");
        }
    }

    private void DispatchNotification(NotificationDispatch request)
    {
        // Recheck on UI immediately before handing ownership to the existing player command.
        RefreshNotificationOwnership();
        if (_notifications.TryConsume(request)) _notificationApp!.OpenPlayerCommand.Execute(request.Target);
    }

    private void DispatchPendingNotification()
    {
        RefreshNotificationOwnership();
        if (_notifications.TakePending() is { } request) DispatchNotification(request);
    }

    private void OnNotificationAccountChanged(object? sender, EventArgs args)
    {
        if (_dispatcherQueue.HasThreadAccess) DispatchPendingNotification();
        else _dispatcherQueue.TryEnqueue(DispatchPendingNotification);
    }

    private void OnNotificationAppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(AppViewModel.IsInitialLoading) or nameof(AppViewModel.IsClosed))) return;
        if (_dispatcherQueue.HasThreadAccess) DispatchPendingNotification();
        else _dispatcherQueue.TryEnqueue(DispatchPendingNotification);
    }

    internal void BeginNotificationRoot(object owner, Func<bool> isCurrent)
    {
        _notificationRoot = owner;
        _notificationRootIsCurrent = isCurrent;
        _notifications.BeginRoot(owner);
    }

    internal void CompleteNotificationNavigation(object owner)
    {
        _notifications.CompleteNavigation(owner);
        DispatchPendingNotification();
    }

    internal void RetireNotificationRoot(object owner)
    {
        _notifications.RetireRoot(owner);
        if (!ReferenceEquals(_notificationRoot, owner)) return;
        _notificationRoot = null;
        _notificationRootIsCurrent = null;
    }

    private async Task RedirectInitialActivationAsync(Microsoft.Windows.AppLifecycle.AppInstance primary, AppActivationArguments arguments)
    {
        try
        {
            await primary.RedirectActivationToAsync(arguments).AsTask();
        }
        catch (Exception)
        {
            Serilog.Log.Error("应用激活转交失败。"); // Fixed text; no activation payload or exception details.
        }
        // Transfer has actually settled (success or observed failure). Preserve the DI-free
        // secondary's existing owned exit, without forcing exit or changing its drain rules.
        await RequestExitAsync();
    }
}
