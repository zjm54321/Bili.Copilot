// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Danmaku;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel
{
    [RelayCommand]
    private async Task BackAsync()
    {
        UseIntegrationOperation = false;
        await CloseAsync();
    }

    [RelayCommand]
    private void ToggleTopMost()
    {
        if (IsPlaybackClosing) return;
        IsTopMost = !IsTopMost;
        Window?.SetTopMost(IsTopMost);
    }

    [RelayCommand]
    private async Task IncreaseVolumeAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        var newVolume = Math.Min(MaxVolume, Player.Volume + 5);
        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = newVolume;

        if (Math.Abs(newVolume - Player.Volume) >= 1)
        {
            await Client!.SetVolumeAsync(newVolume);
        }
    }

    [RelayCommand]
    private async Task DecreaseVolumeAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        var newVolume = Math.Max(0, Player.Volume - 5);
        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = newVolume;

        if (Math.Abs(newVolume - Player.Volume) >= 1)
        {
            await Client!.SetVolumeAsync(newVolume);
        }
    }

    [RelayCommand]
    private async Task IncreaseSpeedAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        var newSpeed = Math.Abs(Player.PlaybackRate - 0.1) < 0.01 ? 0.25 : Math.Min(3, Player.PlaybackRate + 0.25);
        if (Math.Abs(newSpeed - Player.PlaybackRate) > 0.01)
        {
            IsSpeedChanging = true;
            LastSpeedChangingTime = DateTimeOffset.Now;
            await Client!.SetSpeedAsync(newSpeed);
        }
    }

    [RelayCommand]
    private async Task DecreaseSpeedAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        var newSpeed = Math.Max(0.1, Player.PlaybackRate - 0.25);
        if (Math.Abs(newSpeed - Player.PlaybackRate) > 0.01)
        {
            IsSpeedChanging = true;
            LastSpeedChangingTime = DateTimeOffset.Now;
            await Client!.SetSpeedAsync(newSpeed);
        }
    }

    [RelayCommand]
    private async Task BackToDefaultModeAsync()
    {
        if (IsExtraPanelVisible)
        {
            IsExtraPanelVisible = false;
            return;
        }

        if (Presentation != PlayerPresentationMode.Normal)
            await SetPresentationAsync(Presentation is PlayerPresentationMode.FullScreen or PlayerPresentationMode.CompactOverlay
                ? _returnPresentation : PlayerPresentationMode.Normal);
    }

    [RelayCommand]
    private Task SkipBackwardAsync()
    {
        if (IsPlaybackClosing || Player is null) return Task.CompletedTask;
        var backwardSeconds = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.StepBackwardSecond, 10d);
        IsProgressChanging = true;
        LastProgressChangingTime = DateTimeOffset.Now;
        return ChangePositionAsync(Math.Max(Player.Position - backwardSeconds, 0));
    }

    [RelayCommand]
    private Task SkipForwardAsync()
    {
        if (IsPlaybackClosing || Player is null) return Task.CompletedTask;
        var forwardSeconds = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.StepForwardSecond, 30d);
        IsProgressChanging = true;
        LastProgressChangingTime = DateTimeOffset.Now;
        return ChangePositionAsync(Math.Min(Player.Position + forwardSeconds, Player.Duration - 1));
    }

    /// <summary>
    /// 改变当前播放位置.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public Task ChangePositionAsync(double position)
        => IsPlaybackClosing ? Task.CompletedTask : OwnWork(ChangePositionCoreAsync(position));

    private async Task ChangePositionCoreAsync(double position)
    {
        if (Player is null || position < 0 || position > Player.Duration || Math.Abs(Player.Position - position) < 2)
        {
            return;
        }

        await Client!.SetCurrentPositionAsync(position);
    }

    /// <summary>
    /// 改变音量.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public Task ChangeVolumeAsync(double volume)
        => IsPlaybackClosing ? Task.CompletedTask : OwnWork(ChangeVolumeCoreAsync(volume));

    private async Task ChangeVolumeCoreAsync(double volume)
    {
        if (Player is null || volume < 0)
        {
            return;
        }

        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = volume;

        if (Math.Abs(Player.Volume - volume) >= 1)
        {
            await Client!.SetVolumeAsync(volume);
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        if (Player.PlaybackState == MpvPlayerState.Playing)
        {
            await Client!.PauseAsync();
        }
        else if (Player.IsStopped)
        {
            await ReplayAsync();
        }
        else
        {
            await Client!.ResumeAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleFullScreenAsync()
    {
        IsExtraPanelVisible = false;
        await SetPresentationAsync(IsFullScreen ? _returnPresentation : PlayerPresentationMode.FullScreen);
    }

    [RelayCommand]
    private async Task ToggleCompactOverlayAsync()
    {
        IsExtraPanelVisible = false;
        await SetPresentationAsync(Presentation == PlayerPresentationMode.CompactOverlay
            ? _returnPresentation : PlayerPresentationMode.CompactOverlay);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task TripleSpeedAsync()
        => IsTouching ? BeginTemporarySpeedAsync(TouchSpeedOwner) : Task.CompletedTask;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RestoreSpeedAsync() => ReleaseTemporarySpeedAsync(TouchSpeedOwner);

    [RelayCommand]
    private async Task ResetSubtitlePositionAsync()
    {
        if (IsPlaybackClosing) return;
        if (Client != null && Player != null)
        {
            SubtitlePosition = IsVerticalScreen ? _verticalSubtitlePosition : _horizontalSubtitlePosition;
            await Client.SetSubtitlePositionAsync(Convert.ToInt32(SubtitlePosition));
        }
    }

    [RelayCommand]
    private async Task ChangeSubtitlePositionAsync(double position)
    {
        if (IsPlaybackClosing) return;
        if (Client != null && Player != null && Math.Abs(SubtitlePosition - position) >= 1)
        {
            if (IsVerticalScreen)
            {
                _verticalSubtitlePosition = position;
            }
            else
            {
                _horizontalSubtitlePosition = position;
            }

            SubtitlePosition = position;
            await Client.SetSubtitlePositionAsync(Convert.ToInt32(position));
        }
    }

    [RelayCommand]
    private async Task ReloadFontsAsync()
    {
        if (IsPlaybackClosing) return;
        var version = _mediaVersion;
        if (Fonts.Count > 0)
        {
            return;
        }

        var fonts = await this.Get<IFontToolkit>().GetFontsAsync();
        if (!IsCurrentPlayback(version)) return;
        foreach (var item in fonts)
        {
            Fonts.Add(item);
        }

        var localFont = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.SubtitleFontFamily, "Segoe UI");
        if (!Fonts.Select(p => p.LocalName).Contains(localFont))
        {
            localFont = "Segoe UI";
        }

        ChangeSubtitleFontFamilyCommand.Execute(localFont);
    }

    [RelayCommand]
    private async Task ChangeSubtitleFontSizeAsync(double fontSize)
    {
        if (IsPlaybackClosing) return;
        if (Client != null && Player != null && Math.Abs(SubtitleFontSize - fontSize) >= 1)
        {
            SubtitleFontSize = fontSize;
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.SubtitleFontSize, fontSize);
            await Client.SetSubtitleFontSizeAsync(Convert.ToInt32(fontSize));
        }
    }

    [RelayCommand]
    private async Task ChangeSubtitleFontFamilyAsync(string font)
    {
        if (IsPlaybackClosing) return;
        if (Client != null && Player != null && !string.IsNullOrEmpty(font) && SubtitleFontFamily != font && Window?.IsClosed == false)
        {
            SubtitleFontFamily = font;
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.SubtitleFontFamily, font);
            await Client.SetSubtitleFontFamilyAsync(font);
        }
    }

    [RelayCommand]
    private async Task SwitchToPreviousChapterAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        var lastSelectedChapter = Chapters.LastOrDefault(p => p.IsPlayed && !p.IsPlaying);
        if (lastSelectedChapter is null)
        {
            return;
        }

        await Client!.SetCurrentPositionAsync(lastSelectedChapter.Position);
    }

    [RelayCommand]
    private async Task SwitchToNextChapterAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        var nextSelectedChapter = Chapters.FirstOrDefault(p => !p.IsPlayed && !p.IsPlaying);
        if (nextSelectedChapter is null)
        {
            return;
        }

        await Client!.SetCurrentPositionAsync(nextSelectedChapter.Position);
    }

    [RelayCommand]
    private async Task SendKeyAsync(string key)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.SendKeyPressAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to send key: {key}");
        }
    }

    [RelayCommand]
    private async Task ToggleStatsOverlayAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.ToggleStatsOverlayAsync();
            IsStatsOverlayShown = !IsStatsOverlayShown;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to toggle stats overlay.");
        }
    }

    [RelayCommand]
    private async Task SetSubtitleDelaySecondsAsync(double seconds)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.SetSubtitleDelaySecondsAsync(seconds);
            SubtitleDelaySeconds = seconds;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set subtitle delay failed");
        }
    }

    [RelayCommand]
    private async Task ToggleSubtitleEnabledAsync()
    {
        if (IsPlaybackClosing || Client is null) return;
        if (IsSubtitleEnabled)
        {
            var selected = Subtitles.FirstOrDefault(p => p.IsSelected);
            if (selected != null)
            {
                Subtitles.FirstOrDefault(p => p.IsSelected)?.SelectCommand.Execute(default);
            }
        }
        else
        {
            await Client!.SetSubtitleTrackAsync(default);
        }
    }

    [RelayCommand]
    private void HideNextTip()
        => RequestHideNextTip?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task TogglePanscanAsync()
    {
        if (IsPlaybackClosing || Client is null) return;
        var v = UsingPanscan ? 0 : 1;
        UsingPanscan = !UsingPanscan;
        try
        {
            await Client!.SetPanscanAsync(v);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set panscan failed");
        }
    }

    [RelayCommand]
    private async Task ToggleMuteStateAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null || Client is null)
        {
            return;
        }

        IsMute = !IsMute;
        await Client.SetMuteAsync(IsMute);
    }

    [RelayCommand]
    private async Task CopyPlayUrlAsync()
    {
        if (IsPlaybackClosing) return;
        var version = _mediaVersion;
        if (_sourceResolver is null)
        {
            return;
        }

        var source = await _sourceResolver!.GetSourceAsync();
        if (!IsCurrentPlayback(version)) return;
        if (!string.IsNullOrEmpty(source.Url))
        {
            var dp = new DataPackage();
            dp.SetText(source.Url);
            Clipboard.SetContent(dp);
            Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Copied), InfoType.Success);
        }
    }

    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player?.IsPlaybackInitialized != true)
        {
            return;
        }

        try
        {
            var fileName = DateTimeOffset.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".png";
            var customScreenshotFolderPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomScreenshotFolderPath, string.Empty);
            if (string.IsNullOrEmpty(customScreenshotFolderPath) || !Directory.Exists(customScreenshotFolderPath))
            {
                customScreenshotFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Bili screenshots");
            }

            if (!Directory.Exists(customScreenshotFolderPath))
            {
                Directory.CreateDirectory(customScreenshotFolderPath);
            }

            var path = Path.Combine(customScreenshotFolderPath, fileName);
            await Client!.TakeScreenshotAsync(path);
            if (File.Exists(path))
            {
                var action = SettingsToolkit.ReadLocalSetting(SettingNames.ScreenshotAction, ScreenshotAction.Open);
                if (action == ScreenshotAction.None)
                {
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                if (action == ScreenshotAction.Copy)
                {
                    var dp = new DataPackage();
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    Clipboard.SetContent(dp);
                    Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success);
                }
                else if (action == ScreenshotAction.Open)
                {
                    await Launcher.LaunchFileAsync(file).AsTask();
                }
            }
        }
        catch (Exception)
        {

            throw;
        }
    }

    private ContentDialog? _activeDanmakuDialog;

    [RelayCommand]
    private async Task SendDanmakuAsync()
    {
        if (IsPlaybackClosing || Player is null || IsConnecting || IsDanmakuLoading)
        {
            return;
        }

        var version = _mediaVersion;
        var position = Convert.ToInt32(Player.Position);
        var token = _activeMediaToken;
        var wasPlaying = Player.IsPlaying;
        try
        {
            await Client.PauseAsync();
            if (!IsCurrentPlayback(version)) return;
            var dialog = new DanmakuSendDialog(DanmakuSend, () => IsPopupVisible = true)
            {
                XamlRoot = Window!.XamlRoot,
            };
            _activeDanmakuDialog = dialog;
            var result = await dialog.ShowAsync();
            if (!IsCurrentPlayback(version)) return;
            if (result == ContentDialogResult.Primary)
            {
                var input = dialog.GetInputText();
                if (!string.IsNullOrEmpty(input))
                {
                    if (await DanmakuSend.TrySendDanmakuAsync(input, position, token) && IsCurrentPlayback(version))
                        Danmaku.AddDanmaku(input);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Send danmaku failed");
            if (IsCurrentPlayback(version)) Window.ShowTip(ex.Message, InfoType.Error);
        }
        finally
        {
            _activeDanmakuDialog = null;
            IsPopupVisible = false;
            if (wasPlaying && IsCurrentPlayback(version)) await Client.ResumeAsync();
        }
    }

    [RelayCommand]
    private async Task ChangeAnime4KModeAsync(Anime4KMode mode)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            Anime4KMode = mode;
            await Client!.SetShadersAsync(GetCombinedShaders());
            CheckClearShaderEnabled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set Anime4K mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeArtCNNModeAsync(ArtCNNMode mode)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            ArtCNNMode = mode;
            await Client!.SetShadersAsync(GetCombinedShaders());
            CheckClearShaderEnabled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set ArtCNN mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeNnedi3ModeAsync(Nnedi3Mode mode)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            Nnedi3Mode = mode;
            await Client!.SetShadersAsync(GetCombinedShaders());
            CheckClearShaderEnabled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set NNEDI3 mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeRavuModeAsync(RavuMode mode)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            RavuMode = mode;
            await Client!.SetShadersAsync(GetCombinedShaders());
            CheckClearShaderEnabled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set RAVU mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeNvidiaVsrModeAsync(VsrScale scale)
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            VsrScale = scale;
            if (scale == VsrScale.None)
            {
                await Client!.SetNvidiaVsrAsync(false);
                // Restore the GPU context and hardware decode settings.
                var decodeType = DecodeOverride ?? SettingsToolkit.ReadLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
                if (decodeType == PreferDecodeType.Auto)
                {
                    await Client.SetGpuContextAsync(GpuContextType.Auto);
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.Auto);
                }
                else if (decodeType == PreferDecodeType.D3D11)
                {
                    await Client.SetGpuContextAsync(GpuContextType.D3D11);
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D11va);
                }
                else if (decodeType == PreferDecodeType.D3D12)
                {
                    await Client.SetGpuContextAsync(GpuContextType.D3D11);
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D12vaCopy);
                }
                else if (decodeType == PreferDecodeType.NVDEC)
                {
                    await Client.SetGpuContextAsync(GpuContextType.D3D11);
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.Nvdec);
                }
                else if (decodeType == PreferDecodeType.Vulkan)
                {
                    await Client.SetGpuContextAsync(GpuContextType.WindowsVulkan);
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.Vulkan);
                }
                else if (decodeType == PreferDecodeType.Software)
                {
                    await Client.SetHardwareDecodeAsync(HardwareDecodeType.None);
                }
            }
            else
            {
                var scaleNumber = scale switch
                {
                    VsrScale.x1_5 => 1.5,
                    VsrScale.x2_0 => 2.0,
                    VsrScale.x3_0 => 3.0,
                    VsrScale.x4_0 => 4.0,
                    _ => 1.0,
                };

                // Ensure the GPU context and hardware decode are set correctly.
                await Client!.SetGpuContextAsync(GpuContextType.D3D11);
                await Client!.SetHardwareDecodeAsync(HardwareDecodeType.D3D11va);
                await Client!.SetNvidiaVsrAsync(true, scaleNumber);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set Nvidia VSR scale failed");
        }
    }

    [RelayCommand]
    private async Task ClearShadersAsync()
    {
        if (IsPlaybackClosing) return;
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.SetShadersAsync(Array.Empty<string>());
            Anime4KMode = Anime4KMode.None;
            ArtCNNMode = ArtCNNMode.None;
            Nnedi3Mode = Nnedi3Mode.None;
            RavuMode = RavuMode.None;
            CheckClearShaderEnabled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clear shaders failed");
        }
    }
}
