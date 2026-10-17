<p align="center">
<img src="assets/StoreLogo.png" width="48px"/>
</p>

<div align="center">

# 哔哩助理

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/Richasy/Bili.Copilot)](https://github.com/Richasy/Bili.Copilot/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Richasy/Bili.Copilot) ![GitHub All Releases](https://img.shields.io/github/downloads/Richasy/Bili.Copilot/total) ![GitHub stars](https://img.shields.io/github/stars/Richasy/Bili.Copilot?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Richasy/Bili.Copilot)

`哔哩助理` 是 [哔哩哔哩](https://www.bilibili.com) 的第三方桌面应用，适用于 Windows 11.

</div>
<p align="center">
<a href="#概述">概述</a> &nbsp;&bull;&nbsp;
<a href="#开发">开发</a> &nbsp;&bull;&nbsp;
<a href="#安装">安装</a> &nbsp;&bull;&nbsp;
<a href="#使用">使用</a> &nbsp;&bull;&nbsp;
<a href="#模块">模块</a> &nbsp;&bull;&nbsp;
<a href="#交流讨论">交流</a> &nbsp;&bull;&nbsp;
<a href="#鸣谢">鸣谢</a>
</p>

## 概述

哔哩助理在 [哔哩](https://github.com/Richasy/Bili.Uwp) 的基础上通过 Windows App SDK 进行了重构.

哔哩助理将以更开放的态度进行开发，借助社区力量，共同构建一个有意思的 UGC 客户端。

文档地址：[哔哩助理](https://bili.richasy.net)

## 安装

### 商店下载（推荐）

[从 Microsoft Store 中获取](https://apps.microsoft.com/detail/9MVFJLPH517M?launch=true&mode=full)

### 侧加载

1. 打开系统设置，依次选择 `系统` -> `开发者选项`，打开 `开发人员模式`。滚动到页面底部，展开 `PowerShell` 区块，开启 `更改执行策略...` 选项
2. 打开 [Release](https://github.com/Richasy/Bili.Copilot/releases) 页面
3. 在最新版本的 **Assets** 中找到应用包下载。命名格式为：`BiliCopilot_{version}_{platform}.zip`
4. 下载应用包后解压，右键单击文件夹中的 `install.ps1` 脚本，选择 `使用 PowerShell 运行`

## 开发

在开发哔哩助理的过程中，抽离出了多个单独组件，可以被其它开发者集成到自己的项目中：

- [bili-kernel](https://github.com/Richasy/bili-kernel)  
  哔哩助理与 BiliBili API 交互的核心代码，是一层 .NET 包装器，基于 .NET Standard 2.0，完全的 AOT 支持，便于移植和二次开发。
- [mpv-kernel](https://github.com/Richasy/mpv-kernel)  
  哔哩助理的核心播放器之一，将 MPV 集成进 WinUI3 以实现良好的播放体验。
- [winui-kernel](https://github.com/Richasy/winui-kernel)  
  我在多个 WinAppSDK 项目之间共用的一些基础样式及实现。

它们都以 nuget 形式集成在项目内

第一次克隆时可以运行下面的命令：

```shell
git clone https://github.com/Richasy/Bili.Copilot.git
```

切换分支完成后，还需要下载 mpv / ffmpeg 到对应的目录：

| 文件名       | 目录                                                     | 说明                                                                                                                                                                         |
| ------------ | -------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| libmpv-2.dll | src\Desktop\BiliCopilot.UI\Assets\libmpv\x64(或者 arm64) | 可以在 [mpv-winbuild](https://github.com/zhongfly/mpv-winbuild) 下载最新的 dev 构建（x64 对应 x86_x64，arm64 对应 aarch64），把 libmpv-2.dll 放入对应文件夹中，用以 mpv 播放 |
| ffmpeg.exe   | src\Desktop\BiliCopilot.UI\Assets\ffmpeg                 | 可以在 [mpv-winbuild](https://github.com/zhongfly/mpv-winbuild) 下载最新 ffmpeg x64 构建，将 ffmpeg.exe 放入对应文件夹中                               |

## 使用

### 登录

哔哩助理优先使用扫码登录，如果你偏好其它的登录方式（比如手机/用户名密码），你可以选择网页登录。

### 视频播放

新版本的哔哩助理（V2）支持两种种播放方案：

1. MPV
2. 外部播放器（仅支持 MPV）

下面是几种播放方案的具体比较，请根据自己的情况选择合适的播放方案：

| 方案       | 优点                               | 缺点                       |
| ---------- | ---------------------------------- | -------------------------- |
| MPV        | 解码速度快，播放稳定               | 内存占用相对较高           |
| 外部播放器 | 自定义程度高，可以获得最佳播放体验 | 需要额外安装配置，门槛较高 |

## 交流讨论

有兴趣一起交流的话，可以加 QQ 群，进群请注明正在使用哪款软件。

<img src="./assets/qq_group.jpg" width="240px" />

## 应用截图

![截图](assets/screenshot.png)

![截图](assets/player.png)

## 鸣谢

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [BiliBili](https://www.bilibili.com/)
- [哔哩哔哩-API 收集整理](https://github.com/SocialSisterYi/bilibili-API-collect)
- [BewlyBewly](https://github.com/BewlyBewly/BewlyBewly)
- [寒霜弹幕使](https://github.com/cotaku/DanmakuFrostMaster)
- [cnbluefire/WinUI3.Win2D](https://github.com/cnbluefire/WinUI3.Win2D)
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
- [FluentIcons](https://github.com/davidxuang/FluentIcons)
- [ComputeSharp](https://github.com/Sergio0694/ComputeSharp)
- 以及其他在开发过程中提供过助力的小伙伴
