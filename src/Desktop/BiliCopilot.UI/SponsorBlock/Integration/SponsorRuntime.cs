using System.Collections.Immutable;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Content;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>One application lifetime, not a player controller. Construction starts no IO or work.</summary>
internal sealed partial class SponsorRuntime
{
    private readonly SponsorDispatcher dispatcher;
    private readonly SponsorBackupRouter backups;
    private readonly SponsorSettingsViewModel settings;
    private Task<ApplicationResult>? load;
    private SettingsPage? settingsPage;
    private bool settingsBound;
    private bool projectionConfigured;
    private PersistedContentOptions? projectedOptions;
    private ImmutableArray<string> projectedWhitelist;
    private readonly object uiWorkSync = new();
    private readonly HashSet<Task> uiWork = [];

    public SponsorRuntime(DispatcherQueue queue, AppViewModel app, SponsorContentProjectionService contentProjection)
    {
        dispatcher = new SponsorDispatcher(queue);
        backups = new SponsorBackupRouter(app, dispatcher);
        ContentProjection = contentProjection;
        Store = new SponsorPersistenceStore(new WindowsSponsorFiles(SponsorPackageScope.Resolve),
            new WindowsSponsorIdentityProtector());
        // Existing factory is the fixed bsbsb.top S7 transport with isolated handler/consent epochs.
        Coordinator = new SponsorApplicationCoordinator(Store, new SponsorClientFactory(), dispatcher);
        settings = new SponsorSettingsViewModel();
        Coordinator.Updated += OnUpdated;
    }

    internal SponsorPersistenceStore Store { get; }
    internal SponsorApplicationCoordinator Coordinator { get; }
    public SponsorContentProjectionService ContentProjection { get; }
    public bool DanmakuAidAvailable => false;
    public string DanmakuAidUnavailableReason => "RendererAppearanceAndHitTestUnavailable";
    internal ApplicationResult? LastLoadResult { get; private set; }
    internal Task<ApplicationResult> InitializeAsync() => dispatcher.InvokeAsync(() => load ??= LoadCoreAsync());

    /// <summary>UI-owner join for its actual command/identity tasks, including cleanup after Closed.
    /// Must be called immediately after task creation. Returns that same task, never a surrogate.</summary>
    internal Task TrackUiWork(Task actualTask)
    {
        lock (uiWorkSync)
        {
            uiWork.RemoveWhere(task => task.IsCompleted);
            uiWork.Add(actualTask);
        }
        // T5-07: TrackUiWork registers and returns the identical supplied task; registered runners do not await Runtime.Close or its waiter.
        #pragma warning disable VSTHRD003
        return actualTask;
        #pragma warning restore VSTHRD003
    }

    private async Task DrainUiWorkAsync()
    {
        // No new ordinary commands after Coordinator.Close. Cleanup commands can finish here;
        // callbacks must not await runtime Close/Wait, which would create a reverse lifetime join.
        while (true)
        {
            Task[] pending;
            lock (uiWorkSync)
            {
                pending = uiWork.ToArray();
                uiWork.RemoveWhere(task => task.IsCompleted);
            }
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private async Task<ApplicationResult> LoadCoreAsync()
    {
        await Task.Yield(); // Publish the one actual task before any store/VM callbacks.
        var failureStage = FailureStage.SponsorInitialLoad;
        try
        {
            LastLoadResult = await Coordinator.LoadAsync();
            failureStage = FailureStage.SponsorSettingsBind;
            if (!closing && !settingsBound)
            {
                settings.Bind(Coordinator, backups);
                settingsBound = true;
                if (settingsPage is { } page) page.BindSponsorSettings(settings);
            }
            return LastLoadResult;
        }
        catch (Exception ex)
        {
            SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<SponsorRuntime>>());
            LastLoadResult = new(ApplicationStatus.Unavailable, "SponsorRuntimeLoadUnavailable");
            ContentProjection.Configure(null, default);
            return LastLoadResult;
        }
    }

    public void BindSettingsPage(SettingsPage page)
    {
        dispatcher.CheckAccess();
        if (!page.DispatcherQueue.HasThreadAccess || closing) return;
        // A global VM's commands cannot identify which of two pages was clicked. Associate only
        // this exact page; disassociate the old view rather than route a picker to the wrong HWND.
        if (settingsPage is { } old && !ReferenceEquals(old, page))
        { old.UnbindSponsorSettings(); backups.Disassociate(old); }
        settingsPage = null;
        if (!backups.Associate(page)) return;
        settingsPage = page;
        load ??= LoadCoreAsync(); // Retained task; this synchronous designer hook never drops it.
        if (settingsBound) page.BindSponsorSettings(settings);
    }

    public void UnbindSettingsPage(SettingsPage page)
    {
        dispatcher.CheckAccess();
        page.UnbindSponsorSettings();
        backups.Disassociate(page);
        if (ReferenceEquals(settingsPage, page)) settingsPage = null;
        // Page lifetime does not own settings, coordinator, store, protocol clients or projection.
    }

    private void OnUpdated(SponsorApplicationView view)
    {
        dispatcher.CheckAccess();
        UpdateProjection(view);
        ObservePlayers(view);
        QueueCloseStateChanged();
    }

    private void UpdateProjection(SponsorApplicationView view)
    {
        var available = !closing && view.StoreVersion is not null
            && (view.Persistence is PersistenceStatus.Success or PersistenceStatus.Missing)
            && (view.AuxiliaryPersistence is null or PersistenceStatus.Success);
        var options = available ? Coordinator.EffectiveContentPreferences.Content : null;
        var whitelist = available ? view.Auxiliary.WhitelistedUpUids : default;
        if (projectionConfigured && options == projectedOptions
            && whitelist.IsDefault == projectedWhitelist.IsDefault
            && (whitelist.IsDefault || whitelist.SequenceEqual(projectedWhitelist, StringComparer.Ordinal))) return;
        projectedOptions = options;
        projectedWhitelist = whitelist;
        projectionConfigured = true;
        ContentProjection.Configure(options?.ToOptions(), whitelist);
        // Danmaku preferences remain stored only; there is no renderer hook, effect or second clock.
    }
}
