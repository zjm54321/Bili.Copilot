namespace BiliCopilot.UI.Hosting;

/// <summary>UI-thread-owned main-root scopes, independent of any replaceable player host.</summary>
internal sealed class RenderingOcclusionState
{
    private int _count;
    private bool _closed;
    private readonly Action _changed;

    internal RenderingOcclusionState(Action changed) => _changed = changed;
    internal bool IsSuspended => _closed || _count > 0;

    internal IDisposable Suspend()
    {
        ++_count;
        if (!_closed) _changed();
        return new Scope(this);
    }

    internal void Close()
    {
        _closed = true;
        _changed();
    }

    private sealed class Scope(RenderingOcclusionState owner) : IDisposable
    {
        private RenderingOcclusionState? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            --owner._count;
            if (!owner._closed) owner._changed();
        }
    }
}
