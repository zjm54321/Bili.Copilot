// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.UserSpace;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>Only this discovered group's contents/cursor; all transport belongs to the page owner.</summary>
public sealed partial class UserSpaceCollectionViewModel(UserSpaceCollection data) : ObservableObject
{
    public UserSpaceCollection Data { get; } = data;
    public ObservableCollection<UserSpaceVideoRow> Videos { get; } = new();
    public int NextPage { get; internal set; } = 1;
    internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    internal Task LoadTask { get; set; } = Task.CompletedTask;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private UserSpaceLoadState _state;
    [ObservableProperty] private string? _error;
}
