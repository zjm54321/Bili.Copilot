# Release x64 MSIX：从干净克隆构建

## Windows 前置条件与克隆

需要 Windows、Git、PowerShell 5.1 或更新版本、Visual Studio 的 Windows 桌面/WinUI
构建工具及 Windows SDK `10.0.26100.0`（x64 MakeAppx/SignTool）。验证环境为
Visual Studio 18 Community、.NET SDK `10.0.402`；`global.json` 固定该 SDK。
目标框架仍为 .NET 9，运行时、ILLink、Crossgen2 固定 `9.0.20`。
NuGet 网络访问必需；不要求预先存在的本地包缓存。WSL 下请用 Windows PowerShell
执行构建，不要使用 Linux dotnet。手动提供预编译 Visor 时不需要 NativeAOT C++ 工具链。

```powershell
git clone --recurse-submodules <repository-url> Bili.Copilot
cd Bili.Copilot
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget-clean'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PWD '.nuget-http-clean'
# 若之前 clone 时未递归：git submodule update --init --recursive
./scripts/Build-BiliKernel.ps1
# 按下一节手动放入四个原生文件，然后：
./scripts/Build-Release.ps1
# 产物：artifacts/release/BiliCopilot_2.2611.3.0_x64.msix
```

SDK 是固定提交的 `src/Libs/BiliKernel` submodule，源为
<https://github.com/zjm54321/BiliKernel>，基于上游 `0.0.2-preview2`，保留原始 LICENSE。
`Build-BiliKernel.ps1` 只构建 Abstractions 和 Services.Comment/Media/Moment，
版本 `0.0.3-bilicopilot.1`，写入 `artifacts/bilikernel-feed`。
`nuget.config` 用精确 packageSourceMapping 将这四个包限制到本地 feed；其他源保留，
Core/Grpc 等未修改模块仍来自公开包。构建显式使用仓库 NuGet 配置。
`artifacts/`、`.nuget-clean/`、`.nuget-http-clean/`、原生资产目录均被忽略。
重新构建 MSIX 前删除生成的 `artifacts/release/`；不要修改 submodule 的固定提交。

## 手动补齐原生运行时文件

这些大文件不提交 Git。克隆后、构建前手动取得下列固定版本，放入
`src/Desktop/BiliCopilot.UI/Assets/` 下表所示的相对路径。
**文件名相同或版本相近不够，必须匹配 SHA256。** 本流程不要求下载 ZIP，
也不依赖尚未发布的 GitHub Release 附件。

| 文件 | 对编译/运行的影响 | 参考 SHA256 |
| --- | --- | --- |
| `libmpv/x64/libmpv-2.dll` | csproj 用通配符复制，缺失不一定使编译失败；视频、经典播放器、直播播放加载此 DLL | `d10d0994bd1398813da87fdb299961b378c71561bae1d135cb2b641b5864d7eb` |
| `ffmpeg/x64/ffmpeg.exe` | csproj 复制；`AppViewModel` 初始化时取得此文件用于 FFmpeg，不能只打包 libmpv | `97cba10ab56e0ed7b3fc09004f1caedd8468924fd5fb39db126383a053da9c5e` |
| `visor/x64/BiliCopilot.Visor.exe` | csproj 通配符复制；有滚动加速 IPC 客户端，但当前 UI C# 中未找到启动进程或调用客户端的代码，不列为已确认的核心运行依赖 | `cbf75e0cd2ff7f5ff0bdce0b89ad47cd4c4cc154b93d89f602463c1cb33dbedb` |
| `visor/arm64/BiliCopilot.Visor.exe` | ARM64 辅助进程；本次 x64 包不需要此架构 | `6a12b535defb12f35b6abaaba12fe0dcd21a2e40db14aeb4921f5ccdb46e9d14` |

参考 libmpv 文件版本为 `v0.41.0-1102-g6c092d978`；FFmpeg 为
`N-127218-g47313ad3f`（clang `22.1.8`）。

| 文件 | 确切版本/构建标识 | 字节数 |
| --- | --- | ---: |
| `libmpv/x64/libmpv-2.dll` | 文件/产品版本 `v0.41.0-1102-g6c092d978` | 120868352 |
| `ffmpeg/x64/ffmpeg.exe` | `ffmpeg -version`：`N-127218-g47313ad3f`，clang `22.1.8`；PE 不含文件版本资源 | 107600896 |
| `visor/x64/BiliCopilot.Visor.exe` | 文件版本 `1.0.0.0`；产品版本 `1.0.0+6f8ae4dd80a8d56cbda241c50bd5b37fcb95312e`，NativeAOT x64 | 3308032 |
| `visor/arm64/BiliCopilot.Visor.exe` | 文件版本 `1.0.0.0`；产品版本 `1.0.0+6f8ae4dd80a8d56cbda241c50bd5b37fcb95312e`，NativeAOT ARM64 | 3396096 |

