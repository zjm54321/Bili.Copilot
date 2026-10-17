using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.ViewModels;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Danmaku;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class DanmakuRenderViewModel
{
    private SponsorApplicationCoordinator? _sponsorCategoryApp;
    private SponsorViewFeed? _sponsorCategoryFeed;
    private Guid _sponsorCategoryAttachment;
    private Func<SponsorDanmakuSource?>? _sponsorCategorySource;
    private SponsorDanmakuSource? _sponsorDanmakuData;
    private PlaybackOwner? _sponsorCategoryOwner;
    private ImmutableArray<SponsorDanmakuInterval> _sponsorCategoryRanges = [];
    private IList<DanmakuInformation>? _sponsorCategoryPool;

    private bool HasSponsorCategoryProjection => !_sponsorCategoryRanges.IsDefaultOrEmpty;

    internal void BindSponsorCategories(SponsorApplicationCoordinator app, Guid attachment,
        DispatcherQueue dispatcher, Func<SponsorDanmakuSource?> source)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Sponsor category binding requires its player dispatcher.");
        UnbindSponsorCategories();
        _sponsorCategoryApp = app;
        _sponsorCategoryAttachment = attachment;
        _sponsorCategorySource = source;
        // Do not guess the identity of an already-loaded, previously unbound raw pool.
        _sponsorCategoryFeed = new SponsorViewFeed(app, dispatcher, _ => RefreshSponsorCategories());
        _sponsorCategoryFeed.Start();
    }

    internal void UnbindSponsorCategories()
    {
        _sponsorCategoryFeed?.Stop();
        _sponsorCategoryFeed = null;
        _sponsorCategoryApp = null;
        _sponsorCategorySource = null;
        _sponsorDanmakuData = null;
        // The real unbind caller is the existing view-release barrier. Stop this feed without
        // introducing renderer work into native/player cleanup; ordinary preference changes redraw.
        SetSponsorCategoryRanges(null, [], redraw: false);
    }

    private void CaptureSponsorDanmakuData()
    {
        // Initialize's sole production caller checks its captured media version immediately before
        // passing the completed raw SDK list. No await, lookup by title, CID guess or fetch is added.
        _sponsorDanmakuData = _sponsorCategorySource?.Invoke();
        _sponsorCategoryPool = null;
    }

    private void ClearSponsorDanmakuData()
    {
        _sponsorDanmakuData = null;
        SetSponsorCategoryRanges(null, [], redraw: false);
    }

    private SponsorDanmakuSource? CurrentSponsorDanmakuSource()
    {
        var current = _sponsorCategorySource?.Invoke();
        return current is not null && _sponsorDanmakuData is { } data
            && current.Media == data.Media && current.MediaVersion == data.MediaVersion ? current : null;
    }

    private void RefreshSponsorCategories(bool redraw = true)
    {
        var current = CurrentSponsorDanmakuSource();
        ImmutableArray<SponsorDanmakuInterval> ranges = _sponsorCategoryApp is { } app
            ? SponsorDanmakuCategoryProjection.Build(app.Snapshot, _sponsorCategoryAttachment, current)
            : [];
        SetSponsorCategoryRanges(current?.Owner, ranges, redraw);
    }

    private void SetSponsorCategoryRanges(PlaybackOwner? owner, ImmutableArray<SponsorDanmakuInterval> ranges, bool redraw)
    {
        if (owner == _sponsorCategoryOwner && ranges.SequenceEqual(_sponsorCategoryRanges)) return;
        var changed = !ranges.SequenceEqual(_sponsorCategoryRanges);
        _sponsorCategoryOwner = owner;
        _sponsorCategoryRanges = ranges;
        _sponsorCategoryPool = null;
        if (redraw && changed && _cachedDanmakus is not null) RedrawSponsorCategories();
    }

    private IList<DanmakuInformation> GetSponsorCategoryDanmakus()
    {
        if (_cachedDanmakus is null) return [];
        if (!HasSponsorCategoryProjection || CurrentSponsorDanmakuSource()?.Owner != _sponsorCategoryOwner)
            return _cachedDanmakus;
        // Preserve the raw list and its order. Returned rows are the original SDK instances, not
        // rewritten text/timestamps; disabling or losing eligibility simply returns the raw pool.
        return _sponsorCategoryPool ??= _cachedDanmakus.Where(item => item is null
            || !SponsorDanmakuCategoryProjection.Contains(_sponsorCategoryRanges, item.StartPosition)).ToArray();
    }

    private bool IsSponsorCategoryHidden(double mediaTime)
        => HasSponsorCategoryProjection && CurrentSponsorDanmakuSource()?.Owner == _sponsorCategoryOwner
            && SponsorDanmakuCategoryProjection.Contains(_sponsorCategoryRanges, mediaTime);

    private void RedrawSponsorCategories()
    {
        // Existing renderer Redraw ignores an empty list. Clear only presentation, not the raw
        // pool, then restore the already-owned media position (Clear resets the renderer's clock).
        RequestClearDanmaku?.Invoke(this, EventArgs.Empty);
        ProgressChanged?.Invoke(this, _position);
        Redraw();
    }
}
