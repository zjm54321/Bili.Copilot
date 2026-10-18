#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live;

public readonly record struct LiveAccountStamp(string? AccountId, long Generation, CancellationToken Lifetime);

public readonly record struct LiveBinding(Guid SessionId, long RoomGeneration, long RoomId,
    long AccountGeneration, string? AccountId);

public enum LivePlaybackState { Idle, Opening, Playing, Paused, Ended, Failed, Closing, Closed }

public enum LiveChatState { Idle, Connecting, Authenticating, Connected, Retrying, Unavailable, Failed, Closed }

public enum LiveSendOutcome { NotSent, Accepted, Rejected, Unknown }

public sealed record LiveChatMessage(LiveBinding Binding, string? EventId, long UserId,
    string UserName, string Text, DateTimeOffset ReceivedAt);

public sealed record LiveChatStatus(LiveBinding Binding, LiveChatState State, string? SafeError);

public enum LiveAudienceMetric { OnlineRank, Watched }

public sealed record LiveAudienceUpdate(LiveBinding Binding, long Count, LiveAudienceMetric Metric);

// Optional control-data channel: audience counts never enter the display-message collection.
public interface ILiveAudienceSource
{
    event EventHandler<LiveAudienceUpdate>? AudienceChanged;
}

public sealed record LiveSendResult(LiveBinding Binding, LiveSendOutcome Outcome,
    int? ApiCode, string SafeMessage);

public interface ILiveChatClient : IAsyncDisposable
{
    event EventHandler<LiveChatMessage>? MessageReceived;
    event EventHandler<LiveChatStatus>? StatusChanged;
    Task ConnectAsync(LiveBinding binding, CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken);
}

public interface ILiveSession : INotifyPropertyChanged, IAsyncDisposable
{
    LiveBinding? Binding { get; }
    string Title { get; }
    string AnchorName { get; }
    Uri? AnchorAvatar { get; }
    /// <summary>The room owner's profile (in-app user space target); null while the room has not reported one.</summary>
    UserProfile? Anchor { get; }
    bool? IsFollowing { get; }
    long? AudienceCount { get; }
    string AudienceLabel { get; }
    IReadOnlyList<PlayerFormatInformation> Qualities { get; }
    PlayerFormatInformation? SelectedQuality { get; }
    bool CanChangeQuality { get; }
    IAsyncRelayCommand<PlayerFormatInformation> SelectQualityCommand { get; }
    LivePlaybackState PlaybackState { get; }
    LiveChatState ChatState { get; }
    string? PlaybackError { get; }
    string? ChatError { get; }
    double Volume { get; }
    bool IsBusy { get; }
    bool CanSend { get; }
    ReadOnlyObservableCollection<LiveChatMessage> Messages { get; }
    event EventHandler<LiveChatMessage>? MessageReceived;
    Task OpenAsync(LiveInformation live, CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
    Task SelectQualityAsync(PlayerFormatInformation quality, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken);
    Task CloseAsync();
}

/// <summary>Window presentation belongs to the host, independently of the playback session.</summary>
public interface ILivePresentation : INotifyPropertyChanged
{
    bool IsFullScreen { get; }
    bool IsCompactOverlay { get; }
    void EnterFullScreen();
    void ExitFullScreen();
    void EnterCompactOverlay();
    void ExitCompactOverlay();
    void RestorePresentation();
}
