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
    private async Task LoadRecommendVideosAsync(long generation, long accountGeneration, CancellationToken token)
    {
        try
        {
            var (videos, nextOffset) = await _service.GetRecommendVideoListAsync(_recommendOffset, token);
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            _recommendOffset = nextOffset;
            TryAddVideos(videos, VideoCardStyle.Recommend, generation, accountGeneration, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            HasAppendError = true;
            _logger.LogError(ex, "尝试加载推荐视频时出错.");
        }
    }

    private async Task LoadHotVideosAsync(long generation, long accountGeneration, CancellationToken token)
    {
        try
        {
            var (videos, nextOffset) = await _service.GetHotVideoListAsync(_hotOffset, token);
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            _hotOffset = nextOffset;
            TryAddVideos(videos, VideoCardStyle.Hot, generation, accountGeneration, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            HasAppendError = true;
            _logger.LogError(ex, "尝试加载热门视频时出错.");
        }
    }

    private async Task LoadTotalRankVideosAsync(long generation, long accountGeneration, CancellationToken token)
    {
        try
        {
            var videos = await _service.GetGlobalRankingListAsync();
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            TryAddVideos(videos, VideoCardStyle.Rank, generation, accountGeneration, token);
        }
        catch (Exception ex)
        {
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            HasAppendError = true;
            _logger.LogError(ex, "尝试加载全站排行榜视频时出错.");
        }
    }

    private async Task LoadPartitionRankVideosAsync(Partition partition, long generation, long accountGeneration, CancellationToken token)
    {
        try
        {
            var videos = await _service.GetPartitionRankingListAsync(partition);
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            TryAddVideos(videos, VideoCardStyle.Rank, generation, accountGeneration, token);
        }
        catch (Exception ex)
        {
            if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
            HasAppendError = true;
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

    private void TryAddVideos(IReadOnlyList<VideoInformation> videos, VideoCardStyle style,
        long generation, long accountGeneration, CancellationToken token)
    {
        if (videos is not null)
        {
            foreach (var item in videos)
            {
                if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
                if (Videos.Any(video => video.Data.Identifier.Id == item.Identifier.Id)) continue;
                var card = new VideoItemViewModel(item, style);
                if (!IsCurrentVideoRequest(generation, accountGeneration, token)) return;
                Videos.Add(card);
            }
        }
    }
}
