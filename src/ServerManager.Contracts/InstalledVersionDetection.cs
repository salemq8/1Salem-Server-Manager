using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ServerManager.Contracts;

public sealed record InstalledComponentVersion(
    string Name,
    string? Version,
    string? ExecutablePath,
    string Source,
    bool IsRunning = false,
    int? ProcessId = null,
    string? StagedVersion = null,
    string? StagedPath = null,
    int BuildRevision = 0,
    int? StagedBuildRevision = null);

public sealed record InstalledApplicationManifest(
    int SchemaVersion,
    string ActiveVersion,
    string? PreviousVersion,
    string? RollbackVersion,
    string ReleaseChannel,
    string StableLauncherPath,
    string ClientExecutablePath,
    string AgentExecutablePath,
    string UpdaterExecutablePath,
    string? StagedAgentExecutablePath,
    string UpdateStatus,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSuccessfulUpdateUtc = null,
    int ActiveBuildRevision = 0,
    int? PreviousBuildRevision = null,
    int? RollbackBuildRevision = null,
    string? ActivePackageSha256 = null,
    string? PreviousPackageSha256 = null,
    string? RollbackPackageSha256 = null,
    string? RollbackSnapshotPath = null);

public sealed record InstalledVersionReport(
    string? InstallRoot,
    string DataRoot,
    InstalledComponentVersion Client,
    InstalledComponentVersion Agent,
    InstalledComponentVersion Updater,
    string? RegistryVersion,
    string? ManifestVersion,
    string ReleaseChannel,
    string OverallState,
    string? PreviousVersion,
    string? RollbackVersion,
    DateTimeOffset? LastSuccessfulUpdateUtc,
    IReadOnlyList<InstalledComponentVersion> RunningComponents,
    int ManifestBuildRevision = 0,
    int? PreviousBuildRevision = null,
    int? RollbackBuildRevision = null,
    string? ActivePackageSha256 = null,
    string? RollbackSnapshotPath = null)
{
    public IEnumerable<string> KnownVersions()
    {
        foreach (var value in new[]
                 {
                     Client.Version,
                     Client.StagedVersion,
                     Agent.Version,
                     Agent.StagedVersion,
                     Updater.Version,
                     RegistryVersion,
                     ManifestVersion,
                     PreviousVersion,
                     RollbackVersion
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        foreach (var component in RunningComponents)
        {
            if (!string.IsNullOrWhiteSpace(component.Version))
            {
                yield return component.Version;
            }
        }
    }
}

public sealed record InstalledVersionDetectionOptions(
    string? InstallRoot = null,
    string? DataRoot = null,
    bool InspectRunningProcesses = true,
    bool InspectRegistry = true);

public static partial class InstalledVersionDetector
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\1SalemServerManager";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static InstalledVersionReport Detect(
        InstalledVersionDetectionOptions? options = null)
    {
        options ??= new InstalledVersionDetectionOptions();
        var dataRoot = options.DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "1SalemServerManager");
        var registryVersion = options.InspectRegistry ? ReadRegistryVersion() : null;
        var installRoot = ResolveInstallRoot(options.InstallRoot, options.InspectRegistry);
        var manifest = ReadManifest(installRoot, dataRoot);
        var running = options.InspectRunningProcesses
            ? ReadRunningComponents(installRoot)
            : [];

        var clientPath = FirstExisting(
            manifest?.ClientExecutablePath,
            installRoot is null ? null : Path.Combine(
                installRoot,
                "Client",
                "1Salem.ServerManager.exe"));
        var servicePath = options.InspectRegistry ? ReadServiceExecutablePath() : null;
        var agentPath = FirstExisting(
            servicePath,
            manifest?.AgentExecutablePath,
            installRoot is null ? null : Path.Combine(
                installRoot,
                "Agent",
                "1Salem.ServerManager.Agent.exe"));
        var updaterPath = FirstExisting(
            manifest?.UpdaterExecutablePath,
            installRoot is null ? null : Path.Combine(
                installRoot,
                "Client",
                "Updater",
                "1Salem.ServerManager.Updater.exe"));

        var runningClient = running.FirstOrDefault(item =>
            item.Name.Equals("Client", StringComparison.Ordinal));
        var runningAgent = running.FirstOrDefault(item =>
            item.Name.Equals("Agent", StringComparison.Ordinal));
        if (runningClient is not null && IsWithinRoot(installRoot, runningClient.ExecutablePath))
        {
            clientPath = runningClient.ExecutablePath;
        }

        if (runningAgent is not null && IsWithinRoot(installRoot, runningAgent.ExecutablePath))
        {
            agentPath = runningAgent.ExecutablePath;
        }

        var stagedAgentPath = manifest?.StagedAgentExecutablePath;
        var client = Component(
            "Client",
            clientPath,
            runningClient,
            "active installation",
            fallbackBuildRevision: manifest?.ActiveBuildRevision ?? 0,
            fallbackProductVersion: manifest?.ActiveVersion);
        var agent = Component(
            "Agent",
            agentPath,
            runningAgent,
            servicePath is null ? "active installation" : "Windows Service",
            stagedAgentPath,
            manifest?.ActiveBuildRevision ?? 0,
            manifest?.ActiveVersion);
        var updater = Component(
            "Updater",
            updaterPath,
            null,
            "installed updater",
            fallbackBuildRevision: manifest?.ActiveBuildRevision ?? 0,
            fallbackProductVersion: manifest?.ActiveVersion);
        var state = InstalledVersionStatePolicy.Determine(
            client.Version,
            agent.Version,
            updater.Version,
            agent.StagedVersion,
            client.BuildRevision,
            agent.BuildRevision,
            updater.BuildRevision,
            agent.StagedBuildRevision);

        return new InstalledVersionReport(
            installRoot,
            dataRoot,
            client,
            agent,
            updater,
            registryVersion,
            manifest?.ActiveVersion,
            manifest?.ReleaseChannel ?? ProductIdentity.StableChannel,
            state,
            manifest?.PreviousVersion,
            manifest?.RollbackVersion,
            manifest?.LastSuccessfulUpdateUtc,
            running,
            manifest?.ActiveBuildRevision ?? 0,
            manifest?.PreviousBuildRevision,
            manifest?.RollbackBuildRevision,
            manifest?.ActivePackageSha256,
            manifest?.RollbackSnapshotPath);
    }

    public static string? ReadProductVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return NormalizeVersion(info.ProductVersion) ?? NormalizeVersion(info.FileVersion);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ProductVersionRegex().Match(value);
        return match.Success ? match.Value : null;
    }

    public static InstalledApplicationManifest? ReadManifest(
        string? installRoot,
        string dataRoot)
    {
        foreach (var path in new[]
                 {
                     installRoot is null ? null : Path.Combine(installRoot, "current.json"),
                     Path.Combine(dataRoot, "installation.json")
                 })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<InstalledApplicationManifest>(
                    File.ReadAllText(path),
                    JsonOptions);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return null;
    }

    public static int ReadBuildRevision(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return 0;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        var path = Path.Combine(directory, "build-info.json");
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("buildRevision", out var value) &&
                value.TryGetInt32(out var revision) &&
                revision >= 0
                ? revision
                : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    private static InstalledComponentVersion Component(
        string name,
        string? path,
        InstalledComponentVersion? running,
        string source,
        string? stagedPath = null,
        int fallbackBuildRevision = 0,
        string? fallbackProductVersion = null)
    {
        var version = ReadProductVersion(path);
        var detectedBuild = ReadBuildRevision(path);
        return new InstalledComponentVersion(
            name,
            version,
            path,
            source,
            running is not null,
            running?.ProcessId,
            ReadProductVersion(stagedPath),
            stagedPath,
            detectedBuild > 0
                ? detectedBuild
                : running?.BuildRevision > 0
                    ? running.BuildRevision
                    : version is not null && string.Equals(
                            version,
                            fallbackProductVersion,
                            StringComparison.OrdinalIgnoreCase)
                        ? fallbackBuildRevision
                        : 0,
            string.IsNullOrWhiteSpace(stagedPath)
                ? null
                : ReadBuildRevision(stagedPath));
    }

    private static string? ResolveInstallRoot(string? requested, bool inspectRegistry)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested);
        }

        if (inspectRegistry && OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string root &&
                    !string.IsNullOrWhiteSpace(root))
                {
                    return Path.GetFullPath(root);
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProductIdentity.ProductName);
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? ReadRegistryVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            return NormalizeVersion(key?.GetValue("DisplayVersion") as string);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ReadServiceExecutablePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ProductIdentity.ServiceName}");
            return ExtractExecutablePath(key?.GetValue("ImagePath") as string);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string path;
        if (expanded.StartsWith('"'))
        {
            var closing = expanded.IndexOf('"', 1);
            path = closing > 1 ? expanded[1..closing] : expanded.Trim('"');
        }
        else
        {
            var marker = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = marker >= 0 ? expanded[..(marker + 4)] : expanded;
        }

        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }

    private static IReadOnlyList<InstalledComponentVersion> ReadRunningComponents(
        string? installRoot)
    {
        var result = new List<InstalledComponentVersion>();
        foreach (var (processName, componentName) in new[]
                 {
                     ("1Salem.ServerManager", "Client"),
                     ("1Salem.ServerManager.Agent", "Agent"),
                     ("1Salem.ServerManager.Updater", "Updater")
                 })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path) || !IsWithinRoot(installRoot, path))
                        {
                            continue;
                        }

                        result.Add(new InstalledComponentVersion(
                            componentName,
                            ReadProductVersion(path),
                            path,
                            "running process",
                            true,
                            process.Id,
                            BuildRevision: ReadBuildRevision(path)));
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or
                            System.ComponentModel.Win32Exception or
                            NotSupportedException)
                    {
                    }
                }
            }
        }

        return result;
    }

    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));

    private static bool IsWithinRoot(string? root, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?<!\d)\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ProductVersionRegex();
}

public static class InstalledVersionStatePolicy
{
    public static string Determine(
        string? clientVersion,
        string? agentVersion,
        string? updaterVersion,
        string? stagedAgentVersion = null,
        int clientBuildRevision = 0,
        int agentBuildRevision = 0,
        int updaterBuildRevision = 0,
        int? stagedAgentBuildRevision = null)
    {
        if (!string.IsNullOrWhiteSpace(stagedAgentVersion) &&
            !string.Equals(
                agentVersion,
                stagedAgentVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Update pending Agent restart";
        }

        if (stagedAgentBuildRevision is { } stagedBuild &&
            agentBuildRevision != stagedBuild)
        {
            return "Update pending Agent restart";
        }

        var versions = new[] { clientVersion, agentVersion, updaterVersion }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builds = new[] { clientBuildRevision, agentBuildRevision, updaterBuildRevision }
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        return versions.Length > 1 || builds.Length > 1
            ? "Update incomplete"
            : versions.Length == 1
                ? "Consistent"
                : "Not installed";
    }
}
