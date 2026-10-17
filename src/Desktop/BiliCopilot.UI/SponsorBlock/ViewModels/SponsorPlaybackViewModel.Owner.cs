// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 当前视频 UP 主白名单与静音释放能力。
/// 白名单只使用播放页已确认的 UP 主 UID（<c>SponsorPlayerView.VerifiedUpUid</c>），
/// 从不根据标题、BV 号或作者名推测；取消静音只看播放器当前实际的静音能力。
/// </summary>
public sealed partial class SponsorPlaybackViewModel
{
    private bool _releaseMuteAvailable;
    private string? _currentUpUid;
    private long _ownerEpoch;

    [ObservableProperty]
    private bool _hasCurrentUp;

    [ObservableProperty]
    private bool _isCurrentUpWhitelisted;

    [ObservableProperty]
    private string _currentUpText = string.Empty;

    [ObservableProperty]
    private string _currentUpActionText = string.Empty;

    [ObservableProperty]
    private bool _isCurrentUpBusy;

    [ObservableProperty]
    private string _releaseMuteHint = string.Empty;

    private void ResetOwner()
    {
        _ownerEpoch++;
        _releaseMuteAvailable = false;
        _currentUpUid = null;
        HasCurrentUp = false;
        IsCurrentUpWhitelisted = false;
        IsCurrentUpBusy = false;
        CurrentUpText = string.Empty;
        CurrentUpActionText = string.Empty;
        ReleaseMuteHint = string.Empty;
    }

    private void ProjectOwner(SponsorPlayerView player)
    {
        var release = player.EditorCapabilities.IsDefault
            ? null
            : player.EditorCapabilities.FirstOrDefault(c => c.Feature == SponsorFeature.ReleaseMute);
        _releaseMuteAvailable = release?.Available == true;
        ReleaseMuteHint = release is null ? string.Empty : SponsorUiText.Availability(release, string.Empty);

        var uid = player.Channel.Verification == SponsorChannelVerification.Verified ? player.VerifiedUpUid : null;
        if (!string.Equals(uid, _currentUpUid, StringComparison.Ordinal))
        {
            // A different/absent verified UP: forget any in-flight toggle feedback for the previous one.
            _ownerEpoch++;
            IsCurrentUpBusy = false;
        }

        _currentUpUid = uid;
        HasCurrentUp = uid is not null;
        // The same channel fact the playback rules consume (published from the durable whitelist).
        var listed = uid is not null && player.Channel.IsWhitelisted == true;
        IsCurrentUpWhitelisted = listed;
        CurrentUpText = uid is null
            ? string.Empty
            : listed ? "这位 UP 主在白名单中：不会自动跳过或加速（仍会提示）" : "这位 UP 主不在白名单中";
        CurrentUpActionText = listed ? "移出白名单" : "加入白名单";
    }

    /// <summary>显式切换当前已确认 UP 主的白名单。点击时重新读取票据和修订号；失败不自动重试.</summary>
    [RelayCommand]
    private Task ToggleCurrentUpWhitelistAsync()
        => SponsorUiWork.Track(this, ToggleCurrentUpWhitelistWorkAsync());

    private async Task ToggleCurrentUpWhitelistWorkAsync()
    {
        if (_app is not { } app || _currentUpUid is null || IsCurrentUpBusy)
        {
            return;
        }

        var epoch = _epoch;
        var ownerEpoch = _ownerEpoch;
        var target = !IsCurrentUpWhitelisted;
        IsCurrentUpBusy = true;
        try
        {
            // Click-time revision of the current published view; the facade re-checks the verified UID.
            var revision = app.Snapshot.Revision;
            var result = await RunTicketedAsync(_owner, (a, ticket) => a.SetCurrentUpWhitelistAsync(ticket, revision, target));
            if (epoch != _epoch || ownerEpoch != _ownerEpoch || result is null)
            {
                return;
            }

            Feedback(result.Status == ApplicationStatus.Success
                ? (target ? "已加入白名单，这位 UP 主的视频不再自动跳过" : "已移出白名单")
                : SponsorUiText.Result(result));
        }
        finally
        {
            if (epoch == _epoch && ownerEpoch == _ownerEpoch)
            {
                IsCurrentUpBusy = false;
            }
        }
    }
}
