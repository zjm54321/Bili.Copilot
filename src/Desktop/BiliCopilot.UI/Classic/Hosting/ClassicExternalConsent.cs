using BiliCopilot.UI.Classic.Backends.External;

namespace BiliCopilot.UI.Classic.Hosting;

public static class ClassicExternalConsent
{
    public static async Task<ExternalPlayerProtocol> SelectProtocolAsync(XamlRoot owner,
        string executablePath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择已存在的外部播放器绝对 .exe 路径。");
        token.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            XamlRoot = owner, Title = "选择本次外部播放器协议",
            Content = executablePath + "\n\n请选择程序使用的协议，而不是认证其身份。应用不会按文件名判断或运行版本探测。"
                + "所选安装版本/发布者未经验证；mpv.net 的兼容依据仅为官方 7.1.2.0，未知版本未经验证，必须使用 multi 实例模式。",
            PrimaryButtonText = "独立 mpv", SecondaryButtonText = "mpv.net", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        using var cancellation = token.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        token.ThrowIfCancellationRequested();
        return result switch
        {
            ContentDialogResult.Primary => ExternalPlayerProtocol.Mpv,
            ContentDialogResult.Secondary => ExternalPlayerProtocol.MpvNet,
            _ => throw new OperationCanceledException("已取消外部播放器协议选择。", token),
        };
    }

    public static string Describe(ExternalLaunchConsent consent)
        => consent.Disclosure + "\n\n程序：" + consent.ExecutablePath
            + "\n本次协议：" + consent.SelectedProtocol
            + "\n实际交付类别：" + string.Join("、", consent.DataCategories)
            + "\n媒体/音轨 URL、请求头和 Cookie 值已脱敏，不在界面展开；授权仍针对后端准备的本次完整参数。";
}
