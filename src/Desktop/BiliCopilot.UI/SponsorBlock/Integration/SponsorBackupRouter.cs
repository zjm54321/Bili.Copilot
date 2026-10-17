using BiliCopilot.UI.Pages;
using BiliCopilot.UI.SponsorBlock.Platform;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>One visible settings association, exact owning window. Never uses ActivatedWindow.</summary>
internal sealed class SponsorBackupRouter(AppViewModel app, SponsorDispatcher dispatcher) : ISponsorBackupFilePort
{
    private readonly Dictionary<Window, WindowsSponsorBackupFilePort> ports = [];
    private SettingsPage? page;
    private Window? owner;
    private Task? close;

    internal bool Associate(SettingsPage value)
    {
        dispatcher.CheckAccess();
        if (close is not null || !value.IsLoaded || value.XamlRoot is null) return false;
        var window = app.Windows.FirstOrDefault(w => w.Content is FrameworkElement element
            && ReferenceEquals(element.XamlRoot, value.XamlRoot));
        if (window is null || !window.DispatcherQueue.HasThreadAccess) return false;
        if (!ports.ContainsKey(window))
        {
            if (ports.Count >= 16) return false;
            ports.Add(window, new WindowsSponsorBackupFilePort(window));
        }
        page = value;
        owner = window;
        return true;
    }

    internal void Disassociate(SettingsPage value)
    {
        dispatcher.CheckAccess();
        if (ReferenceEquals(page, value)) { page = null; owner = null; }
    }

    private WindowsSponsorBackupFilePort Current()
    {
        dispatcher.CheckAccess();
        if (close is not null || page is not { IsLoaded: true } || owner is null
            || !app.Windows.Any(w => ReferenceEquals(w, owner)) || !owner.AppWindow.IsVisible
            || owner.Content is not FrameworkElement element || !ReferenceEquals(element.XamlRoot, page.XamlRoot))
            throw new InvalidOperationException("Sponsor backup owner is unavailable.");
        return ports[owner];
    }

    public Task<bool> SaveBackupAsync(byte[] secretFreeBackup, string suggestedFileName)
    {
        if (secretFreeBackup is null || secretFreeBackup.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Sponsor backup exceeds the supported size.");
        return Current().SaveBackupAsync(secretFreeBackup, suggestedFileName);
    }
    public Task<byte[]?> PickBackupAsync() => Current().PickBackupAsync();

    internal Task CloseAsync()
    {
        dispatcher.CheckAccess();
        if (close is not null) return close;
        page = null; owner = null;
        var pending = new List<Task>();
        foreach (var port in ports.Values)
        {
            try { pending.Add(port.CloseAsync()); }
            catch (Exception error) { pending.Add(Task.FromException(error)); }
        }
        return close = Task.WhenAll(pending);
    }
}
