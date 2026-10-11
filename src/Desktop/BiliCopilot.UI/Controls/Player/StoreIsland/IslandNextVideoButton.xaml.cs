// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条左侧面板。由 Store 源 <c>VideoPlayerTransportLeftPanel</c> 迁移而来，
/// 仅保留“下一个视频”入口；命令/提示/可见性适配当前 <see cref="ViewModels.Core.PlayerViewModel"/>。
/// </summary>
public sealed partial class IslandNextVideoButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandNextVideoButton),
            new PropertyMetadata(false, (sender, _) => ((IslandNextVideoButton)sender).UpdateInput()));

    private bool _loaded;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
    /// <summary>
    /// Initializes a new instance of the <see cref="IslandNextVideoButton"/> class.
    /// </summary>
    public IslandNextVideoButton()
    {
        InitializeComponent();
        Loaded += (_, _) => { _loaded = true; UpdateInput(); };
        Unloaded += (_, _) => { _loaded = false; UpdateInput(); };
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue) => UpdateInput();

    public void CancelPendingInput() { }

    private void UpdateInput() => IsEnabled = _loaded && IsActive;
}
