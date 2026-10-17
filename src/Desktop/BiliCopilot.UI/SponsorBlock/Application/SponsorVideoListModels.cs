using System.Collections.Immutable;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed record SponsorVideoListAccount(string? AccountId, long Generation, CancellationToken Lifetime);
internal sealed record SponsorVideoListScope(Guid Id, long Generation, CancellationToken Lifetime);
internal sealed record SponsorVideoListContext(SponsorVideoListAccount Account, long PreferenceRevision, bool Enabled);
internal sealed record SponsorVideoListRow(object Original, VideoInformation Video);
internal sealed record SponsorVideoListSnapshot(SponsorVideoListScope Scope, SponsorVideoListAccount Account,
    long PreferenceRevision, long PassRevision, ImmutableArray<SponsorVideoListRow> Rows);
internal enum SponsorVideoListStatus { Original, Pending, Projected, Unavailable, Inactive, Closed }
internal sealed record SponsorVideoListProjection(SponsorVideoListSnapshot Snapshot,
    ImmutableArray<object> RetainedRows, SponsorVideoListStatus Status);
