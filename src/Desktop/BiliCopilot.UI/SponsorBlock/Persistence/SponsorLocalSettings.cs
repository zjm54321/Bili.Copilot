using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

// These are local facts already consumed by N1, not wire Hidden values or remote votes.
public enum LocalExclusionKind { Hidden, Downvoted, ExternalSourceExcluded }
public sealed record LocalSegmentExclusion(string MediaKey, string Uuid, int? Source, LocalExclusionKind Kind);
public sealed record LocalExclusionFacts(ImmutableHashSet<PlaybackSegmentKey> Hidden,
    ImmutableHashSet<PlaybackSegmentKey> Downvoted, ImmutableHashSet<PlaybackSegmentKey> ExternalSourceExcluded);

/// <summary>Local preferences only. Category policies apply to all of that category's allowed actions,
/// exactly as in PlaybackPolicy; there is no second action-policy table or evaluation engine.</summary>
public sealed record SponsorLocalSettings
{
    public PlaybackPolicy Policy { get; init; } = DefaultPolicy();
    public PlaybackSpeedPolicy Speed { get; init; } = new();
    public bool NetworkEnabled { get; init; }
    public bool ViewedTelemetryEnabled { get; init; }
    public bool IncludeExternalSegments { get; init; }
    public bool IncludeDanmakuSegments { get; init; }
    public ImmutableArray<LocalSegmentExclusion> Exclusions { get; init; } = [];

    public static PlaybackPolicy DefaultPolicy() => new(0)
    {
        Categories = SponsorRules.Categories.ToImmutableDictionary(c => c.Id, c => c.DefaultPolicy, StringComparer.Ordinal),
    };

    /// <summary>The controller assigns a fresh live policy revision; persisted revisions are not native stamps.</summary>
    public PlaybackPolicy ForPlayback(long revision) => Policy with { Revision = revision };

    /// <summary>Caller attaches these facts only to the matching current media/observation stamp.</summary>
    public LocalExclusionFacts FactsFor(MediaIdentity media)
    {
        ImmutableHashSet<PlaybackSegmentKey> Select(LocalExclusionKind kind) => Exclusions
            .Where(e => e.MediaKey == media.Combined && e.Kind == kind)
            .Select(e => new PlaybackSegmentKey(media, e.Uuid, e.Source)).ToImmutableHashSet();
        return new(Select(LocalExclusionKind.Hidden), Select(LocalExclusionKind.Downvoted),
            Select(LocalExclusionKind.ExternalSourceExcluded));
    }

    internal bool IsValid()
    {
        var p = Policy;
        if (p is null || Speed is null || p.Revision < 0 || !Enum.IsDefined(p.SkipStrategy)
            || !double.IsFinite(p.MinDuration) || p.MinDuration < 0
            || (p.UpcomingMediaSeconds is double upcoming && (!double.IsFinite(upcoming) || upcoming <= 0))
            || !double.IsFinite(Speed.ConfiguredRate) || Speed.ConfiguredRate is < 1.1 or > 16
            || !Enum.IsDefined(Speed.Fallback) || p.Categories is null
            || p.Categories.Count != SponsorRules.Categories.Length
            || SponsorRules.Categories.Any(c => !p.Categories.TryGetValue(c.Id, out var value) || !Enum.IsDefined(value))
            || Exclusions.IsDefault || Exclusions.Length > 4096) return false;
        return Exclusions.All(e => e is not null && MediaIdentity.TryParse(e.MediaKey, out _)
                && ValidText(e.Uuid, 256) && SponsorRules.IsKnownSource(e.Source) && Enum.IsDefined(e.Kind))
            && Exclusions.Distinct().Count() == Exclusions.Length;
    }

    internal static bool ValidText(string? text, int limit) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit && !text.Any(char.IsControl);
}
