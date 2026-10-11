// Copyright (c) Bili Copilot. All rights reserved.
using System.ComponentModel;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Classic;

public enum PlaybackExperience { New = 0, Classic = 1 }
public enum ClassicBackendKind { Island = 0, Native = 1, External = 2, Web = 3 }
public enum ClassicDisplayMode { Default = 0, FullScreen = 1, FullWindow = 2, CompactOverlay = 3, NewWindow = 4 }
public enum ClassicDecodePreference { Auto = 0, Software = 1, D3D11 = 2 }
public enum ClassicPlaybackState { Idle, Loading, Playing, Paused, Ended, Failed, Closing, Closed }
[Flags]
public enum ClassicCapabilities { None = 0, Transport = 1, Seek = 2, Volume = 4, Speed = 8, Quality = 16, Progress = 32 }

public sealed record ClassicMediaItem(BiliMediaType Type, VideoInformation? Video,
    SeasonInformation? Season, EpisodeInformation? Episode, LiveInformation? Live,
    VideoPart? PreferPart, int? PreferQuality, double? StartPosition, bool IsPrivate);

public sealed record ClassicPlaybackRequest(ClassicMediaItem Item, IReadOnlyList<ClassicMediaItem> Playlist, int? PlaylistIndex);

public static class ClassicRequestMapper
{
    // Value capture of snapshot fields; BiliKernel model objects are treated as read-only.
    public static ClassicPlaybackRequest FromSnapshot(MediaSnapshot snapshot)
        => FromSnapshot(snapshot, null);

    public static ClassicPlaybackRequest FromSnapshot(MediaSnapshot snapshot, int? selectedOrdinal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClassicMediaItem Capture(MediaSnapshot s) => new(s.Type, s.Video, s.Season, s.Episode,
            null, s.PreferPart, s.PreferQuality, s.StartPosition, s.IsPrivate);
        var items = new List<ClassicMediaItem>();
        var snapshots = new List<MediaSnapshot>();
        if (snapshot.Playlist is not null)
            foreach (var s in snapshot.Playlist)
            {
                snapshots.Add(s);
                items.Add(Capture(s));
            }
        return new(Capture(snapshot), items.AsReadOnly(), FindIndex(snapshot, snapshots, selectedOrdinal));
    }

    private static int? FindIndex(MediaSnapshot selected, List<MediaSnapshot> items, int? ordinal)
    {
        if (ordinal is int explicitIndex)
        {
            if (explicitIndex < 0 || explicitIndex >= items.Count) return null;
            var candidate = items[explicitIndex];
            if (ReferenceEquals(selected, candidate)) return explicitIndex;
            if (!SameMedia(selected, candidate)) return null;
            // An explicit click may select repeated media, but not a conflicting known part.
            var part = PartId(selected);
            var candidatePart = PartId(candidate);
            return part is not null && candidatePart is not null && part != candidatePart ? null : explicitIndex;
        }

        int referenceCount = 0;
        int? referenceIndex = null;
        var candidates = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(selected, items[i])) { referenceCount++; referenceIndex = i; }
            if (SameMedia(selected, items[i])) candidates.Add(i);
        }
        if (referenceCount != 0) return referenceCount == 1 ? referenceIndex : null;
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0 || selected.Type != BiliMediaType.Video || PartId(selected) is not string cid) return null;

        int? partIndex = null;
        foreach (var i in candidates)
        {
            var candidateCid = PartId(items[i]);
            // An unspecified duplicate part could be this CID: narrowing would be a guess.
            if (candidateCid is null) return null;
            if (candidateCid != cid) continue;
            if (partIndex is not null) return null;
            partIndex = i;
        }
        return partIndex;
    }

    private static bool SameMedia(MediaSnapshot left, MediaSnapshot right)
    {
        if (left.Type != right.Type) return false;
        var id = MediaId(left);
        return id is not null && string.Equals(id, MediaId(right), StringComparison.Ordinal);
    }

    private static string? MediaId(MediaSnapshot snapshot) => Nonempty(snapshot.Type switch
    {
        BiliMediaType.Video => snapshot.Video?.Identifier.Id,
        BiliMediaType.Pgc => snapshot.Episode?.Identifier.Id,
        _ => null,
    });

    private static string? PartId(MediaSnapshot snapshot) => Nonempty(snapshot.PreferPart?.Identifier.Id);
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static ClassicPlaybackRequest FromLive(LiveInformation live, bool isPrivate = false)
        => new(new(BiliMediaType.Live, null, null, null, live, null, null, null, isPrivate),
            Array.Empty<ClassicMediaItem>(), null);
}

