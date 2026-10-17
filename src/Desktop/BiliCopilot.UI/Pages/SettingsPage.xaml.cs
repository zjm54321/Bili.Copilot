// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Integration;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 设置页面.
/// </summary>
public sealed partial class SettingsPage : SettingsPageBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsPage"/> class.
    /// </summary>
    public SettingsPage() => InitializeComponent();

    // Host association only. The composer owns SponsorSettingsViewModel.Bind/Unbind and the coordinator.
    private SponsorSettingsViewModel? _sponsorSettings;
    private bool _pageLoaded;

    /// <summary>
    /// Shows the SponsorBlock settings group for a view model that the composer has already bound.
    /// Call on the UI thread. Never creates, loads or closes a coordinator.
    /// </summary>
    /// <returns><see langword="false"/> when called off the UI thread.</returns>
    internal bool BindSponsorSettings(SponsorSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (DispatcherQueue?.HasThreadAccess != true) return false;
        _sponsorSettings = viewModel;
        ApplySponsorSettings();
        return true;
    }

    /// <summary>
    /// Forgets the association and hides the group. Does not call <c>Unbind</c> on the view model.
    /// </summary>
    internal void UnbindSponsorSettings()
    {
        _sponsorSettings = null;
        ApplySponsorSettings();
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
    {
        _pageLoaded = true;
        ApplySponsorSettings();
        // Associate this exact page with the app's single runtime (it calls BindSponsorSettings when ready).
        ResolveSponsorRuntime()?.BindSettingsPage(this);
        ViewModel.InitializeCommand.Execute(default);
    }

    /// <inheritdoc/>
    protected override void OnPageUnloaded()
    {
        // Unload drops only the view association (the section's own subscriptions); the
        // registration survives so a cached page that reloads shows the same view model again.
        _pageLoaded = false;
        ApplySponsorSettings();
        // Page association only: never unbinds/disposes the global settings view model or coordinator.
        ResolveSponsorRuntime()?.UnbindSettingsPage(this);
    }

    // The existing DI singleton (registered by the app composition). No fallback runtime is created:
    // without one the SponsorBlock group simply stays hidden.
    private SponsorRuntime? ResolveSponsorRuntime()
    {
        try
        {
            return this.Get<SponsorRuntime>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ApplySponsorSettings()
    {
        var viewModel = _pageLoaded ? _sponsorSettings : null;
        if (!ReferenceEquals(SponsorSettings.ViewModel, viewModel)) SponsorSettings.ViewModel = viewModel;
        var visibility = viewModel is null ? Visibility.Collapsed : Visibility.Visible;
        SponsorSettings.Visibility = visibility;
        if (SponsorGroupHeader is not null) SponsorGroupHeader.Visibility = visibility;
    }
}

/// <summary>
/// 设置页面基类.
/// </summary>
public abstract class SettingsPageBase : LayoutPageBase<SettingsPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsPageBase"/> class.
    /// </summary>
    protected SettingsPageBase() => ViewModel = this.Get<SettingsPageViewModel>();
}
