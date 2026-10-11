// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条中部的弹幕框。由 Store 源 <c>Controls/Danmaku/DanmakuBox</c> 迁移而来：
/// 保留弹幕开关、显示设置、内联输入框与发送设置；开关/显示设置绑定 <see cref="DanmakuRenderViewModel"/>，
/// 内联发送经 <see cref="PlayerViewModel.SendInlineDanmakuAsync"/>，仅在用户提交后发送。
/// </summary>
public sealed partial class IslandDanmakuBox : PlayerControlBase
{
    /// <summary>
    /// <see cref="RenderViewModel"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty RenderViewModelProperty =
        DependencyProperty.Register(nameof(RenderViewModel), typeof(DanmakuRenderViewModel), typeof(IslandDanmakuBox),
            new PropertyMetadata(default, OnRenderChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandDanmakuBox),
            new PropertyMetadata(false, OnActiveChanged));

    private sealed record Submission(PlayerViewModel ViewModel, long Generation, long Epoch, long Revision, string Text);
    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private Submission? _submission;
    private bool _loaded;
    private long _epoch;
    private long _draftRevision;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, IsDanmakuControlVisible: true }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandDanmakuBox"/> class.
    /// </summary>
    public IslandDanmakuBox()
    {
        InitializeComponent();
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    /// <summary>
    /// 弹幕渲染视图模型（开关/显示设置的数据源）.
    /// </summary>
    public DanmakuRenderViewModel? RenderViewModel
    {
        get => (DanmakuRenderViewModel?)GetValue(RenderViewModelProperty);
        set => SetValue(RenderViewModelProperty, value);
    }

    /// <inheritdoc/>
    private void OnLocalLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Subscribe(ViewModel);
        UpdateInput();
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        Subscribe(null);
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        Subscribe(_loaded ? newValue : null);
        UpdateInput();
    }

    public void CancelPendingInput()
    {
        ++_epoch;
        _submission = null;
        SendOptions?.CancelPendingInput();
        _popups.Cancel();
        UpdateInput();
    }

    private static void OnRenderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((IslandDanmakuBox)sender).CancelPendingInput();

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (IslandDanmakuBox)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        control.UpdateInput();
    }

    private void Subscribe(PlayerViewModel? vm)
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
        _subscribedViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.DanmakuSend)
            || !CanInteract) CancelPendingInput();
        UpdateInput();
    }

    private void UpdateInput()
    {
        IsEnabled = CanInteract;
        if (DanmakuInputBox is not null) DanmakuInputBox.IsEnabled = CanInteract && _submission is null;
    }

    private void OnDisplayFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        var render = RenderViewModel;
        _popups.Open(sender, ViewModel, _epoch, claim =>
        {
            if (CanInteract && ReferenceEquals(claim.ViewModel, ViewModel)
                && claim.Generation == ViewModel.MediaGeneration && claim.Epoch == _epoch
                && ReferenceEquals(render, RenderViewModel)) render?.ResetStyle();
        });
    }

    private void OnSendOptionsFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        var claim = _popups.Open(sender, ViewModel, _epoch, _ => SendOptions.CancelPendingInput());
        SendOptions.Activate(() => CanInteract && _popups.IsCurrent(claim, ViewModel, _epoch));
    }

    private void OnDraftChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ++_draftRevision;

    private async void OnDanmakuInputBoxSubmittedAsync(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.QueryText;
        if (!CanInteract || _submission is not null || string.IsNullOrWhiteSpace(text)
            || !string.Equals(text, sender.Text, StringComparison.Ordinal))
        {
            return;
        }

        // QuerySubmitted is the only send entry. A→B→A edits still change the revision.
        var vm = ViewModel;
        var request = new Submission(vm, vm.MediaGeneration, _epoch, _draftRevision, text);
        _submission = request;
        UpdateInput();
        try
        {
            await vm.SendInlineDanmakuAsync(text);
            if (IsCurrent(request) && _draftRevision == request.Revision
                && string.Equals(sender.Text, request.Text, StringComparison.Ordinal))
            {
                sender.Text = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // 媒体已切换/取消：保留草稿，不显示伪成功.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Inline danmaku send failed: {ex.Message}");
        }
        finally
        {
            if (IsCurrent(request))
            {
                _submission = null;
                UpdateInput();
            }
        }
    }

    private bool IsCurrent(Submission request)
        => CanInteract && ReferenceEquals(_submission, request) && ReferenceEquals(request.ViewModel, ViewModel)
            && request.Generation == ViewModel.MediaGeneration && request.Epoch == _epoch;
}
