# LocalTest 本地测试通道说明

LocalTest 是为日常开发与调试提供的独立测试通道。它拥有与正式发布版（LocalRelease）完全隔离的应用身份，允许开发者在同一台机器上并存安装测试版与正式版，互不干扰配置与数据。

## 通道标识信息

以下身份信息固定在 `channel.json` 中，不可随意更改：

| 属性 | 配置值 | 说明 |
| --- | --- | --- |
| 应用包名（Package Name） | `BiliCopilot.LocalTest` | 独立包名称，与正式版隔离 |
| 发布者（Publisher） | `CN=BiliCopilot Local Release` | 统一发布者标识 |
| 系列名称（PFN） | `BiliCopilot.LocalTest_p8tmrechhd89c` | 包系列名称 |
| 显示名称（Display Label） | `哔哩助理（测试）` | 开始菜单与系统设置中的展示名称 |
| 应用 ID（Application ID） | `App` | 主应用程序标识 |
| 默认起始版本 | `2.2611.2.0` | 后续测试版本需严格递增 |
| 运行架构 | `x64` | 目标系统架构 |

## 脚本与文件组件

- `channel.json`：通道元数据配置文件，定义测试包身份、依赖版本及激活参数。
- `LocalTest.psm1`：通道共享函数库，包含身份转换、安全路径校验与依赖核验逻辑。
- `Prepare-Package.ps1`：将编译生成的原生输出布局（Layout）转换为 LocalTest 格式，并在指定 `-Pack` 时调用 MakeAppx 打包成未签名的测试 MSIX。
- `Seal-Package.ps1`：封包脚本。校验已签名的测试 MSIX、外部二进制文件和证书的一致性，生成固化的制品清单 `test-artifacts.json`。
- `Install.ps1`：测试包客户端安装脚本。提供只读预检与安全部署功能，支持同版本检测与防降级。

## 打包与测试流程

测试包打包流程包含准备布局、打包、签名、封包及部署五个步骤：

### 1. 准备测试布局与打包

首先通过外部编译生成就绪的原生 Layout 目录及对应的文件清单（JSON 格式）。

```powershell
# 1.1 只读预检模式：验证文件清单与源布局一致性
.\scripts\local-test\Prepare-Package.ps1 `
  -SourceLayout <原生Layout目录> `
  -OutputRoot <新的测试输出目录> `
  -WorkspaceRoot <工作区目录> `
  -ApprovedInventory <文件清单JSON路径> `
  -ExpectedInventorySha256 <文件清单哈希> `
  -Version '2.2611.2.0'

# 1.2 执行转换并打包生成未签名的测试 MSIX
.\scripts\local-test\Prepare-Package.ps1 `
  -SourceLayout <原生Layout目录> `
  -OutputRoot <新的测试输出目录> `
  -WorkspaceRoot <工作区目录> `
  -ApprovedInventory <文件清单JSON路径> `
  -ExpectedInventorySha256 <文件清单哈希> `
  -Version '2.2611.2.0' `
  -Prepare -Pack `
  -MakeAppxPath <makeappx.exe路径> `
  -ExpectedMakeAppxSha256 <makeappx.exe哈希>
```

> **注意**：
> - `-SourceLayout`、`-OutputRoot` 与 `-WorkspaceRoot` 必须为相互独立的本地绝对路径。
> - `-OutputRoot` 必须是一个全新的目录，不可覆盖已有输出。

### 2. 签名测试包

打包成功后，测试 MSIX 会生成在 `<OutputRoot>\delivery\` 目录下。使用 SignTool 对其进行代码签名（证书 Subject 须为 `CN=BiliCopilot Local Release`）：

```powershell
$msix = "<OutputRoot>\delivery\BiliCopilot.LocalTest_2.2611.2.0_x64.msix"
& signtool.exe sign /fd SHA256 /sha1 <证书指纹> /s My $msix
```

### 3. 封包与固化清单

签名完成后，运行 `Seal-Package.ps1` 校验交付目录文件及签名，并生成固化的 `test-artifacts.json` 清单：

```powershell
.\scripts\local-test\Seal-Package.ps1 `
  -DeliveryRoot "<OutputRoot>\delivery" `
  -LooseExePath "<OutputRoot>\layout\BiliCopilot.UI.exe" `
  -ExpectedLooseExeSha256 <EXE哈希> `
  -ExpectedSignerCertificateSha256 <证书DER哈希> `
  -Version '2.2611.2.0'
```

### 4. 安装与验证

交付目录 `<OutputRoot>\delivery` 包含了安装所需的文件。在交付目录下运行 `Install.ps1`：

```powershell
# 4.1 只读预检：检查依赖包与证书状态
.\Install.ps1 -ExpectedManifestSha256 <test-artifacts.json哈希>

# 4.2 执行部署安装
.\Install.ps1 -ExpectedManifestSha256 <test-artifacts.json哈希> -Install
```

> **说明**：
> - 安装脚本会检查已声明的依赖框架是否已安装，不会自动下载缺失依赖。
> - 系统中若已安装相同或更高版本的测试包，脚本会拒绝重复写入或降级。
