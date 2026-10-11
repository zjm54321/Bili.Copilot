// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 主窗口内嵌播放层：仅负责布局（标题、视频占位、右侧信息栏），不持有播放引擎或会话.
/// </summary>
public sealed partial class EmbeddedPlayerView : PlayerControlBase
{
    /// <summary>
    /// 与 RootLayout 的 <c>WindowNarrowMinWidth</c>（App.xaml = 850）对齐的当前外壳兼容断点；
    /// 源播放页本身没有该宽度阈值（其列宽由 NavColumnWidth + 分栏器承载），此处仅作收起条件.
    /// </summary>
    private const double SidePanelMinWidth = 850d;

    /// <summary>
    /// 侧栏展开宽度默认值：源页实测默认（<c>VideoPlayerPageViewModel.GetDefaultNavColumnWidth()</c>
    /// 覆盖基类 240 返回 360，源 VideoPlayerPageViewModel.cs:66-67）.
    /// </summary>
    private const double SidePanelDefaultWidth = 360d;

    /// <summary>源分栏器合法列宽范围（源 VideoPlayerPage.xaml:130-131 Min/MaxColumnWidth）.</summary>
    private const double SidePanelMinColumnWidth = 300d;
    private const double SidePanelMaxColumnWidth = 500d;

    /// <summary>源 HeaderRow 高度（源 VideoPlayerPage.xaml:34 RowDefinition Height="56"）.</summary>
    private const double PlayerHeaderHeight = 56d;

    private PlayerPresentationMode _mode = PlayerPresentationMode.Normal;
    private bool? _isSidePanelShown;
    private double _expandedSidePanelWidth = SidePanelDefaultWidth;
    private bool _active;
    private bool _loaded;
    private bool _suspended;
    private long _attachmentEpoch;
    private PlayerViewModel? _subscribedViewModel;
    private PropertyChangedEventHandler? _viewModelHandler;
    private Func<IDisposable>? _suspendRendering;
    private IDisposable? _loadingErrorLease;
    private bool _acquiringLease;
    private readonly HeaderPopupState _headerPopups;
    private readonly Dictionary<Button, HeaderContext> _headers = new();
    private object? _headerRefresh;
    private long _lifecycleRequest;

    /// <summary>
    /// 右侧栏是否装载；驱动两个侧栏宿主的 <c>x:Load</c>，隐藏时不创建、不加载重面板.
    /// </summary>
    public static readonly DependencyProperty IsSidePanelActiveProperty =
        DependencyProperty.Register(nameof(IsSidePanelActive), typeof(bool), typeof(EmbeddedPlayerView), new PropertyMetadata(false));

    /// <summary>
    /// 侧栏列宽；与 XAML 中的分栏控制器共享同一数值（承载源 ViewModel.NavColumnWidth）.
    /// </summary>
    public static readonly DependencyProperty NavColumnWidthProperty =
        DependencyProperty.Register(nameof(NavColumnWidth), typeof(double), typeof(EmbeddedPlayerView), new PropertyMetadata(SidePanelDefaultWidth, OnNavColumnWidthChanged));

    /// <summary>
    /// 侧栏是否被用户手动收起；仅由头部 SideSplitButton TwoWay 写入（承载源 ViewModel.IsNavColumnManualHide），
    /// 卸载/切换呈现不得改写该偏好.
    /// </summary>
    public static readonly DependencyProperty IsNavColumnManualHideProperty =
        DependencyProperty.Register(nameof(IsNavColumnManualHide), typeof(bool), typeof(EmbeddedPlayerView), new PropertyMetadata(false, OnIsNavColumnManualHideChanged));

