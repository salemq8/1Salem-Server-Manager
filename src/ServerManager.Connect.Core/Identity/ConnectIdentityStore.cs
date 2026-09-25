using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Identity;

/// <summary>
/// Persists one <see cref="ConnectIdentity"/> as <c>identity.v1.json</c>. The private key is
/// stored as PKCS#8, DPAPI-protected for the current Windows user with Connect-specific
/// entropy. The directory ACL admits only the current user and SYSTEM (contract §5).
/// Before a file is loaded, and before one is created, the directory must be owned by this
/// account and carry exactly that ACL, and a loaded file must be owned by this account too
/// (<see cref="CurrentUserOnlyAccess"/>). The key blob cannot vouch for itself: DPAPI also opens
/// machine-scope blobs, which every account on the PC can make, so a file another account planted
/// would otherwise load as our identity with a key that account holds.
/// A file that exists but cannot be opened or trusted (another PC, another profile, another
/// owner, or damage) is never replaced or deleted. It raises
/// <see cref="ConnectIdentityUnavailableException"/>, and the user re-pairs (§16). Overwriting it
/// silently would orphan every membership bound to the old key without saying why.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectIdentityStore
{
    public const string FileName = "identity.v1.json";

    private const int FormatVersion = 1;
    private const int MaxFileBytes = 16 * 1024;

    private readonly ConnectIdentityKind _kind;
    private readonly IIdentityAccessInspector _inspector;

    public ConnectIdentityStore(string directory, ConnectIdentityKind kind)
        : this(directory, kind, FileSystemIdentityAccessInspector.Instance)
    {
    }

    internal ConnectIdentityStore(string directory, ConnectIdentityKind kind, IIdentityAccessInspector inspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory);
        _kind = kind;
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public string DirectoryPath { get; }

    public string IdentityPath => Path.Combine(DirectoryPath, FileName);

    /// <summary>%LOCALAPPDATA%\1Salem Connect\identity, the friend app's device identity folder.</summary>
    public static string DefaultDeviceDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "1Salem Connect",
            "identity");

    /// <summary>Returns null when no identity has been created yet.</summary>
    public ConnectIdentity? TryLoad()
    {
        byte[] content;
        try
        {
            if (!File.Exists(IdentityPath))
            {
                return null;
            }

            var account = CurrentUserOnlyAccess.CurrentUser;
            if (!CurrentUserOnlyAccess.IsRestrictedDirectory(_inspector.ReadDirectory(DirectoryPath), account))
            {
                throw new ConnectIdentityUnavailableException();
            }

            using var stream = new FileStream(IdentityPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CurrentUserOnlyAccess.IsTrustedOwner(_inspector.ReadFile(stream), account) ||
                stream.Length > MaxFileBytes)
            {
                throw new ConnectIdentityUnavailableException();
            }

            content = new byte[stream.Length];
            stream.ReadExactly(content);
        }
        catch (IOException exception)
        {
            throw new ConnectIdentityUnavailableException(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ConnectIdentityUnavailableException(exception);
        }

        return Open(content);
    }

    /// <summary>
    /// Creates and persists a new identity. Fails if one already exists, and with
    /// <see cref="ConnectIdentityUnavailableException"/> if the directory belongs to another account.
    /// </summary>
    public ConnectIdentity Create()
    {
        var account = CurrentUserOnlyAccess.CurrentUser;
        CurrentUserOnlyAccess.EnsureDirectory(DirectoryPath, account, _inspector);
        var identity = ConnectIdentity.Generate(_kind);
        try
        {
            var content = Seal(identity);
            var temporary = Path.Combine(DirectoryPath, $"{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileInfo(temporary).Create(
                           FileMode.CreateNew,
                           FileSystemRights.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough,
                           CurrentUserOnlyAccess.CreateFileSecurity(account)))
                {
                    stream.Write(content);
                }

                File.Move(temporary, IdentityPath, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }

            return identity;
        }
        catch
        {
            identity.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the identity, creating it only when none exists. If another process creates it at
    /// the same moment, that identity wins and is loaded.
    /// </summary>
    public ConnectIdentity LoadOrCreate()
    {
        var existing = TryLoad();
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return Create();
        }
        catch (IOException) when (File.Exists(IdentityPath))
        {
            return TryLoad() ?? throw new ConnectIdentityUnavailableException();
        }
    }

    private byte[] Seal(ConnectIdentity identity)
    {
        var pkcs8 = identity.UsePrivateKey(key => key.ExportPkcs8PrivateKey());
        byte[] protectedKey;
        try
        {
            protectedKey = CurrentUserDpapi.Protect(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("kind", KindName(_kind));
            writer.WriteString("keyId", identity.KeyId);
            writer.WriteString("spki", identity.PublicKeySpkiBase64Url);
            writer.WriteString("protectedKey", Base64Url.Encode(protectedKey));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private ConnectIdentity Open(byte[] content)
    {
        if (!StrictJsonObject.TryParse(content, out var json) ||
            !json.TryGetInt64("v", out var version) ||
            version != FormatVersion ||
            !json.TryGetString("kind", out var kind) ||
            kind != KindName(_kind) ||
            !json.TryGetString("keyId", out var keyId) ||
            !json.TryGetString("spki", out var spki) ||
            !json.TryGetString("protectedKey", out var protectedKeyText) ||
            !Base64Url.TryDecode(protectedKeyText, out var protectedKey))
        {
            throw new ConnectIdentityUnavailableException();
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = CurrentUserDpapi.Unprotect(protectedKey);
        }
        catch (CryptographicException exception)
        {
            throw new ConnectIdentityUnavailableException(exception);
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out var bytesRead);
            if (bytesRead != pkcs8.Length || !Es256.IsP256(key))
            {
                throw new ConnectIdentityUnavailableException();
            }

            var identity = new ConnectIdentity(_kind, key);

            // The public half and id stored next to the blob must match the key inside it, so
            // a file assembled from parts of two identities is refused.
            if (identity.PublicKeySpkiBase64Url != spki || identity.KeyId != keyId)
            {
                throw new ConnectIdentityUnavailableException();
            }

            return identity;
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw new ConnectIdentityUnavailableException(exception);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static string KindName(ConnectIdentityKind kind) =>
        kind == ConnectIdentityKind.Owner ? "owner" : "device";
}

/// <summary>
/// The saved identity exists but cannot be used by this Windows user on this PC. The remedy is
/// to pair again. The message is safe to show and contains no key material.
/// </summary>
public sealed class ConnectIdentityUnavailableException : Exception
{
    private const string DefaultMessage =
        "The saved 1Salem Connect identity cannot be opened by this Windows user on this PC. Pair again to continue.";

    public ConnectIdentityUnavailableException()
        : base(DefaultMessage)
    {
    }

    public ConnectIdentityUnavailableException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
