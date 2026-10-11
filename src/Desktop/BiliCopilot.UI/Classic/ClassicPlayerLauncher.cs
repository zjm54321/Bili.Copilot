using System.Runtime.InteropServices;
using BiliCopilot.UI.Classic.Backends.External;
using BiliCopilot.UI.Classic.Backends.Island;
using BiliCopilot.UI.Classic.Backends.Native;
using BiliCopilot.UI.Classic.Backends.Web;
using BiliCopilot.UI.Classic.Hosting;
using BiliCopilot.UI.Classic.Input;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.WinUIKernel.Share.Toolkits;
using WinUIEx;

namespace BiliCopilot.UI.Classic;

public sealed class ClassicPlayerLauncher(IPlayerService player, IBiliCookiesResolver cookies,
    IBiliTokenResolver tokens, ISettingsToolkit settings, AccountViewModel account,
    AppViewModel app, DispatcherQueue dispatcher)
{
    // Default adapter uses the final frontend's real session API and VideoHost namescope slot.
    public Func<IClassicPlaybackSession, UIElement, ClassicPlayerContent>? ContentFactory { get; set; }
        = ClassicFrontendAdapter.Create;

    public async Task<ClassicPlayerWindow> OpenAsync(ClassicPlaybackRequest request, ClassicSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Classic must launch on the UI dispatcher.");
        cancellationToken.ThrowIfCancellationRequested();
        if (ContentFactory is null) throw new NotSupportedException("Classic 前端尚未完成接线，未启动任何播放后端。");
        if (!Enum.IsDefined(options.Backend) || !Enum.IsDefined(options.DisplayMode) || !Enum.IsDefined(options.DecodePreference))
            throw new NotSupportedException("Classic 播放选项无效，未替换为其他后端。");
        if (options.ExternalProtocol is { } protocol && !Enum.IsDefined(protocol))
            throw new NotSupportedException("不支持所选外部播放器协议。");
        if (request.Item.IsPrivate && options.Backend is ClassicBackendKind.External or ClassicBackendKind.Web)
            throw new NotSupportedException("私密会话不向外部程序或持久网页配置交付媒体。");
        if (options.Backend == ClassicBackendKind.External && options.DecodePreference != ClassicDecodePreference.Auto)
            throw new NotSupportedException("外部播放器不能应用本应用的强制解码选项，请明确选择自动。");
        if (options.Backend == ClassicBackendKind.Web && options.DecodePreference != ClassicDecodePreference.Auto)
            throw new NotSupportedException("网页播放器不能应用强制解码选项，请明确选择自动。");
        if (options.Backend == ClassicBackendKind.Native && options.DecodePreference != ClassicDecodePreference.Auto)
            throw new NotSupportedException("Native 后端不支持强制软件/D3D11 解码，请明确选择自动。");
        if (options.Backend == ClassicBackendKind.Native && request.Item.Type == BiliMediaType.Live)
            throw new NotSupportedException("Native 后端尚不支持直播，未切换到其他引擎。");

        var volume = settings.ReadLocalSetting("PlayerVolume", 100d) / 100;
        var speed = settings.ReadLocalSetting("PlayerSpeed", 1d);
        var preferences = new ClassicSourcePreferences(
            settings.ReadLocalSetting("AutoLoadHistory", true),
            settings.ReadLocalSetting("AutoPlayNextRecommendVideo", false),
            settings.ReadLocalSetting("ShouldReportProgress", true),
            settings.ReadLocalSetting("PreferQuality", PreferQualityType.Auto),
            settings.ReadLocalSetting("LastSelectedVideoQuality", 0),
            settings.ReadLocalSetting("LastSelectedPgcQuality", 0), AppToolkit.GetPreferCodecId(),
            settings.ReadLocalSetting("PlayWithoutP2P", false));
        var inputPreferences = new ClassicInputPreferences(
            settings.ReadLocalSetting("StepBackwardSecond", 10d), settings.ReadLocalSetting("StepForwardSecond", 30d),
            0.05, settings.ReadLocalSetting("TempPlaybackRate", 3d));
        var theme = settings.ReadLocalSetting("AppTheme", ElementTheme.Default);
        var userId = account.MyProfile?.User?.Id;
        var vip = account.MyProfile?.IsVip == true;
        var loggedIn = tokens.GetToken() is not null;
        ExternalPlayerProtocol? selectedProtocol = options.ExternalProtocol;
        if (options.Backend == ClassicBackendKind.External && selectedProtocol is null)
        {
            var owner = app.ActivatedWindow?.Content?.XamlRoot
                ?? throw new InvalidOperationException("外部协议选择需要可见的所属窗口。");
            selectedProtocol = await ClassicExternalConsent.SelectProtocolAsync(owner, options.ExternalPlayerPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var window = new ClassicPlayerWindow(app.UnregisterClassicWindow, app.ReportClassicFailure);
        IClassicPlaybackBackend? backend = null;
        ClassicPlaybackSession? session = null;
        Action? release = null;
        try
        {
            UIElement surface;
            switch (options.Backend)
            {
                case ClassicBackendKind.Island:
                    var island = new ClassicIslandSurface(window.GetWindowHandle(), app.ReportClassicFailure);
                    release = island.Dispose;
                    surface = island;
                    var architecture = RuntimeInformation.ProcessArchitecture switch
                    {
                        Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
                        _ => throw new NotSupportedException("Classic Island 仅提供 x64/ARM64 的随包引擎。"),
                    };
                    var library = Path.Combine(Package.Current.InstalledPath, "Assets", "libmpv", architecture, "libmpv-2.dll");
                    backend = new IslandPlaybackBackend(island.RenderHwnd, library, options);
                    break;
                case ClassicBackendKind.Native:
                    var native = new MediaPlayerElement { AreTransportControlsEnabled = false };
                    surface = native;
                    backend = new NativePlaybackBackend(native, options);
                    break;
                case ClassicBackendKind.Web:
                    var web = new WebView2(); // Never set Source or call Ensure outside the backend.
                    release = web.Close;
                    surface = web;
                    backend = new WebPlaybackBackend(web);
                    ToolTipService.SetToolTip(web, ((WebPlaybackBackend)backend).Disclosure);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(web, ((WebPlaybackBackend)backend).Disclosure);
                    break;
                case ClassicBackendKind.External:
                    surface = new ContentControl(); // No fake render/transport surface for an external process.
                    backend = new ExternalPlaybackBackend(dispatcher, options.ExternalPlayerPath,
                        selectedProtocol ?? throw new InvalidOperationException("尚未选择外部协议。"),
                        (consent, token) => window.ConfirmAsync("授权本次外部播放", ClassicExternalConsent.Describe(consent)
                            + "\n本次解码偏好为 Auto，不向外部程序强制软件或 D3D11 解码。", token));
                    break;
                default: throw new NotSupportedException("所选 Classic 后端不可用。");
            }
            window.AdoptBackend(backend, release);
            var sources = new ClassicMediaSourceService(player, cookies, preferences, format =>
            {
                var ownVideo = !string.IsNullOrEmpty(userId)
                    && (session?.ResolvingRequest ?? session?.Request)?.Item.Video?.Publisher?.User?.Id == userId;
                return ownVideo || ((!format.NeedLogin || loggedIn) && (!format.NeedVip || vip));
            });
            session = new ClassicPlaybackSession(options, backend, sources, new ClassicProgressReporter(player), dispatcher);
            window.AdoptSession(session, release); // Ownership precedes fallible frontend construction.
            session.Input.Preferences = inputPreferences;
            var content = ContentFactory(session, surface);
            content.Root.RequestedTheme = theme;
            window.Attach(session, content, release);
            app.RegisterClassicWindow(window);
            await window.ShowAsync(options.DisplayMode);
            if (session.IsClosing) throw new OperationCanceledException();
            cancellationToken.ThrowIfCancellationRequested();
            if (backend.Capabilities.HasFlag(ClassicCapabilities.Volume))
                await backend.SetVolumeAsync(double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 1, cancellationToken);
            if (session.IsClosing) throw new OperationCanceledException();
            if (backend.Capabilities.HasFlag(ClassicCapabilities.Speed))
                await backend.SetSpeedAsync(double.IsFinite(speed) && speed > 0 ? speed : 1, cancellationToken);
            if (session.IsClosing) throw new OperationCanceledException();
            await session.OpenAsync(request, cancellationToken);
            return window;
        }
        catch
        {
            if (window.HasCleanupOwner)
            {
                try { await window.CloseAsync(); }
                catch (Exception ex)
                {
                    if (!window.ReleaseConfirmed && !app.ClassicPlayers.Contains(window))
                    {
                        app.RegisterClassicWindow(window);
                        window.Activate();
                    }
                    app.ReportClassicFailure(ex);
                }
            }
            else
            {
                try
                {
                    if (session is not null) await session.CloseAsync();
                    else if (backend is not null) await backend.DisposeAsync();
                }
                catch (Exception ex) { app.ReportClassicFailure(ex); }
                finally
                {
                    try { release?.Invoke(); } catch (Exception ex) { app.ReportClassicFailure(ex); }
                    // No attached session; still use the host's Closing-aware cleanup path.
                    try { await window.CloseAsync(); } catch (Exception ex) { app.ReportClassicFailure(ex); }
                }
            }
            throw;
        }
    }
}
