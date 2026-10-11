
namespace BiliCopilot.UI.ViewModels.Core;

// All requests originate on the player dispatcher. The gate also serializes native
// writes across awaits; a media boundary must await CancelAsync before reuse.
internal sealed class TemporaryPlaybackRateState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private int _owners;
    private long _generation;

    public bool IsHeld => _owners != 0;
    public bool IsActive => _session is not null;

    public Task HoldAsync(int owner, double original, double temporary,
        Func<double, Task> write, Func<bool> isCurrent)
    {
        if (_session is not null && _session.Generation != _generation)
        {
            // A failed boundary restore must be retried, not adopted by a new hold.
            return ReconcileAsync();
        }

        _owners |= owner;
        _session ??= new Session(++_generation, original, temporary, write, isCurrent);
        return ReconcileAsync();
    }

    public Task ReleaseAsync(int owner)
    {
        _owners &= ~owner;
        return ReconcileAsync();
    }

    public Task CancelAsync()
    {
        ++_generation;
        _owners = 0;
        return ReconcileAsync();
    }

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = _session;
            if (session is null)
            {
                return;
            }

            if (!session.IsCurrent())
            {
                _session = null;
                _owners = 0;
                return;
            }

            if (_owners != 0 && session.Generation == _generation && !session.Applied)
            {
                // Even a failed native write may have taken effect: restore on cancel.
                session.Applied = true;
                await session.Write(session.Temporary);
            }

            // Release/cancel can arrive while the begin write is in flight.
            // Never consult the asynchronously updated Player.PlaybackRate to restore.
            if ((_owners == 0 || session.Generation != _generation) && session.Applied)
            {
                if (session.IsCurrent())
                {
                    await session.Write(session.Original);
                }

                session.Applied = false;
            }

            if (_owners == 0 || session.Generation != _generation)
            {
                _session = null;
                _owners = 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Session(long generation, double original, double temporary,
        Func<double, Task> write, Func<bool> isCurrent)
    {
        public long Generation { get; } = generation;
        public double Original { get; } = original;
        public double Temporary { get; } = temporary;
        public Func<double, Task> Write { get; } = write;
        public Func<bool> IsCurrent { get; } = isCurrent;
        public bool Applied { get; set; }
    }
}
