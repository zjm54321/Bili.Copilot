using System.Globalization;
using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Classic.Backends.Web;
using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Classic;

// Messages created here are fixed safe disclosures, never remote exception text/URLs/headers.
public sealed class ClassicSourceException : Exception
{
    public ClassicSourceException() { }
    public ClassicSourceException(string? message) : base(message) { }
    public ClassicSourceException(string? message, Exception? innerException) : base(message, innerException) { }
}

// These preferences and account eligibility are captured by the composition root, not New connectors.
public sealed record ClassicSourcePreferences(bool AutoLoadHistory, bool AutoPlayRecommendations,
    bool ReportProgress, PreferQualityType QualityPreference = PreferQualityType.Auto,
    int LastVideoQuality = 0, int LastPgcQuality = 0, string? PreferredCodec = null,
    bool WithoutP2P = false);

public sealed class ClassicSourceResult
{
    public required ClassicResolvedMedia Media { get; init; }
    public VideoPlayerView? VideoPage { get; init; }
    public PgcPlayerView? PgcPage { get; init; }
    public LivePlayerView? LivePage { get; init; }
    public IReadOnlyList<VideoPart> Parts { get; init; } = Array.Empty<VideoPart>();
    public IReadOnlyList<EpisodeInformation> Episodes { get; init; } = Array.Empty<EpisodeInformation>();
    public IReadOnlyList<VideoInformation> SeasonVideos { get; init; } = Array.Empty<VideoInformation>();
    public IReadOnlyList<VideoInformation> Recommendations { get; init; } = Array.Empty<VideoInformation>();
    public IReadOnlyList<PlayerFormatInformation> Qualities { get; init; } = Array.Empty<PlayerFormatInformation>();
    public PlayerFormatInformation? SelectedQuality { get; init; }
    public ClassicProgressIdentity? ProgressIdentity { get; init; }
}

public sealed class ClassicMediaSourceService
{
    private readonly IPlayerService _player;
    private readonly IBiliCookiesResolver _cookies;
    private readonly Func<PlayerFormatInformation, bool> _canPlayFormat;
    public ClassicSourcePreferences Preferences { get; }

