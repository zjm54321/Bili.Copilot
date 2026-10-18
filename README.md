<p align="center">
<img src="assets/StoreLogo.png" width="48px"/>
</p>

<div align="center">

# 哔哩助理 (Bili.Copilot)

`哔哩助理` 是基于 Windows App SDK (WinUI 3) 构建的哔哩哔哩第三方 Windows 桌面客户端。

本项目是 [Richasy/Bili.Copilot](https://github.com/Richasy/Bili.Copilot) 的二次开发 Fork 分支（维护者：[zjm54321](https://github.com/zjm54321)）。

</div>

<p align="center">
<a href="#本-fork-的改动">改动说明</a> &nbsp;&bull;&nbsp;
<a href="#安装方式">安装方式</a> &nbsp;&bull;&nbsp;
<a href="#从源码构建">从源码构建</a> &nbsp;&bull;&nbsp;
<a href="#文档与链接">文档与链接</a> &nbsp;&bull;&nbsp;
<a href="#鸣谢">鸣谢</a>
</p>

## 本 Fork 的改动

- **BiliKernel SDK 定制与维护**：以 Git Submodule 形式引入了 fork 后的 [zjm54321/BiliKernel](https://github.com/zjm54321/BiliKernel)（位于 `src/Libs/BiliKernel`），修改并适配了 `Abstractions`、`Services.Comment`、`Services.Media` 和 `Services.Moment` 四个核心模块（版本号 `0.0.3-bilicopilot.1`）。通过本地包源与 `nuget.config` 源映射，确保优先使用本地定制包，未修改模块继续使用公开源。
- **本地发布与测试通道支持**：
  - **LocalRelease 通道**：正式发布版本（当前版本为 `2.2611.3.0`），启用 ReadyToRun 编译优化，提供配套的本地证书信任与侧加载安装脚本。
  - **LocalTest 通道**：日常开发测试通道，使用独立包身份（`BiliCopilot.LocalTest`，显示为“哔哩助理（测试）”），与正式版本完全隔离，支持并存安装与升级验证。
- **原生依赖解耦**：将 `libmpv`、`ffmpeg` 和 `BiliCopilot.Visor` 原生可执行文件解耦为构建前按需放入的独立资产，并提供明确的版本说明。

## 安装方式

### 侧加载安装（推荐）

1. 从 Release 页面下载发布包 ZIP 并完整解压到本地文件夹（请勿在压缩包内直接运行）。
2. 打开 Windows PowerShell（普通用户权限即可），进入解压目录后运行：
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install
   ```
3. 脚本会自动进行环境预检。如果当前设备尚未信任发布证书，脚本会通过 UAC 弹窗提示，自动调用 `Trust-Certificate.ps1` 将证书公钥安装至“受信任的人”（`TrustedPeople`）存储区，随后调用系统部署服务完成安装。

> **说明**：如果不加 `-Install` 参数直接运行 `.\Install.ps1`，脚本仅执行只读预检并显示依赖与签名状态，不会对系统做任何修改。

## 从源码构建

详细的前置依赖、参数说明与自签名步骤请参考 [scripts/README.md](scripts/README.md)。

### 快速构建步骤

1. **克隆仓库及子模块**：
   ```powershell
   git clone --recurse-submodules https://github.com/zjm54321/Bili.Copilot.git
   cd Bili.Copilot
   ```
   *若已有克隆未拉取子模块，可执行 `git submodule update --init --recursive`。*

2. **编译 BiliKernel 本地包**：
   ```powershell
   .\scripts\Build-BiliKernel.ps1
   ```
   脚本将编译定制的 4 个 SDK 项目，打包并输出至 `artifacts/bilikernel-feed` 本地包源。

3. **放置原生运行时文件**：
   将下列二进制文件手动放入 `src/Desktop/BiliCopilot.UI/Assets/` 下对应的目录：
   - `libmpv/x64/libmpv-2.dll`（版本 `v0.41.0-1102-g6c092d978`，来自 [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases)）
   - `ffmpeg/x64/ffmpeg.exe`（版本 `N-127218-g47313ad3f`，来自 shinchiro 构建）
   - `visor/x64/BiliCopilot.Visor.exe`（由仓库中的 `src/Desktop/BiliCopilot.Visor` 项目通过 NativeAOT 发布生成）

4. **构建 Release MSIX 包**：
   ```powershell
   .\scripts\Build-Release.ps1
   ```
   产物输出于 `artifacts/release/BiliCopilot_2.2611.3.0_x64.msix`。如需签名，可传入 `-CertificateThumbprint <证书指纹>`。

## 文档与链接

- [从源码构建 Release 说明](scripts/README.md)
- [LocalTest 测试通道说明](scripts/local-test/README.md)
- [应用使用与功能文档](docs/index.md)

## 鸣谢

- 上游原项目：[Richasy/Bili.Copilot](https://github.com/Richasy/Bili.Copilot)
- [Windows App SDK](https://github.com/microsoft/windowsappsdk) & [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml)
- [mpv](https://mpv.io/) & [mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake)
- [FFmpeg](https://ffmpeg.org/)
- 以及其他为本项目提供支持的开源组件与社区贡献者。