// Headers/URLs are sensitive: deliberately not a record with an auto-generated verbose ToString.
public sealed class ClassicResolvedMedia
{
    public required ClassicPlaybackRequest Request { get; init; }
    public DashMediaInformation? Dash { get; init; }
    public DashSegmentInformation? VideoSegment { get; init; }
    public DashSegmentInformation? AudioSegment { get; init; }
    public LiveMediaInformation? Live { get; init; }
    public Uri? VideoUri { get; init; }
    public Uri? AudioUri { get; init; }
    public Uri? PageUri { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public double StartSeconds { get; init; }
}

public sealed record ClassicBackendStatus(ClassicPlaybackState State, double PositionSeconds,
    double DurationSeconds, double Volume, double Speed, string? SafeError);

// Host attachment is backend-specific, not an object-based generalized surface/coordinator.
public interface IClassicPlaybackBackend : IAsyncDisposable
{
    ClassicBackendKind Kind { get; }
    ClassicCapabilities Capabilities { get; }
    event EventHandler<ClassicBackendStatus>? StatusChanged;
    Task OpenAsync(ClassicResolvedMedia media, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task SeekAsync(double seconds, CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

// Position/duration/seek use seconds; volume is normalized 0–1; speed is a multiplier.
public interface IClassicPlaybackSession : INotifyPropertyChanged, IAsyncDisposable
{
    Guid Id { get; }
    ClassicBackendKind Backend { get; }
    ClassicPlaybackRequest Request { get; }
    ClassicCapabilities Capabilities { get; }
    ClassicPlaybackState State { get; }
    string Title { get; }
    string? ErrorMessage { get; }
    bool IsBusy { get; }
    bool IsClosing { get; }
    bool CanGoNext { get; }
    bool CanGoPrevious { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    double Volume { get; }
    double Speed { get; }
    IReadOnlyList<PlayerFormatInformation> Qualities { get; }
    PlayerFormatInformation? SelectedQuality { get; }
    IReadOnlyList<VideoPart> Parts { get; }
    VideoPart? SelectedPart { get; }
    IReadOnlyList<EpisodeInformation> Episodes { get; }
    EpisodeInformation? SelectedEpisode { get; }
    IAsyncRelayCommand PlayCommand { get; }
    IAsyncRelayCommand PauseCommand { get; }
    IAsyncRelayCommand NextCommand { get; }
    IAsyncRelayCommand PreviousCommand { get; }
    IAsyncRelayCommand<double> SeekCommand { get; }
    IAsyncRelayCommand<double> SetVolumeCommand { get; }
    IAsyncRelayCommand<double> SetSpeedCommand { get; }
    IAsyncRelayCommand<PlayerFormatInformation> SelectQualityCommand { get; }
    IAsyncRelayCommand<VideoPart> SelectPartCommand { get; }
    IAsyncRelayCommand<EpisodeInformation> SelectEpisodeCommand { get; }
    // Zero-based ordinal in Request.Playlist, preserving the complete destination item.
    IAsyncRelayCommand<int> SelectPlaylistItemCommand { get; }
    IAsyncRelayCommand CloseCommand { get; }
    Task OpenAsync(ClassicPlaybackRequest request, CancellationToken cancellationToken);
    Task CloseAsync();
}
