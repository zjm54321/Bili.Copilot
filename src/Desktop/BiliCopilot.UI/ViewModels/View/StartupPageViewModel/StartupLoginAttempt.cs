// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// Owns one login's cancellation source until both the login and cancellation callbacks finish.
/// </summary>
internal sealed class StartupLoginAttempt
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly Action<Exception> _reportCancellationError;
    private bool _cancellationStarted;
    private bool _cancellationCompleted;
    private bool _completed;

    public StartupLoginAttempt(Action<Exception> reportCancellationError)
    {
        Token = _source.Token;
        _reportCancellationError = reportCancellationError;
    }

    public CancellationToken Token { get; }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_completed && !_cancellationStarted)
            {
                // CancelAsync marks Token immediately, but callbacks may complete later.
                _cancellationStarted = true;
                _ = CancelCoreAsync();
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            // Do not await callbacks here: a callback may itself be waiting for login to return.
            if (!_cancellationStarted || _cancellationCompleted)
            {
                _source.Dispose();
            }
        }
    }

    private async Task CancelCoreAsync()
    {
        try
        {
            await _source.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _reportCancellationError(ex);
        }
        finally
        {
            lock (_gate)
            {
                _cancellationCompleted = true;
                // Whichever operation finishes second owns disposal; neither waits for the other.
                if (_completed)
                {
                    _source.Dispose();
                }
            }
        }
    }
}
