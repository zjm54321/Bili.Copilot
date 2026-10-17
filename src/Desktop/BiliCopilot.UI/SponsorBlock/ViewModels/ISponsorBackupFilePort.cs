// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 由宿主（拥有窗口句柄与文件选择器的一方）提供的备份文件通道。
/// 本层不打开任何对话框、不猜测路径。未提供时导入/导出按钮显示为“尚未接入”。
/// 两个方法都在界面线程调用；用户取消时返回 false / null，而不是抛出异常。
/// </summary>
internal interface ISponsorBackupFilePort
{
    /// <summary>让用户选择保存位置并写入不含身份的备份字节。返回是否已写入。</summary>
    Task<bool> SaveBackupAsync(byte[] secretFreeBackup, string suggestedFileName);

    /// <summary>让用户选择一个备份文件并读出字节（宿主负责整份备份 4 MiB 上限，与 schema2 备份一致）。取消返回 null。</summary>
    Task<byte[]?> PickBackupAsync();
}
