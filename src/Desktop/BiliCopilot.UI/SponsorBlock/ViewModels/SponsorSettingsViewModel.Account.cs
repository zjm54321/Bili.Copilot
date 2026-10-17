// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>贡献者身份、贡献统计、本机统计与备份。</summary>
public sealed partial class SponsorSettingsViewModel
{
    // Whole schema2 backup bound (4 MiB), shared with Store/Application ReplaceBackupAsync.
    // Not the 2 MiB canonical auxiliary bound, which only applies to the auxiliary part inside it.
    private const int MaximumBackupBytes = PersistenceJson.MaximumDocumentBytes;

    [ObservableProperty]
    private bool _isIdentityBusy;

    [ObservableProperty]
    private bool _hasIdentity;

    [ObservableProperty]
    private bool _canCreateIdentity;

    [ObservableProperty]
    private bool _isCreateIdentityConfirmVisible;

    [ObservableProperty]
    private string _identityStatusText = "正在读取…";

    [ObservableProperty]
    private string _publicId = string.Empty;

    [ObservableProperty]
    private string _publicIdShort = string.Empty;

    [ObservableProperty]
    private bool _isUserStatsBusy;

    [ObservableProperty]
    private bool _hasUserStats;

    [ObservableProperty]
    private string _userStatsStatusText = "联网后可以查看你在空降助手上的贡献";

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _viewCountText = string.Empty;

    [ObservableProperty]
    private string _minutesSavedText = string.Empty;

    [ObservableProperty]
    private string _segmentCountText = string.Empty;

    [ObservableProperty]
    private string _seekStatsText = "还没有跳过任何片段";

    [ObservableProperty]
    private string _rateStatsText = string.Empty;

    [ObservableProperty]
    private bool _hasRateStats;

    [ObservableProperty]
    private string _statsNoteText = string.Empty;

    [ObservableProperty]
    private bool _canUseFiles;

    [ObservableProperty]
    private bool _isBackupBusy;

    [ObservableProperty]
    private string _backupStatusText = string.Empty;

    [ObservableProperty]
    private bool _isImportConfirmVisible;

    [ObservableProperty]
    private string _importSummaryText = string.Empty;

    /// <summary>是否可以查询贡献统计（需联网许可与身份）.</summary>
    public bool CanRefreshUserStats => HasIdentity && IsNetworkEnabled && !IsUserStatsBusy;

    /// <summary>文件通道未接入时的说明.</summary>
    public string FileUnavailableText => CanUseFiles ? string.Empty : "文件选择尚未接入，暂不能导入或导出";

    partial void OnHasIdentityChanged(bool value) => OnPropertyChanged(nameof(CanRefreshUserStats));

