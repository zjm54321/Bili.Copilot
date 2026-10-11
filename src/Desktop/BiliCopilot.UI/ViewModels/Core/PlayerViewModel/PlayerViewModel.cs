// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Richasy.WinUIKernel.Share.ViewModels;
using WinUIEx;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel(DispatcherQueue queue, ILogger<PlayerViewModel> logger) : ViewModelBase, IAsyncDisposable
{
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
        => await CloseAsync();

    private async Task DisposeCoreAsync()
    {
        await DrainPlayerWorkAsync();
        // Decide cancellation failure before any native/host teardown, registry release or CTS disposal.
        if (_closeCancellationErrors.Count > 0)
            throw new AggregateException("Player cancellation failed; ownership retained.", _closeCancellationErrors);

        if (_playerOverlay is not null)
        {
            _playerOverlay.KeyDown -= OnPlayerKeyDown;
            _playerOverlay.RemoveHandler(UIElement.KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPlayerKeyUp));
            _playerOverlay.LostFocus -= OnPlayerInputLostFocus;
            _playerOverlay = null;
        }

        Window?.SetActiveAction(null);
        RequestHideNextTip?.Invoke(this, EventArgs.Empty);
        Danmaku.ClearAll();
        _nativeMp?.Dispose();
        _nativeMp = null;

        if (_tripleTimer != null)
        {
            _tripleTimer.Tick -= OnTripleTimerTick;
            _tripleTimer.Stop();
            _tripleTimer = null;
        }

        if (_smtc != null)
        {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc = null;
        }

        if (Connector != null)
        {
            Connector.PlaylistInitialized -= OnConnectorPlaylistInitialized;
            Connector.NewMediaRequest -= OnConnectorNewMediaRequest;
            Connector.RequestOpenExtraPanel -= OnRequestOpenExtraPanel;
            Connector.PropertiesUpdated -= OnConnectorPropertiesUpdated;
        }

        if (Client?.IsDisposed == false)
        {
            Client.ErrorOccurred -= OnClientErrorOccurred;
            Client.CacheStateChanged -= OnClientCacheStateChanged;
        }

        if (Player is not null)
        {
            Player.PropertyChanged -= OnPlayerPropertyChanged;
            await Player.DisposeAsync();
        }
        else if (Client?.IsDisposed == false)
        {
            // MpvPlayer owns Client whenever it exists. Only partial initialization owns it here.
            await Client.DisposeAsync();
        }

        if (!IsEmbedded && Window is not null)
        {
            var app = this.Get<AppViewModel>();
            var hideMain = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true);
            if ((hideMain && !UseIntegrationOperation) ||
                (app.Players.Count <= 1 && !app.IsMainWindowVisible() && !app.IsClosed && !(hideMain && UseIntegrationOperation)))
                ShowMainWindow();
        }

        if (Window?.IsDisposed == false)
        {
            Window.SizeChanged -= OnWindowSizeChanged;
            Window.CloseRequested -= OnHostCloseRequested;
            Window.PresentationChanged -= OnHostPresentationChanged;
            if (_pointerSource is not null) _pointerSource.PointerReleased -= OnWindowPointerReleased;
            await Window.DisposeAsync();
        }

        this.Get<AppViewModel>().ReleasePlayer(this);
        _mediaCancellation?.Dispose();
        foreach (var retired in _retiredMediaCancellations) retired.Dispose();
        _retiredMediaCancellations.Clear();
        _lifetime.Dispose();
        if (!IsEmbedded && UseIntegrationOperation && _independentInitializationCompleted)
            this.Get<AppViewModel>().Windows.OfType<MainWindow>().FirstOrDefault()?.Close();
    }

    private async Task DrainPlayerWorkAsync()
    {
        Exception? inputError = null;
        try { await CancelPlayerInputAsync(); }
        catch (Exception ex) { inputError = ex; }
        // A failed input restoration or cancellation callback cannot make live work disappear.
        while (_ownedWork.Count > 0)
        {
            try { await Task.WhenAll(_ownedWork.ToArray()); }
            catch (Exception ex) { logger.LogWarning(ex, "Owned player operation ended while closing."); }
            _ownedWork.RemoveWhere(task => task.IsCompleted);
        }

        await DrainTransportCommandsAsync();
        if (inputError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inputError).Throw();
    }

    private async Task InitializeMediaAsync(MediaSnapshot snapshot)
    {
        CheckMediaAdmission();
        _snapshot = snapshot;
        ErrorMessage = default;
        CacheSpeedText = default;
        ShowPlayerWindow();
        IsSourceSelectable = false;
        IsDanmakuEnabled = false;
        IsDanmakuLoading = false;
        IsStatsOverlayShown = false;
        IsEnd = false;
        _isTlsFailed = false;
        _prevPosition = 0;
        IsSubtitleEmpty = true;
        _isDanmakuInitialized = false;
        IsNextTipShownInThisMedia = false;
        Danmaku.ClearAll();
        Chapters.Clear();
        IsChapterVisible = SettingsToolkit.ReadLocalSetting(SettingNames.IsChapterVisible, true);
        UseIntegrationOperation = !IsEmbedded && SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true)
            && SettingsToolkit.ReadLocalSetting(SettingNames.UseIntegrationWhenSinglePlayWindow, true);
        BackwardTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.BackwardTemplate), SettingsToolkit.ReadLocalSetting(SettingNames.StepBackwardSecond, 10d));
        ForwardTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.ForwardTemplate), SettingsToolkit.ReadLocalSetting(SettingNames.StepForwardSecond, 30d));
        RequestHideNextTip?.Invoke(this, EventArgs.Empty);
        if (Client != null)
        {
            await Client.PauseAsync();
            CheckMediaAdmission();

            await Client.StopAsync();
        }

        CheckMediaAdmission();

        await InitializeInternalAsync();
        CheckMediaAdmission();

        if (Connector != null)
        {
            System.Diagnostics.Debug.WriteLine($"Player initialized with connector: {Connector.GetType().Name}");
            await Connector.InitializeAsync(_snapshot, default, _activeMediaToken);
            CheckMediaAdmission();
        }

        Title = Connector switch
        {
            VideoConnectorViewModel videoVM => videoVM.Title,
            PgcConnectorViewModel pgcVM => pgcVM.Title,
            _ => string.Empty
        };

        // TODO: 创建源处理器
        switch (_snapshot.Type)
        {
            case BiliMediaType.Video:
                {
                    var resolver = this.Get<VideoMediaSourceResolver>();
                    var connector = Connector as VideoConnectorViewModel;
                    resolver.Initialize(_snapshot, connector._part);
                    _sourceResolver = resolver;
                }
                break;
            case BiliMediaType.Pgc:
                {
                    var resolver = this.Get<PgcMediaSourceResolver>();
                    var connector = Connector as PgcConnectorViewModel;
                    resolver.Initialize(_snapshot, connector._episode);
                    _sourceResolver = resolver;
                }
                break;
            case BiliMediaType.Live:
                break;
            default:
                break;
        }

        _sourceResolver.SetInitializationCancellation(_activeMediaToken);
        _sourceResolver.WindowHandle = Window!.RenderHwnd;

        if (_historyResolver != null && Player != null)
        {
            await _historyResolver.SaveHistoryAsync(Player.Position, Player.Duration, Richasy.MpvKernel.Core.Enums.MpvPlayerState.End, true);
            CheckMediaAdmission();
        }

        // TODO: 创建历史记录处理器
        switch (_snapshot.Type)
        {
            case BiliMediaType.Video:
                {
                    var resolver = this.Get<VideoMediaHistoryResolver>();
                    var connector = Connector as VideoConnectorViewModel;
                    resolver.Initialize(connector._view, connector._part, _snapshot);
                    _historyResolver = resolver;
                }
                break;
            case BiliMediaType.Pgc:
                {
                    var resolver = this.Get<PgcMediaHistoryResolver>();
                    var connector = Connector as PgcConnectorViewModel;
                    resolver.Initialize(connector._view, connector._episode, _snapshot);
                    _historyResolver = resolver;
                }
                break;
            case BiliMediaType.Live:
                break;
            default:
                break;
        }

        CheckMediaAdmission();

        if (Player is null)
        {
            Player = new Richasy.MpvKernel.Player.MpvPlayer(Client!, _sourceResolver, _historyResolver, logger: logger);
            Player.PropertyChanged += OnPlayerPropertyChanged;
        }
        else
        {
            Player.UpdateResolvers(_sourceResolver, _historyResolver);
        }

        await Player.InitializeAsync();

        CheckMediaAdmission();

        await ResetPlayerPresenterModeAsync();
        CheckMediaAdmission();

        CheckFullScreen();
        CheckCompactOverlay();
        ProgressChanged?.Invoke(this, 0);
        await LoadSourcesAsync();
        CheckMediaAdmission();

        await LoadSubtitlesAsync();
    }

    public void UpdateTheme(ElementTheme theme)
        => Window?.SetTheme(theme);

    private void ShowPlayerWindow()
    {
        if (!_hostReady || Window is null || Window.RenderHwnd == IntPtr.Zero)
            throw new InvalidOperationException("The owned player host must be ready before media initialization.");
        if (IsEmbedded)
        {
            BindHost();
            return;
        }

        var hideMainWindowOnPlay = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true);

        if (!Window.GetWindow().IsVisible)
        {
            Window!.Show(Player == null && !hideMainWindowOnPlay);
        }

        var isFirstLoad = !_independentWindowShown;
        BindHost();

        if (hideMainWindowOnPlay && isFirstLoad)
        {
            var mainWnd = this.Get<Core.AppViewModel>().Windows.First(p => p is MainWindow);
            if (mainWnd.AppWindow.IsVisible)
            {
                var scaleFactor = HwndExtensions.GetDpiForWindow(mainWnd.GetWindowHandle()) / 96d;
                var windowLeft = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionLeft, 0);
                var windowTop = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionTop, 0);
                var windowWidth = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowWidth, 1120d);
                var windowHeight = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowHeight, 740d);
                Window.GetWindow().MoveAndResize(new Windows.Graphics.RectInt32(windowLeft, windowTop, Convert.ToInt32(windowWidth * scaleFactor), Convert.ToInt32(windowHeight * scaleFactor)));
                if (SettingsToolkit.ReadLocalSetting(SettingNames.IsMainWindowMaximized, false))
                {
                    (Window.GetWindow().Presenter as OverlappedPresenter)?.Maximize();
                }

                mainWnd.Hide();
            }
        }

        if (isFirstLoad)
        {
            var defaultPresenter = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerPresenterMode, PlayerPresenterMode.Default);
            if (defaultPresenter == PlayerPresenterMode.FullScreen)
            {
                Window.EnterFullScreen();
            }
            else if (defaultPresenter == PlayerPresenterMode.CompactOverlay)
            {
                Window.EnterCompactOverlay();
            }
        }

        IsVerticalScreen = Window!.GetWindow().Presenter.Kind is Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen or Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay
            ? Window.GetWindow().Size.Height > Window.GetWindow().Size.Width
            : Window.GetWindow().Size.Height >= Window.GetWindow().Size.Width;
        CheckBlackBackgroundVisible();
        _independentWindowShown = true;
    }

    private bool _independentWindowShown;
    private InputPointerSource? _pointerSource;

    private void BindHost()
    {
        if (_uiElementSetted) return;
        Window!.SetActiveAction(OnPlayerWindowActiveChanged);
        Window.SizeChanged += OnWindowSizeChanged;
        Window.CloseRequested += OnHostCloseRequested;
        Window.PresentationChanged += OnHostPresentationChanged;
        var overlay = new PlayerOverlay { ViewModel = this, IsTabStop = true };
        _playerOverlay = overlay;
        overlay.KeyDown += OnPlayerKeyDown;
        overlay.AddHandler(UIElement.KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPlayerKeyUp), true);
        overlay.LostFocus += OnPlayerInputLostFocus;
        Window.SetUIElement(overlay);
        if (Window.XamlRoot?.ContentIsland is { } island)
        {
            _pointerSource = InputPointerSource.GetForIsland(island);
            _pointerSource.PointerReleased += OnWindowPointerReleased;
        }
        Window.SetTheme(SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default));
        Window.ResetFocus();
        _uiElementSetted = true;
        OnHostPresentationChanged(Window, EventArgs.Empty);
    }

    private void CheckBottomProgressBarVisible()
        => IsBottomProgressBarVisible = !IsControlsVisible && SettingsToolkit.ReadLocalSetting(SettingNames.IsBottomProgressBarVisible, true);

    partial void OnIsControlsVisibleChanged(bool value)
        => Window?.SetCaptionButtonVisibility(value);

    partial void OnIsControlsVisibleChanged(bool oldValue, bool newValue)
    {
        IsTopMost = Window?.IsTopMost() ?? false;
        CheckBottomProgressBarVisible();
    }

    partial void OnIsSubtitleEnabledChanged(bool value)
    {
        ToggleSubtitleEnabledCommand.Execute(value);
    }
}
