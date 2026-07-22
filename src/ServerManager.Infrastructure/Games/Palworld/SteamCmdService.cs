using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ServerManager.Core;
using ServerManager.Infrastructure.Persistence;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed partial class SteamCmdService(
    HttpClient httpClient,
    SqliteStorageOptions storageOptions) : ISteamCmdService
{
    public static readonly Uri DownloadUri =
        new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    public async Task<string> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var dependenciesRoot = Path.Combine(storageOptions.DataRoot, "dependencies");
        var steamCmdRoot = Path.Combine(dependenciesRoot, "steamcmd");
        var executable = Path.Combine(steamCmdRoot, "steamcmd.exe");
        if (File.Exists(executable))
        {
            return executable;
        }

        if (Directory.Exists(steamCmdRoot))
        {
            throw new InvalidDataException(
                $"The managed SteamCMD folder is incomplete: {steamCmdRoot}");
        }

        Directory.CreateDirectory(dependenciesRoot);
        var staging = Path.Combine(
            dependenciesRoot,
            $".1salem-steamcmd-staging-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(
            dependenciesRoot,
            $".1salem-steamcmd-{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await httpClient.GetAsync(
                       DownloadUri,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(archivePath);
                await source.CopyToAsync(destination, cancellationToken);
            }

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!SafePathPolicy.IsWithinRoot(target, staging))
                    {
                        throw new InvalidDataException("SteamCMD archive attempted path traversal.");
                    }
                }
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archivePath, staging, false);
            if (!File.Exists(Path.Combine(staging, "steamcmd.exe")))
            {
                throw new InvalidDataException("The official SteamCMD archive did not contain steamcmd.exe.");
            }

            Directory.Move(staging, steamCmdRoot);
            return executable;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(staging) &&
                SafePathPolicy.IsWithinRoot(staging, dependenciesRoot) &&
                Path.GetFileName(staging).StartsWith(
                    ".1salem-steamcmd-staging-",
                    StringComparison.Ordinal))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public async Task<SteamCmdInstallResult> InstallOrUpdateAsync(
        string destinationPath,
        int appId,
        CancellationToken cancellationToken = default)
    {
        var steamCmd = await EnsureInstalledAsync(cancellationToken);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(destination);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = steamCmd,
                Arguments = SteamCmdCommandBuilder.BuildInstallOrUpdateArguments(destination, appId),
                WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows did not start SteamCMD.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            throw new TimeoutException("SteamCMD did not finish within 30 minutes.");
        }

        var output = $"{await outputTask}{Environment.NewLine}{await errorTask}".Trim();
        if (process.ExitCode != 0 ||
            !File.Exists(Path.Combine(destination, "PalServer.exe")))
        {
            throw new InvalidOperationException(
                $"SteamCMD failed to install or validate Palworld. Exit code: {process.ExitCode}.{Environment.NewLine}{output}");
        }

        return new SteamCmdInstallResult(
            destination,
            ReadBuildId(Path.GetDirectoryName(steamCmd)!, destination, appId, output),
            output);
    }

    public async Task<string?> GetLatestBuildIdAsync(
        int appId,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(appId));
        }

        var steamCmd = await EnsureInstalledAsync(cancellationToken);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = steamCmd,
                Arguments = $"+login anonymous +app_info_update 1 +app_info_print {appId} +quit",
                WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows did not start SteamCMD.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        var output = $"{await outputTask}{Environment.NewLine}{await errorTask}";
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"SteamCMD app-info query failed with exit code {process.ExitCode}.");
        }

        var match = PublicBuildIdRegex().Match(output);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? ReadBuildId(
        string steamCmdRoot,
        string destination,
        int appId,
        string output)
    {
        foreach (var manifest in new[]
                 {
                     Path.Combine(steamCmdRoot, "steamapps", $"appmanifest_{appId}.acf"),
                     Path.Combine(destination, "steamapps", $"appmanifest_{appId}.acf")
                 })
        {
            if (!File.Exists(manifest))
            {
                continue;
            }

            var match = BuildIdRegex().Match(File.ReadAllText(manifest));
            if (match.Success)
            {
                return match.Groups["id"].Value;
            }
        }

        var outputMatch = BuildIdOutputRegex().Match(output);
        return outputMatch.Success ? outputMatch.Groups["id"].Value : null;
    }

    [GeneratedRegex("\"buildid\"\\s+\"(?<id>\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex("build\\s*(?:id)?\\s*[:=]?\\s*(?<id>\\d{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex BuildIdOutputRegex();

    [GeneratedRegex(
        "\"public\".*?\"buildid\"\\s+\"(?<id>\\d+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PublicBuildIdRegex();
}
