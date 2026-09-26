using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Connect.Core.Identity;
using ServerManager.Infrastructure.Security;

namespace ServerManager.Infrastructure.Connect;

/// <summary>DPAPI-protected persistence for the owner's Tailscale OAuth client.</summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectOAuthCredentialStore
{
    private const int MaxFileBytes = 16 * 1024;
    private readonly string _path;
    private readonly WindowsDpapiSecretStore _dpapi = new("1Salem.Connect.OAuthClient.v1");

    public ConnectOAuthCredentialStore(ConnectOwnerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.OAuthCredentialFile;
    }

    public bool Exists => File.Exists(_path);

    public void Save(TailscaleOAuthCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var directory = Path.GetDirectoryName(_path)!;
        ConnectProtectedDirectory.Ensure(directory);
        var json = JsonSerializer.Serialize(new StoredCredential(1, credential.ClientId, credential.ClientSecret));
        string protectedValue;
        try
        {
            protectedValue = _dpapi.Protect(json);
        }
        finally
        {
            // Strings cannot be wiped; do not retain another copy beyond this call.
            json = string.Empty;
        }

        AtomicWrite(_path, Encoding.ASCII.GetBytes(protectedValue));
    }

    public TailscaleOAuthCredential? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        ConnectProtectedDirectory.Verify(Path.GetDirectoryName(_path)!);
        byte[] content = [];
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is 0 or > MaxFileBytes)
            {
                throw new ConnectCredentialUnavailableException();
            }

            content = new byte[stream.Length];
            stream.ReadExactly(content);

            var protectedValue = Encoding.ASCII.GetString(content);
            var json = _dpapi.Unprotect(protectedValue);
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            if (stored is not { Version: 1 })
            {
                throw new ConnectCredentialUnavailableException();
            }

            return new TailscaleOAuthCredential(stored.ClientId, stored.ClientSecret);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ConnectCredentialUnavailableException(exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            ConnectProtectedDirectory.Verify(Path.GetDirectoryName(_path)!);
            File.Delete(_path);
        }
    }

    internal static void AtomicWrite(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private sealed record StoredCredential(int Version, string ClientId, string ClientSecret);
}

public sealed class ConnectCredentialUnavailableException : Exception
{
    public ConnectCredentialUnavailableException()
        : base("The saved 1Salem Connect OAuth credential cannot be opened on this PC. Set it up again to continue.")
    {
    }

    public ConnectCredentialUnavailableException(Exception innerException)
        : base("The saved 1Salem Connect OAuth credential cannot be opened on this PC. Set it up again to continue.", innerException)
    {
    }
}
