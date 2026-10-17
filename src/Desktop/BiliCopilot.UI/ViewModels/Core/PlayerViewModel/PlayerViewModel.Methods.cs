// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Media;
using Windows.Storage.Streams;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel
{
    private bool _clientConfigured;
    /// <summary>
    /// 初始化播放器.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    private async Task InitializeInternalAsync()
    {
        if (Connector is not null && !(_snapshot.Type == BiliMediaType.Video && Connector is VideoConnectorViewModel)
            && !(_snapshot.Type == BiliMediaType.Pgc && Connector is PgcConnectorViewModel))
        {
            Connector.InvalidateRequests();
            Connector.PlaylistInitialized -= OnConnectorPlaylistInitialized;
            Connector.NewMediaRequest -= OnConnectorNewMediaRequest;
            Connector.RequestOpenExtraPanel -= OnRequestOpenExtraPanel;
            Connector.PropertiesUpdated -= OnConnectorPropertiesUpdated;
            Connector = null;
        }
        if (Connector is null)
        {
            Connector = _snapshot.Type switch
            {
                BiliMediaType.Video => new VideoConnectorViewModel(this),
                BiliMediaType.Pgc => new PgcConnectorViewModel(this),
                _ => default,
            };

            if (Connector != null)
            {
                Connector.PlaylistInitialized += OnConnectorPlaylistInitialized;
                Connector.NewMediaRequest += OnConnectorNewMediaRequest;
                Connector.RequestOpenExtraPanel += OnRequestOpenExtraPanel;
                Connector.PropertiesUpdated += OnConnectorPropertiesUpdated;
            }
        }

        Subtitles.Clear();
        IsSubtitleEmpty = true;

        if (Client is not null && !_clientConfigured && Player is null)
        {
            Client.ErrorOccurred -= OnClientErrorOccurred;
            Client.CacheStateChanged -= OnClientCacheStateChanged;
            await Client.DisposeAsync();
            Client = null;
            _nativeMp?.Dispose();
            _nativeMp = null;
            if (_smtc is not null) _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc = null;
            CheckMediaAdmission();
        }

        if (Client?.IsDisposed != false)
        {
            _nativeMp = new Windows.Media.Playback.MediaPlayer();
            _nativeMp.CommandManager.IsEnabled = false; // 禁用原生播放器的命令管理器.
            _smtc = _nativeMp.SystemMediaTransportControls;
            _smtc.IsEnabled = true; // 启用系统媒体控制.
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            var gpuNames = AppToolkit.GetGpuNames();
            HasNvidiaGpu = gpuNames.Any(p => p.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            var mpvPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomLibmpvPath, string.Empty);
            if (string.IsNullOrEmpty(mpvPath) || !File.Exists(mpvPath))
            {
                var architecture = RuntimeInformation.ProcessArchitecture;
                var identifier = architecture == Architecture.Arm64 ? "arm64" : "x64";
                mpvPath = Path.Combine(Package.Current.InstalledPath, "Assets", "libmpv", identifier, "libmpv-2.dll");
            }

            var configPath = string.Empty;
            var decodeType = DecodeOverride ?? SettingsToolkit.ReadLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
            if (decodeType == PreferDecodeType.Custom)
            {
                configPath = await AppToolkit.EnsureMpvConfigExistAsync();
                CheckMediaAdmission();
            }

            var options = new MpvInitializeOptions
            {
                UseConfig = false,
                LoadScripts = true,
            };
            var client = await MpvClient.CreateAsync(mpvPath, options, logger: logger);
            Client = client;
            LogEmbeddedPlaybackSnapshot("CreateAsyncReturned");
            CheckMediaAdmission();
            Client.ErrorOccurred += OnClientErrorOccurred;
            Client.CacheStateChanged += OnClientCacheStateChanged;
            SetSubtitleDelaySecondsCommand.Execute(SubtitleDelaySeconds);

            if (decodeType == PreferDecodeType.Auto)
            {
                await AwaitInitializationStepAsync(() => Client.SetVideoOutputAsync(VideoOutputType.GpuNext));
                await AwaitInitializationStepAsync(() => Client.SetGpuApiAsync(GpuApiType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetGpuContextAsync(GpuContextType.Auto));
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.Auto));
            }
            else if (decodeType == PreferDecodeType.D3D11)
            {
                await AwaitInitializationStepAsync(() => Client.SetVideoOutputAsync(VideoOutputType.GpuNext));
                await AwaitInitializationStepAsync(() => Client.SetGpuApiAsync(GpuApiType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetGpuContextAsync(GpuContextType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D11va));
            }
            else if (decodeType == PreferDecodeType.D3D12)
            {
                await AwaitInitializationStepAsync(() => Client.SetVideoOutputAsync(VideoOutputType.GpuNext));
                await AwaitInitializationStepAsync(() => Client.SetGpuApiAsync(GpuApiType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetGpuContextAsync(GpuContextType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D12vaCopy));
            }
            else if (decodeType == PreferDecodeType.NVDEC)
            {
                await AwaitInitializationStepAsync(() => Client.SetVideoOutputAsync(VideoOutputType.GpuNext));
                await AwaitInitializationStepAsync(() => Client.SetGpuApiAsync(GpuApiType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetGpuContextAsync(GpuContextType.D3D11));
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.Nvdec));
            }
            else if (decodeType == PreferDecodeType.Vulkan)
            {
                await AwaitInitializationStepAsync(() => Client.SetVideoOutputAsync(VideoOutputType.GpuNext));
                await AwaitInitializationStepAsync(() => Client.SetGpuApiAsync(GpuApiType.Vulkan));
                await AwaitInitializationStepAsync(() => Client.SetGpuContextAsync(GpuContextType.WindowsVulkan));
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.Vulkan));
            }
            else if (decodeType == PreferDecodeType.Software)
            {
                await AwaitInitializationStepAsync(() => Client.SetHardwareDecodeAsync(HardwareDecodeType.None));
            }

            await AwaitInitializationStepAsync(() => Client.SetMuteAsync(IsMute));

            if (!string.IsNullOrEmpty(configPath))
            {
                var configLines = await File.ReadAllLinesAsync(configPath);
                if (IsPlaybackClosing)
                {
                    return;
                }

                MaxVolume = 130;

                // 查找以 volume-max 开头的行，获取等号后面的数字值.
                var volumeMaxLine = Array.Find(configLines, line => line.TrimStart().StartsWith("volume-max", StringComparison.OrdinalIgnoreCase));
                if (volumeMaxLine != null)
                {
                    // 提取等号后的数字值（等号后可能跟着空格）.
                    var match = Regex.Match(volumeMaxLine, @"=\s*(\d+(\.\d+)?)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var maxVolume))
                    {
                        MaxVolume = maxVolume;
                    }
                }

                await AwaitInitializationStepAsync(() => Client.SetConfigFileAsync(configPath));
            }
            else
            {
                // 未使用自定义配置文件时，使用附加设置.
                var preferAudioChannelLayout = SettingsToolkit.ReadLocalSetting(SettingNames.PreferAudioChannelLayout, Richasy.MpvKernel.Core.Enums.AudioChannelLayoutType.Auto);
                if (preferAudioChannelLayout != AudioChannelLayoutType.Auto)
                {
                    if (preferAudioChannelLayout == AudioChannelLayoutType.Custom)
                    {
                        await AwaitInitializationStepAsync(() => Client.SetAudioChannelLayoutAsync(AudioChannelLayoutType.Custom, ["7.1", "5.1", "stereo"]));
                    }
                    else
                    {
                        await AwaitInitializationStepAsync(() => Client.SetAudioChannelLayoutAsync(preferAudioChannelLayout));
                    }
                }

                var cacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSize, 300d);
                var cacheSeconds = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSeconds, 360d);
                var backCacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxBackCacheSize, 0d);
                await AwaitInitializationStepAsync(() => Client.SetDemuxerMaxBytesAsync($"{Convert.ToInt32(cacheSize)}MiB"));
                await AwaitInitializationStepAsync(() => Client.SetDemuxerReadheadSecondsAsync(Convert.ToInt32(cacheSeconds)));
                if (Convert.ToInt32(backCacheSize) > 0)
                {
                    await AwaitInitializationStepAsync(() => Client.SetDemuxerMaxBackBytesAsync($"{Convert.ToInt32(backCacheSize)}MiB"));
                }

                var useCacheOnDisk = SettingsToolkit.ReadLocalSetting(SettingNames.CacheOnDisk, false);
                await AwaitInitializationStepAsync(() => Client.SetCacheOnDiskAsync(useCacheOnDisk));
                if (useCacheOnDisk)
                {
                    var cacheDir = SettingsToolkit.ReadLocalSetting(SettingNames.CacheDir, string.Empty);
                    if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
                    {
                        try
                        {
                            await Client.SetCacheDirAsync(cacheDir);
                        }
                        catch
                        {
                        }

                        if (IsPlaybackClosing)
                        {
                            return;
                        }
                    }
                }

                var profile = SettingsToolkit.ReadLocalSetting(SettingNames.MpvBuiltInProfile, MpvBuiltInProfile.HighQuality);
                if (profile != MpvBuiltInProfile.HighQuality)
                {
                    await AwaitInitializationStepAsync(() => Client.SetLoadAutoProfilesAsync(true));
                    await AwaitInitializationStepAsync(() => Client.SetBuiltInProfileAsync(profile.ToString().ToLowerInvariant()));
                }

                var hrSeek = SettingsToolkit.ReadLocalSetting(SettingNames.HrSeek, HrSeekType.Default);
                await AwaitInitializationStepAsync(() => Client.SetHrSeekAsync(hrSeek));

                MaxVolume = SettingsToolkit.ReadLocalSetting(SettingNames.MaxVolume, 100d);
                await AwaitInitializationStepAsync(() => Client.SetMaxVolumeAsync(Convert.ToInt32(MaxVolume)));

                var audioExclusive = SettingsToolkit.ReadLocalSetting(SettingNames.AudioExclusiveEnabled, false);
                await AwaitInitializationStepAsync(() => Client.SetAudioExclusiveAsync(audioExclusive));
            }

            var logLevel = SettingsToolkit.ReadLocalSetting(SettingNames.MpvLogLevel, MpvLogLevel.Warn);
            await AwaitInitializationStepAsync(() => Client.SetLogLevelAsync(logLevel));
            await AwaitInitializationStepAsync(() => Client.UseIdleAsync(true));
            await AwaitInitializationStepAsync(() => Client.UseKeepOpenAsync(true));
            await AwaitInitializationStepAsync(() => Client.SetTlsVerifyAsync(false));
            await AwaitInitializationStepAsync(() => Client.SetAutoCreatePlaylistAsync(AutoCreatePlaylistKind.No));
            _clientConfigured = true;
        }

        if (_tripleTimer == null)
        {
            _tripleTimer = queue.CreateTimer();
            _tripleTimer.Interval = TimeSpan.FromSeconds(0.2);
            _tripleTimer.Tick += OnTripleTimerTick;
        }
    }

    // Stops managed initialization continuations after close; this does not
    // cancel a native operation that has already been issued.
    private async Task AwaitInitializationStepAsync(Func<Task> step)
    {
        CheckMediaAdmission();
        await step();
        CheckMediaAdmission();
    }

    private void LogEmbeddedPlaybackSnapshot(string phase)
    {
        if (Window is not MainPlayerHost host) return;
        string? videoOutput = null;
        string? gpuContext = null;
        int? hresult = null;
        try
        {
            if (Client is { IsDisposed: false } client)
            {
                videoOutput = MpvNative.GetPropertyString(client.Handle, "vo");
                gpuContext = MpvNative.GetPropertyString(client.Handle, "gpu-context");
            }
        }
        catch (Exception error) { hresult = error.HResult; }
        host.LogWindowSnapshot(phase, videoOutput, gpuContext, hresult: hresult);
    }

    private void OnTripleTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_altSpeedHeld && (PInvoke.GetAsyncKeyState(0x12) & 0x8000) == 0)
        {
            _altSpeedHeld = false;
            _ = ReleaseTemporarySpeedAsync(AltSpeedOwner);
        }

        if (!IsTouching)
        {
            _ = ReleaseTemporarySpeedAsync(TouchSpeedOwner);
        }
    }

    private void OnConnectorPropertiesUpdated(object? sender, PlayerInformationUpdatedEventArgs e)
    {
        if (!IsCurrentPlayback(_mediaVersion) || _smtc is null || !ReferenceEquals(sender, Connector))
        {
            return;
        }

        var updater = _smtc!.DisplayUpdater;
        //updater.AppMediaId = GetSnapshotItemId();
        updater.Type = MediaPlaybackType.Video;
        updater.VideoProperties.Title = e.Title;
        updater.VideoProperties.Subtitle = e.Subtitle;
        if (e.Cover is RandomAccessStreamReference streamRef)
        {
            updater.Thumbnail = streamRef;
        }
        else if (e.Cover is Uri uri)
        {
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        }
        else
        {
            updater.Thumbnail = null;
        }

        updater.Update();
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        queue.TryEnqueue(() =>
        {
            if (args.Button is SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause)
            {
                PlayPauseCommand.Execute(default);
            }
            else if (args.Button == SystemMediaTransportControlsButton.Next && IsNextButtonEnabled)
            {
                PlayNextCommand.Execute(default);
            }
            else if (args.Button == SystemMediaTransportControlsButton.Previous && IsPrevButtonEnabled)
            {
                PlayPreviousCommand.Execute(default);
            }
        });
    }

    private void OnClientCacheStateChanged(object? sender, MpvCacheStateEventArgs e)
    {
        var version = _mediaVersion;
        queue.TryEnqueue(() => { if (IsCurrentPlayback(version)) CacheStateChanged?.Invoke(this, e); });
    }

    private async void OnClientErrorOccurred(object? sender, MpvError e)
    {
        if (!queue.HasThreadAccess)
        {
            var version = _mediaVersion;
            queue.TryEnqueue(() => { if (IsCurrentPlayback(version)) OnClientErrorOccurred(sender, e); });
            return;
        }
        if (!IsCurrentPlayback(_mediaVersion) || Player is null) return;
        var mediaVersion = _mediaVersion;
        if (e == MpvError.TlsError)
        {
            if (!_isTlsFailed)
            {
                _isTlsFailed = true;
                return;
            }

            return;
        }
        else if (Player.Duration < 1 && !_isBroken)
        {
            _isBroken = true;
            try { await ReplayAsync(); }
            catch (Exception ex) { if (IsCurrentPlayback(mediaVersion)) ReportPlaybackFailure(ex); }
            return;
        }

        queue.TryEnqueue(() =>
        {
            if (!IsCurrentPlayback(mediaVersion)) return;
            var errorContent = e switch
            {
                MpvError.Nomem => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorNoMemory),
                MpvError.Uninitialized => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUninitialized),
                MpvError.LoadingFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorLoadingFailed),
                MpvError.AoInitFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorAudioFailed),
                MpvError.VoInitFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorVideoFailed),
                MpvError.NothingToPlay => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorNotingToPlay),
                MpvError.UnknownFormat => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUnknownFormat),
                MpvError.Unsupported => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUnsupported),
                _ => default,
            };

            if (!string.IsNullOrEmpty(errorContent) && (Player.Duration < 1 || e == MpvError.PassthroughFormatUnsupported))
            {
                ErrorMessage = errorContent;
            }
            else
            {
                WarningOccurred?.Invoke(this, ResourceToolkit.GetLocalizedString(StringNames.MpvErrorGeneric));
            }

            CheckBlackBackgroundVisible();
        });
    }

    private async Task ResetPlayerPresenterModeAsync(MainPlayerHost.PresentationDiagnostic? diagnostic = null)
    {
        if (Window is null || Client is null || IsPlaybackClosing)
        {
            return;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Task fullScreen;
            try { fullScreen = Client.SetFullScreenStateAsync(IsFullScreen); }
            finally { if (diagnostic is not null) diagnostic.FullScreenSyncMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            await fullScreen;
        }
        finally { if (diagnostic is not null) diagnostic.FullScreenWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (IsPlaybackClosing) return;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Task compact;
            try { compact = Client.SetCompactOverlayStateAsync(Presentation == PlayerPresentationMode.CompactOverlay); }
            finally { if (diagnostic is not null) diagnostic.CompactSyncMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            await compact;
        }
        finally { if (diagnostic is not null) diagnostic.CompactWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }

    private void CheckFullScreen()
    {
        OnHostPresentationChanged(Window, EventArgs.Empty);
    }

    private void CheckCompactOverlay()
    {
        OnHostPresentationChanged(Window, EventArgs.Empty);
    }

    private void ShowMainWindow()
    {
        var mainWnd = this.Get<Core.AppViewModel>().Windows.Find(p => p is MainWindow);
        if (!this.Get<Core.AppViewModel>().IsClosed && mainWnd is not null)
        {
            var canReadHost = _hostReady && Window is { IsClosed: false, IsDisposed: false };
            if (canReadHost && !PInvoke.IsZoomed(new(Window!.Handle)))
            {
                var currentPos = Window!.GetWindow().Position;
                var currentSize = Window.GetWindow().Size;
                if (Window.GetWindow().Presenter.Kind == AppWindowPresenterKind.Overlapped)
                {
                    mainWnd.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(currentPos.X, currentPos.Y, currentSize.Width, currentSize.Height));
                }
            }

            mainWnd.Activate();
        }
    }

    private void CheckBlackBackgroundVisible()
        => IsBlackBackgroundVisible = Player?.IsPlaybackInitialized != true || !string.IsNullOrEmpty(ErrorMessage);

    private void OnWindowSizeChanged(object? sender, Size e)
    {
        queue.TryEnqueue(() =>
        {
            var isVertical = e.Height > e.Width;
            if (isVertical != IsVerticalScreen)
            {
                IsVerticalScreen = isVertical;
                ResetSubtitlePositionCommand.Execute(default);
            }
        });
    }

    private async void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!queue.HasThreadAccess)
        {
            var version = _mediaVersion;
            queue.TryEnqueue(() => { if (IsCurrentPlayback(version)) OnPlayerPropertyChanged(sender, e); });
            return;
        }
        if (!IsCurrentPlayback(_mediaVersion) || !ReferenceEquals(sender, Player))
        {
            return;
        }

        // Notifications are reread prompts only; never feed this event's old payload into Sponsor.
        if (e.PropertyName is nameof(Player.Position) or nameof(Player.Duration) or nameof(Player.PlaybackRate)
            or nameof(Player.PlaybackState) or nameof(Player.IsPlaybackInitialized))
            _sponsor?.RequestObservation();
        var mediaVersion = _mediaVersion;
        try
        {
            await OwnWork(HandlePlayerPropertyChangedAsync(e, mediaVersion));
        }
        catch (Exception ex)
        {
            if (IsCurrentPlayback(mediaVersion))
            {
                ReportPlaybackFailure(ex);
            }
            else
            {
                logger.LogError(ex, "Stale playback event failed.");
            }
        }
    }

    private async Task HandlePlayerPropertyChangedAsync(PropertyChangedEventArgs e, long mediaVersion)
    {
        if (e.PropertyName == nameof(Player.Position))
        {
            IsPreviewProgressChanging = false;
            PositionText = TimeSpan.FromSeconds(Player.Position).ToString(@"hh\:mm\:ss");
            ProgressChanged?.Invoke(this, Player.Position);
            Danmaku.UpdatePosition(Convert.ToInt32(Player.Position));
            foreach (var chapter in Chapters)
            {
                chapter.IsPlayed = chapter.Position <= Player.Position;
                chapter.IsPlaying = Math.Abs(chapter.Position - Player.Position) < 2;
            }

            if (Player.Duration > 5)
            {
                IsEnd = false;
                var isShowTip = !SettingsToolkit.ReadLocalSetting(SettingNames.PlayNextWithoutTip, false);
                var endSec = isShowTip ? 2 : 0.1;
                if (Math.Abs(Player.Position - Player.Duration) < endSec)
                {
                    await CancelPlayerInputAsync();

                    // The same Player object is reused for another media. A reference
                    // check alone cannot reject a delayed end-of-media continuation.
                    if (!IsCurrentPlayback(mediaVersion) || IsConnecting)
                    {
                        return;
                    }

                    if (_isTlsFailed)
                    {
                        // 如果 TLS 失败了，可能会导致播放器无法正确获取位置，所以这里不处理跳过逻辑.
                        ErrorMessage = ResourceToolkit.GetLocalizedString(StringNames.SeekFailed);
                        CheckBlackBackgroundVisible();
                        return;
                    }

                    // 因为使用了 keep-open，所以视作播放结束.
                    var autoPlayNext = SettingsToolkit.ReadLocalSetting(SettingNames.AutoPlayNext, true);
                    if (autoPlayNext && IsNextButtonEnabled && !IsConnecting)
                    {
                        if (isShowTip)
                        {
                            if (!IsNextTipShown && !IsNextTipShownInThisMedia)
                            {
                                IsNextTipShownInThisMedia = true;
                                RequestShowNextTip?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        else
                        {
                            PlayNextCommand.Execute(default);
                            _isTlsFailed = false;
                            return;
                        }
                    }
                    else
                    {
                        IsEnd = true;
                        IsControlsVisible = true;
                    }
                }
                else if (Math.Abs(Player.Position - Player.Duration) > 2 && IsNextTipShown)
                {
                    RequestHideNextTip?.Invoke(this, EventArgs.Empty);
                }

                if (Player.PlaybackState == MpvPlayerState.Playing && _isTlsFailed)
                {
                    _isTlsFailed = false;
                }

                _prevPosition = Player.Position;
            }
            else if (Player.Duration < 5)
            {
                return;
            }
        }
        else if (e.PropertyName == nameof(Player.Duration))
        {
            DurationText = TimeSpan.FromSeconds(Player.Duration).ToString(@"hh\:mm\:ss");
            ChapterInitialized?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(Player.IsFullScreen))
        {
            CheckFullScreen();
        }
        else if (e.PropertyName == nameof(Player.IsCompactOverlay))
        {
            CheckCompactOverlay();
        }
        else if (e.PropertyName == nameof(Player.Volume) && Player.IsPlaybackInitialized)
        {
            if (IsVolumeChanging)
            {
                if (DateTimeOffset.Now - LastVolumeChangingTime < TimeSpan.FromMilliseconds(500))
                {
                    return;
                } 

                IsVolumeChanging = false;
            }

            CurrentVolume = Player.Volume;
            SettingsToolkit.WriteLocalSetting(SettingNames.PlayerVolume, Player.Volume);
        }
        else if (e.PropertyName == nameof(Player.PlaybackRate) && Player.IsPlaybackInitialized)
        {
            Danmaku.ExtraSpeed = Player.PlaybackRate;
            // Persistence belongs exclusively to confirmed persistent user-rate intent, not MPV echoes.
        }
        else if (e.PropertyName == nameof(Player.IsPlaybackInitialized) && Player.IsPlaybackInitialized && !(Window?.IsClosed ?? true))
        {
            ErrorMessage = default;
            _prevPosition = 0;
            CheckBlackBackgroundVisible();
            CheckBottomProgressBarVisible();
            ResetSubtitlePositionCommand.Execute(default);
            ReloadFontsCommand.Execute(default);
            var subtitleFontSize = SettingsToolkit.ReadLocalSetting(SettingNames.SubtitleFontSize, 32d);
            ChangeSubtitleFontSizeCommand.Execute(Math.Max(1, subtitleFontSize));
            // 强制关闭可能开启的统计信息覆盖层.
            if (_sponsor?.CanUseBinding == true) await SendOpaqueUserKeyAsync("ESC");
            else if (_sponsor?.HasBinding != true) await Client!.SendKeyPressAsync("ESC"); // Initial source setup, before any Sponsor binding exists.
            if (!IsCurrentPlayback(mediaVersion))
            {
                return;
            }

            IsStatsOverlayShown = false;

            // Make sure playing.
            if (Player.PlaybackState != MpvPlayerState.Playing)
            {
                if (_sponsor?.CanUseBinding == true) await SetUserPausedAsync(false);
                else if (_sponsor?.HasBinding != true) await Client!.ResumeAsync();
                if (!IsCurrentPlayback(mediaVersion))
                {
                    return;
                }
            }

            await VerifyChaptersAsync();
            if (!IsCurrentPlayback(mediaVersion))
            {
                return;
            }

            if (!_isDanmakuInitialized)
            {
                InitializeDanmakuCommand.Execute(default);
                _isDanmakuInitialized = true;
            }
        }
        else if (e.PropertyName == nameof(Player.CacheSpeed))
        {
            CacheSpeedText = Player.CacheSpeed < 1 ? default : Player.CacheSpeed.Bytes().Humanize() + "/s";
        }
        else if (e.PropertyName == nameof(Player.PlaybackState))
        {
            if (IsDanmakuControlVisible && Danmaku.IsShowDanmaku)
            {
                if (Player.PlaybackState == MpvPlayerState.Playing)
                {
                    Danmaku.Resume();
                }
                else
                {
                    Danmaku.Pause();
                }
            }

            if (Player.PlaybackState is MpvPlayerState.Buffering or MpvPlayerState.Seeking && !string.IsNullOrEmpty(ErrorMessage))
            {
                ErrorMessage = default;
                CheckBlackBackgroundVisible();
            }

            _smtc?.PlaybackStatus = Player.PlaybackState switch
            {
                MpvPlayerState.Playing => MediaPlaybackStatus.Playing,
                MpvPlayerState.Paused => MediaPlaybackStatus.Paused,
                MpvPlayerState.Buffering or MpvPlayerState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
    }

    private async void OnConnectorNewMediaRequest(object? sender, MediaSnapshot e)
    {
        if (_snapshot == e || IsPlaybackClosing || !ReferenceEquals(sender, Connector))
        {
            return;
        }

        try
        {
            await InitializeAsync(e);
        }
        catch (Exception ex)
        {
            ReportPlaybackFailure(ex);
        }
    }

    private void OnConnectorPlaylistInitialized(object? sender, PlaylistInitializedEventArgs e)
    {
        if (!IsCurrentPlayback(_mediaVersion) || !ReferenceEquals(sender, Connector))
        {
            return;
        }

        IsPrevButtonEnabled = e.CanPrev;
        IsNextButtonEnabled = e.CanNext;
        PrevButtonToolTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.PrevVideoTemplate), e.PrevTitle);
        NextButtonToolTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.NextVideoTemplate), e.NextTitle);
        IsVideoNavigationAvailable = e.CanPrev || e.CanNext;
        if (_smtc != null)
        {
            _smtc.IsNextEnabled = IsNextButtonEnabled;
            _smtc.IsPreviousEnabled = IsPrevButtonEnabled;
        }
    }

    private void OnRequestOpenExtraPanel(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayback(_mediaVersion) || !ReferenceEquals(sender, Connector)) return;
        if (IsControlsVisible)
        {
            IsControlsVisible = false;
        }

        IsExtraPanelVisible = true;
    }

    private void OnWindowPointerReleased(InputPointerSource sender, PointerEventArgs args)
    {
        if ((args.CurrentPoint.Properties.PointerUpdateKind is PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released) && UseIntegrationOperation)
        {
            BackCommand.Execute(default);
            args.Handled = true;
        }
    }

    private async Task VerifyChaptersAsync()
    {
        if (Client is null || Player is null)
        {
            return;
        }

        var mediaVersion = _mediaVersion;
        var chapters = await Client.GetChaptersAsync();
        if (!IsCurrentPlayback(mediaVersion))
        {
            return;
        }

        if (chapters.IsSuccess && Chapters.Count == 0 && chapters.Value.Count > 1)
        {
            var index = 0;
            foreach (var chapter in chapters.Value)
            {
                Chapters.Add(new ChapterItemViewModel(index, chapter.Title, chapter.Time));
                index++;
            }

            ChapterInitialized?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadSourcesAsync()
    {
        var version = _mediaVersion;
        try
        {
            if (_sourceResolver is VideoMediaSourceResolver videoResolver)
            {
                var sources = await videoResolver.GetSourcesAsync();
                if (!IsCurrentPlayback(version)) return;

                IsSourceSelectable = sources?.Count > 1;
                Sources.Clear();
                if (sources != null)
                {
                    var currentSource = videoResolver.GetCurrentFormat();
                    foreach (var source in sources)
                    {
                        var vm = new SourceItemViewModel(source, item => IsCurrentPlayback(version)
                            ? OwnWork(SelectSourceAsync(item)) : Task.CompletedTask);
                        if (currentSource != null)
                        {
                            vm.IsSelected = source.Quality == currentSource.Quality;
                        }

                        Sources.Add(vm);
                    }

                    SelectedSource = Sources.FirstOrDefault(p => p.IsSelected);
                }

                var c = ((VideoConnectorViewModel)Connector);
                DanmakuSend.ResetData(c._view.Information.Identifier.Id, c._part.Identifier.Id);
            }
            else if (_sourceResolver is PgcMediaSourceResolver pgcResolver)
            {
                var sources = await pgcResolver.GetSourcesAsync();
                if (!IsCurrentPlayback(version)) return;

                IsSourceSelectable = sources?.Count > 1;
                Sources.Clear();
                if (sources != null)
                {
                    var currentSource = pgcResolver.GetCurrentFormat();
                    foreach (var source in sources)
                    {
                        var vm = new SourceItemViewModel(source, item => IsCurrentPlayback(version)
                            ? OwnWork(SelectSourceAsync(item)) : Task.CompletedTask);
                        if (currentSource != null)
                        {
                            vm.IsSelected = source.Quality == currentSource.Quality;
                        }

                        Sources.Add(vm);
                    }

                    SelectedSource = Sources.FirstOrDefault(p => p.IsSelected);
                }

                var c = ((PgcConnectorViewModel)Connector);
                var aid = c._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
                var cid = c._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString();
                DanmakuSend.ResetData(aid.ToString(), cid.ToString());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载清晰度列表失败");
        }
    }

    private async Task LoadSubtitlesAsync()
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsSubtitleEnabled)
            {
                return;
            }

            var service = this.Get<ISubtitleService>();
            if (Connector is VideoConnectorViewModel videoVM)
            {
                var aid = videoVM._view.Information.Identifier.Id;
                var cid = videoVM._part.Identifier.Id;
                var metas = await service.GetSubtitleMetasAsync(aid, cid, token);
                if (!IsCurrentPlayback(version))
                {
                    return;
                }

                Subtitles.Clear();
                if (metas?.Count > 0)
                {
                    foreach (var item in metas)
                    {
                        var vm = new SubtitleItemViewModel(item,
                            selected => IsCurrentPlayback(version) ? OwnWork(SelectSubtitleAsync(selected)) : Task.CompletedTask,
                            selected => IsCurrentPlayback(version) ? OwnWork(DeselectSubtitleAsync(selected)) : Task.CompletedTask);
                        Subtitles.Add(vm);
                    }
                }

                if (Subtitles.Count > 0 && Subtitles.Any(p => !p.Data.IsAI))
                {
                    var firstNonAi = Subtitles.First(p => !p.Data.IsAI);
                    firstNonAi.SelectCommand.Execute(default);
                }

                IsSubtitleEmpty = Subtitles.Count == 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载字幕列表失败");
        }
    }

    private async Task SelectSubtitleAsync(SubtitleItemViewModel vm)
    {
        foreach (var item in Subtitles)
        {
            item.IsSelected = item.Data.Equals(vm.Data);
        }

        if (string.IsNullOrEmpty(vm._srtFilePath))
        {
            return;
        }

        await Client!.SetExternalSubtitleTrackAsync(vm._srtFilePath);
    }

    private async Task DeselectSubtitleAsync(SubtitleItemViewModel vm)
    {
        foreach (var item in Subtitles)
        {
            item.IsSelected = false;
        }

        await Client!.SetSubtitleTrackAsync(default);
    }

    private static string[] FindAnime4KShaders(Anime4KMode mode)
    {
        if (mode == Anime4KMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "Anime4K");
        if (mode == Anime4KMode.LowA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowC)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowAA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_Restore_CNN_S.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowBB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_S.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowCA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_S.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.HighA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighC)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighAA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighBB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighCA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private static string[] FindArtCNNShaders(ArtCNNMode mode)
    {
        if (mode == ArtCNNMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "ArtCNN");
        if (mode == ArtCNNMode.C4F16)
        {
            return [GetShaderPath("ArtCNN_C4F16.glsl")];
        }
        else if (mode == ArtCNNMode.C4F16DN)
        {
            return [GetShaderPath("ArtCNN_C4F16_DN.glsl")];
        }
        else if (mode == ArtCNNMode.C4F16DS)
        {
            return [GetShaderPath("ArtCNN_C4F16_DS.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32)
        {
            return [GetShaderPath("ArtCNN_C4F32.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32DN)
        {
            return [GetShaderPath("ArtCNN_C4F32_DN.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32DS)
        {
            return [GetShaderPath("ArtCNN_C4F32_DS.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32Ani4KI2)
        {
            return [GetShaderPath("Ani4Kv2_ArtCNN_C4F32_i2.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32AniSDI4)
        {
            return [GetShaderPath("AniSD_ArtCNN_C4F32_i4.glsl")];
        }

        return [];
        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private static string[] FindNnedi3Shaders(Nnedi3Mode mode)
    {
        if (mode == Nnedi3Mode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "nnedi3");
        var igvFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "igv");

        if (mode == Nnedi3Mode.Basic)
        {
            return [GetShaderPath("nnedi3-nns32-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.Plus)
        {
            return [GetShaderPath("nnedi3-nns64-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.Pro)
        {
            return [GetShaderPath("nnedi3-nns128-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.ProMax)
        {
            return [GetShaderPath("nnedi3-nns256-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
        string GetIgvShaderPath(string shaderName) => Path.Combine(igvFolder, shaderName);
    }

    private static string[] FindRavuShaders(RavuMode mode)
    {
        if (mode == RavuMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "Ravu");

        if (mode == RavuMode.LiteArR2)
        {
            return [GetShaderPath("ravu-lite-ar-r2.hook")];
        }
        else if (mode == RavuMode.LiteArR3)
        {
            return [GetShaderPath("ravu-lite-ar-r2.hook")];
        }
        else if (mode == RavuMode.LiteArR4)
        {
            return [GetShaderPath("ravu-lite-ar-r4.hook")];
        }
        else if (mode == RavuMode.LiteR2)
        {
            return [GetShaderPath("ravu-lite-r2.hook")];
        }
        else if (mode == RavuMode.LiteR3)
        {
            return [GetShaderPath("ravu-lite-r3.hook")];
        }
        else if (mode == RavuMode.LiteR4)
        {
            return [GetShaderPath("ravu-lite-r4.hook")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private string[] GetCombinedShaders()
    {
        var shaders = new List<string>();
        shaders.AddRange(FindAnime4KShaders(Anime4KMode));
        shaders.AddRange(FindArtCNNShaders(ArtCNNMode));
        shaders.AddRange(FindRavuShaders(RavuMode));
        shaders.AddRange(FindNnedi3Shaders(Nnedi3Mode));
        return [.. shaders.Distinct()];
    }

    private void CheckClearShaderEnabled()
    {
        IsClearShaderEnabled = Anime4KMode != Anime4KMode.None ||
                                 ArtCNNMode != ArtCNNMode.None ||
                                 RavuMode != RavuMode.None ||
                                 Nnedi3Mode != Nnedi3Mode.None;
    }
}
