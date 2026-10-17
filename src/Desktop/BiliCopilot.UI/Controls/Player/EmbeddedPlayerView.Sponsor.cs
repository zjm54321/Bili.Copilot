// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Hosting;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 空降助手的页面标题入口（常规内嵌呈现，紧挨“分享”左侧）.
/// 本视图只决定按钮显隐并转发点击：视图模型、面板与输入申请仍归当前播放浮层
/// （<see cref="PlayerOverlay"/>）所有，面板在视频区域顶部向下展开.
/// </summary>
public sealed partial class EmbeddedPlayerView
{
    private readonly HashSet<Button> _sponsorEntries = [];

    private void OnSponsorEntryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_sponsorEntries.Count == 0) PlayerOverlay.SponsorPageEntryChanged += OnSponsorPageEntryChanged;
        _sponsorEntries.Add(button);
        UpdateSponsorEntry(button, IsSponsorEntryAvailable());
    }

    private void OnSponsorEntryUnloaded(object sender, RoutedEventArgs e)
    {
        // A late Unloaded of a re-parented, already reloaded button must not drop it.
        if (sender is not Button { IsLoaded: false } button || !_sponsorEntries.Remove(button)) return;
        button.Visibility = Visibility.Collapsed;
        if (_sponsorEntries.Count == 0) PlayerOverlay.SponsorPageEntryChanged -= OnSponsorPageEntryChanged;
    }

    private void OnSponsorEntryClick(object sender, RoutedEventArgs e)
    {
        if (!IsSponsorEntryAvailable() || !PlayerOverlay.TryToggleSponsorPanelFromPage(ViewModel)) UpdateSponsorEntries();
    }

    private void OnSponsorPageEntryChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) UpdateSponsorEntries();
        else DispatcherQueue.TryEnqueue(UpdateSponsorEntries);
    }

    private bool IsSponsorEntryAvailable()
        => _loaded && IsCurrentAttachment(ViewModel, _attachmentEpoch) && _mode == PlayerPresentationMode.Normal
            && ViewModel is { } owner && !owner.IsPlaybackClosing && owner.Presentation == PlayerPresentationMode.Normal
            && PlayerOverlay.IsSponsorPageEntryAvailable(owner);

    private void UpdateSponsorEntries()
    {
        var available = IsSponsorEntryAvailable();
        foreach (var button in _sponsorEntries) UpdateSponsorEntry(button, available);
    }

    private static void UpdateSponsorEntry(Button button, bool available)
    {
        var visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (button.Visibility != visibility) button.Visibility = visibility;
    }
}
