// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal sealed partial class PgcMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<PgcMediaSourceResolver> logger) : MediaSourceResolverBase
{
    private MediaSnapshot _snapshot;
    private EpisodeInformation _episode;
    private Task? _initTask;
    private DashMediaInformation? _playbackInfo;

    public void Initialize(MediaSnapshot snapshot, EpisodeInformation episode)
    {
        lock (InitializationSync)
        {
            InvalidateInitialization();
            _snapshot = snapshot;
            _episode = episode;
        }
    }

    protected override void ResetInitialization()
    {
        _playbackInfo = default;
        _initTask = default;
    }

    internal void ResetSnapshot(MediaSnapshot snapshot)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
        }
    }

    internal PlayerFormatInformation? GetCurrentFormat()
    {
        var preferFormatSetting = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PreferQuality, PreferQualityType.Auto);
        var availableFormats = _playbackInfo.Formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default)).Where(p => p.IsEnabled).ToList();
        SourceItemViewModel? selectedFormat = default;
        if (_snapshot.PreferQuality > 0)
        {
            selectedFormat = availableFormats.Find(p => p.Data.Quality == _snapshot.PreferQuality);
        }

        if (selectedFormat == null)
        {
            if (preferFormatSetting == PreferQualityType.Auto)
            {
                var lastSelectedFormat = SettingsToolkit.ReadLocalSetting(SettingNames.LastSelectedVideoQuality, 0);
                selectedFormat = availableFormats.Find(p => p.Data.Quality == lastSelectedFormat);
            }
            else if (preferFormatSetting == PreferQualityType.UHD)
            {
                selectedFormat = availableFormats.Find(p => p.Data.Quality == 120);
            }
            else if (preferFormatSetting == PreferQualityType.HD)
            {
                var hdFormats = availableFormats.Where(p => p.Data.Quality == 116 || p.Data.Quality == 80).ToList();
                selectedFormat = hdFormats.OrderByDescending(p => p.Data.Quality).FirstOrDefault();
            }
        }

        if (selectedFormat is null)
        {
            var maxQuality = availableFormats.Max(p => p.Data.Quality);
            selectedFormat = availableFormats.Find(p => p.Data.Quality == maxQuality);
        }

        return selectedFormat.Data;
    }

    public override IMpvMediaSourceResolver Clone() => throw new NotImplementedException();

    public override async Task<MpvMediaSource> GetSourceAsync()
    {
        var request = CaptureInitialization();
        var volume = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerVolume, 100d);
        var speed = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerSpeed, 1d);
        var cookies = cookiesResolver.GetCookieString();
        const string referer = InternalHttpExtensions.VideoReferer;
        const string userAgent = InternalHttpExtensions.VideoUserAgent;
        var headers = new Dictionary<string, string>
        {
            { "Cookie", cookies },
            { "Referer", referer },
        };

        var options = new MpvPlayOptions
        {
            WindowHandle = WindowHandle,
            InitialVolume = volume,
            InitialSpeed = speed,
            UserAgent = userAgent,
            HttpHeaders = headers,
            EnableCookies = true,
        };

        await LoadMediaInfoAsync(request);
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            var id = _snapshot.Episode?.Identifier.Id ?? _snapshot.Season?.Identifier.Id;
            var title = _snapshot.Episode?.Identifier.Title ?? _snapshot.Season?.Identifier.Title;
            options.MediaName = title;
            var (videoUrl, audioUrl) = GetPlayUrls();
            var fileUrl = string.Empty;
            if (!string.IsNullOrEmpty(videoUrl))
            {
                fileUrl = videoUrl;
                if (!string.IsNullOrEmpty(audioUrl))
                {
                    options.AudioTracks ??= [];
                    options.AudioTracks.Add(audioUrl);
                }
            }
            else if (!string.IsNullOrEmpty(audioUrl))
            {
                fileUrl = audioUrl;
            }

            return new MpvMediaSource(fileUrl, id, title, options);
        }
    }

    public async Task<List<PlayerFormatInformation>?> GetSourcesAsync()
    {
        var request = CaptureInitialization();
        await LoadMediaInfoAsync(request);
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            return [.. _playbackInfo!.Formats];
        }
    }

    internal Task LoadMediaInfoAsync() => LoadMediaInfoAsync(CaptureInitialization());

    private async Task LoadMediaInfoAsync((long Generation, CancellationToken CancellationToken) request)
    {
        Task loadTask;
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            if (_snapshot is null || _episode is null)
            {
                throw new InvalidOperationException("必须先初始化媒体信息.");
            }

            loadTask = _initTask ??= LoadMediaInfoCoreAsync(_episode, request);
        }

        await loadTask;
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
        }
    }

    private async Task LoadMediaInfoCoreAsync(EpisodeInformation episode, (long Generation, CancellationToken CancellationToken) request)
    {
        try
        {
            var cid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid);
            var seasonType = episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.SeasonType);
            var info = await playerService.GetPgcPlayDetailAsync(cid.ToString(), episode.Identifier.Id, seasonType, request.CancellationToken);
            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
                _playbackInfo = info;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
            }

            logger.LogError(ex, "获取剧集播放信息失败");
            throw;
        }
    }

    private (string? video, string? audio) GetPlayUrls()
    {
        var videoSegments = _playbackInfo.Videos;
        var audioSegments = _playbackInfo.Audios;
        var selectedFormat = GetCurrentFormat();
        if (selectedFormat is null)
        {
            throw new InvalidCastException("无法获取到有效的播放地址");
        }

        var maxAudioQuality = audioSegments?.Max(p => Convert.ToInt32(p.Id));
        var preferCodec = AppToolkit.GetPreferCodecId();
        var vSeg = videoSegments?.FirstOrDefault(p => p.Id == selectedFormat.Quality.ToString() && p.Codecs.Contains(preferCodec))
            ?? videoSegments?.FirstOrDefault(p => p.Id == selectedFormat.Quality.ToString());
        var aSeg = audioSegments?.FirstOrDefault(p => p.Id == maxAudioQuality.ToString());

        var videoUrl = vSeg?.BaseUrl;
        var audioUrl = aSeg?.BaseUrl;

        if (SettingsToolkit.ReadLocalSetting(SettingNames.PlayWithoutP2P, false))
        {
            if (vSeg?.BackupUrls is not null)
            {
                string[] videoUrls = [vSeg?.BaseUrl, .. vSeg?.BackupUrls];
                videoUrl = Array.Find(videoUrls, p => !AppToolkit.IsP2PUrl(p)) ?? vSeg?.BaseUrl;
            }

            if (aSeg?.BackupUrls is not null)
            {
                string[] audioUrls = [aSeg?.BaseUrl, .. aSeg?.BackupUrls];
                audioUrl = Array.Find(audioUrls, p => !AppToolkit.IsP2PUrl(p)) ?? aSeg?.BaseUrl;
            }
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedPgcQuality, selectedFormat.Quality);
        return (videoUrl, audioUrl);
    }
}
