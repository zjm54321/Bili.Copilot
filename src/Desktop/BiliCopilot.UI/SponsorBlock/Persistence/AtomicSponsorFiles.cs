
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

/// <summary>Caller-supplied, app-local root only. No IO at construction. One lock file excludes other
/// cooperating store instances/processes; contention is Busy, never an unbounded retry loop.</summary>
public sealed class AtomicSponsorFiles : IAtomicSponsorFiles
{
    public AtomicSponsorFiles(SponsorPersistenceScope scope) => Scope = scope;
    public SponsorPersistenceScope Scope { get; }

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckAncestors(Scope.DirectoryPath);
        Directory.CreateDirectory(Scope.DirectoryPath);
        CheckAncestors(Scope.DirectoryPath);
        var path = Path.Combine(Scope.DirectoryPath, "store.lock");
        CheckFile(path);
        try
        {
            IDisposable lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult(lease);
        }
        catch (IOException) { throw new SponsorFileException(PersistenceStatus.Busy); }
    }

    public async Task<byte[]?> ReadAsync(SponsorLocalFile file, int maximumBytes, CancellationToken cancellationToken)
    {
        var path = PathFor(file);
        CheckAncestors(Scope.DirectoryPath);
        CheckFile(path);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length > maximumBytes) throw new SponsorFileException(PersistenceStatus.TooLarge);
            // Bound even a file that grows after the length check. Never allocate from an untrusted length.
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                    maximumBytes + 1 - (int)output.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                if (output.Length > maximumBytes) throw new SponsorFileException(PersistenceStatus.TooLarge);
            }
        }
    }

    public async Task ReplaceAsync(SponsorLocalFile file, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var destination = PathFor(file);
        var temporary = Path.Combine(Scope.DirectoryPath, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        CheckAncestors(Scope.DirectoryPath);
        CheckFile(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // This final durable flush is deliberately off the UI synchronization context.
                await Task.Run(() => stream.Flush(flushToDisk: true)).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            CheckAncestors(Scope.DirectoryPath);
            CheckFile(destination);
            // Same-directory rename/replace is the commit point. No delete-then-move or non-atomic fallback.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            // Cancellation arriving after commit does not make the committed update appear to have failed.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string PathFor(SponsorLocalFile file) => Path.Combine(Scope.DirectoryPath, file switch
    {
        SponsorLocalFile.State => "state.json",
        SponsorLocalFile.Identity => "identity.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    });

    private static void CheckFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new SponsorFileException(PersistenceStatus.IoFailure);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void CheckAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SponsorFileException(PersistenceStatus.IoFailure);
    }
}