    /// <summary>
    /// 侧栏 effective hidden（显示条件取反：active/loaded/suspended/连接器/呈现模式/宽度/手动折叠）；
    /// 只读供分栏控制器读取，宽度回写不能借它强行显示.
    /// </summary>
    public static readonly DependencyProperty IsNavColumnEffectiveHideProperty =
        DependencyProperty.Register(nameof(IsNavColumnEffectiveHide), typeof(bool), typeof(EmbeddedPlayerView), new PropertyMetadata(true));

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedPlayerView"/> class.
    /// </summary>
    public EmbeddedPlayerView()
    {
        InitializeComponent();
        _headerPopups = new HeaderPopupState(
            action => DispatcherQueue.TryEnqueue(() => action()),
            ex => this.Get<ILogger<EmbeddedPlayerView>>().LogWarning("标题菜单遮挡申请失败 ({ErrorType})", ex.GetType().Name));
        Loaded += OnEmbeddedLoaded;
        Unloaded += OnEmbeddedUnloaded;
        SizeChanged += OnEmbeddedSizeChanged;
    }

    /// <summary>
    /// 获取或设置右侧栏是否装载.
    /// </summary>
    public bool IsSidePanelActive
    {
        get => (bool)GetValue(IsSidePanelActiveProperty);
        set => SetValue(IsSidePanelActiveProperty, value);
    }

    /// <summary>
    /// 获取或设置侧栏列宽.
    /// </summary>
    public double NavColumnWidth
    {
        get => (double)GetValue(NavColumnWidthProperty);
        set => SetValue(NavColumnWidthProperty, value);
    }

    /// <summary>
    /// 获取或设置侧栏是否被用户手动收起.
    /// </summary>
    public bool IsNavColumnManualHide
    {
        get => (bool)GetValue(IsNavColumnManualHideProperty);
        set => SetValue(IsNavColumnManualHideProperty, value);
    }

    /// <summary>
    /// 获取侧栏 effective hidden.
    /// </summary>
    public bool IsNavColumnEffectiveHide
    {
        get => (bool)GetValue(IsNavColumnEffectiveHideProperty);
        set => SetValue(IsNavColumnEffectiveHideProperty, value);
    }

    /// <summary>
    /// 获取供宿主定位原生子窗口的空视频占位元素.
    /// </summary>
    public FrameworkElement VideoSlot => VideoSlotElement;

    /// <summary>Attach a session without replacing the view or its measurable video slot.</summary>
    public FrameworkElement Attach(PlayerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var request = ++_lifecycleRequest;
        if (!ReferenceEquals(ViewModel, viewModel))
        {
            if (!ReleaseCore(request)) return VideoSlot;
            _active = true;
            _suspended = false;
            ViewModel = viewModel;
        }
        else
        {
            _active = true;
            _suspended = false;
            ResumeAttachment();
        }

        return VideoSlot;
    }

    /// <summary>Receive the main window's existing multi-owner rendering-occlusion factory.</summary>
    internal void SetRenderingSuspensionFactory(Func<IDisposable> suspendRendering)
    {
        ArgumentNullException.ThrowIfNull(suspendRendering);
        if (ReferenceEquals(_suspendRendering, suspendRendering)) return;
        _suspendRendering = suspendRendering;
        var loadingLease = TakeLoadingErrorLease();
        var popupLease = _headerPopups.ChangeFactory(suspendRendering);
        try { loadingLease?.Dispose(); }
        finally { popupLease?.Dispose(); }
        UpdateLoadingErrorLease();
        _headerPopups.UpdateLease();
    }

    /// <summary>
    /// 应用呈现模式：仅切换本视图内部布局（标题/侧栏）与侧栏装载，不重建 UIElement、不触碰会话.
    /// </summary>
    /// <param name="mode">目标呈现模式.</param>
    public void SetPresentationMode(PlayerPresentationMode mode)
    {
        _mode = mode;
        // 源 PlayerHostState（源 VideoPlayerPage.xaml:142-150）：非 Normal 呈现隐藏标题/分隔线/分栏并把视频列扩至整宽.
        var hosted = mode != PlayerPresentationMode.Normal;
        PlayerHeader.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
        HeaderDivider.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
        SideSplitter.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = hosted ? GridLength.Auto : new GridLength(PlayerHeaderHeight);
        Grid.SetColumnSpan(VideoContainer, hosted ? 3 : 1);
        UpdateSidePanel();
        RefreshHeaderRegistrations();
        QueueHeaderRefresh();
    }