    partial void OnIsUserStatsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRefreshUserStats));

    partial void OnCanUseFilesChanged(bool value) => OnPropertyChanged(nameof(FileUnavailableText));


    private void ApplyStatistics(SponsorStatistics stats)
    {
        SeekStatsText = stats.ConfirmedSeekCount > 0
            ? $"已确认跳过 {stats.ConfirmedSeekCount.ToString("N0", CultureInfo.CurrentCulture)} 次，共 {Duration(stats.ConfirmedSeekMediaSeconds)}"
            : "还没有确认跳过的片段";
        HasRateStats = stats.ObservedRateMediaSeconds > 0;
        RateStatsText = HasRateStats
            ? $"加速播放了 {Duration(stats.ObservedRateMediaSeconds)}，估计节省 {Duration(stats.ObservedRateEstimatedSavedSeconds)}"
            : string.Empty;

        // Persisted vs. newer observed totals, capacity and the explicit save are projected in ApplyDurable.
    }

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return "0 秒";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
            : $"{Math.Max(1, (int)Math.Round(seconds))} 秒";
    }

    [RelayCommand]
    private Task LoadIdentityAsync()
        => SponsorUiWork.Track(this, LoadIdentityWorkAsync());

    private async Task LoadIdentityWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app)
        {
            return;
        }

        IsIdentityBusy = true;
        try
        {
            // Local, load-only: never creates and never touches the network.
            var identity = await app.LoadIdentityViewAsync();
            if (epoch == _epoch)
            {
                ApplyIdentity(identity);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                IdentityStatusText = "暂时无法读取贡献者身份";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsIdentityBusy = false;
            }
        }
    }

    private void ApplyIdentity(IdentityView identity)
    {
        var id = identity.Status == PersistenceStatus.Success ? identity.PublicId : null;
        HasIdentity = !string.IsNullOrEmpty(id);
        PublicId = id ?? string.Empty;
        PublicIdShort = SponsorUiText.ShortId(id);
        CanCreateIdentity = identity.Status == PersistenceStatus.Missing;
        IdentityStatusText = identity.Status switch
        {
            PersistenceStatus.Success => "已创建。公开 ID 可以分享，私密部分只保存在本机，不会出现在备份中。",
            PersistenceStatus.Missing => "尚未创建。提交片段或投票前，需要在本机创建一个贡献者身份。",
            _ => SponsorUiText.Persistence(identity.Status),
        };
        if (!HasIdentity)
        {
            HasUserStats = false;
        }
    }

    [RelayCommand]
    private void RequestCreateIdentity() => IsCreateIdentityConfirmVisible = CanCreateIdentity;

    [RelayCommand]
    private void CancelCreateIdentity() => IsCreateIdentityConfirmVisible = false;

    [RelayCommand]
    private Task ConfirmCreateIdentityAsync()
        => SponsorUiWork.Track(this, ConfirmCreateIdentityWorkAsync());

    private async Task ConfirmCreateIdentityWorkAsync()
    {
        IsCreateIdentityConfirmVisible = false;
        var epoch = _epoch;
        if (_app is not { } app || !CanCreateIdentity)
        {
            return;
        }

        IsIdentityBusy = true;
        try
        {
            // Explicit user intent only; protection failure never falls back to plaintext.
            var identity = await app.CreateIdentityAsync(SponsorIdentityCreateIntent.CreateNewLocalIdentity);
            if (epoch == _epoch)
            {
                ApplyIdentity(identity);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                IdentityStatusText = "未能创建贡献者身份";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsIdentityBusy = false;
            }
        }
    }

    [RelayCommand]
    private void CopyPublicId()
    {
        if (!HasIdentity)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(PublicId);
            Clipboard.SetContent(package);
            IdentityStatusText = "已复制公开 ID";
        }
        catch (Exception)
        {
            IdentityStatusText = "无法访问剪贴板";
        }
    }

    [RelayCommand]
    private Task RefreshUserStatsAsync()
        => SponsorUiWork.Track(this, RefreshUserStatsWorkAsync());

    private async Task RefreshUserStatsWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app || !CanRefreshUserStats)
        {
            return;
        }

        var publicId = PublicId;
        IsUserStatsBusy = true;
        UserStatsStatusText = "正在查询…";
        try
        {
            var read = await app.ReadUserInfoAsync(publicId);
            if (epoch != _epoch || publicId != PublicId)
            {
                return;
            }

            if (read.IsSuccess && read.Value is { } info)
            {
                HasUserStats = true;
                UserName = string.IsNullOrWhiteSpace(info.UserName) || info.UserName == publicId ? "（未设置）" : info.UserName;
                ViewCountText = info.ViewCount is double views ? views.ToString("N0", CultureInfo.CurrentCulture) : "—";
                MinutesSavedText = info.MinutesSaved is double minutes ? Duration(minutes * 60) : "—";
                SegmentCountText = info.SegmentCount is double count ? count.ToString("N0", CultureInfo.CurrentCulture) : "—";
                UserStatsStatusText = "来自空降助手服务器，仅供参考";
            }
            else
            {
                HasUserStats = false;
                UserStatsStatusText = read.Status is ReadStatus.Empty200 or ReadStatus.Empty404
                    ? "服务器上还没有你的贡献记录"
                    : SponsorUiText.Read(read.Status);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                UserStatsStatusText = "查询失败，请稍后重试";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsUserStatsBusy = false;
            }
        }
    }

    [RelayCommand]
    private Task ExportBackupAsync()
        => SponsorUiWork.Track(this, ExportBackupWorkAsync());

    private async Task ExportBackupWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app || _files is not { } files)
        {
            return;
        }

        IsBackupBusy = true;
        BackupStatusText = "正在准备备份…";
        try
        {
            var exported = await app.ExportBackupAsync();
            if (epoch != _epoch)
            {
                return;
            }

            if (!exported.Success || exported.Value is not { } bytes)
            {
                BackupStatusText = exported.Status switch
                {
                    PersistenceStatus.Busy => "请先结束标注，并等待正在进行的请求完成",
                    PersistenceStatus.UnresolvedMutations => "有操作结果还没写入本机，请先在“操作记录”中写入本机，再导出",
                    _ => "无法导出：" + SponsorUiText.Persistence(exported.Status),
                };
                return;
            }

            var name = "BiliCopilot-SponsorBlock-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".json";
            var saved = await files.SaveBackupAsync(bytes, name);
            if (epoch == _epoch)
            {
                BackupStatusText = saved
                    ? "已导出设置、草稿、白名单、过滤偏好、统计和操作记录（不包含贡献者身份的私密部分）"
                    : "已取消导出";
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                BackupStatusText = "导出失败";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBackupBusy = false;
            }
        }
    }

    [RelayCommand]
    private Task PickImportAsync()
        => SponsorUiWork.Track(this, PickImportWorkAsync());

    private async Task PickImportWorkAsync()
    {
        var epoch = _epoch;
        if (_files is not { } files)
        {
            return;
        }

        if (HasPendingChanges || IsFilterDirty)
        {
            BackupStatusText = "请先保存或放弃未保存的设置和过滤偏好";
            return;
        }

        if (IsStatisticsDirty || HasUnsavedOutcomes)
        {
            // The facade refuses replacement while observed statistics or received outcomes are not yet durable.
            BackupStatusText = "有统计或操作结果还没写入本机，请先保存后再导入";
            return;
        }

        IsBackupBusy = true;
        try
        {
            var bytes = await files.PickBackupAsync();
            if (epoch != _epoch)
            {
                return;
            }

            if (bytes is null)
            {
                BackupStatusText = "已取消导入";
                return;
            }

            if (bytes.Length == 0)
            {
                BackupStatusText = "备份文件是空的";
                return;
            }

            if (bytes.Length > MaximumBackupBytes)
            {
                BackupStatusText = $"备份文件超过 {MaximumBackupBytes / 1024 / 1024} MiB，不能导入";
                return;
            }

            _importBytes = bytes;
            ImportSummaryText = $"将用所选备份（{Math.Max(1, bytes.Length / 1024)} KB）替换本机的空降助手设置、全部草稿、白名单、过滤偏好、统计和操作记录。"
                + "当前内容会被覆盖且无法撤销；贡献者身份不受影响。需要先关闭所有播放窗口，结果不确定的草稿或操作记录会阻止替换。";
            IsImportConfirmVisible = true;
            BackupStatusText = string.Empty;
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                BackupStatusText = "无法读取所选文件";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBackupBusy = false;
            }
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        _importBytes = null;
        IsImportConfirmVisible = false;
        BackupStatusText = "已取消导入";
    }

    [RelayCommand]
    private Task ConfirmImportAsync()
        => SponsorUiWork.Track(this, ConfirmImportWorkAsync());

    private async Task ConfirmImportWorkAsync()
    {
        var epoch = _epoch;
        var bytes = _importBytes;
        _importBytes = null;
        IsImportConfirmVisible = false;
        if (_app is not { } app || bytes is null)
        {
            return;
        }

        if (bytes.Length == 0 || bytes.Length > MaximumBackupBytes)
        {
            BackupStatusText = $"备份文件为空或超过 {MaximumBackupBytes / 1024 / 1024} MiB，不能导入";
            return;
        }

        IsBackupBusy = true;
        BackupStatusText = "正在导入…";
        try
        {
            var result = await app.ReplaceBackupAsync(bytes, BackupReplaceIntent.ReplaceAfterQuiescingLocalOwner);
            if (epoch == _epoch)
            {
                BackupStatusText = result.Status == ApplicationStatus.Success
                    ? "已导入备份"
                    : "未导入：" + SponsorUiText.Result(result);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                BackupStatusText = "导入失败";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBackupBusy = false;
            }
        }
    }
}
