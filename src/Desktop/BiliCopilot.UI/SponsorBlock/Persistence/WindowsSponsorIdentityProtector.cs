using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819", Justification = "This binary protection result intentionally transfers the owned DPAPI byte buffer to its caller.")]
public sealed record IdentityProtectionResult(PersistenceStatus Status, byte[]? Bytes = null);

/// <summary>Trusted injected seam. Production must use OS-backed protection, never a plaintext fallback.
/// Returned buffers belong to the caller and must be zeroed after use; implementations must not retain input.</summary>
public interface ISponsorIdentityProtector
{
    bool IsSupported { get; }
    Task<IdentityProtectionResult> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken);
    Task<IdentityProtectionResult> UnprotectAsync(ReadOnlyMemory<byte> ciphertext, string purpose, CancellationToken cancellationToken);
}

/// <summary>Windows current-user DPAPI with UI forbidden, scope/root-bound optional entropy, and no machine
/// scope, default credentials, WinRT/package dependency, or plaintext fallback. Native calls occur only on use.</summary>
public sealed class WindowsSponsorIdentityProtector : ISponsorIdentityProtector
{
    public bool IsSupported => OperatingSystem.IsWindows();
    public Task<IdentityProtectionResult> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken) =>
        TransformAsync(plaintext, purpose, decrypt: false, cancellationToken);
    public Task<IdentityProtectionResult> UnprotectAsync(ReadOnlyMemory<byte> ciphertext, string purpose, CancellationToken cancellationToken) =>
        TransformAsync(ciphertext, purpose, decrypt: true, cancellationToken);

    private Task<IdentityProtectionResult> TransformAsync(ReadOnlyMemory<byte> input, string purpose, bool decrypt, CancellationToken token)
    {
        if (!IsSupported) return Task.FromResult(new IdentityProtectionResult(PersistenceStatus.ProtectionUnavailable));
        if (input.Length == 0 || input.Length > PersistenceJson.MaximumIdentityBytes)
            return Task.FromResult(new IdentityProtectionResult(PersistenceStatus.InvalidData));
        var copy = input.ToArray();
        // No cancellation token on Task.Run: even pre-cancelled work must run the zeroing finally block.
        return Task.Run(() =>
        {
            Blob source = default, entropy = default, output = default;
            byte[]? result = null;
            try
            {
                token.ThrowIfCancellationRequested();
                source = Allocate(copy);
                entropy = Allocate(Encoding.UTF8.GetBytes(purpose));
                const int uiForbidden = 1;
                var ok = decrypt
                    ? CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output)
                    : CryptProtectData(ref source, null, ref entropy, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output);
                if (!ok) return new IdentityProtectionResult(decrypt ? PersistenceStatus.DecryptionFailed : PersistenceStatus.ProtectionUnavailable);
                if (output.Size <= 0 || output.Size > PersistenceJson.MaximumIdentityBytes)
                    return new IdentityProtectionResult(PersistenceStatus.InvalidData);
                result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                token.ThrowIfCancellationRequested();
                var response = new IdentityProtectionResult(PersistenceStatus.Success, result);
                result = null; // Ownership transfers to the caller.
                return response;
            }
            catch (OperationCanceledException) { return new IdentityProtectionResult(PersistenceStatus.Cancelled); }
            catch (DllNotFoundException) { return new IdentityProtectionResult(PersistenceStatus.ProtectionUnavailable); }
            catch (EntryPointNotFoundException) { return new IdentityProtectionResult(PersistenceStatus.ProtectionUnavailable); }
            catch (BadImageFormatException) { return new IdentityProtectionResult(PersistenceStatus.ProtectionUnavailable); }
            finally
            {
                CryptographicOperations.ZeroMemory(copy);
                if (result is not null) CryptographicOperations.ZeroMemory(result);
                Free(source, local: false); Free(entropy, local: false); Free(output, local: true);
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    private static Blob Allocate(byte[] bytes)
    {
        var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }
    private static void Free(Blob blob, bool local)
    {
        if (blob.Data == IntPtr.Zero) return;
        for (var offset = 0; offset < blob.Size; offset++) Marshal.WriteByte(blob.Data, offset, 0);
        if (local) _ = LocalFree(blob.Data); else Marshal.FreeHGlobal(blob.Data);
    }

    [DllImport("Crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("Crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("Kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