    /// <summary>
    /// 解除本视图持有的会话引用和渲染遮挡，不关闭会话、引擎或原生宿主.
    /// </summary>
    public void Release()
        => ReleaseCore(++_lifecycleRequest);

    private bool ReleaseCore(long request)
    {
        var owner = ViewModel;
        _active = false;
        _suspended = true;
        var epoch = ++_attachmentEpoch;
        SubscribeViewModel(null);
        ClearBoundTargets(owner, epoch, () =>
        {
            _mode = PlayerPresentationMode.Normal;
            Bindings.StopTracking();
            if (request == _lifecycleRequest && _attachmentEpoch == epoch && ReferenceEquals(ViewModel, owner)) ClearValue(ViewModelProperty);
        }, TakeLoadingErrorLease());
        return request == _lifecycleRequest && ViewModel is null;
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        var request = _lifecycleRequest;
        var epoch = ++_attachmentEpoch;
        SubscribeViewModel(null);
        if (!_active) return;
        var suspended = _suspended;
        _suspended = true;
        ClearBoundTargets(newValue, epoch, () =>
        {
            Bindings.StopTracking();
            if (request != _lifecycleRequest || _attachmentEpoch != epoch || !ReferenceEquals(ViewModel, newValue)) return;
            _suspended = suspended;
            ResumeAttachment();
        }, TakeLoadingErrorLease());
    }

