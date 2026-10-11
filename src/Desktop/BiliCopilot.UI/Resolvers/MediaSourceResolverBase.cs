// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal abstract class MediaSourceResolverBase : IMpvMediaSourceResolver
{
    protected readonly object InitializationSync = new();
    private CancellationToken _initializationCancellation;
    private long _initializationGeneration;

    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// Set before initializing/replacing media. The caller owns, cancels and drains its CTS.
    /// This resolver never disposes the caller's cancellation source.
    /// </summary>
    public void SetInitializationCancellation(CancellationToken cancellationToken)
    {
        lock (InitializationSync)
        {
            _initializationCancellation = cancellationToken;
            InvalidateInitialization();
        }
    }

    protected void InvalidateInitialization()
    {
        // Called while holding InitializationSync, together with the derived media reset.
        _initializationGeneration++;
        ResetInitialization();
    }

    protected abstract void ResetInitialization();

    protected (long Generation, CancellationToken CancellationToken) CaptureInitialization()
    {
        lock (InitializationSync)
        {
            _initializationCancellation.ThrowIfCancellationRequested();
            return (_initializationGeneration, _initializationCancellation);
        }
    }

    protected void ThrowIfInitializationInvalid((long Generation, CancellationToken CancellationToken) request)
    {
        // Check and commit under the same InitializationSync lock.
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Generation != _initializationGeneration)
        {
            throw new OperationCanceledException("The media source request was superseded.", request.CancellationToken);
        }
    }

    public abstract Task<MpvMediaSource> GetSourceAsync();

    public abstract IMpvMediaSourceResolver Clone();
}
