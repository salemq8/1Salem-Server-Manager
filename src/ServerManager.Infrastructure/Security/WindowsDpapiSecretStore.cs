using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Security;

public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private const uint CryptProtectLocalMachine = 0x4;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("1Salem.ServerManager.v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureWindows();
        var inputBytes = Encoding.UTF8.GetBytes(plaintext);
        var input = CreateBlob(inputBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "1Salem Server Manager",
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectLocalMachine,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI protection failed.");
            }

            try
            {
                var protectedBytes = new byte[output.Length];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Length);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        EnsureWindows();
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var input = CreateBlob(protectedBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    nint.Zero,
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    0,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI unprotection failed.");
            }

            try
            {
                var plaintextBytes = new byte[output.Length];
                Marshal.Copy(output.Data, plaintextBytes, 0, output.Length);
                try
                {
                    return Encoding.UTF8.GetString(plaintextBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintextBytes);
                }
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            Length = data.Length,
            Data = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.Data, data.Length);
        return blob;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != nint.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage requires Windows.");
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
