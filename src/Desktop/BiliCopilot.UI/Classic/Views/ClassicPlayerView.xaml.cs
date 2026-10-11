// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Classic.Views;

/// <summary>
/// 经典播放器前端视图。仅绑定 <see cref="IClassicPlaybackSession"/>（强类型），
/// 不创建引擎、历史记录或外部进程；播放画面由宿主后端承载。
/// 岛式后端表面为原生子窗口（airspace），本视图控件一律位于视频槽之外；
/// 右键菜单与全窗口选集浮出使用弹出层，不在视频槽内叠加 XAML 控件。
/// </summary>
public sealed partial class ClassicPlayerView : UserControl, INotifyPropertyChanged
{
    private static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    private static readonly TimeSpan ControlsAutoHideDelay = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _autoHideTimer;
    private IClassicPlaybackSession? _session;
    private IReadOnlyList<ClassicMediaItem>? _playlistSource;
    private IReadOnlyList<ClassicPlaylistEntry> _playlistEntries = Array.Empty<ClassicPlaylistEntry>();
    private MenuFlyout? _contextMenu;
    private bool _syncingControls;
    private bool _interactionActive;
    private bool _isFullWindow;
    private bool _areControlsVisible = true;
    private bool _autoHideArmed;
    private bool _isContextMenuOpen;
    private bool _isListsFlyoutOpen;
    private double? _previewSeconds;
    private int? _syncedPlaylistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClassicPlayerView"/> class.
    /// </summary>
    public ClassicPlayerView()
    {
        InitializeComponent();
        SpeedSelector.ItemsSource = Speeds;
        SpeedSelector.SelectedItem = 1.0;
        _autoHideTimer = new DispatcherTimer { Interval = ControlsAutoHideDelay };
        _autoHideTimer.Tick += OnAutoHideTick;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 阻塞性面板（右键菜单、全窗口选集浮出）开合时触发。
    /// 宿主应随即刷新输入上下文（如 <c>ClassicPlayerWindow.RefreshInputContextAsync</c>）以取消 pending 输入。
    /// </summary>
    public event EventHandler? PanelStateChanged;

    /// <summary>
    /// 当前绑定的经典播放会话，由宿主设置。
    /// </summary>
    public IClassicPlaybackSession? Session
    {
        get => _session;
        private set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                _session.PropertyChanged -= OnSessionPropertyChanged;
            }

            _session = value;

            if (_session is not null)
            {
                _session.PropertyChanged += OnSessionPropertyChanged;
            }

            _previewSeconds = null;
            _contextMenu?.Hide();
            Raise(nameof(Session));
            Raise(nameof(PreviewText));
            Raise(nameof(IsPreviewVisible));
            Raise(nameof(PreviewOpacity));
            SyncFromSession();
        }
    }

    /// <summary>是否处于全窗口（沉浸）布局；由 <see cref="SetFullWindow"/> 驱动。</summary>
    public bool IsFullWindow => _isFullWindow;

    /// <summary>传输控制栏（传输条）是否应显示；页眉/侧栏仅由显示模式决定。</summary>
    public bool AreControlsVisible => _areControlsVisible;

    /// <summary>阻塞性面板是否打开（右键菜单、全窗口选集浮出）。宿主输入映射依据。</summary>
    public bool IsInputBlocked => _isContextMenuOpen || _isListsFlyoutOpen;

    /// <summary>
    /// 页眉可见性（仅由显示模式决定）。页眉属于窗口导航面（标题/关闭），
    /// 不参与超时隐藏，以免折叠时改变视频表面几何并隐藏普通窗口的必要操作。
    /// </summary>
    public bool ShowHeader => !_isFullWindow;

    /// <summary>
    /// 侧栏可见性（仅由显示模式决定）。侧栏是列表/选区入口，折叠会改变主列宽度
    /// 并导致视频表面重新布局，故不参与超时隐藏。
    /// </summary>
    public bool ShowSidePanel => !_isFullWindow;

    /// <summary>传输条是否应显示（控制栏显隐的逻辑状态，实际渲染见 <see cref="TransportOpacity"/>）。</summary>
    public bool ShowTransport => _areControlsVisible;

    /// <summary>
    /// 传输条不透明度。隐藏时置 0 但保留布局占位，视频表面几何不变，
    /// 避免岛式原生子窗口反复 <c>SetWindowPos</c> 造成尺寸跳动。
    /// </summary>
    public double TransportOpacity => _areControlsVisible ? 1d : 0d;

    /// <summary>传输条是否参与命中测试（隐藏时不拦截指针）。</summary>
    public bool IsTransportInteractive => _areControlsVisible;

    /// <summary>寻址预览行不透明度（保留布局，避免拖动寻址时视频尺寸跳动）。</summary>
    public double PreviewOpacity => _previewSeconds is not null ? 1d : 0d;

    /// <summary>寻址预览是否可见。</summary>
    public bool IsPreviewVisible => _previewSeconds is not null;

    /// <summary>寻址预览文本（位于传输条内，视频槽之外）。</summary>
    public string PreviewText => _previewSeconds is double seconds
        ? $"预览 {FormatTime(seconds)} / {FormatTime(_session?.DurationSeconds ?? 0)}"
        : string.Empty;

    /// <summary>播放列表条目（显式零基序号；ItemsView 无 SelectedIndex，重复条目按序号选择）。</summary>
    public IReadOnlyList<ClassicPlaylistEntry> PlaylistEntries => _playlistEntries;

    /// <summary>播放位置文本。</summary>
    public string PositionText => FormatTime(_session?.PositionSeconds ?? 0);

    /// <summary>总时长文本。</summary>
    public string DurationText => FormatTime(_session?.DurationSeconds ?? 0);

    /// <summary>音量文本。</summary>
    public string VolumeText => $"{(int)Math.Round((_session?.Volume ?? 0) * 100)}%";

    /// <summary>由宿主在验证通过后挂接经典播放会话。</summary>
    public void SetSession(IClassicPlaybackSession session) => Session = session;

    /// <summary>解绑当前会话。</summary>
    public void ClearSession() { _interactionActive = false; Session = null; }

    /// <summary>Pointer contact suppresses auto-hide only; it is not an input-blocking panel.</summary>
    public void SetInteractionActive(bool active)
    {
        if (_interactionActive == active) return;
        _interactionActive = active;
        if (IsLoaded) RefreshAutoHideTimer();
    }

    /// <summary>
    /// 切换真实全窗口布局：隐藏页眉与侧栏、去除外边距，视频槽占满窗口，传输条保留在槽外。
    /// 由宿主的显示模式应用调用（对应 <c>ClassicPlayerContent.SetFullWindow</c>）。
    /// </summary>
    public void SetFullWindow(bool isFullWindow)
    {
        if (_isFullWindow == isFullWindow)
        {
            return;
        }

        _isFullWindow = isFullWindow;
        _areControlsVisible = true;
        UpdateChromeLayout();
        RaiseChromeState();
        Raise(nameof(IsFullWindow));
        RefreshAutoHideTimer();
    }

    /// <summary>
    /// 切换传输控制栏可见性；普通与全窗口布局均支持，配合 3 秒显示超时自动隐藏。
    /// 由宿主输入映射调用（对应 <c>ClassicPlayerContent.ToggleControls</c>）。
    /// </summary>
    public void ToggleControls()
    {
        _areControlsVisible = !_areControlsVisible;
        RaiseChromeState();
        RefreshAutoHideTimer();
    }

    /// <summary>Reveal controls for accepted pointer/keyboard activity and renew the existing deadline.</summary>
    public void ShowControlsForInput()
    {
        if (!IsLoaded || _session is null || _session.IsClosing || IsInputBlocked) return;
        if (!_areControlsVisible)
        {
            _areControlsVisible = true;
            UpdateChromeLayout();
            RaiseChromeState();
        }
        // Force a fresh deadline on the one existing timer; reuse the same interval and policy.
        _autoHideArmed = false;
        _autoHideTimer.Stop();
        RefreshAutoHideTimer();
    }

    /// <summary>
    /// 在视频槽处显示播放右键菜单（播放/暂停、上一个/下一个、清晰度、控制栏显隐）。
    /// 由宿主输入映射调用（对应 <c>ClassicPlayerContent.ShowContextMenu</c>）。
    /// </summary>
    public void ShowContextMenu()
    {
        if (_session is null || _isContextMenuOpen)
        {
            return;
        }

        var menu = BuildContextMenu(_session);
        _contextMenu = menu;
        menu.Closed += OnContextMenuClosed;
        _isContextMenuOpen = true;
        Raise(nameof(IsInputBlocked));
        RaisePanelStateChanged();
        RefreshAutoHideTimer();
        menu.ShowAt(VideoHost);
    }

    /// <summary>
    /// 显示/隐藏寻址预览（单位秒；null 隐藏）。由宿主输入桥接调用
    /// （对应 <c>ClassicPlayerContent.PreviewPosition</c>）。
    /// </summary>
    public void PreviewPosition(double? seconds)
    {
        double? next = seconds is double value && double.IsFinite(value) && value >= 0 ? value : null;
        if (_previewSeconds == next)
        {
            return;
        }

        _previewSeconds = next;
        Raise(nameof(PreviewText));
        Raise(nameof(IsPreviewVisible));
        Raise(nameof(PreviewOpacity));
        // 拖动寻址期间预览可见即视为交互中：停止自动隐藏；拖动结束立即重新计时。
        RefreshAutoHideTimer();
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.PropertyChanged += OnSessionPropertyChanged;
        }

        SyncFromSession();
        RefreshAutoHideTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _interactionActive = false;
        _autoHideArmed = false;
        _autoHideTimer.Stop();
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e) => SyncFromSession();

    private void SyncFromSession()
    {
        Raise(nameof(PositionText));
        Raise(nameof(DurationText));
        Raise(nameof(VolumeText));
        SyncPlaylist();
        SyncPlaylistSelection();
        _syncingControls = true;
        if (SeekSlider is not null)
        {
            SeekSlider.Value = Math.Min(_session?.PositionSeconds ?? 0, SeekSlider.Maximum);
        }

        if (VolumeSlider is not null)
        {
            VolumeSlider.Value = _session?.Volume ?? 0;
        }

        _syncingControls = false;
        RefreshAutoHideTimer();
    }

    private void SyncPlaylist()
    {
        var playlist = _session?.Request.Playlist;
        if (ReferenceEquals(_playlistSource, playlist))
        {
            return;
        }

        _playlistSource = playlist;
        _playlistEntries = playlist is null
            ? Array.Empty<ClassicPlaylistEntry>()
            : playlist.Select((item, index) => new ClassicPlaylistEntry(index, item)).ToArray();
        _syncedPlaylistIndex = null;
        Raise(nameof(PlaylistEntries));
    }

    private void SyncPlaylistSelection()
    {
        var index = _session?.Request.PlaylistIndex;
        if (_syncedPlaylistIndex == index)
        {
            return;
        }

        _syncedPlaylistIndex = index;
        if (index is int ordinal && ordinal >= 0 && ordinal < _playlistEntries.Count)
            PlaylistItemsView.Select(ordinal);
        else
            PlaylistItemsView.DeselectAll();
    }

    private MenuFlyout BuildContextMenu(IClassicPlaybackSession session)
    {
        var menu = new MenuFlyout();
        var playing = session.State == ClassicPlaybackState.Playing;
        var transport = playing ? session.PauseCommand : session.PlayCommand;
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = playing ? "暂停" : "播放",
            Command = transport,
            IsEnabled = transport.CanExecute(null),
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "上一个",
            Command = session.PreviousCommand,
            IsEnabled = session.CanGoPrevious && session.PreviousCommand.CanExecute(null),
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "下一个",
            Command = session.NextCommand,
            IsEnabled = session.CanGoNext && session.NextCommand.CanExecute(null),
        });
        menu.Items.Add(new MenuFlyoutSeparator());

        var quality = new MenuFlyoutSubItem { Text = "清晰度" };
        quality.IsEnabled = (session.Capabilities & ClassicCapabilities.Quality) == ClassicCapabilities.Quality
            && session.Qualities.Count > 0;
        foreach (var item in session.Qualities)
        {
            var entry = new ToggleMenuFlyoutItem
            {
                Text = item.Description ?? string.Empty,
                IsChecked = ReferenceEquals(session.SelectedQuality, item),
                IsEnabled = session.SelectQualityCommand.CanExecute(item),
            };
            entry.Click += (_, _) => session.SelectQualityCommand.Execute(item);
            quality.Items.Add(entry);
        }

        menu.Items.Add(quality);
        menu.Items.Add(new MenuFlyoutSeparator());
        var controls = new ToggleMenuFlyoutItem { Text = "显示控制栏", IsChecked = _areControlsVisible };
        controls.Click += (_, _) => ToggleControls();
        menu.Items.Add(controls);
        return menu;
    }

    private void OnContextMenuClosed(object? sender, object e)
    {
        if (_contextMenu is not null)
        {
            _contextMenu.Closed -= OnContextMenuClosed;
            _contextMenu = null;
        }

        _isContextMenuOpen = false;
        Raise(nameof(IsInputBlocked));
        RaisePanelStateChanged();
        RefreshAutoHideTimer();
    }

    private void OnListsFlyoutOpened(object sender, object e)
    {
        _isListsFlyoutOpen = true;
        Raise(nameof(IsInputBlocked));
        RaisePanelStateChanged();
        RefreshAutoHideTimer();
    }

    private void OnListsFlyoutClosed(object sender, object e)
    {
        _isListsFlyoutOpen = false;
        Raise(nameof(IsInputBlocked));
        RaisePanelStateChanged();
        RefreshAutoHideTimer();
    }

    private void RefreshAutoHideTimer()
    {
        // 状态 tick 会以空 PropertyChanged 高频回调本方法；仅在武装状态真正变化时
        // Start/Stop，避免已武装的截止期被反复重置（普通与全窗口共用同一计时器与时长）。
        var armed = CanAutoHide();
        if (armed == _autoHideArmed)
        {
            return;
        }

        _autoHideArmed = armed;
        if (armed)
        {
            _autoHideTimer.Start();
        }
        else
        {
            _autoHideTimer.Stop();
        }
    }

    /// <summary>
    /// 3 秒超时隐藏的适用条件：普通与全窗口布局一致；仅在播放中、控制栏可见、
    /// 无阻塞面板、未在拖动寻址、且焦点未落在控制栏宿主控件时武装计时器。
    /// 空闲/加载/暂停/结束/失败等状态保留必要操作，不隐藏。
    /// </summary>
    private bool CanAutoHide()
        => _areControlsVisible
            && !_interactionActive
            && !IsInputBlocked
            && !IsPreviewVisible
            && SpeedSelector?.IsDropDownOpen != true
            && !IsChromeFocused()
            && _session is { IsClosing: false, State: ClassicPlaybackState.Playing };

    private void OnAutoHideTick(object? sender, object e)
    {
        _autoHideArmed = false;
        _autoHideTimer.Stop();
        if (!CanAutoHide())
        {
            return;
        }

        // 仅隐藏传输条视觉（保留布局占位），不改变视频表面几何。
        _areControlsVisible = false;
        RaiseChromeState();
    }

    /// <summary>
    /// 判断键盘焦点是否落在本视图的控制栏宿主控件（页眉/侧栏/传输条）内。
    /// 视频表面持有焦点（宿主输入桥在按下时请求焦点）不视为控制栏交互。
    /// </summary>
    private bool IsChromeFocused()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            node is not null;
            node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, VideoHost))
            {
                return false;
            }

            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateChromeLayout()
    {
        LayoutRoot.Padding = _isFullWindow ? new Thickness(0) : new Thickness(12);
        LayoutRoot.RowSpacing = _isFullWindow ? 0 : 8;
        SidePanelColumn.Width = ShowSidePanel ? new GridLength(320) : new GridLength(0);
    }

    private void RaiseChromeState()
    {
        Raise(nameof(AreControlsVisible));
        Raise(nameof(ShowHeader));
        Raise(nameof(ShowSidePanel));
        Raise(nameof(ShowTransport));
        Raise(nameof(TransportOpacity));
        Raise(nameof(IsTransportInteractive));
    }

    private void RaisePanelStateChanged() => PanelStateChanged?.Invoke(this, EventArgs.Empty);

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingControls || _session is null)
        {
            return;
        }

        _session.SeekCommand.Execute(e.NewValue);
    }

    private void OnVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingControls || _session is null)
        {
            return;
        }

        _session.SetVolumeCommand.Execute(e.NewValue);
    }

    private void OnSpeedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || _session is null)
        {
            return;
        }

        if (SpeedSelector.SelectedItem is double speed)
        {
            _session.SetSpeedCommand.Execute(speed);
        }
    }

    private void OnQualitySelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_session is not null && sender.SelectedItem is PlayerFormatInformation quality)
        {
            _session.SelectQualityCommand.Execute(quality);
        }
    }

    private void OnPartSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_session is not null && sender.SelectedItem is VideoPart part)
        {
            _session.SelectPartCommand.Execute(part);
        }
    }

    private void OnEpisodeSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_session is not null && sender.SelectedItem is EpisodeInformation episode)
        {
            _session.SelectEpisodeCommand.Execute(episode);
        }
    }

    private void OnPlaylistSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_session is null || sender.SelectedItem is not ClassicPlaylistEntry entry)
        {
            return;
        }

        // 程序matic 同步选择时避免以相同序号重开；用户重复点选当前项同样跳过。
        if (_session.Request.PlaylistIndex == entry.Ordinal)
        {
            return;
        }

        _session.SelectPlaylistItemCommand.Execute(entry.Ordinal);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
