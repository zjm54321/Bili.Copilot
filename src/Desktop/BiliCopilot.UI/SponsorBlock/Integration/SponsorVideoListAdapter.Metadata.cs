using System.Collections.Immutable;
using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.SponsorBlock.Integration;

internal sealed partial class SponsorVideoListAdapter
{
    // Metadata only: no page payload, progress, relation, cookie, task or label-fact cache.
    private readonly Dictionary<DescriptorKey, Descriptor> _descriptors = [];
    private readonly Queue<DescriptorKey> _descriptorOrder = [];
    private sealed record Descriptor(ImmutableArray<MediaIdentity> Parts);

    private async Task<SponsorVideoListProjection> EvaluateAsync(Work work, CancellationToken token)
    {
        var planned = new Dictionary<DescriptorKey, Descriptor>();
        var identities = new HashSet<MediaIdentity>();
        // The prefix is fixed from the caller's explicit pass, NOT from the filtered layout.
        // Never scan new rows until enough display items remain or refill after a completion.
        foreach (var row in work.Rows.Take(16).DistinctBy(row => row.Key))
        {
            if (!IsCurrent(work)) return Original(work.Snapshot, SponsorVideoListStatus.Inactive);
            token.ThrowIfCancellationRequested();
            if (!TryLookup(row.Key, out var lookup, out _, out _)) continue;
            if (!_descriptors.TryGetValue(row.Key, out var descriptor))
            {
                VideoPlayerView detail;
                try
                {
                    // Real public detail API; NOT play-detail/stream initialization. SDK fallback
                    // and progress/relation reads mean this is not a promise of one HTTP request.
                    detail = await _player.GetVideoPageDetailAsync(new MediaIdentifier(lookup, null, null), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { continue; } // Unknown retains the row; no raw error/payload log.
                if (!IsCurrent(work)) return Original(work.Snapshot, SponsorVideoListStatus.Inactive);
                descriptor = Describe(detail, row.Key);
                if (_descriptors.Count == 64) _descriptors.Remove(_descriptorOrder.Dequeue());
                _descriptors.Add(row.Key, descriptor);
                _descriptorOrder.Enqueue(row.Key);
            }
            if (descriptor.Parts.IsEmpty || identities.Union(descriptor.Parts).Count() > 16) continue;
            planned.Add(row.Key, descriptor);
            identities.UnionWith(descriptor.Parts);
        }
        if (identities.Count == 0 || !IsCurrent(work)) return Original(work.Snapshot, SponsorVideoListStatus.Unavailable);
        token.ThrowIfCancellationRequested();
        // This APP API has no token. Keep awaiting its actual Task after invalidation/close;
        // it owns its physical reads and client epoch. Never stamp old facts with this serial.
        var facts = await _app.ReadFilterScopeAsync(new(identities.ToImmutableArray(), true), work.Serial);
        if (!IsCurrent(work) || token.IsCancellationRequested || facts.IsDefault || facts.Length > 16)
            return Original(work.Snapshot, SponsorVideoListStatus.Unavailable);
        var options = new SponsorFilterOptions(true, _categories.ToImmutableHashSet(StringComparer.Ordinal));
        bool Hide(CapturedRow row)
        {
            // UI captures identity at submission; mutable model identity drift cannot inherit evidence.
            if (!RowIdentityMatches(row) || !planned.TryGetValue(row.Key, out var descriptor)) return false;
            foreach (var part in descriptor.Parts)
            {
                var matches = facts.Where(fact => fact.Media == part).ToArray();
                if (matches.Length != 1 || SponsorFilterProjection.Decide(SponsorContentSurface.VideoCard,
                    part, work.Serial, matches[0], options).Decision != SponsorFilterDecision.HideByExplicitPreference)
                    return false;
            }
            return !descriptor.Parts.IsEmpty; // ALL ordinary parts, never first/history CID as a whole-video proxy.
        }
        return new(work.Snapshot, work.Rows.Where(row => !Hide(row)).Select(row => row.Row.Original).ToImmutableArray(),
            SponsorVideoListStatus.Projected);
    }

    private static Descriptor Describe(VideoPlayerView? detail, DescriptorKey requested)
    {
        static Descriptor Unknown() => new([]);
        if (!TryLookup(requested, out _, out var expectedAid, out var expectedBv)
            || detail is null || detail.IsInteractiveVideo || detail.Information is not { } info
            || !IsBvId(info.BvId)
            || expectedAid is not null && NormalizeIdentifier(info.Identifier.Id) != expectedAid
            || expectedBv is not null && info.BvId != expectedBv
            || detail.Parts is not { Count: > 0 and <= 16 } parts)
            return Unknown();
        var result = ImmutableArray.CreateBuilder<MediaIdentity>();
        var unique = new HashSet<MediaIdentity>();
        var indexes = new HashSet<int>();
        for (var i = 0; i < parts.Count; i++)
        {
            // The public SDK returns the ordinary page's entire Parts list, not a page cursor.
            // Validate every 1..N slot without assuming wire order; gaps/duplicates/unsupported shapes pass through.
            var part = parts[i];
            if (part is null || part.Index < 1 || part.Index > parts.Count || !indexes.Add(part.Index) || part.Duration < 0
                || !long.TryParse(part.Identifier.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var cid) || cid <= 0
                || cid.ToString(CultureInfo.InvariantCulture) != part.Identifier.Id
                || !MediaIdentity.TryParse(info.BvId + "+" + part.Identifier.Id, out var identity)
                || !unique.Add(identity!)) return Unknown();
            result.Add(identity!);
        }
        return new(result.ToImmutable());
    }

    private static bool RowIdentityMatches(CapturedRow row) => CaptureKey(row.Row.Video) == row.Key;
    private static DescriptorKey CaptureKey(VideoInformation video)
        => new(NormalizeIdentifier(video.Identifier.Id), string.IsNullOrWhiteSpace(video.BvId) ? null : video.BvId);
    private static string? NormalizeIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "0") return null;
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var aid) && aid > 0
            ? aid.ToString(CultureInfo.InvariantCulture) : id;
    }
    private static bool IsBvId(string? id) => id is { Length: > 2 and <= 128 }
        && id.StartsWith("BV", StringComparison.Ordinal) && id.All(char.IsAsciiLetterOrDigit);
    private static bool TryLookup(DescriptorKey key, out string lookup, out string? aid, out string? bv)
    {
        lookup = string.Empty; aid = null; bv = key.BvId;
        if (bv is not null && !IsBvId(bv)) return false;
        if (key.Id is { } id)
        {
            if (id.Length > 128) return false;
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) aid = id;
            else if (IsBvId(id))
            {
                if (bv is not null && bv != id) return false;
                bv = id;
            }
            else return false; // Invalid nonempty identity is not silently discarded as "unknown".
        }
        // The existing SDK accepts both aid and BV. A missing aid may use a supplied real BV;
        // a missing BV stays unknown until the actual detail response supplies it. No fake CID/ID.
        lookup = aid ?? bv ?? string.Empty;
        return lookup.Length > 0;
    }
}
