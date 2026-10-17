// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Serilog;

namespace BiliCopilot.UI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static readonly string Id = AppChannelIdentity.InstanceKey;
    private readonly DispatcherQueue _dispatcherQueue;
    private AppNotificationManager _notificationManager;
    private readonly object _exitSync = new();
    private Task<bool>? _exitRequest;
    private Task? _exitWorker;
    private List<MainWindow>? _terminalMainWindows;
    private Task? _sponsorInitialization;

    // Existing App.Current.Exit callers (including account-init failure) must use this owned path.
    // This does not intercept a deliberately base-typed Application.Exit or forced process exit.
    public new static App Current => (App)Application.Current;

    public new void Exit() => _ = RequestExitAsync();

    /// <summary>False never forces exit. Later explicit requests may retry terminal work, never playback release.</summary>
    public Task<bool> RequestExitAsync()
    {
        lock (_exitSync)
        {
            if (_exitRequest is not null) return _exitRequest;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _exitRequest = done.Task; // Publish before UI dispatch, cancellation or owner callbacks.
            try
            {
                if (_dispatcherQueue.HasThreadAccess) _exitWorker = CompleteExitAsync(done);
                else if (!_dispatcherQueue.TryEnqueue(() => _exitWorker = CompleteExitAsync(done))) CompleteRejectedExitAttempt(done);
            }
            catch { CompleteRejectedExitAttempt(done); }
            return done.Task;
        }
    }

    private async Task CompleteExitAsync(TaskCompletionSource<bool> done)
    {
        // Publish _exitWorker before application callbacks, even when shutdown is already completed.
        await Task.Yield();
        AppViewModel? app = null;
        object? reservation = null;
        var exitCallEntered = false;
        try
        {
            // Redirect-only secondary instance never initializes DI and cannot own playback.
            if (GlobalDependencies.Kernel is not null)
            {
                app = this.Get<AppViewModel>();
                if (!await app.PreparePlaybackShutdownAsync())
                {
                    CompleteRejectedExitAttempt(done);
                    return;
                }
                await app.DrainPlaybackTerminationNoticesAsync();
                reservation = app.TryClaimPlaybackTermination();
                if (reservation is null)
                {
                    CompleteRejectedExitAttempt(done);
                    return;
                }
            }
            TrayIcon?.Dispose();
            TrayIcon = null;
            _notificationManager?.Unregister();
            _notificationManager = null;
            if (app is not null)
            {
                // Retain unfinished chrome even if a failed finalization removed it from app.Windows.
                _terminalMainWindows ??= app.Windows.OfType<MainWindow>().ToList();
                this.Get<BiliCopilot.UI.SponsorBlock.Integration.SponsorRuntime>().AuthorizeNativeExit();
                foreach (var mainWindow in _terminalMainWindows.ToArray())
                {
                    mainWindow.FinalizeAndClose();
                    _terminalMainWindows.Remove(mainWindow); // A later failure must not close it twice.
                }
            }
            exitCallEntered = true; // Invocation boundary, not proof that native exit completed.
            base.Exit(); // Only after actual pending opens, sends, controls, closes and release have drained.
            done.TrySetResult(true);
        }
        catch
        {
            // Only this attempt's claim may be released, and never after an uncertain native exit.
            if (reservation is not null && !exitCallEntered) app!.ReleasePlaybackTermination(reservation);
            app?.ReportLiveFailure();
            CompleteRejectedExitAttempt(done);
        }
    }

    private void CompleteRejectedExitAttempt(TaskCompletionSource<bool> done)
    {
        lock (_exitSync)
        {
            // Clear only this attempt, before publishing false to a later explicit caller.
            // The AppViewModel still owns its cached actual shutdown and any uncertain native release.
            // The retained worker has no callbacks/awaits after this branch; queued UI work follows it.
            if (ReferenceEquals(_exitRequest, done.Task)) _exitRequest = null;
            done.TrySetResult(false);
        }
    }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += OnUnhandledException;
        _notificationManager = AppNotificationManager.Default;
        _notificationManager.NotificationInvoked += OnAppNotificationInvoked;
        _notificationManager.Register();
    }

    /// <summary>
    /// 日志文件夹.
    /// </summary>
    public static string LoggerFolder { get; private set; }

    private TaskbarIcon TrayIcon { get; set; }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (_initialActivationSelected) return;
        _initialActivationSelected = true;
        // Register ran in the constructor. Read this process's activation, never the remote primary's.
        AppActivationArguments? activatedArgs = null;
        try
        {
            activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception)
        {
            Log.Warning("启动激活参数不可用。"); // No DI, exception details or private activation data.
        }
        var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(Id);
        if (instance.IsCurrent)
        {
            _isPrimaryInstance = true;
            var rootFolder = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder;
            var fullPath = $"{rootFolder.Path}\\Logger";
            LoggerFolder = fullPath;
            if (!Directory.Exists(fullPath))
            {
                _ = Directory.CreateDirectory(fullPath);
            }

            instance.Activated += OnInstanceActivated;
            Richasy.WinUIKernel.Share.Base.ImageExBase.EnableBackgroundDecoding = false;
            GlobalDependencies.Initialize();
            InitializeNotificationRouting();
            if (activatedArgs is not null) CaptureActivation(activatedArgs, initial: true);
            _sponsorInitialization = GlobalDependencies.Kernel
                .GetRequiredService<BiliCopilot.UI.SponsorBlock.Integration.SponsorRuntime>().InitializeAsync();
            GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().LaunchCommand.Execute(default);
        }
        else
        {
            _notifications.Reject(); // No DI or playback in a redirect-only secondary.
            _activationRedirect = activatedArgs is null
                ? RequestExitAsync()
                : RedirectInitialActivationAsync(instance, activatedArgs);
        }
    }

    private static MainWindow? GetMainWindow()
        => GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().Windows.Find(p => p is MainWindow) as MainWindow;

    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        CaptureActivation(e);
    }

    private void InitializeTrayIcon()
    {
        if (TrayIcon != null)
        {
            return;
        }

        var showHideWindowCommand = (XamlUICommand)Resources["ShowHideWindowCommand"];
        showHideWindowCommand.ExecuteRequested += OnShowHideWindowCommandExecuteRequested;

        var exitApplicationCommand = (XamlUICommand)Resources["QuitCommand"];
        exitApplicationCommand.ExecuteRequested += OnQuitCommandExecuteRequested;

        try
        {
            TrayIcon = (TaskbarIcon)Resources["TrayIcon"];
            TrayIcon.ForceCreate();
        }
        catch (Exception)
        {
            var logger = Log.Logger.ForContext<App>();
            logger.Error("初始化任务栏图标失败");
        }
    }

    private void ExitApp()
    {
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        this.Get<ILogger<App>>().LogError(e.Exception, "Unhandled exception occurred.");
        e.Handled = true;

        // 一旦出现布局循环检测异常，就删除上次选中的功能页，然后重启应用.
        if (e.Message.Contains("Layout cycle detected"))
        {
            SettingsToolkit.DeleteLocalSetting(Models.Constants.SettingNames.LastSelectedFeaturePage);
            this.Get<AppViewModel>().RestartCommand.Execute(default);
        }
    }

    private void OnAppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        => CaptureNotification(args);

    private void OnQuitCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        => ExitApp();

    private void OnShowHideWindowCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        => GetMainWindow()?.Activate();
}
