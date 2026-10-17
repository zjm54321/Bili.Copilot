// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 点赞按钮.
/// </summary>
public sealed partial class LikeButton : LayoutUserControlBase
{
    /// <summary>
    /// <see cref="IsLiked"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsLikedProperty =
        DependencyProperty.Register(nameof(IsLiked), typeof(bool), typeof(LikeButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="Tip"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TipProperty =
        DependencyProperty.Register(nameof(LikeButton), typeof(string), typeof(LikeButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="Command"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(LikeButton), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="TripleCommand"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TripleCommandProperty =
        DependencyProperty.Register(nameof(TripleCommand), typeof(ICommand), typeof(LikeButton), new PropertyMetadata(default, OnTipCommandChanged));

    private readonly PlayerActionTip _tripleTip;

    /// <summary>
    /// Initializes a new instance of the <see cref="LikeButton"/> class.
    /// </summary>
    public LikeButton()
    {
        InitializeComponent();
        _tripleTip = new PlayerActionTip(this, TripleTip, () => new ICommand?[] { TripleCommand });
    }

    /// <summary>
    /// 是否已点赞.
    /// </summary>
    public bool IsLiked
    {
        get => (bool)GetValue(IsLikedProperty);
        set => SetValue(IsLikedProperty, value);
    }

    /// <summary>
    /// 提示文本.
    /// </summary>
    public string Tip
    {
        get => (string)GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    /// <summary>
    /// 命令.
    /// </summary>
    public ICommand Command
    {
        get => (ICommand)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// 一键三连命令.
    /// </summary>
    public ICommand TripleCommand
    {
        get => (ICommand)GetValue(TripleCommandProperty);
        set => SetValue(TripleCommandProperty, value);
    }

    // A rebound connector replaces the command: the tip opened for the old one closes instead of running the new one.
    private static void OnTipCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LikeButton)d)._tripleTip?.Close();

    private void OnLikeButtonClick(object sender, RoutedEventArgs e)
    {
        if (!IsLiked)
        {
            _tripleTip.TryOpen();
        }
    }

    private void OnTripleButtonClick(TeachingTip sender, object args)
        => _tripleTip.ExecuteAndClose(0, null);
}

/// <summary>
/// Per-opening ticket shared by the like/coin/favorite tips. No UI types: the captured owner, media generation
/// and command instances decide whether an action button may still run.
/// </summary>
internal sealed class ActionTipTicketState
{
    private Ticket? _current;

    internal sealed class Ticket(object owner, long generation, ICommand?[] commands)
    {
        internal readonly object Owner = owner;
        internal readonly long Generation = generation;
        internal readonly ICommand?[] Commands = commands;
        internal bool Retired;
    }

    internal Ticket? Current => _current;

    internal Ticket Begin(object owner, long generation, ICommand?[] commands)
    {
        Retire();
        var ticket = new Ticket(owner, generation, (ICommand?[])commands.Clone());
        _current = ticket;
        return ticket;
    }

    internal Ticket? Retire()
    {
        var ticket = _current;
        _current = null;
        if (ticket is not null) ticket.Retired = true;
        return ticket;
    }

    /// <summary>The ticket is still the open one, for the same owner/media, and no bound command was replaced.</summary>
    internal bool IsCurrent(Ticket? ticket, object? owner, long generation, ICommand?[] liveCommands)
    {
        if (ticket is null || ticket.Retired || !ReferenceEquals(ticket, _current)
            || !ReferenceEquals(ticket.Owner, owner) || ticket.Generation != generation
            || liveCommands.Length != ticket.Commands.Length) return false;
        for (var i = 0; i < liveCommands.Length; i++)
        {
            if (!ReferenceEquals(liveCommands[i], ticket.Commands[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// Runs only the command captured at opening. CanExecute is a callback that may rebind/close us,
    /// so currency is checked again after it and immediately before Execute.
    /// </summary>
    internal static bool TryExecute(Ticket ticket, int index, object? parameter, Func<bool> isCurrent)
    {
        if (index < 0 || index >= ticket.Commands.Length || !isCurrent()) return false;
        var command = ticket.Commands[index];
        if (command is null || !command.CanExecute(parameter) || !isCurrent()) return false;
        command.Execute(parameter);
        return true;
    }

    /// <summary>
    /// Identity check for cleanup after an execution: the opening captured before CanExecute/Execute is still the
    /// open one (same unretired ticket and same claim object). A retired or replaced opening is never closed again.
    /// </summary>
    internal static bool IsSameOpening(Ticket? currentTicket, Ticket capturedTicket, object? currentClaim, object? capturedClaim)
        => capturedClaim is not null && !capturedTicket.Retired
            && ReferenceEquals(currentTicket, capturedTicket) && ReferenceEquals(currentClaim, capturedClaim);

    /// <summary>
    /// Action-button flow of one opening: capture its ticket and claim, run the captured command (currency is
    /// re-checked around CanExecute), then close only that opening. Whether the command completes, throws, or
    /// synchronously closes this tip and opens a newer one, the newer opening is never closed here.
    /// </summary>
    internal static void ExecuteAndCloseOpening(Func<Ticket?> currentTicket, Func<object?> currentClaim, int index,
        object? parameter, Func<Ticket, bool> isCurrent, Action close)
    {
        if (currentTicket() is not { } ticket)
        {
            close();
            return;
        }

        var claim = currentClaim();
        try { TryExecute(ticket, index, parameter, () => isCurrent(ticket)); }
        finally
        {
            if (IsSameOpening(currentTicket(), ticket, currentClaim(), claim)) close();
        }
    }
}

/// <summary>
/// One like/coin/favorite TeachingTip shown as a windowed popup over the video. Each opening is bound to the
/// connector/player/media that opened it and holds the player's shared popup claim (suppresses surface
/// play/pause and shortcuts while open). Unload, connector or command rebind, media replacement, presentation
/// change and playback closing close only this tip.
/// </summary>
internal sealed class PlayerActionTip
{
    private readonly FrameworkElement _host;
    private readonly TeachingTip _tip;
    private readonly Func<ICommand?[]> _liveCommands;
    private readonly ActionTipTicketState _tickets = new();
    private IslandPopupClaims _claims = new();
    private IslandPopupClaims.Claim? _claim;
    private PlayerViewModel? _player;
    private PropertyChangedEventHandler? _playerChanged;
    private long _epoch;

    internal PlayerActionTip(FrameworkElement host, TeachingTip tip, Func<ICommand?[]> liveCommands)
    {
        _host = host;
        _tip = tip;
        _liveCommands = liveCommands;
        host.Unloaded += (_, _) => Close();
        host.Loaded += (_, _) =>
        {
            // A tip closed while detached may never raise Closed; its claim already released the flag in Close,
            // so only that stale close barrier is dropped (never an open claim that still holds the flag).
            if (_claim is null && _claims.Get(_tip) is null && _claims.IsClosing(_tip) && !_tip.IsOpen) _claims = new();
        };
    }

    internal bool TryOpen()
    {
        if (_tip.IsOpen || _claim is not null || !_host.IsLoaded) return false;
        // A previous opening's close is still pending (light-dismiss/animation): it keeps its own claim until Closed.
        if (_claims.Get(_tip) is not null || _claims.IsClosing(_tip)) return false;
        if (FindOwner(_host) is not { } found || found.Player.IsPlaybackClosing) return false;
        var (owner, player) = found;

        var ticket = _tickets.Begin(owner, player.MediaGeneration, _liveCommands());
        var claim = _claims.Open(_tip, player, ++_epoch, OnClaimClosed);
        if (claim is null)
        {
            _tickets.Retire();
            return false;
        }

        _claim = claim;
        _player = player;
        _playerChanged = (sender, args) =>
        {
            if (!ReferenceEquals(sender, player)) return;
            if (args.PropertyName is null or "" or nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Presentation)
                or nameof(PlayerViewModel.IsPlaybackClosing) or nameof(PlayerViewModel.Connector)) Close();
        };
        player.PropertyChanged += _playerChanged;

        // Publishing the popup flag can synchronously notify and rebind; open only if still current.
        if (!IsCurrent(ticket))
        {
            Close();
            return false;
        }

        _tip.IsOpen = true;
        return true;
    }

    /// <summary>
    /// Run the captured command at <paramref name="index"/> if the opening is still current, then close only that
    /// opening. CanExecute/Execute may synchronously close it and open a newer one; completion or an exception
    /// from the old invocation must never close the newer opening or release its claim.
    /// </summary>
    internal void ExecuteAndClose(int index, object? parameter)
        => ActionTipTicketState.ExecuteAndCloseOpening(() => _tickets.Current, () => _claim, index, parameter, IsCurrent, Close);

    /// <summary>Close only this tip's current opening; a later opening is never affected by an older close.</summary>
    internal void Close()
    {
        var claim = _claim;
        Detach();
        if (claim is not null) _claims.Cancel();
        else if (_tip.IsOpen) _tip.IsOpen = false;
    }

    private bool IsCurrent(ActionTipTicketState.Ticket ticket)
    {
        var claim = _claim;
        if (claim is null || !_host.IsLoaded || FindOwner(_host) is not { } found
            || !ReferenceEquals(found.Player, _player) || found.Player.IsPlaybackClosing) return false;
        return _claims.IsCurrent(claim, found.Player, _epoch)
            && _tickets.IsCurrent(ticket, found.Owner, found.Player.MediaGeneration, _liveCommands());
    }

    private void OnClaimClosed(IslandPopupClaims.Claim claim)
    {
        // Light-dismiss or user close of exactly this opening; a late Closed of an older opening does not match.
        if (ReferenceEquals(claim, _claim)) Detach();
    }

    private void Detach()
    {
        _claim = null;
        _tickets.Retire();
        if (_player is not null && _playerChanged is not null) _player.PropertyChanged -= _playerChanged;
        _player = null;
        _playerChanged = null;
    }

    /// <summary>The operation section's connector and its player; the tip has no other owner source.</summary>
    internal static (object Owner, PlayerViewModel Player)? FindOwner(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is VideoPlayerControlBase { ViewModel: { } video }) return video.Parent is { } player ? (video, player) : null;
            if (node is PgcPlayerControlBase { ViewModel: { } pgc }) return pgc.Parent is { } pgcPlayer ? (pgc, pgcPlayer) : null;
        }

        return null;
    }
}
