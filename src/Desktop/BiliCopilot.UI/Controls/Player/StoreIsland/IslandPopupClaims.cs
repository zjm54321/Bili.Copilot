// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.CompilerServices;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>Local transport popup leases. All calls stay on the owning UI thread.</summary>
internal sealed class IslandPopupClaims
{
    internal sealed record Claim(object Popup, PlayerViewModel ViewModel, long Generation, long Epoch)
    {
        internal DependencyObject? ContentRoot { get; init; }
        internal Func<bool>? IsOwnerCurrent { get; init; }
        internal bool HasOpened { get; set; }
        internal bool FlagAcquired { get; set; }
        internal EventHandler<object>? OpenedHandler { get; set; }
        internal EventHandler<object>? ClosedHandler { get; set; }
    }

    private sealed class FlagState
    {
        public int Count;
        public bool Preserve;
    }

    // Both bars and their slots share only the existing VM flag, not input admission.
    private static readonly ConditionalWeakTable<PlayerViewModel, FlagState> Flags = new();
    private readonly Dictionary<object, Claim> _open = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Claim> _closing = new(ReferenceEqualityComparer.Instance);
    private bool _canceling;

    internal int Count => _open.Count;
    internal bool IsClosing(object popup) => _closing.ContainsKey(popup);
    internal Claim? Get(object popup) => _open.GetValueOrDefault(popup);

    internal bool IsCurrent(Claim? claim, PlayerViewModel? vm, long epoch)
        => claim is not null && vm is not null && ReferenceEquals(Get(claim.Popup), claim)
            && ReferenceEquals(claim.ViewModel, vm) && claim.Generation == vm.MediaGeneration
            && claim.Epoch == epoch;

    internal bool IsCurrentFlyout(Claim? claim, Flyout popup, PlayerViewModel? vm, long epoch, bool requireOpened)
        => IsCurrent(claim, vm, epoch) && ReferenceEquals(claim!.Popup, popup)
            && ReferenceEquals(claim.ContentRoot, popup.Content) && claim.IsOwnerCurrent?.Invoke() == true
            && (!requireOpened || (claim.HasOpened && popup.IsOpen));

    internal Claim? BeginOpening(Flyout popup, DependencyObject contentRoot, PlayerViewModel vm, long epoch,
        Func<bool> isOwnerCurrent, Action<Claim>? opened = null, Action<Claim>? closed = null)
    {
        if (_canceling || IsClosing(popup))
        {
            Hide(popup);
            return null;
        }

        if (Get(popup) is { } existing)
        {
            if (IsCurrentFlyout(existing, popup, vm, epoch, requireOpened: false)) return existing;
            RetireFlyout(existing);
            return null;
        }

        var claim = new Claim(popup, vm, vm.MediaGeneration, epoch)
        {
            ContentRoot = contentRoot,
            IsOwnerCurrent = isOwnerCurrent,
        };
        // Build both per-opening delegates and metadata before publishing the flag:
        // IsPopupVisible can synchronously cancel, replace media, or open another popup.
        claim.OpenedHandler = (_, _) =>
        {
            if (!IsCurrentFlyout(claim, popup, vm, epoch, requireOpened: false) || !popup.IsOpen)
            {
                RetireFlyout(claim);
                return;
            }

            claim.HasOpened = true;
            opened?.Invoke(claim);
        };
        claim.ClosedHandler = (_, _) =>
        {
            DetachFlyoutHandlers(claim);
            claim.HasOpened = false;
            CompleteClose(claim, closed);
        };

        try
        {
            popup.Opened += claim.OpenedHandler;
            popup.Closed += claim.ClosedHandler;
            _open.Add(popup, claim);
            AcquireFlag(claim);
            if (IsCurrentFlyout(claim, popup, vm, epoch, requireOpened: false)) return claim;
            RetireFlyout(claim);
            return null;
        }
        catch
        {
            // No partially registered ticket is returned as a successful opening.
            if (ReferenceEquals(Get(popup), claim))
            {
                _open.Remove(popup);
                _closing[popup] = claim;
            }
            else if (Get(popup) is null && !IsClosing(popup)) _closing[popup] = claim;
            try { ReleaseFlag(claim); }
            finally
            {
                try { DetachFlyoutHandlers(claim); }
                finally
                {
                    if (Get(popup) is null && ReferenceEquals(_closing.GetValueOrDefault(popup), claim)) Hide(popup);
                }
            }

            throw;
        }
    }

