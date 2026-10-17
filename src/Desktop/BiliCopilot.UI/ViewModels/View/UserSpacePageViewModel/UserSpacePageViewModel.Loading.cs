// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Services.UserSpace;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.User;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

public sealed partial class UserSpacePageViewModel
{
    private async Task LoadCurrentAsync(long generation, CancellationToken token)
    {
        var profile = _profile!;
        if (IsSearchMode)
        {
            if (string.IsNullOrWhiteSpace(SearchKeyword)) { HasMore = false; IsSearchEmpty = true; State = UserSpaceLoadState.Empty; return; }
            var (videos, _, more) = await _searchService.SearchUserVideosAsync(profile.Id, SearchKeyword, _searchPn, token);
            CheckCurrent(generation, token);
            if (videos is null || videos.Count > 100) throw UserSpaceService.Schema();
            var rows = videos.Select(video => new VideoItemViewModel(video, VideoCardStyle.Search)).ToArray();
            // Use the response identifiers, not a view-model display string, for deduplication.
            var added = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                CheckCurrent(generation, token);
                if (!_seen.Add(videos[i].Identifier.Id)) continue;
                SearchVideos.Add(rows[i]); added++;
            }
            CheckCurrent(generation, token); CheckProgress(added, more);
            HasMore = more; if (more) _searchPn++;
            IsSearchEmpty = SearchVideos.Count == 0;
            State = IsSearchEmpty ? UserSpaceLoadState.Empty : UserSpaceLoadState.Ready;
            SearchUpdated?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (SelectedTab)
        {
            case UserSpaceTab.Home:
            {
                if (_pageNumber == 1) await LoadFeaturedAsync(generation, token);
                CheckCurrent(generation, token);
                var page = await _space.GetVideosAsync(profile.Id, UserVideoOrder.Latest, _pageNumber, token);
                CheckCurrent(generation, token);
                var added = Append(HomeVideos, page.Items, row => row.Id, _seen, generation, token);
                CommitPage(page.NextPage, page.HasMore, added, HomeVideos.Count);
                break;
            }
            case UserSpaceTab.Dynamics:
            {
                var firstPage = _offset is null;
                var (moments, offset, more) = await _moments.GetUserComprehensiveMomentsAsync(profile, _offset, token);
                CheckCurrent(generation, token);
                if (moments is null || moments.Count > 100) throw UserSpaceService.Schema();
                CheckOffset(offset, more);
                var rows = moments.Select(moment => new MomentItemViewModel(moment, MomentCardStyle.Comprehensive, ShowComment)).ToArray();
                var added = Append(Dynamics, rows, row => row.Data.Id, _seen, generation, token);
                CommitOffset(offset, more, added, Dynamics.Count);
                if (firstPage)
                {
                    // Render the primary feed before the independent Web pin request, which can time out.
                    _logger.LogInformation("个人主页动态首屏已加载 Count={Count}", Dynamics.Count);
                    ListUpdated?.Invoke(this, EventArgs.Empty);
                    try
                    {
                        var pinned = await _space.GetPinnedDynamicAsync(profile.Id, token);
                        CheckCurrent(generation, token);
                        PinnedDynamicId = pinned?.Id;
                        if (pinned is not null)
                        {
                            var existing = Dynamics.FirstOrDefault(row => row.Data.Id == pinned.Id);
                            if (existing is not null)
                            {
                                var index = Dynamics.IndexOf(existing);
                                if (index > 0) Dynamics.Move(index, 0);
                            }
                            else if (_seen.Add(pinned.Id))
                            {
                                Dynamics.Insert(0, new MomentItemViewModel(pinned, MomentCardStyle.Comprehensive, ShowComment));
                            }

                            State = UserSpaceLoadState.Ready;
                        }
                    }
                    catch (OperationCanceledException) when (!Current(generation, token)) { throw; }
                    catch (Exception error)
                    {
                        CheckCurrent(generation, token);
                        _logger.LogWarning("个人主页置顶动态读取失败 ({ErrorType})", error.GetType().Name);
                    }
                }

                break;
            }
            case UserSpaceTab.Submissions:
                await LoadSubmissionsAsync(profile.Id, generation, token);
                break;
            case UserSpaceTab.Collections:
            {
                var page = await _space.GetCollectionsAsync(profile.Id, _pageNumber, token);
                CheckCurrent(generation, token);
                var groups = page.Items.Select(group => new UserSpaceCollectionViewModel(group)).ToArray();
                var added = Append(Collections, groups, group => $"{group.Data.Kind}:{group.Data.Id}", _seen, generation, token);
                CommitPage(page.NextPage, page.HasMore, added, Collections.Count);
                break;
            }
            default: throw UserSpaceService.Schema();
        }
        CheckCurrent(generation, token);
        ListUpdated?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadSubmissionsAsync(string mid, long generation, CancellationToken token)
    {
        switch (SelectedSubmission)
        {
            case UserSubmissionKind.Video:
            {
                var page = await _space.GetVideosAsync(mid, VideoOrder, _pageNumber, token);
                CheckCurrent(generation, token);
                var added = Append(SubmissionVideos, page.Items, row => row.Id, _seen, generation, token);
                CommitPage(page.NextPage, page.HasMore, added, SubmissionVideos.Count);
                break;
            }
            case UserSubmissionKind.Opus:
            {
                var page = await _space.GetOpusAsync(mid, OpusFilter, _offset, token);
                CheckCurrent(generation, token); CheckOffset(page.NextOffset, page.HasMore);
                var added = Append(OpusPosts, page.Items, row => row.Id, _seen, generation, token);
                CommitOffset(page.NextOffset, page.HasMore, added, OpusPosts.Count);
                break;
            }
            case UserSubmissionKind.Audio:
            {
                var page = await _space.GetAudioAsync(mid, _pageNumber, token);
                CheckCurrent(generation, token);
                var added = Append(AudioTracks, page.Items, row => row.Id, _seen, generation, token);
                CommitPage(page.NextPage, page.HasMore, added, AudioTracks.Count);
                break;
            }
            default: throw UserSpaceService.Schema();
        }
    }

    private async Task LoadFeaturedAsync(long generation, CancellationToken token)
    {
        FeaturedState = UserSpaceLoadState.Loading; FeaturedError = null;
        try
        {
            IsFollowed = null;
            var relation = await _relationshipService.GetRelationshipAsync(_profile!.Id, token);
            CheckCurrent(generation, token);
            IsFollowed = relation switch
            {
                UserRelationStatus.Following or UserRelationStatus.Friends or UserRelationStatus.SpeciallyFollowed => true,
                UserRelationStatus.Unfollow or UserRelationStatus.BeFollowed or UserRelationStatus.Blocked => false,
                _ => null,
            };
            if (IsFollowed is not bool followed) throw UserSpaceService.Schema();
            var rows = await _space.GetFeaturedAsync(_profile.Id, followed, token);
            CheckCurrent(generation, token);
            FeaturedVideos.Clear();
            foreach (var row in rows) { CheckCurrent(generation, token); FeaturedVideos.Add(row); }
            CheckCurrent(generation, token);
            FeaturedState = rows.Count == 0 ? UserSpaceLoadState.Empty : UserSpaceLoadState.Ready;
        }
        catch (OperationCanceledException) when (!Current(generation, token)) { throw; }
        catch (Exception error)
        {
            CheckCurrent(generation, token);
            FeaturedState = FailureState(error); FeaturedError = FailureText(error);
            // Failure to establish featured content is not an invented empty list or a reason
            // to suppress the independent ordinary-video list below it.
        }
    }

    [RelayCommand]
    private void SelectCollection(UserSpaceCollectionViewModel group)
    {
        if (!Collections.Any(item => ReferenceEquals(item, group))) return;
        Invalidate(); SelectedCollection = group;
        if (group.State is UserSpaceLoadState.Idle or UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable)
            LoadCollectionCommand.Execute(group);
    }

    [RelayCommand]
    private Task LoadCollectionAsync(UserSpaceCollectionViewModel group)
    {
        if (group.State == UserSpaceLoadState.Loading) return group.LoadTask;
        if (!Collections.Any(item => ReferenceEquals(item, group)) || !group.HasMore)
            return Task.CompletedTask;
        group.LoadTask = RunAsync(async (generation, token) =>
        {
            try
            {
                var page = await _space.GetCollectionVideosAsync(group.Data, group.NextPage, token);
                CheckCurrent(generation, token);
                if (!Collections.Any(item => ReferenceEquals(item, group))) return;
                var added = Append(group.Videos, page.Items, row => row.Id, group.Seen, generation, token);
                CheckProgress(added, page.HasMore);
                group.NextPage = page.NextPage; group.HasMore = page.HasMore;
                group.State = group.Videos.Count == 0 ? UserSpaceLoadState.Empty : UserSpaceLoadState.Ready;
            }
            catch (Exception error)
            {
                // A group failure is shown inside that group; it must not turn the whole page into an error.
                if (!Current(generation, token)) throw;
                group.State = FailureState(error); group.Error = FailureText(error);
                _logger.LogWarning("个人主页合集分组加载失败 ({ErrorType})", error.GetType().Name);
            }
        });
        group.State = UserSpaceLoadState.Loading; group.Error = null;
        return group.LoadTask;
    }

    [RelayCommand]
    private async Task PlayCollectionAsync(UserSpaceCollectionViewModel group)
    {
        if (!Collections.Any(item => ReferenceEquals(item, group))) return;
        var generation = _generation;
        var token = _scope.Token;
        // The player takes a fixed snapshot, so finish pagination rather than silently truncating at 30.
        while (group.HasMore || group.State == UserSpaceLoadState.Loading)
        {
            await LoadCollectionAsync(group);
            if (!Current(generation, token) || group.State is UserSpaceLoadState.Error or UserSpaceLoadState.Unavailable) return;
        }

        if (!Current(generation, token) || group.Videos.Count == 0) return;
        var playlist = group.Videos.Select(row => new MediaSnapshot(row.Video)).ToList();
        var snapshot = playlist[0];
        snapshot.Playlist = playlist;
        await this.Get<AppViewModel>().OpenPlayerCommand.ExecuteAsync(snapshot);
    }

    private int Append<T>(ObservableCollection<T> destination, IEnumerable<T> rows, Func<T, string> id,
        HashSet<string> seen, long generation, CancellationToken token)
    {
        var added = 0;
        foreach (var row in rows)
        {
            CheckCurrent(generation, token);
            var key = id(row);
            if (string.IsNullOrWhiteSpace(key)) throw UserSpaceService.Schema();
            if (!seen.Add(key)) continue;
            destination.Add(row); added++;
        }
        CheckCurrent(generation, token);
        return added;
    }

    private static void CheckProgress(int added, bool more) { if (added == 0 && more) throw UserSpaceService.Schema(); }
    private void CheckOffset(string? next, bool more)
    {
        if (more && (string.IsNullOrEmpty(next) || next == _offset || _offsets.Contains(next))) throw UserSpaceService.Schema();
    }
    private void CommitOffset(string? next, bool more, int added, int count)
    {
        CheckProgress(added, more);
        _offset = next; if (!string.IsNullOrEmpty(next)) _offsets.Add(next);
        HasMore = more; State = count == 0 ? UserSpaceLoadState.Empty : UserSpaceLoadState.Ready;
    }
    private void CommitPage(int next, bool more, int added, int count)
    {
        CheckProgress(added, more);
        _pageNumber = next; HasMore = more; State = count == 0 ? UserSpaceLoadState.Empty : UserSpaceLoadState.Ready;
    }
}
