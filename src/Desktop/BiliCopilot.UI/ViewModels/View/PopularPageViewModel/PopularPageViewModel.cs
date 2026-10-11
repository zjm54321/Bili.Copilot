// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Media;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 流行视频页面视图模型.
/// </summary>
public sealed partial class PopularPageViewModel : LayoutPageViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PopularPageViewModel"/> class.
    /// </summary>
    public PopularPageViewModel(
        IVideoDiscoveryService discoveryService,
        ILogger<PopularPageViewModel> logger)
    {
        _service = discoveryService;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override string GetPageKey() => nameof(PopularPage);

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Sections.Count > 0)
        {
            return;
        }

        Sections.Add(new PopularSectionItemViewModel(FluentIcons.Common.Symbol.Balloon, ResourceToolkit.GetLocalizedString(StringNames.Recommend), PopularSectionType.Recommend));
        Sections.Add(new PopularSectionItemViewModel(FluentIcons.Common.Symbol.Fire, ResourceToolkit.GetLocalizedString(StringNames.Hot), PopularSectionType.Hot));
        Sections.Add(new PopularSectionItemViewModel(FluentIcons.Common.Symbol.RibbonStar, ResourceToolkit.GetLocalizedString(StringNames.Rank), PopularSectionType.Rank));
        await LoadPartitionsAsync();
        await Task.Delay(200);
        var lastSelectedSectionId = SettingsToolkit.ReadLocalSetting(SettingNames.PopularPageLastSelectedSectionId, PopularSectionType.Recommend.ToString());
        if (int.TryParse(lastSelectedSectionId, out var partitionId))
        {
            var section = Sections.OfType<PopularRankPartitionViewModel>().FirstOrDefault(p => p.Data.Id == partitionId.ToString());
            if (section != null)
            {
                SelectSectionCommand.Execute(section);
            }
            else
            {
                SelectSectionCommand.Execute(Sections.First());
            }
        }
        else if (Enum.TryParse<PopularSectionType>(lastSelectedSectionId, out var sectionType))
        {
            var section = Sections.OfType<PopularSectionItemViewModel>().First(p => p.Type == sectionType);
            SelectSectionCommand.Execute(section);
        }
        else
        {
            SelectSectionCommand.Execute(Sections.First());
        }

        SectionInitialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectSection(IPopularSectionItemViewModel vm)
    {
        if (vm is null || vm == SelectedSection)
        {
            return;
        }

        // The previous selection may still be waiting in the dispatcher queue.
        // Cache the list under its actual owner, not the pending selection.
        if (_displayedSection is not null && Videos.Count > 0)
        {
            _videoCache[_displayedSection] = Videos.ToList();
        }

        var generation = ++_videoGeneration;
        IsVideoLoading = true;
        SelectedSection = vm;
        var sectionId = vm switch
        {
            PopularSectionItemViewModel section => section.Type.ToString(),
            PopularRankPartitionViewModel partition => partition.Data.Id,
            _ => string.Empty
        };
        SettingsToolkit.WriteLocalSetting(SettingNames.PopularPageLastSelectedSectionId, sectionId);

        if (!this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () => await ApplySectionAsync(vm, generation)))
        {
            // Do not leave a pending selection permanently blocking retries.
            _ = ApplySectionAsync(vm, generation);
        }
    }

    private async Task ApplySectionAsync(IPopularSectionItemViewModel vm, long generation)
    {
        if (!IsCurrentVideoGeneration(generation))
        {
            return;
        }

        Videos.Clear();
        _displayedSection = vm;
        IsVideoLoading = false;
        if (_videoCache.TryGetValue(vm, out var cacheVideos))
        {
            foreach (var video in cacheVideos)
            {
                Videos.Add(video);
            }

            VideoListUpdated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            await LoadVideosAsync();
        }
    }

    [RelayCommand]
    private async Task LoadVideosAsync()
    {
        if (IsVideoLoading || SelectedSection is null)
        {
            return;
        }

        if (SelectedSection is PopularSectionItemViewModel section2 && section2.Type == PopularSectionType.Rank && Videos.Count > 0)
        {
            return;
        }

        var generation = _videoGeneration;
        IsVideoLoading = true;
        try
        {
            if (SelectedSection is PopularSectionItemViewModel section)
            {
                if (section.Type == PopularSectionType.Recommend)
                {
                    await LoadRecommendVideosAsync(generation);
                }
                else if (section.Type == PopularSectionType.Hot)
                {
                    await LoadHotVideosAsync(generation);
                }
                else if (section.Type == PopularSectionType.Rank && Videos.Count == 0)
                {
                    await LoadTotalRankVideosAsync(generation);
                }
            }
            else if (SelectedSection is PopularRankPartitionViewModel partition && Videos.Count == 0)
            {
                await LoadPartitionRankVideosAsync(partition.Data, generation);
            }

            if (IsCurrentVideoGeneration(generation))
            {
                VideoListUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (IsCurrentVideoGeneration(generation)
                && !this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (IsCurrentVideoGeneration(generation))
                    {
                        IsVideoLoading = false;
                    }
                }))
            {
                IsVideoLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task RefreshVideoAsync()
    {
        if (SelectedSection is null)
        {
            return;
        }

        // Refresh is a new generation even when the selected section is unchanged.
        ++_videoGeneration;
        Videos.Clear();
        _displayedSection = SelectedSection;
        _videoCache.Remove(SelectedSection);
        if (SelectedSection is PopularSectionItemViewModel section)
        {
            if (section.Type == PopularSectionType.Recommend)
            {
                _recommendOffset = 0;
            }
            else if (section.Type == PopularSectionType.Hot)
            {
                _hotOffset = 0;
            }
        }

        IsVideoLoading = false;
        await LoadVideosAsync();
    }

    private bool IsCurrentVideoGeneration(long generation) => generation == _videoGeneration;

    [RelayCommand]
    private async Task PlayCuratedListAsync()
    {
        try
        {
            var videos = await _service.GetCuratedPlaylistAsync();
            var firstSnapshot = new MediaSnapshot(videos[0]) { Playlist = videos.ToList().ConvertAll(p => new MediaSnapshot(p)) };
            this.Get<AppViewModel>().OpenPlayerCommand.Execute(firstSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取精选视频时失败");
        }
    }
}
