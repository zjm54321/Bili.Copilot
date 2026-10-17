// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using Richasy.BiliKernel.Models.Media;
using System.Text.Json;

namespace BiliCopilot.UI.Services.Notifications;

/// <summary>One startup mailbox; no persistence, content deduplication, timers or player ownership.</summary>
internal sealed class NotificationActivationGate
{
    internal const int MaximumArgumentCharacters = 16 * 1024;
    private readonly object _sync = new();
    private MediaSnapshot? _pending;
    private bool _primary;
    private bool _rejected;
    private string? _accountId;
    private long _generation;
    private bool _profileReady;
    private bool _mainReady;
    private object? _root;
    private bool _navigationComplete;

    internal void SelectPrimary(string? tokenAccountId, string? profileAccountId, long generation)
    {
        lock (_sync)
        {
            if (_primary || _rejected) return;
            if (string.IsNullOrEmpty(tokenAccountId)
                || profileAccountId is not null && profileAccountId != tokenAccountId)
            {
                RejectCore();
                return;
            }
            _primary = true;
            _accountId = tokenAccountId;
            _generation = generation;
            _profileReady = profileAccountId is not null;
        }
    }

    internal void Reject()
    {
        lock (_sync) RejectCore();
    }

    private void RejectCore()
    {
        _rejected = true;
        _pending = null;
        _root = null;
        _mainReady = false;
    }

    internal void UpdateOwnership(string? tokenAccountId, string? profileAccountId, long generation,
        bool accountCancelled, bool initialLoading, bool currentMainRoot)
    {
        lock (_sync)
        {
            if (!_primary || _rejected) return;
            if (tokenAccountId != _accountId || accountCancelled)
            {
                RejectCore();
                return;
            }
            if (generation != _generation)
            {
                // The first null -> matching profile publication itself increments AccountGeneration.
                // Any other boundary (including sign-out while still null) discards the mailbox.
                if (!_profileReady && generation == _generation + 1 && profileAccountId == _accountId)
                {
                    _profileReady = true;
                    _generation = generation;
                }
                else
                {
                    RejectCore();
                    return;
                }
            }
            if (profileAccountId is not null && profileAccountId != _accountId
                || _profileReady && profileAccountId != _accountId)
            {
                RejectCore();
                return;
            }
            _mainReady = _profileReady && !initialLoading && currentMainRoot;
        }
    }

    internal void BeginRoot(object owner)
    {
        lock (_sync)
        {
            if (!_primary || _rejected || ReferenceEquals(_root, owner)) return;
            if (_root is not null) _pending = null;
            _root = owner;
            _navigationComplete = _mainReady = false;
        }
    }

    internal void CompleteNavigation(object owner)
    {
        lock (_sync)
            if (ReferenceEquals(_root, owner)) _navigationComplete = true;
    }

    internal void RetireRoot(object owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_root, owner)) return;
            _root = null;
            _pending = null;
            _navigationComplete = _mainReady = false;
        }
    }

    internal NotificationDispatch? Capture(string? page, string? arguments, bool initial = false)
    {
        var target = ParseTarget(page, arguments);
        if (target is null) return null;
        lock (_sync)
        {
            if (_rejected) return null;
            if (CanDispatch) return new(target, _root!, _generation);
            // Any warm callback is later than the process's initial cold input, even if it
            // reaches this mailbox before OnLaunched reads that initial activation.
            if (!initial || _pending is null) _pending = target;
            return null;
        }
    }

    internal NotificationDispatch? TakePending()
    {
        lock (_sync)
        {
            if (!CanDispatch || _pending is not { } target) return null;
            _pending = null;
            return new(target, _root!, _generation);
        }
    }

    internal bool TryConsume(NotificationDispatch request)
    {
        lock (_sync)
        {
            if (request.Consumed) return false;
            request.Consumed = true; // Claim once even if an account/root change made it stale.
            return CanDispatch && ReferenceEquals(request.Root, _root) && request.Generation == _generation;
        }
    }

    private bool CanDispatch => _primary && !_rejected && _mainReady && _navigationComplete && _root is not null;

    private static MediaSnapshot? ParseTarget(string? page, string? arguments)
    {
        if (page is not ("video" or "episode") || string.IsNullOrWhiteSpace(arguments)
            || arguments.Length > MaximumArgumentCharacters) return null;
        try
        {
            var identifier = JsonSerializer.Deserialize(arguments, GlobalSerializeContext.Default.MediaIdentifier);
            if (string.IsNullOrWhiteSpace(identifier.Id) || identifier.Id.Length > 128
                || identifier.Id == "0" || !identifier.Id.All(char.IsAsciiLetterOrDigit)) return null;
            return page == "video"
                ? new MediaSnapshot(new VideoInformation(identifier, default))
                : new MediaSnapshot(default, new EpisodeInformation(identifier));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return null; // No exception text, payload, identifier, title, URL or account data is logged.
        }
    }
}

internal sealed class NotificationDispatch(MediaSnapshot target, object root, long generation)
{
    internal MediaSnapshot Target { get; } = target;
    internal object Root { get; } = root;
    internal long Generation { get; } = generation;
    internal bool Consumed { get; set; }
}
