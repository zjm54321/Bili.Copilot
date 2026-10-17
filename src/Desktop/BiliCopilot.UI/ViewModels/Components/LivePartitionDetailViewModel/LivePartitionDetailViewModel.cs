// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 直播分区详情视图模型.
/// </summary>
public sealed partial class LivePartitionDetailViewModel : ViewModelBase<PartitionViewModel>
{
    private const string DefaultSortType = "online";
    private static readonly LiveTag DefaultTag = new("online", "综合", DefaultSortType);

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private string _loadError = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionDetailViewModel"/> class.
    /// </summary>
    public LivePartitionDetailViewModel(
        PartitionViewModel partition,
        ILiveDiscoveryService service)
        : base(partition)
    {
        _service = service;
        _logger = this.Get<ILogger<LivePartitionDetailViewModel>>();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (IsLiveLoading || (CurrentTag is not null && Children?.Count > 0))
        {
            return;
        }

        IsLiveLoading = true;
        ClearLoadError();
        try
        {
            // Always send sort_type=online; omitting it can return an empty list.
            var (lives, tags, nextPageNumber) = await _service.GetPartitionLiveListAsync(Data.Data, DefaultTag);
            var children = tags?.Select(WithUsableSort).ToList() ?? [];
            var firstTag = children.FirstOrDefault(tag => tag.SortType == DefaultSortType);
            if (firstTag is null)
            {
                firstTag = DefaultTag;
                children.Insert(0, firstTag);
            }

            Children = children;
            CurrentTag = firstTag;
            _childPartitionOffsetCache[firstTag] = nextPageNumber;
            TryAddRooms(lives);
        }
        catch (Exception ex)
        {
            SetLoadError();
            _logger.LogError(ex, $"尝试加载 {Data.Data.Name} 分区视频时出错.");
        }
        finally
        {
            IsLiveLoading = false;
        }
        Initialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLiveLoading)
        {
            return;
        }

        _preventLoadMore = false;
        Rooms.Clear();
        ClearLoadError();
        if (CurrentTag is null)
        {
            await InitializeAsync();
            return;
        }

        _childPartitionRoomCache.Remove(CurrentTag);
        _childPartitionOffsetCache.Remove(CurrentTag);
        await LoadRoomsAsync();
    }

    [RelayCommand]
    private async Task ChangeChildPartitionAsync(LiveTag tag)
    {
        if (IsLiveLoading || tag is null || tag.Equals(CurrentTag))
        {
            return;
        }

        _preventLoadMore = false;
        if (CurrentTag is not null && Rooms.Count > 0)
        {
            _childPartitionRoomCache[CurrentTag] = Rooms.ToList();
        }

        CurrentTag = tag;
        ClearLoadError();
        Rooms.Clear();
        if (_childPartitionRoomCache.TryGetValue(tag, out var cache))
        {
            foreach (var item in cache)
            {
                Rooms.Add(item);
            }
        }

        if (Rooms.Count == 0)
        {
            await LoadRoomsAsync();
        }
        else
        {
            LiveListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task LoadRoomsAsync()
    {
        if (IsLiveLoading)
        {
            return;
        }

        if (CurrentTag is null)
        {
            await InitializeAsync();
            return;
        }

        IsLiveLoading = true;
        try
        {
            await LoadChildPartitionRoomsAsync();
        }
        finally
        {
            IsLiveLoading = false;
        }
        LiveListUpdated?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadChildPartitionRoomsAsync()
    {
        if (_preventLoadMore)
        {
            return;
        }

        ClearLoadError();
        try
        {
            var pn = 0;
            if (_childPartitionOffsetCache.TryGetValue(CurrentTag, out var cachePN))
            {
                pn = cachePN;
            }

            // Normalize empty server sort_type; pn=0 maps to the SDK's page=1.
            var (rooms, _, nextPn) = await _service.GetPartitionLiveListAsync(Data.Data, WithUsableSort(CurrentTag), pn);
            _childPartitionOffsetCache[CurrentTag] = nextPn;
            TryAddRooms(rooms);
        }
        catch (Exception ex)
        {
            _preventLoadMore = true;
            SetLoadError();
            _logger.LogError(ex, $"尝试加载 {Data.Data.Name}/{CurrentTag.Name} 分区视频时出错.");
        }
    }

    private static LiveTag WithUsableSort(LiveTag tag)
        => string.IsNullOrWhiteSpace(tag.SortType) ? new LiveTag(tag.Id, tag.Name, DefaultSortType) : tag;

    private void ClearLoadError()
    {
        HasLoadError = false;
        LoadError = string.Empty;
    }

    private void SetLoadError()
    {
        HasLoadError = true;
        LoadError = "直播分区加载失败，请点击刷新重试。";
    }

    private void TryAddRooms(IReadOnlyList<LiveInformation> rooms)
    {
        if (rooms is not null)
        {
            foreach (var item in rooms)
            {
                Rooms.Add(new LiveItemViewModel(item, Models.Constants.LiveCardStyle.Partition));
            }
        }
    }
}
