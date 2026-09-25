using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ServerManager.Connect.Core.Identity;

/// <summary>
/// Per-user DPAPI for byte payloads, mirroring
/// <c>ServerManager.Infrastructure.Security.WindowsDpapiSecretStore</c>. It is duplicated rather
/// than referenced because this library must stand alone in the friend app.
/// It differs from that store in three deliberate ways. The entropy is specific to Connect, so a
/// blob from one product cannot be fed to the other. Every unmanaged copy of plaintext is zeroed
/// before it is freed. And UI is forbidden, so a service account can never block on a prompt.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CurrentUserDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly byte[] Entropy = "1Salem.Connect.Identity.v1"u8.ToArray();

    public static byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var input = CreateBlob(plaintext);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "1Salem Connect identity",
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI protection failed.");
            }

            try
            {
                var protectedBytes = new byte[output.Length];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Length);
                return protectedBytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            ZeroAndFree(input);
            ZeroAndFree(entropy);
        }
    }

    /// <summary>
    /// Throws <see cref="CryptographicException"/> when the blob cannot be opened by this user
    /// on this machine (another profile, another PC, or a damaged file).
    /// </summary>
    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        var input = CreateBlob(protectedData);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    nint.Zero,
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new CryptographicException(
                    "DPAPI could not open the identity for this Windows user.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var plaintext = new byte[output.Length];
                Marshal.Copy(output.Data, plaintext, 0, output.Length);
                return plaintext;
            }
            finally
            {
                Zero(output);
                LocalFree(output.Data);
            }
        }
        finally
        {
            ZeroAndFree(input);
            ZeroAndFree(entropy);
        }
    }

    private static DataBlob CreateBlob(ReadOnlySpan<byte> data)
    {
        var blob = new DataBlob
        {
            Length = data.Length,
            Data = Marshal.AllocHGlobal(Math.Max(data.Length, 1))
        };
        var copy = data.ToArray();
        try
        {
            Marshal.Copy(copy, 0, blob.Data, copy.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }

        return blob;
    }

    private static void ZeroAndFree(DataBlob blob)
    {
        if (blob.Data != nint.Zero)
        {
            Zero(blob);
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void Zero(DataBlob blob)
    {
        if (blob.Data != nint.Zero && blob.Length > 0)
        {
            Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        nint description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public nint Data;
    }
}