    internal void RetireFlyout(Claim claim)
    {
        if (ReferenceEquals(Get(claim.Popup), claim))
        {
            _open.Remove(claim.Popup);
            _closing[claim.Popup] = claim;
            claim.HasOpened = false;
            ReleaseFlag(claim);
        }

        // An old invocation-list callback must never Hide a later ticket.
        if (Get(claim.Popup) is null && ReferenceEquals(_closing.GetValueOrDefault(claim.Popup), claim)) Hide(claim.Popup);
    }

    private static void DetachFlyoutHandlers(Claim claim)
    {
        if (claim.Popup is not Flyout popup) return;
        try { if (claim.OpenedHandler is { } opened) popup.Opened -= opened; }
        finally { if (claim.ClosedHandler is { } closed) popup.Closed -= closed; }
    }

    internal Claim? Open(object popup, PlayerViewModel vm, long epoch, Action<Claim>? closed = null)
    {
        if (_canceling || IsClosing(popup))
        {
            Hide(popup);
            return null;
        }

        if (Get(popup) is { } existing) return existing;
        var claim = new Claim(popup, vm, vm.MediaGeneration, epoch);
        _open.Add(popup, claim);
        // Capture this exact opening, not the control's current owner at Closed time.
        if (popup is FlyoutBase flyout)
        {
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                flyout.Closed -= handler;
                CompleteClose(claim, closed);
            };
            flyout.Closed += handler;
        }
        else if (popup is TeachingTip tip)
        {
            TypedEventHandler<TeachingTip, TeachingTipClosedEventArgs>? handler = null;
            handler = (_, _) =>
            {
                tip.Closed -= handler;
                CompleteClose(claim, closed);
            };
            tip.Closed += handler;
        }

        AcquireFlag(claim);

        return ReferenceEquals(Get(popup), claim) ? claim : null;
    }

    internal void Cancel(Action? beforeHide = null)
    {
        if (_canceling) return;
        _canceling = true;
        try
        {
            var claims = _open.Values.ToArray();
            _open.Clear();
            foreach (var claim in claims)
            {
                claim.HasOpened = false;
                _closing[claim.Popup] = claim;
            }
            // Invalidate every lease and release flags BEFORE Hide can call back/reenter.
            foreach (var claim in claims) ReleaseFlag(claim);
            beforeHide?.Invoke();
            foreach (var claim in claims)
            {
                if (Get(claim.Popup) is null && ReferenceEquals(_closing.GetValueOrDefault(claim.Popup), claim)) Hide(claim.Popup);
            }
        }
        finally { _canceling = false; }
    }

    internal static void Hide(object popup)
    {
        if (popup is FlyoutBase flyout) flyout.Hide();
        else if (popup is TeachingTip tip) tip.IsOpen = false;
    }

    private void CompleteClose(Claim claim, Action<Claim>? closed)
    {
        if (ReferenceEquals(_closing.GetValueOrDefault(claim.Popup), claim))
        {
            _closing.Remove(claim.Popup);
            return;
        }

        if (!ReferenceEquals(Get(claim.Popup), claim)) return;
        _open.Remove(claim.Popup);
        ReleaseFlag(claim);
        // Releasing IsPopupVisible can synchronously open the same object again.
        if (claim.ContentRoot is not null || Get(claim.Popup) is null) closed?.Invoke(claim);
    }

    private static void ReleaseFlag(Claim claim)
    {
        if (!claim.FlagAcquired) return;
        claim.FlagAcquired = false;
        if (!Flags.TryGetValue(claim.ViewModel, out var flag) || --flag.Count != 0) return;
        var preserve = flag.Preserve;
        Flags.Remove(claim.ViewModel);
        if (!preserve) claim.ViewModel.IsPopupVisible = false;
    }

    private static void AcquireFlag(Claim claim)
    {
        var vm = claim.ViewModel;
        var flag = Flags.GetValue(vm, static _ => new FlagState());
        claim.FlagAcquired = true;
        if (flag.Count++ == 0)
        {
            flag.Preserve = vm.IsPopupVisible;
            vm.IsPopupVisible = true;
        }
    }
}
