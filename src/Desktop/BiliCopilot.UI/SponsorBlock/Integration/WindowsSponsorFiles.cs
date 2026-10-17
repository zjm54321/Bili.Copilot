using System.Security.Cryptography;
using BiliCopilot.UI.SponsorBlock.Persistence;

namespace BiliCopilot.UI.SponsorBlock.Integration;

/// <summary>One existing store's platform seam. Load never creates a directory or lock file. If no
/// lock exists, reads are optimistic; an explicit commit acquires the real exclusive file lease and
/// validates every byte revision read in that transaction before replacing anything.</summary>
internal sealed class WindowsSponsorFiles(Func<SponsorPersistenceScope> resolve) : IAtomicSponsorFiles
{
    private SponsorPersistenceScope? scope;
    private Lease? current;
    public SponsorPersistenceScope Scope => scope ??= resolve();

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current is not null) throw new SponsorFileException(PersistenceStatus.Busy);
        CheckDirectory();
        var path = Path.Combine(Scope.DirectoryPath, "store.lock");
        CheckFile(path);
        FileStream? handle = null;
        try { handle = OpenLease(path, FileMode.Open); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        current = new Lease(this, handle);
        return Task.FromResult<IDisposable>(current);
    }

    public async Task<byte[]?> ReadAsync(SponsorLocalFile file, int maximumBytes, CancellationToken token)
    {
        var lease = RequireLease();
        var bytes = await ReadBytesAsync(file, maximumBytes, token).ConfigureAwait(false);
        var revision = Revision(bytes);
        if (lease.Reads.TryGetValue(file, out var earlier) && earlier.Revision != revision)
            throw new SponsorFileException(PersistenceStatus.Conflict);
        lease.Reads[file] = new ReadVersion(revision, maximumBytes);
        return bytes;
    }

    public async Task ReplaceAsync(SponsorLocalFile file, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var lease = RequireLease();
        if (!lease.Reads.ContainsKey(file)) throw new SponsorFileException(PersistenceStatus.InvalidData);
        if (bytes.Length > PersistenceJson.MaximumDocumentBytes) throw new SponsorFileException(PersistenceStatus.TooLarge);
        // Only this explicit mutation path may create our own directory/lock/temp/destination.
        token.ThrowIfCancellationRequested();
        CheckDirectory();
        Directory.CreateDirectory(Scope.DirectoryPath);
        CheckDirectory();
        if (lease.Handle is null)
        {
            var lockPath = Path.Combine(Scope.DirectoryPath, "store.lock");
            CheckFile(lockPath);
            lease.Handle = OpenLease(lockPath, FileMode.OpenOrCreate);
        }
        foreach (var read in lease.Reads)
        {
            var now = await ReadBytesAsync(read.Key, read.Value.MaximumBytes, token).ConfigureAwait(false);
            if (Revision(now) != read.Value.Revision) throw new SponsorFileException(PersistenceStatus.Conflict);
        }

        var destination = PathFor(file);
        var temporary = Path.Combine(Scope.DirectoryPath, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                // This final durable flush is deliberately off the UI synchronization context.
                await Task.Run(() => stream.Flush(flushToDisk: true)).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            CheckDirectory();
            CheckFile(destination);
            // Same-directory atomic commit only. Never delete old bytes or fall back to copy/overwrite.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            // No cancellation/fallible work may relabel the committed write as failed.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<byte[]?> ReadBytesAsync(SponsorLocalFile file, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes < 1 || maximumBytes > PersistenceJson.MaximumDocumentBytes)
            throw new SponsorFileException(PersistenceStatus.InvalidData);
        token.ThrowIfCancellationRequested();
        CheckDirectory();
        var path = PathFor(file);
        CheckFile(path);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        using (var output = new MemoryStream())
        {
            if (stream.Length > maximumBytes) throw new SponsorFileException(PersistenceStatus.TooLarge);
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), token).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                if (output.Length > maximumBytes) throw new SponsorFileException(PersistenceStatus.TooLarge);
            }
        }
    }

    private static FileStream OpenLease(string path, FileMode mode)
    {
        try { return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new SponsorFileException(PersistenceStatus.Busy); }
    }

    private Lease RequireLease() => current ?? throw new SponsorFileException(PersistenceStatus.InvalidData);
    private static string? Revision(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private string PathFor(SponsorLocalFile file) => Path.Combine(Scope.DirectoryPath, file switch
    {
        SponsorLocalFile.State => "state.json",
        SponsorLocalFile.Identity => "identity.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    });

    private void CheckDirectory()
    {
        // Metadata only, bounded to this actual package LocalState root; no drive/global audit.
        for (var directory = new DirectoryInfo(Scope.DirectoryPath); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SponsorFileException(PersistenceStatus.IoFailure);
            if (string.Equals(directory.FullName, Scope.AppLocalRoot, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new SponsorFileException(PersistenceStatus.IoFailure);
    }

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

    private sealed record ReadVersion(string? Revision, int MaximumBytes);
    private sealed class Lease(WindowsSponsorFiles owner, FileStream? handle) : IDisposable
    {
        internal FileStream? Handle = handle;
        internal readonly Dictionary<SponsorLocalFile, ReadVersion> Reads = [];
        public void Dispose()
        {
            try { Handle?.Dispose(); }
            finally { if (ReferenceEquals(owner.current, this)) owner.current = null; }
        }
    }
}
