// Copyright (c) Bili Copilot. All rights reserved.


namespace BiliCopilot.UI.SponsorBlock.Platform;

/// <summary>Byte transport bounds only; the coordinator owns SponsorBlock JSON policy.</summary>
internal static class SponsorBackupStreamIO
{
    // Schema 2 total backup bound; includes the complete opaque JSON document.
    internal const int MaximumBackupBytes = 4 * 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>
    /// Reads through EOF with a limit + one-byte overflow probe. Never trusts Length,
    /// Position, CanSeek, StorageFile metadata, or a size sampled before the read.
    /// A nonterminating/growing source cannot make the retained payload exceed 4 MiB.
    /// Owns no cancellation waiter and leaves stream disposal to the caller.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using var result = new MemoryStream();
        var buffer = new byte[ReadBufferBytes];
        var total = 0;
        while (true)
        {
            var requested = Math.Min(buffer.Length, MaximumBackupBytes - total + 1);
            var readTask = stream.ReadAsync(buffer.AsMemory(0, requested));
            var read = await readTask.ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToArray();
            }

            if (read > MaximumBackupBytes - total)
            {
                throw new InvalidDataException("The backup exceeds the 4 MiB limit.");
            }

            await result.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            total += read;
        }
    }
}
