// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.ViewModels.Components;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Player;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PgcConnectorViewModel : ViewModelBase, IPlayerConnectorViewModel
{
    private long _requestGeneration;
    private CancellationToken _initializationCancellation;
    private bool _requestsInvalidated = true;

    public PgcConnectorViewModel(PlayerViewModel parent)
    {
        Parent = parent;
        _comments = this.Get<CommentMainViewModel>();
        Downloader = this.Get<DownloadViewModel>();
    }

    public async Task InitializeAsync(MediaSnapshot snapshot, IMpvMediaSourceResolver sourceResolver, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateRequests();
        var generation = _requestGeneration;
        _initializationCancellation = cancellationToken;
        _requestsInvalidated = false;
        _snapshot = snapshot;
        ClearView();
        IsLoading = true;
        try
        {
            await LoadItemDetailAsync(snapshot, generation, cancellationToken);
            ThrowIfRequestInvalid(generation, cancellationToken);

            _snapshot.Season = _view.Information;
            _snapshot.Episode = _episode;
            var prev = FindPreviousEpisode();
            var next = FindNextEpisode();
            var prevName = prev?.Identifier.Title;
            var nextName = next?.Identifier.Title;
            var initArgs = new PlaylistInitializedEventArgs(prev != null, next != null, prevName, nextName);
            PlaylistInitialized?.Invoke(this, initArgs);
        }
        finally
        {
            if (generation == _requestGeneration)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Synchronously detach pending media/comment results. Call on the UI thread.</summary>
    public void InvalidateRequests()
    {
        _requestsInvalidated = true;
        _requestGeneration++;
        _comments.InvalidateRequests();
        IsLoading = false;
    }

    private bool IsRequestCurrent(long generation, CancellationToken cancellationToken)
        => !_requestsInvalidated && generation == _requestGeneration && !cancellationToken.IsCancellationRequested && !Parent.IsPlaybackClosing;

    private void ThrowIfRequestInvalid(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRequestCurrent(generation, cancellationToken))
        {
            throw new OperationCanceledException("The media request was superseded.", cancellationToken);
        }
    }

    public void InitializeDownloader(List<PlayerFormatInformation> formats)
    {
        Downloader.Clear();
        Downloader.InitializeMetas(
            GetEpisodeUrl(),
            GetSeasonUrl(),
            formats.AsReadOnly(),
            _view.Episodes?.Count > 1 ? _view.Episodes.AsReadOnly() : default,
            _view.Episodes.IndexOf(_episode));
    }

    public async Task PlayNextAsync()
    {
        if (IsLoading || Parent.IsConnecting || Parent.IsPlaybackClosing || !IsRequestCurrent(_requestGeneration, _initializationCancellation))
        {
            return;
        }

        var next = FindNextEpisode();
        if (next is null)
        {
            return;
        }

        _snapshot.Episode = next;
        await Parent.InitializeAsync(_snapshot);
    }

    public async Task PlayPreviousAsync()
    {
        if (IsLoading || Parent.IsConnecting || Parent.IsPlaybackClosing || !IsRequestCurrent(_requestGeneration, _initializationCancellation))
        {
            return;
        }

        var prev = FindPreviousEpisode();
        if (prev is null)
        {
            return;
        }

        _snapshot.Episode = prev;
        await Parent.InitializeAsync(_snapshot);
    }

    [RelayCommand]
    private void OpenPanel()
        => RequestOpenExtraPanel?.Invoke(this, EventArgs.Empty);

    private async Task LoadItemDetailAsync(MediaSnapshot snapshot, long generation, CancellationToken cancellationToken)
    {
        var service = this.Get<IPlayerService>();
        try
        {
            var view = await service.GetPgcPageDetailAsync(snapshot.Season?.Identifier.Id, snapshot.Episode?.Identifier.Id, cancellationToken);
            ThrowIfRequestInvalid(generation, cancellationToken);

            _view = view;
            _episode = FindInitialEpisode(snapshot.Episode?.Identifier.Id);
            EpisodeTitle = _episode.Identifier.Title;
            EpisodeId = _episode.Identifier.Id;
            UpdateProperties();
            ThrowIfRequestInvalid(generation, cancellationToken);
            InitializeView(_view);
            var aid = _episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
            _comments.Initialize(aid, Richasy.BiliKernel.Models.CommentTargetType.Video, Richasy.BiliKernel.Models.CommentSortType.Hot, cancellationToken);
            ThrowIfRequestInvalid(generation, cancellationToken);
            InitializeSections();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ThrowIfRequestInvalid(generation, cancellationToken);
            this.Get<ILogger<PgcConnectorViewModel>>().LogError(ex, "获取剧集信息失败");
            throw;
        }
    }

    private void UpdateProperties()
    {
        Title = _episode.Identifier.Title;
        Subtitle = string.IsNullOrEmpty(_episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.Subtitle)) ? _view.Information.Identifier.Title : _episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.Subtitle);
        Description = _view.Information.GetExtensionIfNotNull<string>(SeasonExtensionDataId.Description) ?? string.Empty;
        Cover = _view.Information.Identifier.Cover?.Uri;
        var args = new PlayerInformationUpdatedEventArgs(Title, Subtitle, Cover);
        PropertiesUpdated?.Invoke(this, args);
    }
}
