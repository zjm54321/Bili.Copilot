# Release 构建：干净克隆检查

## 当前阻塞（2026-10-18）

**目前不能仅用仓库源码和 `nuget.config` 中的公开源完成 Release 构建。**
在 Windows 上从提交 `74f83b94f91f75710c547916cdc01b722def4f0a` 执行
`git clone --recurse-submodules`，使用克隆目录内全新的 `NUGET_PACKAGES`，
按已知成功版本的参数执行 restore，结果为 **0 warnings / 5 errors (NU1102)**。
没有使用参考构建的本地 packages 缓存。

`src/Directory.Packages.props` 引用以下四个私有构建版本；所有已声明源均找不到它们：

- `Richasy.BiliKernel.Abstractions`
- `Richasy.BiliKernel.Services.Comment`
- `Richasy.BiliKernel.Services.Media`
- `Richasy.BiliKernel.Services.Moment`

版本均为 `0.0.2-preview2-bilicopilot.2611.2.sb2`；nuget.org 的最近版本是
`0.0.2-preview2`。Abstractions 被两个项目引用，因此共有五个错误。
不能降级到公开版本来绕过此问题：该版本承载本地修改，降级可能改变应用功能。
需要先将对应修改后的 SDK 源码及构建流程纳入版本管理，或明确提供可长期访问的固定版本包源。
复制开发机缓存不能满足“只用 git 仓库源码”的验收条件。

## Windows 前置条件与克隆

参考成功构建使用 Visual Studio 18 Community 的 MSBuild、.NET SDK 和
Windows SDK `10.0.26100.0` 的 x64 MakeAppx/SignTool。此次机器上的 .NET SDK 是
`10.0.402`；目标框架仍为 .NET 9，参考构建固定运行时、ILLink、Crossgen2 为 `9.0.20`。
需要 Windows 桌面/WinUI 构建工具。若还要从源码发布 NativeAOT Visor，需要 C++ 工具链。

```powershell
git clone --recurse-submodules <repository-url> Bili.Copilot
cd Bili.Copilot
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget-clean'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PWD '.nuget-http-clean'
# 必须显式指定仓库配置，避免用户级包源或 fallback 隐式补齐缺失包。
dotnet msbuild src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj /t:Restore `
  /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
  /p:RestoreConfigFile="$PWD/nuget.config" /p:RestorePackagesPath="$env:NUGET_PACKAGES" `
  /p:RestoreFallbackFolders= /p:RestoreAdditionalProjectFallbackFolders= `
  /p:RestoreAdditionalProjectSources= /p:RestoreIgnoreFailedSources=false
