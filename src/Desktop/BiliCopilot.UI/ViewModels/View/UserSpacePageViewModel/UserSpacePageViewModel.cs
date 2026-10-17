// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Services.UserSpace;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Bili.Search;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>Space-specific UI owner. One cancellation scope and one physical read/mutation slot.</summary>
public sealed partial class UserSpacePageViewModel : ViewModelBase
{
    public UserSpacePageViewModel(CommentMainViewModel comment, IRelationshipService relationshipService,
        IUserService userService, ISearchService searchService, ILogger<UserSpacePageViewModel> logger,
        UserSpaceService space, IMomentDiscoveryService moments, AccountViewModel account)
    {
        CommentModule = comment;
        _relationshipService = relationshipService;
        _userService = userService;
        _searchService = searchService;
        _logger = logger;
        _space = space;
        _moments = moments;
        _account = account;
        _account.AccountChanged += (_, _) => Reset(_profile);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InitializeAsync(UserProfile profile)
    {
        UserSpaceService.Id(profile.Id);
        Reset(profile);
        var generation = _generation;
        await LoadMoreAsync();
        if (generation == _generation) Initialized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Preserves direct Reset()/Reset(profile) and ResetCommand entry points; never starts a request.</summary>
    [RelayCommand]
    public void Reset(UserProfile? profile = null)
    {
        Invalidate();
        _profile = profile;
        UserName = profile?.Name;
        Card = null;
        IsFollowed = null;
        IsCommentsOpened = false;
        SelectedTab = UserSpaceTab.Home;
        SelectedSubmission = UserSubmissionKind.Video;
        VideoOrder = UserVideoOrder.Latest;
        OpusFilter = UserOpusFilter.All;
        IsSearchMode = false;
        SearchKeyword = string.Empty;
        SearchVideos.Clear();
        HomeVideos.Clear(); FeaturedVideos.Clear(); Dynamics.Clear(); PinnedDynamicId = null; SubmissionVideos.Clear();
        OpusPosts.Clear(); AudioTracks.Clear(); Collections.Clear(); SelectedCollection = null;
        ResetActiveFeed();
    }

    private void Invalidate()
    {
        ++_generation;
        var previous = _scope;
        _scope = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
        previous.Cancel(); previous.Dispose();
        _loadTask = Task.CompletedTask;
        IsLoading = IsSearching = false;
        State = UserSpaceLoadState.Idle;
        ErrorText = null;
        foreach (var group in Collections)
            if (group.State == UserSpaceLoadState.Loading) group.State = group.Videos.Count == 0 ? UserSpaceLoadState.Idle : UserSpaceLoadState.Ready;
    }

    private void ResetActiveFeed()
    {
        _pageNumber = 1; _searchPn = 0; _offset = null; _seen.Clear(); _offsets.Clear(); HasMore = true;
        State = UserSpaceLoadState.Idle; ErrorText = null; IsSearchEmpty = false;
        switch (SelectedTab)
        {
            case UserSpaceTab.Home:
                HomeVideos.Clear(); FeaturedVideos.Clear(); FeaturedState = UserSpaceLoadState.Idle; FeaturedError = null; break;
            case UserSpaceTab.Dynamics: Dynamics.Clear(); PinnedDynamicId = null; break;
            case UserSpaceTab.Submissions: SubmissionVideos.Clear(); OpusPosts.Clear(); AudioTracks.Clear(); break;
            case UserSpaceTab.Collections: Collections.Clear(); SelectedCollection = null; break;
        }
    }

    private void ChangeFeed()
    {
        Invalidate(); IsSearchMode = false; SearchKeyword = string.Empty; SearchVideos.Clear();
        ResetActiveFeed(); LoadMoreCommand.Execute(null);
    }

    [RelayCommand]
    private void SelectTab(UserSpaceTab tab) { if (SelectedTab != tab) { SelectedTab = tab; ChangeFeed(); } }
    [RelayCommand]
    private void SelectSubmission(UserSubmissionKind kind) { if (SelectedSubmission != kind) { SelectedSubmission = kind; ChangeFeed(); } }
    [RelayCommand]
    private void SelectVideoOrder(UserVideoOrder order) { if (VideoOrder != order) { VideoOrder = order; ChangeFeed(); } }
    [RelayCommand]
    private void SelectOpusFilter(UserOpusFilter filter) { if (OpusFilter != filter) { OpusFilter = filter; ChangeFeed(); } }

    [RelayCommand]
    private void Search(string text)
    {
        Invalidate(); IsSearchMode = true; SearchKeyword = text; SearchVideos.Clear();
        _searchPn = 0; _seen.Clear(); HasMore = true; IsSearchEmpty = false;
        LoadMoreCommand.Execute(null);
    }
    [RelayCommand]
    private void ExitSearch() => ChangeFeed();
    [RelayCommand]
    private Task LoadMoreSearchResultAsync() => LoadMoreAsync();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshAsync()
    {
        if (IsSearchMode) { Search(SearchKeyword); var search = _loadTask; return search; }
        Invalidate(); ResetActiveFeed(); return LoadMoreAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LoadMoreAsync()
    {
        var pending = _loadTask;
        if (!pending.IsCompleted) return pending;
        if (_profile is null || !HasMore) return Task.CompletedTask;
        var operation = RunAsync(LoadCurrentAsync);
        _loadTask = operation;
        return operation;
    }

    private bool Current(long generation, CancellationToken token) => generation == _generation && !token.IsCancellationRequested && _profile is not null;
    private void CheckCurrent(long generation, CancellationToken token) { if (!Current(generation, token)) throw new OperationCanceledException(token); }

    private async Task RunAsync(Func<long, CancellationToken, Task> action)
    {
        var generation = _generation;
        var token = _scope.Token;
        // Publish the caller's coalescer before property/collection callbacks or service entry.
        await Task.Yield();
        if (!Current(generation, token)) return;
        var entered = false;
        try
        {
            await _requests.WaitAsync(token); entered = true;
            CheckCurrent(generation, token);
            IsLoading = true; IsSearching = IsSearchMode; State = UserSpaceLoadState.Loading; ErrorText = null;
            await action(generation, token);
            if (Current(generation, token) && State == UserSpaceLoadState.Loading) State = UserSpaceLoadState.Ready;
        }
        catch (OperationCanceledException) when (!Current(generation, token)) { }
        catch (Exception error)
        {
            if (Current(generation, token))
            {
                State = FailureState(error); ErrorText = FailureText(error);
                _logger.LogWarning("个人主页请求未完成 ({ErrorType})", error.GetType().Name);
            }
        }
        finally
        {
            // Advisory cancellation never releases the slot before the real service task settles.
            if (entered) _requests.Release();
            if (Current(generation, token)) { IsLoading = IsSearching = false; }
        }
    }

    private static UserSpaceLoadState FailureState(Exception error) => error is UserSpaceDataException failure ? failure.State
        : error is System.Text.Json.JsonException or InvalidOperationException or FormatException ? UserSpaceLoadState.Unavailable : UserSpaceLoadState.Error;
    private static string FailureText(Exception error) => error is UserSpaceDataException failure ? failure.Message : "个人主页请求未完成，请重试。";

    [RelayCommand]
    private Task InitializeUserInformationAsync() => RunAsync(async (generation, token) =>
    {
        if (Card is not null) return;
        var card = await _userService.GetUserInformationAsync(_profile!.Id, token);
        CheckCurrent(generation, token); Card = card ?? throw UserSpaceService.Schema();
        UserName = card.Profile?.User?.Name ?? UserName;
    });

    [RelayCommand]
    private Task ToggleFollowAsync()
    {
        if (_profile is null || IsFollowed is not bool followed) return Task.CompletedTask;
        Invalidate();
        return RunAsync(async (generation, token) =>
        {
            if (followed) await _relationshipService.UnfollowUserAsync(_profile!.Id, token);
            else await _relationshipService.FollowUserAsync(_profile!.Id, token);
            CheckCurrent(generation, token);
            IsFollowed = !followed;
            FeaturedVideos.Clear(); FeaturedState = UserSpaceLoadState.Idle; FeaturedError = null;
            if (SelectedTab == UserSpaceTab.Home) await LoadFeaturedAsync(generation, token);
        });
    }

    [RelayCommand]
    private void Pin()
    {
        if (_profile is null) return;
        this.Get<PinnerViewModel>().AddItemCommand.Execute(new PinItem(_profile.Id, _profile.Name, _profile.Avatar?.SourceUri.ToString() ?? string.Empty, PinContentType.User));
    }

    [RelayCommand]
    private async Task OpenHomeInBrowserAsync()
    {
        if (_profile is null) return;
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://space.bilibili.com/" + UserSpaceService.Id(_profile.Id)));
    }

    [RelayCommand]
    private void OpenVideo(UserSpaceVideoRow row) => this.Get<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(row.Video));
    [RelayCommand]
    private void OpenOpus(UserSpaceOpusRow row)
    {
        object target = row.ArticleId is { } articleId
            ? new ArticleIdentifier(articleId, row.Content, default, default)
            : new ArticleReaderOpusTarget(row.Id, row.Content);
        _logger.LogInformation("个人主页图文导航 TargetKind={TargetKind}", row.ArticleId is not null ? "Article" : "Opus");
        this.Get<NavigationViewModel>().NavigateToOver(typeof(ArticleReaderPage), target);
    }
    [RelayCommand]
    private async Task OpenAudioAsync(UserSpaceAudioRow row) => await Windows.System.Launcher.LaunchUriAsync(row.OriginalUri);

    private void ShowComment(MomentItemViewModel data)
    {
        var moment = data.Data;
        if (string.IsNullOrEmpty(moment.CommentId) || moment.CommentType is null) return;
        IsCommentsOpened = true;
        if (CommentModule.Id == moment.CommentId) return;
        CommentModule.Initialize(moment.CommentId, moment.CommentType.Value, Richasy.BiliKernel.Models.CommentSortType.Hot);
        CommentModule.RefreshCommand.Execute(default);
    }
}
