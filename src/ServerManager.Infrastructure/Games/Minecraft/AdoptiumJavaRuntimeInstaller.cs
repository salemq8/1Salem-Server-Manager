using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ServerManager.Core;
using ServerManager.Infrastructure.Persistence;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class AdoptiumJavaRuntimeInstaller(
    HttpClient httpClient,
    SqliteStorageOptions storageOptions,
    IJavaRuntimeLocator runtimeLocator) : IJavaRuntimeInstaller
{
    public async Task<JavaRuntimeInfo> InstallAsync(
        int majorVersion,
        CancellationToken cancellationToken = default)
    {
        if (majorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(majorVersion),
                "The requested Java major version is invalid.");
        }

        var target = Path.Combine(
            storageOptions.DataRoot,
            "dependencies",
            "java",
            $"temurin-{majorVersion}");
        var existing = Directory.Exists(target)
            ? Directory.EnumerateFiles(
                    target,
                    "java.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;
        if (existing is not null)
        {
            var installed = await runtimeLocator.InspectAsync(existing, cancellationToken);
            if (installed is not null && installed.MajorVersion >= majorVersion)
            {
                return installed;
            }
        }

        var package = await ResolvePackageAsync(majorVersion, cancellationToken);
        var parent = Directory.GetParent(target)
            ?? throw new DirectoryNotFoundException(
                "The managed Java destination is invalid.");
        Directory.CreateDirectory(parent.FullName);
        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(parent.FullName, $".1salem-java-{operationId}.zip");
        var staging = Path.Combine(parent.FullName, $".1salem-java-{operationId}");
        try
        {
            await DownloadAsync(package.Link, archivePath, cancellationToken);
            await VerifyAsync(package, archivePath, cancellationToken);
            Directory.CreateDirectory(staging);
            ExtractSafely(archivePath, staging);
            var java = Directory.EnumerateFiles(
                    staging,
                    "java.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "The downloaded Java runtime did not contain bin\\java.exe.");
            var relativeJava = Path.GetRelativePath(staging, java);

            if (Directory.Exists(target))
            {
                throw new IOException(
                    "A managed Java destination already exists but is not usable. " +
                    $"Inspect {target} before retrying.");
            }

            Directory.Move(staging, target);
            var installedPath = Path.Combine(target, relativeJava);
            var runtime = await runtimeLocator.InspectAsync(
                installedPath,
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The installed Java runtime could not report its version.");
            if (runtime.MajorVersion < majorVersion)
            {
                throw new InvalidDataException(
                    $"Installed Java {runtime.MajorVersion}, but Java {majorVersion} is required.");
            }

            return runtime;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(staging) &&
                Path.GetFileName(staging).StartsWith(
                    ".1salem-java-",
                    StringComparison.Ordinal))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    private async Task<AdoptiumPackage> ResolvePackageAsync(
        int majorVersion,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot" +
            "?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot" +
            "&os=windows&vendor=eclipse");
        var assets = await httpClient.GetFromJsonAsync<AdoptiumAsset[]>(
            endpoint,
            cancellationToken)
            ?? [];
        var package = assets
            .Select(asset => asset.Binary?.Package)
            .FirstOrDefault(candidate =>
                candidate is not null &&
                candidate.Link is not null &&
                !string.IsNullOrWhiteSpace(candidate.Checksum))
            ?? throw new InvalidDataException(
                $"No verified Windows x64 Temurin Java {majorVersion} runtime was returned.");
        return package;
    }

    private async Task DownloadAsync(
        Uri source,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task VerifyAsync(
        AdoptiumPackage package,
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (package.Size > 0 && new FileInfo(archivePath).Length != package.Size)
        {
            throw new InvalidDataException(
                "The Java runtime size did not match the verified package metadata.");
        }

        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(package.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Java runtime hash mismatch. Expected {package.Checksum}, received {actual}.");
        }
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!SafePathPolicy.IsWithinRoot(target, destination))
            {
                throw new InvalidDataException(
                    "The Java archive contained a path outside its managed destination.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }

    private sealed record AdoptiumAsset(
        [property: JsonPropertyName("binary")] AdoptiumBinary? Binary);

    private sealed record AdoptiumBinary(
        [property: JsonPropertyName("package")] AdoptiumPackage? Package);

    private sealed record AdoptiumPackage(
        [property: JsonPropertyName("link")] Uri Link,
        [property: JsonPropertyName("checksum")] string Checksum,
        [property: JsonPropertyName("size")] long Size);
}
