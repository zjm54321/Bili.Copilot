// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.UserSpace;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Bili.Search;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

public sealed partial class UserSpacePageViewModel
{
    private readonly UserSpaceService _space;
    private readonly IMomentDiscoveryService _moments;
    private readonly IRelationshipService _relationshipService;
    private readonly IUserService _userService;
    private readonly ISearchService _searchService;
    private readonly AccountViewModel _account;
    private readonly ILogger<UserSpacePageViewModel> _logger;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private CancellationTokenSource _scope = new();
    private Task _loadTask = Task.CompletedTask;
    private UserProfile? _profile;
    private long _generation;
    private int _pageNumber = 1;
    private int _searchPn;
    private string? _offset;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offsets = new(StringComparer.Ordinal);

    [ObservableProperty] private UserSpaceTab _selectedTab = UserSpaceTab.Home;
    [ObservableProperty] private UserSubmissionKind _selectedSubmission = UserSubmissionKind.Video;
    [ObservableProperty] private UserVideoOrder _videoOrder = UserVideoOrder.Latest;
    [ObservableProperty] private UserOpusFilter _opusFilter = UserOpusFilter.All;
    [ObservableProperty] private UserSpaceCollectionViewModel? _selectedCollection;
    [ObservableProperty] private UserSpaceLoadState _state;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private UserSpaceLoadState _featuredState;
    [ObservableProperty] private string? _featuredError;
    [ObservableProperty] private string? _pinnedDynamicId;
    [ObservableProperty] private bool? _isFollowed;
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private UserCard? _card;
    [ObservableProperty] private bool _isCommentsOpened;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private string _searchKeyword = string.Empty;
    [ObservableProperty] private bool _isSearchEmpty;

    public ObservableCollection<UserSpaceVideoRow> FeaturedVideos { get; } = new();
    public ObservableCollection<UserSpaceVideoRow> HomeVideos { get; } = new();
    public ObservableCollection<MomentItemViewModel> Dynamics { get; } = new();
    public ObservableCollection<UserSpaceVideoRow> SubmissionVideos { get; } = new();
    public ObservableCollection<UserSpaceOpusRow> OpusPosts { get; } = new();
    public ObservableCollection<UserSpaceAudioRow> AudioTracks { get; } = new();
    public ObservableCollection<UserSpaceCollectionViewModel> Collections { get; } = new();
    public ObservableCollection<VideoItemViewModel> SearchVideos { get; } = new();
    public CommentMainViewModel CommentModule { get; }
    public event EventHandler? Initialized;
    public event EventHandler? SearchUpdated;
    public event EventHandler? ListUpdated;
}
