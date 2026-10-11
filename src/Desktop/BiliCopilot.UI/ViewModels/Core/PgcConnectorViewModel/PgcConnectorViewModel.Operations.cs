// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using WinUIEx;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PgcConnectorViewModel
{
    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken))
        {
            return;
        }

        try
        {
            var state = !IsLiked;
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            await this.Get<IPlayerService>().ToggleVideoLikeAsync(aid, state, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsLiked = state;
            ReloadEpisodeOperationCommand.Execute(default);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试点赞/取消点赞视频时失败.");
        }
    }

    [RelayCommand]
    private async Task CoinAsync(int count = 1)
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken))
        {
            return;
        }

        try
        {
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            await this.Get<IPlayerService>().CoinVideoAsync(aid, count, IsCoinAlsoLike, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsCoined = true;
            if (IsCoinAlsoLike && !IsLiked)
            {
                IsLiked = true;
            }

            ReloadEpisodeOperationCommand.Execute(default);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试投币/取消投币视频时失败.");
        }
    }

    [RelayCommand]
    private async Task TripleAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken))
        {
            return;
        }

        try
        {
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            await this.Get<IPlayerService>().TripleVideoAsync(aid, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsLiked = true;
            IsCoined = true;
            IsFavorited = true;
            ReloadEpisodeOperationCommand.Execute(default);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试一键三连视频时失败.");
        }
    }

    [RelayCommand]
    private async Task InitializeFavoritesAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken) || FavoriteFolders is not null)
        {
            return;
        }

        try
        {
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            var (folders, ids) = await this.Get<IFavoriteService>().GetPlayingVideoFavoriteFoldersAsync(aid, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            FavoriteFolders = folders.Select(p => new PlayerFavoriteFolderViewModel(p, ids.Contains(p.Id))).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试获取收藏夹列表时失败.");
        }
    }

    [RelayCommand]
    private async Task FavoriteAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken) || FavoriteFolders is null)
        {
            return;
        }

        var selectedFolders = FavoriteFolders?.Where(p => p.IsSelected).Select(p => p.Data.Id).ToList();
        var unselectedFolders = FavoriteFolders?.Where(p => !p.IsSelected).Select(p => p.Data.Id).ToList();
        try
        {
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            await this.Get<IPlayerService>().FavoriteVideoAsync(aid, selectedFolders, unselectedFolders, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsFavorited = selectedFolders.Count != 0;
            FavoriteFolders = default;
            ReloadEpisodeOperationCommand.Execute(default);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试收藏视频时失败.");
        }
    }

    [RelayCommand]
    private void CopyEpisodeUrl()
    {
        var url = GetEpisodeUrl();
        var dp = new DataPackage();
        dp.SetText(url);
        dp.SetWebLink(new Uri(url));
        Clipboard.SetContent(dp);
        this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success));
    }

    [RelayCommand]
    private void CopySeasonUrl()
    {
        var url = GetSeasonUrl();
        var dp = new DataPackage();
        dp.SetText(url);
        dp.SetWebLink(new Uri(url));
        Clipboard.SetContent(dp);
        this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success));
    }

    [RelayCommand]
    private void ShareEpisodeUrl()
    {
        var handle = this.Get<AppViewModel>().ActivatedWindow.GetWindowHandle();
        var transferManager = DataTransferManagerInterop.GetForWindow(handle);
        transferManager.DataRequested += OnTransferDataRequested;
        DataTransferManagerInterop.ShowShareUIForWindow(handle);

        void OnTransferDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var url = GetEpisodeUrl();
            var dp = new DataPackage();
            dp.SetText(url);
            dp.SetWebLink(new Uri(url));
            dp.Properties.Title = EpisodeTitle;
            dp.Properties.Description = Description;
            dp.Properties.Thumbnail = RandomAccessStreamReference.CreateFromUri(_view.Information.Identifier.Cover.Uri);
            args.Request.Data = dp;
            sender.DataRequested -= OnTransferDataRequested;
        }
    }

    [RelayCommand]
    private async Task OpenInBrowserAsync()
    {
        var url = GetEpisodeUrl();
        await Launcher.LaunchUriAsync(new Uri(url)).AsTask();
    }

    [RelayCommand]
    private async Task ReloadEpisodeOperationAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken) || _episode is null)
        {
            return;
        }

        try
        {
            var operation = await this.Get<IPlayerService>().GetEpisodeOperationInformationAsync(_episode.Identifier.Id, cancellationToken);
            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsLiked = operation.IsLiked;
            IsCoined = operation.IsCoined;
            IsFavorited = operation.IsFavorited;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "尝试获取剧集操作信息时失败.");
        }
    }

    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        var generation = _requestGeneration;
        var cancellationToken = _initializationCancellation;
        if (!IsRequestCurrent(generation, cancellationToken))
        {
            return;
        }

        try
        {
            var state = !IsFollow;
            var service = this.Get<IEntertainmentDiscoveryService>();
            if (state)
            {
                await service.FollowAsync(_view.Information.Identifier.Id, cancellationToken);
            }
            else
            {
                await service.UnfollowAsync(_view.Information.Identifier.Id, cancellationToken);
            }

            if (!IsRequestCurrent(generation, cancellationToken))
            {
                return;
            }

            IsFollow = state;
        }
        catch (Exception ex)
        {
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, $"尝试追番/追剧失败 ({!IsFollow})");
        }
    }

    [RelayCommand]
    private void Pin()
    {
        var pinItem = new PinItem($"ss_{_view.Information.Identifier.Id}", _view.Information.Identifier.Title, _view.Information.Identifier.Cover.Uri.ToString(), PinContentType.Pgc);
        this.Get<PinnerViewModel>().AddItemCommand.Execute(pinItem);
    }

    private string GetSeasonUrl()
        => $"https://www.bilibili.com/bangumi/play/ss{_view.Information.Identifier.Id}";

    private string GetEpisodeUrl()
        => $"https://www.bilibili.com/bangumi/play/ep{_episode.Identifier.Id}";
}
