using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    internal Task<SponsorFilterFacts?> CurrentFilterFactsAsync(PlayerTicket ticket) => OnOwner(() =>
    {
        if (!Current(ticket, out var state)) return null;
        var local = settings.FactsFor(ticket.Stamp.Owner.Media);
        return new SponsorFilterFacts(ticket.Stamp.Owner.Media, revision, state.ReadStatus, state.Raw,
            state.Aligned, local.Hidden.Union(local.Downvoted).Union(local.ExternalSourceExcluded));
    });
    internal Task<SponsorStatisticsArchive> ExportStatisticsAsync() => OnOwner(() => ledger.Export() with
        { IsDurable = version is not null && !statisticsDirty, StoreVersion = version });
    internal Task<ApplicationResult> ImportStatisticsAsync(SponsorStatisticsArchive archive) =>
        OnOwner(() => ImportStatisticsCoreAsync(revision, archive));

    /// <summary>Explicit linked-record view. Original and target are separate values throughout.
    /// No target duration is borrowed; no YouTube timeline/offset/alignment is constructed.</summary>
    internal async Task<RelatedReadResult> ReadRelatedAsync(PlayerTicket original, MediaIdentity target, bool refresh = false)
    {
        var work = await OnOwner(() =>
        {
            if (!Current(original, out _) || !settings.NetworkEnabled || requests.Count >= 15) return null;
            var epoch = AcquireClient();
            var port = epoch.Client.GetPortVideoAsync(target, refresh);
            var segments = epoch.Client.GetSegmentsAsync(Query(target), refresh);
            requests.Add(port); requests.Add(segments);
            return new RelatedWork(epoch, port, segments);
        });
        if (work is null) return new(ApplicationStatus.Disabled);
        ReadResult<PortVideo> portResult;
        ReadResult<ImmutableArray<SponsorSegment>> segmentsResult;
        try { portResult = await work.Port.ConfigureAwait(false); } catch { portResult = new(ReadStatus.TransportFailure); }
        try { segmentsResult = await work.Segments.ConfigureAwait(false); } catch { segmentsResult = new(ReadStatus.TransportFailure); }
        return await OnOwner(() =>
        {
            requests.Remove(work.Port); requests.Remove(work.Segments); ReleaseClient(work.Epoch);
            if (!StillIntent(original)) return new RelatedReadResult(ApplicationStatus.Stale);
            return new RelatedReadResult(ApplicationStatus.Success, new(original.Stamp.Owner.Media, target, portResult, segmentsResult));
        });
    }
    private sealed record RelatedWork(ClientEpoch Epoch, Task<ReadResult<PortVideo>> Port,
        Task<ReadResult<ImmutableArray<SponsorSegment>>> Segments);

    internal async Task<ReadResult<SponsorUserInfo>> ReadUserInfoAsync(string publicId)
    {
        var work = await OnOwner(() =>
        {
            if (Stopping || !settings.NetworkEnabled || requests.Count >= 16) return null;
            var epoch = AcquireClient(); var task = epoch.Client.GetUserInfoAsync(publicId);
            requests.Add(task); return new UserWork(epoch, task, policyRevision);
        });
        if (work is null) return new(ReadStatus.Disabled);
        ReadResult<SponsorUserInfo> result;
        try { result = await work.Task.ConfigureAwait(false); } catch { result = new(ReadStatus.TransportFailure); }
        return await OnOwner(() =>
        {
            requests.Remove(work.Task); ReleaseClient(work.Epoch);
            return Stopping || !settings.NetworkEnabled || policyRevision != work.Revision ? new(ReadStatus.Cancelled) : result;
        });
    }
    private sealed record UserWork(ClientEpoch Epoch, Task<ReadResult<SponsorUserInfo>> Task, long Revision);

    /// <summary>One explicit bounded batch; never calls itself, advances cursors, or fetches until visible.</summary>
    internal async Task<ImmutableArray<SponsorFilterFacts>> ReadFilterScopeAsync(SponsorReadOnlyScope scope, long callerRevision)
    {
        if (!scope.FullLabelsOnly || scope.Media.IsDefault || scope.Media.Length > 16 || callerRevision < 0) return [];
        var work = await OnOwner(() =>
        {
            if (Stopping || !settings.NetworkEnabled || requests.Count != 0 || scope.Media.IsEmpty
                || scope.Media.Any(m => m is null)) return null;
            // At most 16 outstanding application requests, S7 independently limits physical HTTP to four.
            var media = scope.Media.Distinct().ToImmutableArray();
            var epoch = AcquireClient();
            var tasks = media.Select(m => epoch.Client.GetFullLabelsAsync(m)).ToArray();
            foreach (var task in tasks) requests.Add(task);
            return new FilterWork(epoch, media, tasks, policyRevision);
        });
        if (work is null) return [];
        var output = ImmutableArray.CreateBuilder<SponsorFilterFacts>();
        for (var i = 0; i < work.Tasks.Length; i++)
        {
            ReadResult<ImmutableArray<SponsorSegment>> read;
            try { read = await work.Tasks[i].ConfigureAwait(false); } catch { read = new(ReadStatus.TransportFailure); }
            output.Add(new(work.Media[i], callerRevision, read.Status, read.IsSuccess ? read.Value : []));
        }
        return await OnOwner(() =>
        {
            foreach (var task in work.Tasks) requests.Remove(task);
            ReleaseClient(work.Epoch);
            if (Stopping || !settings.NetworkEnabled || policyRevision != work.Revision) return ImmutableArray<SponsorFilterFacts>.Empty;
            return output.Select(fact =>
            {
                var local = settings.FactsFor(fact.Media);
                return fact with { LocalExcluded = local.Hidden.Union(local.Downvoted).Union(local.ExternalSourceExcluded) };
            }).ToImmutableArray();
        });
    }
    private sealed record FilterWork(ClientEpoch Epoch, ImmutableArray<MediaIdentity> Media,
        Task<ReadResult<ImmutableArray<SponsorSegment>>>[] Tasks, long Revision);

    internal Task<ReadResult<ImmutableArray<string>>> ReadLockedCategoriesAsync(PlayerTicket ticket) => ReadLocksCoreAsync(ticket);
    private async Task<ReadResult<ImmutableArray<string>>> ReadLocksCoreAsync(PlayerTicket ticket)
    {
        var work = await OnOwner(() =>
        {
            if (!Current(ticket, out _) || !settings.NetworkEnabled || requests.Count >= 16) return null;
            var epoch = AcquireClient(); var task = epoch.Client.GetLockedCategoriesAsync(ticket.Stamp.Owner.Media);
            requests.Add(task); return new LockWork(epoch, task);
        });
        if (work is null) return new(ReadStatus.Disabled);
        ReadResult<ImmutableArray<string>> result;
        try { result = await work.Task.ConfigureAwait(false); } catch { result = new(ReadStatus.TransportFailure); }
        return await OnOwner(() =>
        {
            requests.Remove(work.Task); ReleaseClient(work.Epoch);
            return StillIntent(ticket) ? result : new(ReadStatus.Cancelled);
        });
    }
    private sealed record LockWork(ClientEpoch Epoch, Task<ReadResult<ImmutableArray<string>>> Task);
}
