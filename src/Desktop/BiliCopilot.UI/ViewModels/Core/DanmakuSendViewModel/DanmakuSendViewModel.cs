// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class DanmakuSendViewModel : ViewModelBase
{
    public DanmakuSendViewModel(
        IDanmakuService service,
        ILogger<DanmakuSendViewModel> logger)
    {
        _danmakuService = service;
        _logger = logger;
        Locations = [DanmakuLocation.Scroll, DanmakuLocation.Top, DanmakuLocation.Bottom];
        Colors = [Microsoft.UI.Colors.White, Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Orange, Microsoft.UI.Colors.Khaki, Microsoft.UI.Colors.Yellow, Microsoft.UI.Colors.GreenYellow, Microsoft.UI.Colors.Green, Microsoft.UI.Colors.Blue, Microsoft.UI.Colors.Purple, Microsoft.UI.Colors.LightBlue];
        Location = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuLocation, DanmakuLocation.Scroll);
        IsStandardSize = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsDanmakuStandardSize, true);
        Color = AppToolkit.HexToColor(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuColor, Microsoft.UI.Colors.White.ToString()));
    }

    /// <summary>
    /// 重置数据.
    /// </summary>
    public void ResetData(string aid, string cid)
    {
        _aid = aid;
        _cid = cid;
    }

    public async Task SendDanmakuAsync(string text, int position)
        => _ = await TrySendDanmakuAsync(text, position);

    /// <summary>
    /// Send only on explicit user action. The caller owns media/draft identity and UI completion.
    /// False includes cancellation, even if a service ignored cancellation and later completed.
    /// </summary>
    public async Task<bool> TrySendDanmakuAsync(string text, int position, CancellationToken cancellationToken = default)
    {
        var aid = _aid;
        var cid = _cid;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var danmakuColor = (Color.R * 256 * 256) + (Color.G * 256) + Color.B;
            await _danmakuService.SendVideoDanmakuAsync(text, aid, cid, position, danmakuColor.ToString(), IsStandardSize, Location, cancellationToken);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // Service exceptions can contain authenticated request URLs; keep the original
            // failure message, but do not log the exception, draft or media identifiers.
            _logger.LogError("弹幕发送失败.");
            return false;
        }
    }

    partial void OnIsStandardSizeChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsDanmakuStandardSize, value);

    partial void OnLocationChanged(DanmakuLocation value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuLocation, value);

    partial void OnColorChanged(Windows.UI.Color value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuColor, value.ToString());
}
