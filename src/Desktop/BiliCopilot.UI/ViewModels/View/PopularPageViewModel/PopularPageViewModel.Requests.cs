// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 流行视频页面视图模型.
/// </summary>
public sealed partial class PopularPageViewModel
{
    private async Task LoadRecommendVideosAsync(long generation)
    {
        try
        {
            var (videos, nextOffset) = await _service.GetRecommendVideoListAsync(_recommendOffset);
            if (!IsCurrentVideoGeneration(generation))
            {
                return;
            }

            _recommendOffset = nextOffset;
            TryAddVideos(videos, VideoCardStyle.Recommend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载推荐视频时出错.");
        }
    }

    private async Task LoadHotVideosAsync(long generation)
    {
        try
        {
            var (videos, nextOffset) = await _service.GetHotVideoListAsync(_hotOffset);
            if (!IsCurrentVideoGeneration(generation))
            {
                return;
            }

            _hotOffset = nextOffset;
            TryAddVideos(videos, VideoCardStyle.Hot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载热门视频时出错.");
        }
    }

    private async Task LoadTotalRankVideosAsync(long generation)
    {
        try
        {
            var videos = await _service.GetGlobalRankingListAsync();
            if (!IsCurrentVideoGeneration(generation))
            {
                return;
            }

            TryAddVideos(videos, VideoCardStyle.Rank);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载全站排行榜视频时出错.");
        }
    }

    private async Task LoadPartitionRankVideosAsync(Partition partition, long generation)
    {
        try
        {
            var videos = await _service.GetPartitionRankingListAsync(partition);
            if (!IsCurrentVideoGeneration(generation))
            {
                return;
            }

            TryAddVideos(videos, VideoCardStyle.Rank);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"尝试加载 {partition.Name} 分区排行榜视频时出错.");
        }
    }

    private async Task LoadPartitionsAsync()
    {
        IsPartitionLoading = true;

        try
        {
            var partitions = await _service.GetVideoPartitionsAsync();
            if (partitions != null)
            {
                // 去除资讯分区，因为资讯分区没有排行榜.
                foreach (var item in partitions.Where(p => p.Id != "202"))
                {
                    Sections.Add(new PopularRankPartitionViewModel(item));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载视频分区列表时出错.");
        }

        IsPartitionLoading = false;
    }

    private void TryAddVideos(IReadOnlyList<VideoInformation> videos, VideoCardStyle style)
    {
        if (videos is not null)
        {
            foreach (var item in videos)
            {
                Videos.Add(new VideoItemViewModel(item, style));
            }
        }
    }
}
