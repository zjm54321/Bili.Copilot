// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>A published message request slot with immutable data/account identity.</summary>
internal sealed class MessageRequestOwner(long generation, long accountGeneration, string? accountId, Task previous, bool refresh = false)
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> Task => _completion.Task;
    internal Task Previous { get; } = previous;
    internal bool Refresh { get; } = refresh;
    internal bool IsCurrent(MessageRequestOwner? slot, long currentGeneration, long currentAccountGeneration, string? currentAccountId)
        => ReferenceEquals(slot, this) && generation == currentGeneration && accountGeneration == currentAccountGeneration
            && accountId is not null && accountId == currentAccountId;
    internal void Complete(bool success) => _completion.TrySetResult(success);
}