    public ClassicMediaSourceService(IPlayerService player, IBiliCookiesResolver cookies,
        ClassicSourcePreferences preferences, Func<PlayerFormatInformation, bool> canPlayFormat)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _canPlayFormat = canPlayFormat ?? throw new ArgumentNullException(nameof(canPlayFormat));
    }

    public async Task<ClassicSourceResult> ResolveAsync(ClassicPlaybackRequest request,
        ClassicBackendKind backend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = request.Item;
        if (item.IsPrivate && backend is ClassicBackendKind.External or ClassicBackendKind.Web)
            throw new ClassicSourceException("Private playback is unavailable for this backend.");
        if (backend == ClassicBackendKind.Web)
        {
            // No page-detail, DASH or cookies calls on the Web path.
            var webStart = item.StartPosition ?? 0; // The route validates; never clamp explicit instructions.
            try
            {
                return new() { Media = new() { Request = request,
                    PageUri = WebPlaybackRoute.CreatePageUri(item, webStart),
                    Headers = new Dictionary<string, string>(), StartSeconds = webStart } };
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                // The shared pure route emits fixed, safe validation messages only.
                throw new ClassicSourceException(ex.Message);
            }
        }
        if (backend is not (ClassicBackendKind.Island or ClassicBackendKind.Native or ClassicBackendKind.External))
            throw new ClassicSourceException("Selected Classic backend is unavailable.");

        VideoPlayerView? videoPage = null;
        PgcPlayerView? pgcPage = null;
        LivePlayerView? livePage = null;
        DashMediaInformation? dash = null;
        LiveMediaInformation? live = null;
        VideoPart[] parts = [];
        EpisodeInformation[] episodes = [];
        VideoInformation[] season = [];
        VideoInformation[] recommendations = [];
        ClassicProgressIdentity? progress = null;
        var start = Seconds(item.StartPosition ?? 0);
        if (item.Type == BiliMediaType.Video && item.Video is not null)
        {
            videoPage = await _player.GetVideoPageDetailAsync(item.Video.Identifier, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            parts = videoPage.Parts?.ToArray() ?? [];
            var part = item.PreferPart is not null
                ? parts.SingleOrDefault(p => p.Identifier.Id == item.PreferPart.Identifier.Id)
                : Preferences.AutoLoadHistory && !item.IsPrivate
                    ? parts.FirstOrDefault(p => p.Identifier.Id == videoPage.Progress?.Cid) ?? parts.FirstOrDefault()
                    : parts.FirstOrDefault();
            if (part is null) throw new ClassicSourceException("Requested video part is unavailable.");
            if (item.StartPosition is null && Preferences.AutoLoadHistory && !item.IsPrivate
                && videoPage.Progress?.Cid == part.Identifier.Id)
                start = Seconds(videoPage.Progress.Progress);
            item = item with { Video = videoPage.Information, PreferPart = part };
            var matchingSeasons = videoPage.Seasons?.Where(s => s.Videos.Any(v => v.Identifier.Id == item.Video.Identifier.Id)).ToArray();
            if (matchingSeasons?.Length == 1) season = matchingSeasons[0].Videos.ToArray();
            recommendations = videoPage.Recommends?.ToArray() ?? [];
            progress = new(item.Video.Identifier.Id, part.Identifier.Id, null, null, item.IsPrivate);
            dash = await _player.GetVideoPlayDetailAsync(item.Video.Identifier,
                long.Parse(part.Identifier.Id, CultureInfo.InvariantCulture), cancellationToken);
        }
        else if (item.Type == BiliMediaType.Pgc)
        {
            pgcPage = await _player.GetPgcPageDetailAsync(item.Season?.Identifier.Id, item.Episode?.Identifier.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            episodes = pgcPage.Episodes?.ToArray() ?? [];
            var episode = item.Episode is not null
                ? episodes.SingleOrDefault(e => e.Identifier.Id == item.Episode.Identifier.Id)
                : Preferences.AutoLoadHistory && !item.IsPrivate
                    ? episodes.FirstOrDefault(e => e.Identifier.Id == pgcPage.Progress?.Cid) ?? episodes.FirstOrDefault()
                    : episodes.FirstOrDefault();
            if (episode is null) throw new ClassicSourceException("Requested episode is unavailable.");
            if (item.StartPosition is null && Preferences.AutoLoadHistory && !item.IsPrivate
                && pgcPage.Progress?.Cid == episode.Identifier.Id) start = Seconds(pgcPage.Progress.Progress);
            item = item with { Season = pgcPage.Information, Episode = episode };
            var cid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString(CultureInfo.InvariantCulture);
            var aid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString(CultureInfo.InvariantCulture);
            var seasonType = episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.SeasonType);
            progress = new(aid, cid, episode.Identifier.Id, item.Season.Identifier.Id, item.IsPrivate);
            dash = await _player.GetPgcPlayDetailAsync(cid, episode.Identifier.Id, seasonType, cancellationToken);
        }
        else if (item.Type == BiliMediaType.Live && item.Live is not null)
        {
            livePage = await _player.GetLivePageDetailAsync(item.Live.Identifier, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            item = item with { Live = livePage.Information };
            live = await _player.GetLivePlayDetailAsync(item.Live.Identifier, item.PreferQuality is > 0 ? item.PreferQuality.Value : 10000, false, cancellationToken);
            start = 0;
        }
        else throw new ClassicSourceException("Media identity is missing.");
        cancellationToken.ThrowIfCancellationRequested();
        var qualities = (dash?.Formats ?? live?.Formats)?.Where(_canPlayFormat).ToArray() ?? [];
        var selected = SelectQuality(item, qualities);
        if (live is not null)
            selected = qualities.FirstOrDefault(q => q.Quality == item.PreferQuality && live.Lines.Any(line => line.Quality == q.Quality))
                ?? qualities.FirstOrDefault(q => live.Lines.Any(line => line.Quality == q.Quality));
        if (item.PreferQuality is > 0 && selected?.Quality != item.PreferQuality)
            throw new ClassicSourceException("Requested quality is unavailable.");
        var video = dash?.Videos?.FirstOrDefault(v => v.Id == selected?.Quality.ToString(CultureInfo.InvariantCulture)
                && Preferences.PreferredCodec is not null && v.Codecs.Contains(Preferences.PreferredCodec, StringComparison.Ordinal))
            ?? dash?.Videos?.FirstOrDefault(v => v.Id == selected?.Quality.ToString(CultureInfo.InvariantCulture));
        // UGC/PGC are video requests: an audio track cannot substitute for an eligible video.
        if ((item.Type is BiliMediaType.Video or BiliMediaType.Pgc) && (selected is null || video is null))
            throw new ClassicSourceException("Playable video quality or matching video segment is unavailable.");
        var audio = dash?.Audios?.OrderByDescending(a => int.TryParse(a.Id, out var id) ? id : 0).FirstOrDefault();
        Uri? videoUri = video is null ? null : SegmentUri(video);
        if (live is not null)
        {
            var url = live.Lines?.Where(l => selected is null || l.Quality == selected.Quality)
                .SelectMany(l => l.Urls).FirstOrDefault();
            if (url is not null) videoUri = new Uri(url.ToString(), UriKind.Absolute);
        }
        if (videoUri is null && audio is null) throw new ClassicSourceException("Playable media is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        // Headers never enter diagnostic records or messages.
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = InternalHttpExtensions.VideoReferer,
            ["User-Agent"] = InternalHttpExtensions.VideoUserAgent,
            ["Cookie"] = _cookies.GetCookieString(),
        };
        return new()
        {
            Media = new() { Request = request with { Item = item }, Dash = dash, Live = live,
                VideoSegment = video, AudioSegment = audio, VideoUri = videoUri,
                AudioUri = audio is null ? null : SegmentUri(audio), PageUri = PageUri(item),
                Headers = headers, StartSeconds = start },
            VideoPage = videoPage, PgcPage = pgcPage, LivePage = livePage, Parts = Array.AsReadOnly(parts),
            Episodes = Array.AsReadOnly(episodes), SeasonVideos = Array.AsReadOnly(season),
            Recommendations = Array.AsReadOnly(recommendations), Qualities = Array.AsReadOnly(qualities),
            SelectedQuality = selected, ProgressIdentity = progress,
        };
    }

    private PlayerFormatInformation? SelectQuality(ClassicMediaItem item, IReadOnlyList<PlayerFormatInformation> qualities)
    {
        if (item.PreferQuality is < 0) throw new ClassicSourceException("Requested quality is invalid.");
        if (item.PreferQuality is > 0)
            return qualities.FirstOrDefault(q => q.Quality == item.PreferQuality)
                ?? throw new ClassicSourceException("Requested quality is unavailable.");
        PlayerFormatInformation? selected = null;
        if (Preferences.QualityPreference == PreferQualityType.Auto)
        {
            var last = item.Type == BiliMediaType.Pgc ? Preferences.LastPgcQuality : Preferences.LastVideoQuality;
            selected = qualities.FirstOrDefault(q => q.Quality == last);
        }
        else if (Preferences.QualityPreference == PreferQualityType.UHD)
            selected = qualities.FirstOrDefault(q => q.Quality == 120);
        else if (Preferences.QualityPreference == PreferQualityType.HD)
            selected = qualities.Where(q => q.Quality is 116 or 80).OrderByDescending(q => q.Quality).FirstOrDefault();
        return selected ?? qualities.OrderByDescending(q => q.Quality).FirstOrDefault();
    }

    private Uri SegmentUri(DashSegmentInformation segment)
    {
        var url = segment.BaseUrl;
        if (Preferences.WithoutP2P)
            url = new[] { segment.BaseUrl }.Concat(segment.BackupUrls ?? [])
                .FirstOrDefault(u => !BiliCopilot.UI.Toolkits.AppToolkit.IsP2PUrl(u)) ?? url;
        return new Uri(url, UriKind.Absolute);
    }

    private static double Seconds(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;

    private static Uri PageUri(ClassicMediaItem item)
    {
        var id = item.Type switch
        {
            BiliMediaType.Video => item.Video?.Identifier.Id,
            BiliMediaType.Pgc => item.Episode?.Identifier.Id ?? item.Season?.Identifier.Id,
            BiliMediaType.Live => item.Live?.Identifier.Id,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id)) throw new ClassicSourceException("Page identity is missing.");
        var safe = Uri.EscapeDataString(id);
        var url = item.Type switch
        {
            BiliMediaType.Video => "https://www.bilibili.com/video/" + (id.StartsWith("BV", StringComparison.Ordinal) ? safe : "av" + safe),
            BiliMediaType.Pgc => "https://www.bilibili.com/bangumi/play/" + (item.Episode is null ? "ss" : "ep") + safe,
            _ => "https://live.bilibili.com/" + safe,
        };
        return new Uri(url);
    }
}