上游 `.github/workflows/build-release.yml` 调用
`.github/actions/setup-env/action.yml`：从 `shinchiro/mpv-winbuild-cmake`
的 **latest release** 下载 `mpv-dev-x86_64-*-git-*.7z`、
`ffmpeg-x86_64-git-*.7z`，用 7-Zip 分别提取 `libmpv-2.dll` 和 `ffmpeg.exe`。
这个浮动下载没有版本及哈希固定，不能保证得到表中相同字节。
当前证据尚未确定这两个文件对应的 release tag、压缩包文件名及压缩包 SHA256；
可在 <https://github.com/shinchiro/mpv-winbuild-cmake/releases> 查找历史构建，
但 latest 不能保证等价；不要把浮动下载当成上述固定文件的来源证明。
Visor 有源码项目 `src/Desktop/BiliCopilot.Visor/BiliCopilot.Visor.csproj`，
对应上游 <https://github.com/Richasy/Bili.Copilot>；上述 workflow 没有生成或下载
Visor 的步骤。自行 NativeAOT 发布得到的文件也必须核对哈希，不能只比较版本号。

本次复现明确允许从已知成功的参考源码树手动复制这四个文件；这不是 NuGet 缓存复用。
在本机 Windows PowerShell 中，从克隆根目录运行：

```powershell
$referenceAssets = 'C:\Users\Zhang\AppData\Local\Temp\BiliCopilot-setup\p7-final-consumer-20261012-578008fae33f\source\src\Desktop\BiliCopilot.UI\Assets'
$assets = Join-Path $PWD 'src/Desktop/BiliCopilot.UI/Assets'
$files = @{
  'libmpv/x64/libmpv-2.dll' = 'd10d0994bd1398813da87fdb299961b378c71561bae1d135cb2b641b5864d7eb'
  'ffmpeg/x64/ffmpeg.exe' = '97cba10ab56e0ed7b3fc09004f1caedd8468924fd5fb39db126383a053da9c5e'
  'visor/x64/BiliCopilot.Visor.exe' = 'cbf75e0cd2ff7f5ff0bdce0b89ad47cd4c4cc154b93d89f602463c1cb33dbedb'
  'visor/arm64/BiliCopilot.Visor.exe' = '6a12b535defb12f35b6abaaba12fe0dcd21a2e40db14aeb4921f5ccdb46e9d14'
}
foreach ($relative in $files.Keys) {
  $from = Join-Path $referenceAssets $relative
  $to = Join-Path $assets $relative
  if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ine $files[$relative]) { throw "Source hash mismatch: $relative" }
  New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
  Copy-Item -LiteralPath $from -Destination $to -Force
  if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ine $files[$relative]) { throw "Destination hash mismatch: $relative" }
}
```

其他机器可把 `$referenceAssets` 改为保存了这些经核验文件的目录；没有这些文件时，
需先找到上述相同构建，不能宣称只下载源码就已具备运行时依赖。
参考树没有 ARM64 版 libmpv/FFmpeg，所以上述清单只支持本次 **x64 MSIX** 验证；
ARM64 Visor 虽保留以完整记录参考资产，但不随 x64 包一起使用。

`Assets/bbdown` 在参考源码树不存在，当前 csproj 和 C# 中也没有 BBDown 引用。
`BiliCopilot.UI_TemporaryKey.pfx` 和 `Properties/launchSettings.json` 实际已经在 Git 中，
不是本次克隆缺失项。未签名打包不需要 PFX；launchSettings 用于调试，不是发布依赖。
本次未修改、导入或导出任何证书，也未安装包。

## Release 设置与签名

`Build-Release.ps1` 使用已纳入仓库的 `scripts/build-config/release.props` 和
`runtime-pin.targets`，隔离各项目的 obj/bin，开启 UI 的 AOT/Trim/SingleFile 分析器，
固定 .NET 9 运行时、ILLink、Crossgen2 至 `9.0.20`，不再读取参考构建目录的配置。
发布参数为 Release/x64/win-x64、Optimize/SelfContained/PublishReadyToRun=true、
PublishAot/PublishTrimmed=false，RunAnalyzers、RunAnalyzersDuringBuild、EnableNETAnalyzers、
TreatWarningsAsErrors 全部为 true，AnalysisLevel=latest、AnalysisMode=AllEnabledByDefault。
沿用现有 NoWarn，不能通过关闭分析器或扩充抑制列表取得成功。

脚本从 `BiliCopilot.UI.build.appxrecipe` 建立 layout，同名文件优先采用 publish 输出，
核对 manifest 身份、COM executable 和三个 x64 原生文件的哈希，然后用 MakeAppx 打包。
日志、binlog、有效编译参数和结果保存在 `artifacts/release/`。默认输出未签名包。
自动发现 Visual Studio MSBuild；其他工具安装位置可用 `-MSBuildPath`、`-WindowsSdkBin` 指定。

签名使用 Windows 当前用户证书库中的本地私钥，不从 Git 中的旧 TemporaryKey PFX
导入私钥。已有匹配 Publisher 的证书时：

```powershell
./scripts/Build-Release.ps1 -CertificateThumbprint '<证书指纹>'
```

没有私钥时，可创建仅用于本机测试的证书，再把指纹传给脚本：

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=BiliCopilot Local Release' `
  -KeyUsage DigitalSignature -FriendlyName 'BiliCopilot local build' `
  -CertStoreLocation 'Cert:\CurrentUser\My' -HashAlgorithm SHA256 `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
./scripts/Build-Release.ps1 -CertificateThumbprint $cert.Thumbprint
```

证书 Subject 必须和 manifest 的 Publisher 完全一致。安装签名包前需由用户主动信任
导出的公钥证书；私钥不要提交 Git。未签名 MSIX 可以用于内容和构建验证，不能承诺
开发者模式直接安装任意未签名 MSIX；安装测试请先自行签名并信任证书。
本任务不安装或修改已安装应用。

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