```

**上述命令目前会遇到所述私有包阻塞，不是完整的可用构建教程。**
完整验收还必须恢复下面的运行时文件，开启分析器和 warnings-as-errors 完成 publish，
最后检查 MSIX 内容；restore 成功或 C# 编译成功均不足以证明包可用。

## Git 外运行时文件清单

路径均相对于 `src/Desktop/BiliCopilot.UI/Assets/`；以下 SHA256 来自参考成功构建的源码树。

| 文件 | 对编译/运行的影响 | 参考 SHA256 |
| --- | --- | --- |
| `libmpv/x64/libmpv-2.dll` | csproj 用通配符复制，缺失不一定使编译失败；视频、经典播放器、直播播放加载此 DLL | `d10d0994bd1398813da87fdb299961b378c71561bae1d135cb2b641b5864d7eb` |
| `ffmpeg/x64/ffmpeg.exe` | csproj 复制；`AppViewModel` 初始化时取得此文件用于 FFmpeg，不能只打包 libmpv | `97cba10ab56e0ed7b3fc09004f1caedd8468924fd5fb39db126383a053da9c5e` |
| `visor/x64/BiliCopilot.Visor.exe` | csproj 通配符复制；有滚动加速 IPC 客户端，但当前 UI C# 中未找到启动进程或调用客户端的代码，不列为已确认的核心运行依赖 | `cbf75e0cd2ff7f5ff0bdce0b89ad47cd4c4cc154b93d89f602463c1cb33dbedb` |
| `visor/arm64/BiliCopilot.Visor.exe` | ARM64 辅助进程；本次 x64 包不需要此架构 | `6a12b535defb12f35b6abaaba12fe0dcd21a2e40db14aeb4921f5ccdb46e9d14` |

参考 libmpv 文件版本为 `v0.41.0-1102-g6c092d978`；FFmpeg 为
`N-127218-g47313ad3f`（clang `22.1.8`）。

上游 `.github/workflows/build-release.yml` 调用
`.github/actions/setup-env/action.yml`：从 `shinchiro/mpv-winbuild-cmake`
的 **latest release** 下载 `mpv-dev-x86_64-*-git-*.7z`、
`ffmpeg-x86_64-git-*.7z`，用 7-Zip 分别提取 `libmpv-2.dll` 和 `ffmpeg.exe`。
这个浮动下载没有版本及哈希固定，不能保证得到表中相同字节。
当前证据尚未确定这两个文件对应的 release tag、压缩包文件名及压缩包 SHA256；
在这些信息查明前，不应编写一个用 latest 冒充固定版本的恢复脚本。
Visor 有源码项目 `src/Desktop/BiliCopilot.Visor/BiliCopilot.Visor.csproj`，
上述 workflow 没有生成或下载 Visor 的步骤，不能依赖 self-hosted runner 中的旧文件。

`Assets/bbdown` 在参考源码树不存在，当前 csproj 和 C# 中也没有 BBDown 引用。
`BiliCopilot.UI_TemporaryKey.pfx` 和 `Properties/launchSettings.json` 实际已经在 Git 中，
不是本次克隆缺失项。未签名打包不需要 PFX；launchSettings 用于调试，不是发布依赖。
本次未修改、导入或导出任何证书，也未安装包。

## 还需纳入仓库的参考构建设置

成功构建并非只运行上游 workflow：它另外传入了仓库外的 `release.props`、
`isolation.props` 和 `runtime-pin.targets`。
前两者隔离各项目的 `BaseIntermediateOutputPath`、`MSBuildProjectExtensionsPath`、
`BaseOutputPath`，并为 UI 开启 AOT/Trim/SingleFile 分析器；后者固定 .NET 9 运行时、
ILLink 和 Crossgen2 至 `9.0.20`。复现脚本应使用仓库相对路径移植这些设置。
参考发布参数为 Release/x64/win-x64、Optimize/SelfContained/PublishReadyToRun=true、
PublishAot/PublishTrimmed=false，RunAnalyzers、RunAnalyzersDuringBuild、EnableNETAnalyzers、
TreatWarningsAsErrors 全部为 true，AnalysisLevel=latest、AnalysisMode=AllEnabledByDefault。
沿用现有 NoWarn，不能通过关闭分析器或扩充抑制列表取得成功。

参考打包从 `BiliCopilot.UI.build.appxrecipe` 建立 layout，同名文件优先采用 publish
输出，然后用 MakeAppx 打包。移植时不要保留临时目录绝对路径或读取旧的身份 JSON；
应从仓库中的 manifest 检查身份和 COM executable，并检查 MSIX 中上述 x64 运行时文件的哈希。
本次 restore 失败，未进入 publish/打包，不能报告新的 MSIX 已通过。

## 安装脚本的字节哈希

原来 Windows `core.autocrlf=true` 克隆会把安装脚本转为 CRLF，与
`scripts/local-release/release-artifacts.json` 的 LF 字节哈希不符。
`.gitattributes` 已对两个受保护文件指定 `text eol=lf`，不改变已有制品记录。

```powershell
$manifest = Get-Content scripts/local-release/release-artifacts.json -Raw | ConvertFrom-Json
if ((Get-FileHash scripts/local-release/Install.ps1 -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.scripts.installSha256) { throw 'Install.ps1 hash mismatch' }
if ((Get-FileHash scripts/local-release/Trust-Certificate.ps1 -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.scripts.trustSha256) { throw 'Trust-Certificate.ps1 hash mismatch' }
```

这两个哈希只证明安装脚本字节匹配旧的制品清单；新构建的包若要交付安装，必须另行生成
与新 MSIX/签名证书匹配的制品清单，不能沿用旧包 SHA256。
