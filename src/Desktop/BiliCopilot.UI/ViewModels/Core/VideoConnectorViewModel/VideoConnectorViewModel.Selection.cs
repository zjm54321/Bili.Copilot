// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class VideoConnectorViewModel
{
    private object? FindPrevVideo()
    {
        // 1. 先检查分P列表中是否有下一个视频.
        if (Sections.OfType<VideoPlayerPartSectionDetailViewModel>().FirstOrDefault() is VideoPlayerPartSectionDetailViewModel partSection)
        {
            var index = partSection.Parts.ToList().IndexOf(_part);
            if (index > 0)
            {
                return partSection.Parts[index - 1];
            }
        }

        // 2. 检查播放列表中是否有下一个视频.
        if (_snapshot.Playlist is not null)
        {
            var index = _snapshot.Playlist.Select(p => p.Video).ToList().IndexOf(_snapshot.Video);
            if (index > 0)
            {
                return _snapshot.Playlist[index - 1].Video;
            }

            if (SettingsToolkit.ReadLocalSetting(SettingNames.EndWithPlaylist, true))
            {
                return default;
            }
        }

        // 3. 检查合集中是否有上一个视频.
        if (Sections.OfType<VideoPlayerSeasonSectionDetailViewModel>().FirstOrDefault() is VideoPlayerSeasonSectionDetailViewModel seasonSection)
        {
            var selectedItem = seasonSection.Items.Find(p => p.IsSelected);
            var index = seasonSection.Items.ToList().IndexOf(selectedItem);
            if (index > 0)
            {
                return seasonSection.Items[index - 1].Data;
            }
        }

        return default;
    }

    private object? FindNextVideo()
    {
        // 1. 先检查分P列表中是否有下一个视频.
        if (Sections.OfType<VideoPlayerPartSectionDetailViewModel>().FirstOrDefault() is VideoPlayerPartSectionDetailViewModel partSection)
        {
            var index = partSection.Parts.ToList().IndexOf(_part);
            if (index < partSection.Parts.Count - 1)
            {
                return partSection.Parts[index + 1];
            }
        }

        // 2. 检查播放列表中是否有下一个视频.
        if (_snapshot.Playlist is not null)
        {
            var index = _snapshot.Playlist.ConvertAll(p => p.Video).IndexOf(_snapshot.Video);
            if (index < _snapshot.Playlist.Count - 1)
            {
                return _snapshot.Playlist[index + 1].Video;
            }

            if (SettingsToolkit.ReadLocalSetting(SettingNames.EndWithPlaylist, true))
            {
                return default;
            }
        }

        // 3. 检查合集中是否有下一个视频.
        if (Sections.OfType<VideoPlayerSeasonSectionDetailViewModel>().FirstOrDefault() is VideoPlayerSeasonSectionDetailViewModel seasonSection)
        {
            var selectedItem = seasonSection.Items.Find(p => p.IsSelected);
            var index = seasonSection.Items.ToList().IndexOf(selectedItem);
            if (index < seasonSection.Items.Count - 1)
            {
                return seasonSection.Items[index + 1].Data;
            }
        }

        // 4. 检查推荐视频中是否有下一个视频.
        var isAutoPlayRecommendVideo = SettingsToolkit.ReadLocalSetting(SettingNames.AutoPlayNextRecommendVideo, false);
        if (isAutoPlayRecommendVideo && Sections.OfType<VideoPlayerRecommendSectionDetailViewModel>().FirstOrDefault() is VideoPlayerRecommendSectionDetailViewModel recommendSection)
        {
            return recommendSection.Items.FirstOrDefault()?.Data;
        }

        return default;
    }
}
