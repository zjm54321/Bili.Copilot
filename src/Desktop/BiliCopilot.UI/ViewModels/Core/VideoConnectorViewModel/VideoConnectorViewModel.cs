// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Player;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class VideoConnectorViewModel : ViewModelBase, IPlayerConnectorViewModel
{
    private long _requestGeneration;
    private CancellationToken _initializationCancellation;
    private bool _requestsInvalidated = true;

    public VideoConnectorViewModel(PlayerViewModel parent)
    {
        Parent = parent;
        _comments = this.Get<CommentMainViewModel>();
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

            var prev = FindPrevVideo();
            var next = FindNextVideo();
            var prevName = prev is VideoPart part ? part.Identifier.Title : prev is VideoInformation video ? video.Identifier.Title : default;
            var nextName = next is VideoPart part2 ? part2.Identifier.Title : next is VideoInformation video2 ? video2.Identifier.Title : default;
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

    public async Task PlayNextAsync()
    {
        if (IsLoading || Parent.IsConnecting || Parent.IsPlaybackClosing || !IsRequestCurrent(_requestGeneration, _initializationCancellation))
        {
            return;
        }

        var next = FindNextVideo();
        if (next is null)
        {
            return;
        }

        if (next is VideoPart part)
        {
            _snapshot.PreferPart = part;
            await Parent.InitializeAsync(_snapshot);
            return;
        }
        else if (next is VideoInformation video)
        {
            await Parent.InitializeAsync(new MediaSnapshot(video, IsPrivatePlay) { Playlist = _snapshot.Playlist, Type = BiliMediaType.Video });
        }

    }

    public async Task PlayPreviousAsync()
    {
        if (IsLoading || Parent.IsConnecting || Parent.IsPlaybackClosing || !IsRequestCurrent(_requestGeneration, _initializationCancellation))
        {
            return;
        }

        var next = FindPrevVideo();
        if (next is null)
        {
            return;
        }

        if (next is VideoPart part)
        {
            _snapshot.PreferPart = part;
            _snapshot.StartPosition = 0;
            await Parent.InitializeAsync(_snapshot);
            return;
        }
        else if (next is VideoInformation video)
        {
            await Parent.InitializeAsync(new MediaSnapshot(video, IsPrivatePlay) { Playlist = _snapshot.Playlist, Type = BiliMediaType.Video });
        }

    }

    [RelayCommand]
    private void OpenPanel()
        => RequestOpenExtraPanel?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SelectSection(IPlayerSectionDetailViewModel section)
    {
        if (!IsRequestCurrent(_requestGeneration, _initializationCancellation) || section is null || section == SelectedSection || Sections?.Contains(section) != true)
        {
            return;
        }

        SelectedSection = section;
        SelectedSection.TryFirstLoadCommand.Execute(default);
    }

    private void PlayItem(PlaylistMediaViewModel vm)
    {
        if (!IsRequestCurrent(_requestGeneration, _initializationCancellation) || vm.Data.Video.Identifier.Id == _snapshot.Video.Identifier.Id)
        {
            return;
        }

        NewMediaRequest?.Invoke(this, vm.Data);
    }

    private async Task LoadItemDetailAsync(MediaSnapshot snapshot, long generation, CancellationToken cancellationToken)
    {
        var service = this.Get<IPlayerService>();
        try
        {
            var view = await service.GetVideoPageDetailAsync(snapshot.Video.Identifier, cancellationToken);
            ThrowIfRequestInvalid(generation, cancellationToken);

            _view = view;
            _part = FindInitialPart(snapshot.PreferPart?.Identifier.Id);
            UpdateProperties();
            ThrowIfRequestInvalid(generation, cancellationToken);
            InitializeView(_view);
            _comments?.Initialize(_view.Information.Identifier.Id, Richasy.BiliKernel.Models.CommentTargetType.Video, Richasy.BiliKernel.Models.CommentSortType.Hot, cancellationToken);
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
            this.Get<ILogger<VideoConnectorViewModel>>().LogError(ex, "获取视频信息失败");
            throw;
        }
    }

    private VideoPart? FindInitialPart(string? initialPartId = default)
    {
        if (_view.Parts?.Count == 1)
        {
            return _view.Parts[0];
        }

        VideoPart? part = default;
        if (!string.IsNullOrEmpty(initialPartId))
        {
            part = _view.Parts.FirstOrDefault(p => p.Identifier.Id == initialPartId);
        }

        if (part == null)
        {
            var historyPartId = _view.Progress?.Cid;
            var autoLoadHistory = SettingsToolkit.ReadLocalSetting(SettingNames.AutoLoadHistory, true);
            if (!string.IsNullOrEmpty(historyPartId) && autoLoadHistory)
            {
                part = _view.Parts.FirstOrDefault(p => p.Identifier.Id == historyPartId);
            }
        }

        return part ?? _view.Parts.FirstOrDefault();
    }

    private void UpdateProperties()
    {
        Title = _view.Information.Identifier.Title;
        Subtitle = string.IsNullOrEmpty(_view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description)) ? _view.Information.Publisher.User.Name : _view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description);
        Description = _view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description) ?? string.Empty;
        Cover = _view.Information.Identifier.Cover?.Uri;
        var args = new PlayerInformationUpdatedEventArgs(Title, Subtitle, Cover);
        PropertiesUpdated?.Invoke(this, args);
    }
}
