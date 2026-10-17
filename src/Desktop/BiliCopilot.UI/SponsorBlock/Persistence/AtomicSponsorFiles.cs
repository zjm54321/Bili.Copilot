namespace BiliCopilot.UI.SponsorBlock.Persistence;

public enum SponsorLocalFile { State, Identity }

/// <summary>Trusted test/platform seam. All methods are called under AcquireAsync's scope-wide lease.
/// ReplaceAsync must retain old bytes on failure/cancellation before its atomic commit; after commit it must
/// return success, not report a late cancellation. No method may inspect any other application's data.</summary>
public interface IAtomicSponsorFiles
{
    SponsorPersistenceScope Scope { get; }
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken);
    Task<byte[]?> ReadAsync(SponsorLocalFile file, int maximumBytes, CancellationToken cancellationToken);
    Task ReplaceAsync(SponsorLocalFile file, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

public sealed class SponsorFileException : IOException
{
    public SponsorFileException() : this(PersistenceStatus.IoFailure) { }
    public SponsorFileException(string? message) : base(message) { }
    public SponsorFileException(string? message, Exception? innerException) : base(message, innerException) { }
    public SponsorFileException(string? message, int hresult) : base(message, hresult) { }
    public SponsorFileException(PersistenceStatus status) : base("Sponsor local storage unavailable") => Status = status;
    public PersistenceStatus Status { get; } = PersistenceStatus.IoFailure;
}