    private void OnEmbeddedLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _suspended = false;
        ResumeAttachment();
    }

    private void OnEmbeddedUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded && _suspended) return;
        _loaded = false;
        _suspended = true;
        ++_lifecycleRequest;
        var epoch = ++_attachmentEpoch;
        SubscribeViewModel(null);
        ClearBoundTargets(ViewModel, epoch, () => Bindings.StopTracking(), TakeLoadingErrorLease());
        // Keep the attached VM only for temporary unload. Release clears it explicitly.
    }

    private void ResumeAttachment()
    {
        var owner = ViewModel;
        var epoch = _attachmentEpoch;
        if (!IsCurrentAttachment(owner, epoch))
        {
            UpdateLoadingErrorLease();
            return;
        }

        // Attach may precede the first Loaded/host creation: observe loading immediately.
        SubscribeViewModel(owner);
        UpdateLoadingErrorLease();
        if (!_loaded || !IsCurrentAttachment(owner, epoch)) return;
        Bindings.Update();
        if (IsCurrentAttachment(owner, epoch)) SetPresentationMode(owner.Presentation);
    }

    private bool IsCurrentAttachment(PlayerViewModel? owner, long epoch)
        => _active && !_suspended && owner is not null && ReferenceEquals(owner, ViewModel)
            && epoch == _attachmentEpoch;

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;
        if (_subscribedViewModel is not null && _viewModelHandler is not null)
            _subscribedViewModel.PropertyChanged -= _viewModelHandler;
        _subscribedViewModel = viewModel;
        _viewModelHandler = null;
        if (viewModel is null) return;

        var epoch = _attachmentEpoch;
        _viewModelHandler = (sender, args) =>
        {
            if (!ReferenceEquals(sender, viewModel) || !ReferenceEquals(_subscribedViewModel, viewModel)
                || !IsCurrentAttachment(viewModel, epoch)) return;
            if (args.PropertyName is null or "" or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.ErrorMessage))
                UpdateLoadingErrorLease();
            if (!IsCurrentAttachment(viewModel, epoch)) return;
            if (viewModel.IsPlaybackClosing || args.PropertyName is null or "" or nameof(PlayerViewModel.Connector)
                or nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Presentation) or nameof(PlayerViewModel.IsPlaybackClosing))
            {
                _headerRefresh = null;
                RefreshHeaderRegistrations();
                QueueHeaderRefresh();
            }
        };
        viewModel.PropertyChanged += _viewModelHandler;
    }

    private void ClearBoundTargets(PlayerViewModel? owner, long epoch, Action finish, IDisposable? loadingLease)
    {
        var request = _lifecycleRequest;
        _headerRefresh = null;
        var generation = owner?.MediaGeneration;
        // Snapshot before command/Hide/Dispose callbacks can reenter and materialize another owner.
        var back = ErrorBackButton;
        var hosts = FindMaterializedHosts(this).ToArray();
        var targets = hosts.SelectMany(MaterializedTree).Reverse().ToArray();
        var retired = DetachHeaders(_headers.Values.ToArray());
        bool IsCurrent() => request == _lifecycleRequest && epoch == _attachmentEpoch
            && ReferenceEquals(owner, ViewModel) && owner?.MediaGeneration == generation;
        AttachmentCleanup.Run(IsCurrent, () =>
        {
            if (back is not null) back.Command = null;
            foreach (var target in targets)
            {
                if (!IsCurrent()) return;
                ClearBoundTarget(target, IsCurrent);
            }
            foreach (var host in hosts)
            {
                if (!IsCurrent()) return;
                host.Content = null;
            }
            if (!IsCurrent()) return;
            _isSidePanelShown = false;
            IsSidePanelActive = false;
            if (!IsCurrent()) return;
            IsNavColumnEffectiveHide = true;
            if (!IsCurrent()) return;
            SidePanelColumn.Width = new GridLength(0);
        }, finish, retired, loadingLease);
    }

    private static IEnumerable<ContentControl> FindMaterializedHosts(DependencyObject element)
    {
        // Do not read generated deferred-name accessors: that can call FindName and realize x:Load.
        if (element is ContentControl { Name: "HeaderHost" or "SideHeaderHost" or "SideBodyHost" } host)
        {
            yield return host;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            foreach (var child in FindMaterializedHosts(VisualTreeHelper.GetChild(element, i)))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<DependencyObject> MaterializedTree(DependencyObject element)
    {
        yield return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var child in MaterializedTree(VisualTreeHelper.GetChild(element, i))) yield return child;
    }

    private static void ClearBoundTarget(DependencyObject element, Func<bool> isCurrent)
    {
        if (element is VideoPlayerControlBase video) video.ClearValue(VideoPlayerControlBase.ViewModelProperty);
        else if (element is PgcPlayerControlBase pgc) pgc.ClearValue(PgcPlayerControlBase.ViewModelProperty);
        if (!isCurrent()) return;
        if (element is ButtonBase button)
        {
            button.Command = null;
            if (!isCurrent()) return;
            button.CommandParameter = null;
        }
        if (!isCurrent()) return;
        if (element is Button { Flyout: MenuFlyout headerMenu })
        {
            foreach (var item in headerMenu.Items.OfType<MenuFlyoutItem>().ToArray())
            {
                if (!isCurrent()) return;
                item.Command = null;
                if (!isCurrent()) return;
                item.CommandParameter = null;
            }
        }
        if (!isCurrent()) return;
        if (element is ContentControl content && content.Content is not UIElement) content.Content = null;
        if (!isCurrent()) return;
        // A presenter that is the template root of a ContentControl (HeaderHost/Button...) receives Content through
        // TemplateBinding. Writing a local null would permanently replace that TemplateBinding, so a later re-attach
        // (Bindings.Update → HeaderHost.Content = connector) could no longer show the title. The owning ContentControl
        // is cleared by its own branch/host cleanup, which releases the same reference through the TemplateBinding.
        if (element is ContentPresenter presenter && presenter.Content is not UIElement
            && VisualTreeHelper.GetParent(presenter) is not ContentControl) presenter.Content = null;
    }

    private sealed class HeaderContext(Button button, MenuFlyout flyout, ContentControl host, PlayerViewModel owner,
        object connector, long epoch, long generation)
    {
        internal readonly Button Button = button;
        internal readonly MenuFlyout Flyout = flyout;
        internal readonly ContentControl Host = host;
        internal readonly PlayerViewModel Owner = owner;
        internal readonly object Connector = connector;
        internal readonly long Epoch = epoch, Generation = generation;
        internal HeaderPopupState.Registration Registration = null!;
        internal EventHandler<object>? Opened;
        internal long TagToken;
        internal readonly List<(MenuFlyoutItem Item, HeaderOwnerCommand Command)> Commands = new();
    }

    private ContentControl? FindHeaderHost(Button button)
    {
        ContentControl? host = null;
        for (DependencyObject? node = button; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement element && element.Visibility != Visibility.Visible) return null;
            if (node is ContentControl { Name: "HeaderHost" } found) host = found;
            if (ReferenceEquals(node, this)) return host;
        }
        return null;
    }

    private bool IsCurrentHeader(HeaderContext context)
        => _loaded && IsLoaded && IsCurrentAttachment(context.Owner, context.Epoch) && !context.Owner.IsPlaybackClosing
        && _mode == PlayerPresentationMode.Normal && context.Owner.Presentation == PlayerPresentationMode.Normal
        && PlayerHeader.Visibility == Visibility.Visible && context.Button.IsLoaded
        && context.Owner.MediaGeneration == context.Generation && ReferenceEquals(context.Owner.Connector, context.Connector)
        && ReferenceEquals(context.Button.Tag, context.Connector) && ReferenceEquals(context.Host.Content, context.Connector)
        && ReferenceEquals(context.Button.Flyout, context.Flyout) && ReferenceEquals(FindHeaderHost(context.Button), context.Host)
        && _headers.TryGetValue(context.Button, out var current) && ReferenceEquals(current, context);

    private void OnHeaderButtonLoaded(object sender, RoutedEventArgs e)
    {
        RefreshHeaderRegistrations();
        QueueHeaderRefresh();
    }

    private void OnHeaderButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { IsLoaded: false } button && _headers.TryGetValue(button, out var context))
            foreach (var retirement in DetachHeaders(new[] { context })) retirement.Close();
    }

    private void QueueHeaderRefresh()
    {
        if (_headerRefresh is not null || !_loaded || !_active || _suspended || ViewModel is not { } owner) return;
        var epoch = _attachmentEpoch;
        var generation = owner.MediaGeneration;
        var ticket = new object();
        _headerRefresh = ticket;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_headerRefresh, ticket)) return;
            _headerRefresh = null;
            if (IsCurrentAttachment(owner, epoch) && owner.MediaGeneration == generation) RefreshHeaderRegistrations();
        }) && ReferenceEquals(_headerRefresh, ticket)) _headerRefresh = null;
    }

    private void RefreshHeaderRegistrations()
    {
        var owner = ViewModel;
        var epoch = _attachmentEpoch;
        var generation = owner?.MediaGeneration;
        foreach (var retirement in DetachHeaders(_headers.Values.Where(context => !IsCurrentHeader(context)).ToArray())) retirement.Close();
        if (!_loaded || owner is null || !IsCurrentAttachment(owner, epoch) || owner.IsPlaybackClosing || owner.MediaGeneration != generation
            || _mode != PlayerPresentationMode.Normal || owner.Presentation != PlayerPresentationMode.Normal
            || PlayerHeader.Visibility != Visibility.Visible || owner.Connector is not { } connector) return;
        // HeaderHost has no x:Load, so its generated field is materialized with the view: start from it instead of
        // walking the whole tree (sidebar included). Its children are still read live on every refresh, nothing cached.
        var headerHosts = HeaderHost is { } header && ReferenceEquals(header.Content, connector)
            ? new[] { header } : Array.Empty<ContentControl>();
        foreach (var host in headerHosts)
        {
            foreach (var button in MaterializedTree(host).OfType<Button>().Where(button => button.IsLoaded && button.Flyout is MenuFlyout).ToArray())
            {
                if (!IsCurrentAttachment(owner, epoch) || owner.MediaGeneration != generation) return;
                if (_headers.ContainsKey(button) || !ReferenceEquals(button.Tag, connector) || !ReferenceEquals(FindHeaderHost(button), host)) continue;
                var flyout = (MenuFlyout)button.Flyout;
                var context = new HeaderContext(button, flyout, host, owner, connector, epoch, owner.MediaGeneration);
                var registration = new HeaderPopupState.Registration(flyout, button, owner, connector, epoch, owner.MediaGeneration,
                    () => IsCurrentHeader(context), flyout.Hide);
                context.Registration = registration;
                if (!_headerPopups.Register(registration)) continue;
                _headers.Add(button, context);
                context.Opened = (sender, args) =>
                {
                    if (!ReferenceEquals(sender, flyout)) return;
                    _headerPopups.Open(registration, openTicket =>
                    {
                        EventHandler<object>? closed = null;
                        closed = (closedSender, closedArgs) =>
                        {
                            if (!ReferenceEquals(closedSender, flyout)) return;
                            flyout.Closed -= closed;
                            _headerPopups.Closed(openTicket);
                            QueueHeaderRefresh();
                        };
                        flyout.Closed += closed;
                    });
                };
                flyout.Opened += context.Opened;
                context.TagToken = button.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, (sender, property) =>
                {
                    if (!_headers.TryGetValue(button, out var current) || !ReferenceEquals(current, context)) return;
                    RefreshHeaderRegistrations();
                    QueueHeaderRefresh();
                });
                if (connector is VideoConnectorViewModel)
                {
                    foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
                    {
                        ICommand? command = item.Tag switch
                        {
                            "OpenInNewWindow" => owner.OpenInNewWindowCommand,
                            "OpenWithMpv" => owner.OpenWithMpvCommand,
                            "OpenWithMpvNet" => owner.OpenWithMpvNetCommand,
                            _ => null,
                        };
                        if (command is null) continue;
                        var guarded = new HeaderOwnerCommand(command, () => registration.CanUse);
                        context.Commands.Add((item, guarded));
                        guarded.Subscribe();
                        item.CommandParameter = null;
                        item.Command = guarded;
                    }
                }
            }
        }
    }

    private List<HeaderPopupState.Retirement> DetachHeaders(HeaderContext[] contexts)
    {
        var retired = new List<HeaderPopupState.Retirement>();
        // Detach every owner/ticket before touching commands or invoking a Hide/lease callback.
        foreach (var context in contexts)
        {
            if (!_headers.TryGetValue(context.Button, out var current) || !ReferenceEquals(current, context)) continue;
            _headers.Remove(context.Button);
            if (_headerPopups.Unregister(context.Registration) is { } retirement) retired.Add(retirement);
            context.Flyout.Opened -= context.Opened;
            context.Button.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, context.TagToken);
        }
        foreach (var context in contexts)
        {
            foreach (var (item, command) in context.Commands)
            {
                command.Dispose();
                if (!ReferenceEquals(item.Command, command)) continue;
                item.Command = null;
                // A Command callback may have attached a new owner; don't clear its parameter.
                if (item.Command is null) item.CommandParameter = null;
            }
            context.Commands.Clear();
        }
        return retired;
    }

    private void UpdateLoadingErrorLease()
    {
        if (_acquiringLease) return;
        var owner = ViewModel;
        var epoch = _attachmentEpoch;
        var factory = _suspendRendering;
        if (!IsCurrentAttachment(owner, epoch) || factory is null
            || !(owner.IsConnecting || !string.IsNullOrEmpty(owner.ErrorMessage)))
        {
            ReleaseLoadingErrorLease();
            return;
        }

        if (_loadingErrorLease is not null) return; // loading → error retains this same owner lease.
        _acquiringLease = true;
        try
        {
            var lease = factory();
            // The factory can synchronously notify the host: reject a replaced/released attachment.
            if (IsCurrentAttachment(owner, epoch) && ReferenceEquals(factory, _suspendRendering)
                && (owner.IsConnecting || !string.IsNullOrEmpty(owner.ErrorMessage))) _loadingErrorLease = lease;
            else lease.Dispose();
        }
        finally { _acquiringLease = false; }

        if (_loadingErrorLease is null && _active && !_suspended && ViewModel is { } current
            && (current.IsConnecting || !string.IsNullOrEmpty(current.ErrorMessage))) UpdateLoadingErrorLease();
    }

    private void ReleaseLoadingErrorLease()
    {
        var lease = TakeLoadingErrorLease();
        lease?.Dispose();
    }

    private IDisposable? TakeLoadingErrorLease()
    {
        var lease = _loadingErrorLease;
        _loadingErrorLease = null;
        return lease;
    }

    private void OnEmbeddedSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSidePanel();

    private void UpdateSidePanel()
    {
        var width = XamlRoot?.Size.Width ?? ActualWidth;
        var show = _active && _loaded && !_suspended && ViewModel is not null
            && _mode == PlayerPresentationMode.Normal && width >= SidePanelMinWidth
            && !IsNavColumnManualHide;

        // 仅在可见性结论变化时提交，避免逐帧布局或反复清建子元素；effective hidden 与显示结论同源.
        if (_isSidePanelShown == show)
        {
            return;
        }

        _isSidePanelShown = show;
        IsNavColumnEffectiveHide = !show;
        SidePanelColumn.Width = show ? new GridLength(_expandedSidePanelWidth) : new GridLength(0);
        IsSidePanelActive = show;
    }

    private static void OnNavColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (EmbeddedPlayerView)d;
        var width = (double)e.NewValue;
        // 仅接受源分栏器的合法范围（Min/MaxColumnWidth）作为展开宽度；初始默认值不触发回调.
        if (width >= SidePanelMinColumnWidth && width <= SidePanelMaxColumnWidth)
        {
            view._expandedSidePanelWidth = width;
        }

        // 只有当前实际显示时才改渲染列；隐藏/非 Normal 下保持列 0，避免迟到回写强行显示.
        if (view._isSidePanelShown == true && view.SidePanelColumn is { } column)
        {
            column.Width = new GridLength(view._expandedSidePanelWidth);
        }
    }

    private static void OnIsNavColumnManualHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmbeddedPlayerView)d).UpdateSidePanel();

    // View-private event/lease state. No UI tree, player flow or native owner is created here.
    internal sealed class HeaderPopupState(Func<Action, bool> schedule, Action<Exception> reportError)
    {
        private readonly Dictionary<object, Registration> _registrations = new(ReferenceEqualityComparer.Instance);
        private Func<IDisposable>? _factory;
        private Ticket? _current;
        private bool _updating;
        private bool _again;
        private bool _queued;

        internal sealed class Registration(object flyout, object button, object owner, object connector,
            long epoch, long generation, Func<bool> isCurrent, Action hide)
        {
            internal readonly object Flyout = flyout, Button = button, Owner = owner, Connector = connector;
            internal readonly long Epoch = epoch, Generation = generation;
            internal readonly Func<bool> IsCurrent = isCurrent;
            internal readonly Action Hide = hide;
            internal bool Registered;
            internal Ticket? OpenTicket;
            internal bool CanUse => Registered && IsCurrent();
        }

        internal sealed class Ticket(Registration registration)
        {
            internal readonly Registration Registration = registration;
            internal IDisposable? Lease;
            internal bool Retired;
            internal bool Closed;
        }

        internal sealed class Retirement(Ticket ticket, IDisposable? lease)
        {
            private bool _finished;
            internal void Close()
            {
                if (_finished) return;
                _finished = true;
                // Both the ticket and this captured lease were detached before any callback.
                var oldLease = lease;
                lease = null;
                try { if (!ticket.Closed) ticket.Registration.Hide(); }
                finally { oldLease?.Dispose(); }
            }
        }

        internal bool Register(Registration registration)
        {
            // A retired registration with an outstanding Closed still owns this flyout identity.
            if (_registrations.ContainsKey(registration.Flyout)) return false;
            _registrations.Add(registration.Flyout, registration);
            registration.Registered = true;
            return true;
        }

        internal bool Open(Registration registration, Action<Ticket> subscribeClosed)
        {
            if (!registration.CanUse || !_registrations.TryGetValue(registration.Flyout, out var currentRegistration)
                || !ReferenceEquals(currentRegistration, registration) || registration.OpenTicket is not null) return false;
            var previous = Detach(_current);
            var ticket = new Ticket(registration);
            registration.OpenTicket = ticket;
            _current = ticket;
            try { subscribeClosed(ticket); }
            finally { previous?.Close(); }
            UpdateLease();
            return true;
        }

        internal void Closed(Ticket ticket)
        {
            if (ticket.Closed) return;
            ticket.Closed = true;
            var registration = ticket.Registration;
            if (ReferenceEquals(registration.OpenTicket, ticket)) registration.OpenTicket = null;
            Detach(ticket)?.Close();
            RemoveRetiredRegistration(registration);
            UpdateLease();
        }

        internal Retirement? Unregister(Registration registration)
        {
            registration.Registered = false;
            var retirement = Detach(registration.OpenTicket);
            RemoveRetiredRegistration(registration);
            return retirement;
        }

        private void RemoveRetiredRegistration(Registration registration)
        {
            if (!registration.Registered && registration.OpenTicket is null
                && _registrations.TryGetValue(registration.Flyout, out var current) && ReferenceEquals(current, registration))
                _registrations.Remove(registration.Flyout);
        }

        private Retirement? Detach(Ticket? ticket)
        {
            if (ticket is null || ticket.Retired) return null;
            ticket.Retired = true;
            if (ReferenceEquals(_current, ticket)) _current = null;
            var lease = ticket.Lease;
            ticket.Lease = null;
            return new Retirement(ticket, lease);
        }

        internal IDisposable? ChangeFactory(Func<IDisposable> factory)
        {
            _factory = factory;
            var lease = _current?.Lease;
            if (_current is { } ticket) ticket.Lease = null;
            return lease;
        }

        internal void UpdateLease()
        {
            if (_updating) { _again = true; return; }
            _updating = true;
            try
            {
                // One merged reconsideration after synchronous factory/Hide reentrancy.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    _again = false;
                    var ticket = _current;
                    var factory = _factory;
                    if (ticket is not null && !ticket.Registration.CanUse) Detach(ticket)?.Close();
                    else if (ticket is not null && !ticket.Retired && ticket.Lease is null && factory is not null)
                    {
                        try
                        {
                            var lease = factory();
                            if (ReferenceEquals(_current, ticket) && !ticket.Retired && ticket.Registration.CanUse
                                && ReferenceEquals(factory, _factory)) ticket.Lease = lease;
                            else lease.Dispose();
                        }
                        catch (Exception ex) { reportError(ex); }
                    }

                    if (!_again) break;
                }
            }
            finally { _updating = false; }
            if (_again && !_queued)
            {
                _queued = true;
                if (!schedule(() => { _queued = false; UpdateLease(); })) _queued = false;
            }
        }
    }

    [WinRT.GeneratedWinRTExposedType]
    internal sealed partial class HeaderOwnerCommand(ICommand command, Func<bool> isCurrent) : ICommand, IDisposable
    {
        private bool _disposed;
        private EventHandler? _changed;
        public event EventHandler? CanExecuteChanged;
        internal void Subscribe()
        {
            _changed = (sender, args) => CanExecuteChanged?.Invoke(this, args);
            command.CanExecuteChanged += _changed;
        }
        public bool CanExecute(object? parameter)
            => !_disposed && isCurrent() && command.CanExecute(parameter) && !_disposed && isCurrent();
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter) && !_disposed && isCurrent()) command.Execute(parameter);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_changed is not null) command.CanExecuteChanged -= _changed;
            _changed = null;
            CanExecuteChanged = null;
        }
    }

    internal static class AttachmentCleanup
    {
        internal static void Run(Func<bool> isCurrent, Action clearTargets, Action finish,
            IReadOnlyList<HeaderPopupState.Retirement> popups, IDisposable? loadingLease)
        {
            try { if (isCurrent()) clearTargets(); }
            finally
            {
                try { foreach (var popup in popups) popup.Close(); }
                finally { loadingLease?.Dispose(); }
            }
            // Hide/Dispose may synchronously Attach another owner; never run the old finish on it.
            if (isCurrent()) finish();
        }
    }
}
