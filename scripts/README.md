# Release x64 MSIX 从源码构建说明

本文档介绍如何在干净的 Windows 环境下从源码构建 Bili.Copilot 的 Release x64 MSIX 安装包。

## 环境要求

- **操作系统**：Windows 10（19041 或更高版本）/ Windows 11，x64 架构。
- **PowerShell**：Windows PowerShell 5.1 或更高版本。构建脚本须在 Windows 原生环境中执行，请勿在 WSL Linux 下使用 Linux dotnet。
- **Git**：已配置好环境变量。
- **.NET SDK**：.NET 9 SDK（由仓库中的配置约束，运行时及工具链固定为 `9.0.20`）。
- **Visual Studio**：需安装“.NET 桌面开发”工作负载以及 Windows App SDK / WinUI 相关工具，包含 MSBuild。
- **Windows SDK**：`10.0.26100.0`，用于提供 x64 的 `makeappx.exe` 与 `signtool.exe`（默认路径为 `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64`）。
- **网络连接**：构建过程中需要从 NuGet 官方源恢复公开依赖包。

## 构建步骤

### 1. 克隆仓库及子模块

```powershell
git clone --recurse-submodules https://github.com/zjm54321/Bili.Copilot.git
cd Bili.Copilot
```

如果克隆时未加 `--recurse-submodules`，可以在仓库根目录下执行：

```powershell
git submodule update --init --recursive
```

SDK 位于 `src/Libs/BiliKernel` 子模块，源为 <https://github.com/zjm54321/BiliKernel>。

### 2. 编译 BiliKernel 本地依赖包

```powershell
.\scripts\Build-BiliKernel.ps1
```

脚本会还原并编译 BiliKernel 中的 `Abstractions`、`Services.Comment`、`Services.Media` 和 `Services.Moment` 四个项目，生成版本号为 `0.0.3-bilicopilot.1` 的 NuGet 包，并存入 `artifacts/bilikernel-feed/`。仓库根目录的 `nuget.config` 已通过 `packageSourceMapping` 将这四个包指向该本地源，其他依赖继续使用官方源。

### 3. 手动放置原生运行时文件

由于大文件不纳入 Git，在执行打包前需手动将下列原生可执行文件与动态库放入 `src/Desktop/BiliCopilot.UI/Assets/` 对应子目录：

| 文件相对路径 | 对应版本 | 获取方式 |
| --- | --- | --- |
| `libmpv/x64/libmpv-2.dll` | `v0.41.0-1102-g6c092d978` | 访问 [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) 下载对应的 7z 构建包解压提取 |
| `ffmpeg/x64/ffmpeg.exe` | `N-127218-g47313ad3f` | 同样从 shinchiro 的构建发布中下载提取 |
| `visor/x64/BiliCopilot.Visor.exe` | `1.0.0` (NativeAOT x64) | 在仓库根目录下对 `src/Desktop/BiliCopilot.Visor/BiliCopilot.Visor.csproj` 执行 NativeAOT 发布（`dotnet publish -c Release -r win-x64`）获得 |

构建 x64 包只需要上面三个文件。`Build-Release.ps1` 开始时会按 `scripts/native-assets.json` 检查它们是否存在，缺了会直接报错并停止。

### 4. 执行 Release 构建与打包

```powershell
.\scripts\Build-Release.ps1
```

重新构建前如果已有旧的产物目录，请先删除 `artifacts/release/`。

构建成功后，生成的 MSIX 包保存在 `artifacts/release/BiliCopilot_2.2611.3.0_x64.msix`。

#### 脚本可用参数

- `-MSBuildPath <路径>`：可选。手动指定 `MSBuild.exe` 路径。默认会自动通过 `vswhere` 定位最新安装的 Visual Studio。
- `-WindowsSdkBin <路径>`：可选。手动指定 Windows SDK 工具目录。默认使用 `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64`。
- `-CertificateThumbprint <证书指纹>`：可选。指定用于对 MSIX 进行代码签名的证书指纹（40 位十六进制）。默认不传时将生成未签名的 MSIX 包。

## 签名与证书配置

MSIX 应用在 Windows 上安装必须具备有效的数字签名，且证书的 Subject 必须与应用清单中的发布者完全一致（`CN=BiliCopilot Local Release`）。

私钥不包含在仓库中。如果你需要在本地测试安装，可以在 Windows PowerShell 中创建一张自签名代码签名证书，并将其指纹传入构建脚本：

```powershell
# 1. 创建本地自签名证书
$cert = New-SelfSignedCertificate -Type Custom `
  -Subject 'CN=BiliCopilot Local Release' `
  -KeyUsage DigitalSignature `
  -FriendlyName 'BiliCopilot Local Release' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -HashAlgorithm SHA256 `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

# 2. 传入证书指纹进行构建与签名
.\scripts\Build-Release.ps1 -CertificateThumbprint $cert.Thumbprint
```

## 安装与测试

已签名的安装包需要系统信任其签名证书：

1. **导出公钥并信任**：将签名证书的公钥（.cer）导出并导入到本地计算机的“受信任的人”（`TrustedPeople`）存储区。
2. **安装应用**：双击 MSIX 安装，或在 PowerShell 中运行 `Add-AppxPackage -Path artifacts\release\BiliCopilot_2.2611.3.0_x64.msix`。

如果使用的是正式分发结构（包含 `Install.ps1` 和 `Trust-Certificate.ps1` 的发布包），普通用户直接在解压目录下运行即可：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install
```

脚本会自动检测证书信任状态，必要时申请管理员权限执行 `Trust-Certificate.ps1` 导入证书，并完成应用安装。
