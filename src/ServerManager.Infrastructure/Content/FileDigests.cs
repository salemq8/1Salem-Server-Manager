using System.Security.Cryptography;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Hashes a file once for every digest we need: SHA-1 and SHA-256 identify a file to the two
/// providers, and SHA-512 is what Modrinth publishes for verification.
/// </summary>
public static class FileDigests
{
    public static async Task<ContentFileDigests> ComputeAsync(
        string path,
        bool includeSha512 = false,
        CancellationToken cancellationToken = default)
    {
        using var sha1 = SHA1.Create();
        using var sha256 = SHA256.Create();
        using var sha512 = includeSha512 ? SHA512.Create() : null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            useAsync: true);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha1.TransformBlock(buffer, 0, read, null, 0);
            sha256.TransformBlock(buffer, 0, read, null, 0);
            sha512?.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock([], 0, 0);
        sha256.TransformFinalBlock([], 0, 0);
        sha512?.TransformFinalBlock([], 0, 0);

        return new ContentFileDigests(
            Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
            Convert.ToHexString(sha256.Hash!).ToLowerInvariant(),
            sha512 is null ? null : Convert.ToHexString(sha512.Hash!).ToLowerInvariant());
    }

    /// <summary>
    /// Compares a provider hash with what we computed, ignoring case and spacing. A provider
    /// that published no hash returns null, which the caller must report as "not verified"
    /// rather than as a pass.
    /// </summary>
    public static bool? Matches(string? providerHash, string computed)
    {
        if (string.IsNullOrWhiteSpace(providerHash))
        {
            return null;
        }

        return string.Equals(providerHash.Trim(), computed, StringComparison.OrdinalIgnoreCase);
    }
}
