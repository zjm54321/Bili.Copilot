// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
        _account = this.Get<AccountViewModel>();
        _cacheAccountGeneration = _account.AccountGeneration;
    }

    /// <inheritdoc/>
    protected override string GetPageKey() => nameof(PopularPage);

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Sections.Count > 0)
        {
            if (_cacheAccountGeneration != _account.AccountGeneration) await RefreshVideoAsync();
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
    private async Task SelectSectionAsync(IPopularSectionItemViewModel vm)
    {
        if (vm is null || vm == SelectedSection)
        {
            return;
        }

        if (_cacheAccountGeneration == _account.AccountGeneration && _displayedSection is not null && Videos.Count > 0)
        {
            _videoCache[_displayedSection] = Videos.ToList();
        }

        var generation = ++_videoGeneration;
        _isSelectingSection = true;
        SelectedSection = vm;
        var sectionId = vm switch
        {
            PopularSectionItemViewModel section => section.Type.ToString(),
            PopularRankPartitionViewModel partition => partition.Data.Id,
            _ => string.Empty
        };
        SettingsToolkit.WriteLocalSetting(SettingNames.PopularPageLastSelectedSectionId, sectionId);

        try
        {
            var cancellation = _videoCancellation;
            var loadTask = _videoLoadTask;
            if (cancellation is not null) await cancellation.CancelAsync();
            // Join the actual canceled SDK request before another section reuses its cursor.
#pragma warning disable VSTHRD003
            try { await loadTask; } catch { /* The previous request already reported its failure. */ }
#pragma warning restore VSTHRD003
            if (generation != _videoGeneration) return;
            ResetAccountCacheIfNeeded();
            Videos.Clear();
            _displayedSection = vm;
            HasAppendError = false;
            IsPrefetchExhausted = false;
            if (_videoCache.TryGetValue(vm, out var cacheVideos))
            {
                foreach (var video in cacheVideos)
                {
                    if (generation != _videoGeneration || _cacheAccountGeneration != _account.AccountGeneration) return;
                    Videos.Add(video);
                }

                if (generation != _videoGeneration || _cacheAccountGeneration != _account.AccountGeneration) return;
                if (vm is PopularRankPartitionViewModel || vm is PopularSectionItemViewModel { Type: PopularSectionType.Rank })
                    IsPrefetchExhausted = true;
                VideoListUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (generation == _videoGeneration) _isSelectingSection = false;
        }
        if (generation == _videoGeneration && Videos.Count == 0) await LoadVideosAsync();
    }

    [RelayCommand]
    private Task LoadVideosAsync()
    {
        if (IsVideoLoading || _isSelectingSection || _isRefreshing || SelectedSection is null) return Task.CompletedTask;
        if (_cacheAccountGeneration != _account.AccountGeneration) return RefreshVideoAsync();
        if (!ReferenceEquals(SelectedSection, _displayedSection)) return Task.CompletedTask;
        if (Videos.Count > 0 && (SelectedSection is PopularRankPartitionViewModel
            || SelectedSection is PopularSectionItemViewModel { Type: PopularSectionType.Rank })) return Task.CompletedTask;
        IsVideoLoading = true;
        IsAppending = Videos.Count > 0;
        HasAppendError = false;
        _videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
        var loadTask = LoadCurrentVideosAsync(_videoGeneration, _account.AccountGeneration, SelectedSection, _videoCancellation);
        _videoLoadTask = loadTask;
        return loadTask;
    }

    private async Task LoadCurrentVideosAsync(long generation, long accountGeneration,
        IPopularSectionItemViewModel selected, CancellationTokenSource cancellation)
    {
        try
        {
            if (selected is PopularSectionItemViewModel section)
            {
                if (section.Type == PopularSectionType.Recommend)
                    await LoadRecommendVideosAsync(generation, accountGeneration, cancellation.Token);
                else if (section.Type == PopularSectionType.Hot)
                    await LoadHotVideosAsync(generation, accountGeneration, cancellation.Token);
                else if (section.Type == PopularSectionType.Rank)
                    await LoadTotalRankVideosAsync(generation, accountGeneration, cancellation.Token);
            }
            else if (selected is PopularRankPartitionViewModel partition)
                await LoadPartitionRankVideosAsync(partition.Data, generation, accountGeneration, cancellation.Token);

            if (!IsCurrentVideoRequest(generation, accountGeneration, cancellation.Token)) return;
            _videoCache[selected] = Videos.ToList();
            if (!HasAppendError && (selected is PopularRankPartitionViewModel
                || selected is PopularSectionItemViewModel { Type: PopularSectionType.Rank })) IsPrefetchExhausted = true;
            VideoListUpdated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (ReferenceEquals(_videoCancellation, cancellation)) _videoCancellation = null;
            cancellation.Dispose();
            IsVideoLoading = false;
            IsAppending = false;
        }
    }

    [RelayCommand]
    private async Task RefreshVideoAsync()
    {
        if (_isRefreshing || _isSelectingSection || SelectedSection is null) return;
        _isRefreshing = true;
        var generation = ++_videoGeneration;
        try
        {
            var cancellation = _videoCancellation;
            var loadTask = _videoLoadTask;
            if (cancellation is not null) await cancellation.CancelAsync();
            // Reset raw SDK offsets only after its previous physical fetch has completed.
#pragma warning disable VSTHRD003
            try { await loadTask; } catch { /* The previous request already reported its failure. */ }
#pragma warning restore VSTHRD003
            if (generation != _videoGeneration) return;
            ResetAccountCacheIfNeeded();
            Videos.Clear();
            _videoCache.Remove(SelectedSection);
            _displayedSection = SelectedSection;
            HasAppendError = false;
            IsPrefetchExhausted = false;
            if (SelectedSection is PopularSectionItemViewModel section)
            {
                if (section.Type == PopularSectionType.Recommend) _recommendOffset = 0;
                else if (section.Type == PopularSectionType.Hot) _hotOffset = 0;
            }
        }
        finally { _isRefreshing = false; }
        if (generation == _videoGeneration) await LoadVideosAsync();
    }

    [RelayCommand]
    private Task RetryAppendAsync() => LoadVideosAsync();

    private bool IsCurrentVideoRequest(long generation, long accountGeneration, CancellationToken token)
        => !token.IsCancellationRequested && generation == _videoGeneration && accountGeneration == _account.AccountGeneration;

    private void ResetAccountCacheIfNeeded()
    {
        if (_cacheAccountGeneration == _account.AccountGeneration) return;
        _videoCache.Clear();
        Videos.Clear();
        _recommendOffset = 0;
        _hotOffset = 0;
        _cacheAccountGeneration = _account.AccountGeneration;
    }

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
