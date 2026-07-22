using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using System.Runtime.Versioning;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Updates;

public sealed record VersionedUpdateOptions(
    string PackagePath,
    string InstallRoot,
    string DataRoot,
    string TargetVersion,
    string ServiceName,
    bool ActivateAgent = false,
    bool AllowSameVersion = false,
    bool SkipBinaryVersionVerification = false,
    bool UpdateRegistry = true,
    bool SkipServiceHealthCheck = false,
    Uri? HealthUri = null,
    bool VerifyOnly = false);

public sealed record VersionedUpdateResult(
    bool Success,
    bool RolledBack,
    bool AgentUpdatePending,
    string Message,
    string? RollbackPath,
    string? StableLauncherPath,
    InstalledVersionReport? InstalledAfter,
    int ShortcutsRetargeted = 0);

public sealed class VersionedUpdateInstaller
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\1SalemServerManager";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string, string, bool, CancellationToken, Task> _serviceCommand;
    private readonly Func<Uri, string, CancellationToken, Task> _healthVerifier;
    private readonly Func<bool> _managedGameProcessDetector;

    public VersionedUpdateInstaller(
        Func<string, string, bool, CancellationToken, Task>? serviceCommand = null,
        Func<Uri, string, CancellationToken, Task>? healthVerifier = null,
        Func<bool>? managedGameProcessDetector = null)
    {
        _serviceCommand = serviceCommand ?? RunServiceCommandAsync;
        _healthVerifier = healthVerifier ?? VerifyHealthAsync;
        _managedGameProcessDetector = managedGameProcessDetector ?? HasManagedGameProcesses;
    }

    public async Task<VersionedUpdateResult> ApplyAsync(
        VersionedUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var target = SemanticVersion.Parse(options.TargetVersion);
        _ = UpdatePackageSecurity.ValidateArchive(options.PackagePath);
        var installRoot = Path.GetFullPath(options.InstallRoot);
        var dataRoot = Path.GetFullPath(options.DataRoot);
        EnsureSeparateRoots(installRoot, dataRoot);
        var before = InstalledVersionDetector.Detect(new InstalledVersionDetectionOptions(
            installRoot,
            dataRoot,
            InspectRunningProcesses: true,
            InspectRegistry: options.UpdateRegistry));
        var currentVersion = HighestActiveVersion(before) ?? new SemanticVersion(0, 0, 0);
        var comparison = target.CompareTo(currentVersion);
        if (comparison < 0 || (comparison == 0 && !options.AllowSameVersion))
        {
            throw new InvalidDataException(
                $"Update rejected: target {target} is not newer than installed version {currentVersion}.");
        }

        var operationId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var workRoot = Path.Combine(dataRoot, "updates", "work", operationId);
        var extracted = Path.Combine(workRoot, "payload");
        var rollbackRoot = Path.Combine(
            dataRoot,
            "updates",
            "rollback",
            $"{currentVersion}-to-{target}-{operationId}");
        Directory.CreateDirectory(extracted);
        PayloadPaths paths;
        try
        {
            UpdatePackageSecurity.ExtractSafe(options.PackagePath, extracted);
            paths = ValidatePayload(extracted, target.ToString(), options.SkipBinaryVersionVerification);
        }
        catch
        {
            TryDeleteDirectory(workRoot);
            throw;
        }

        if (options.VerifyOnly)
        {
            TryDeleteDirectory(workRoot);
            return new VersionedUpdateResult(
                true,
                false,
                false,
                $"Verified versioned update package {target}; no files changed.",
                null,
                null,
                before);
        }

        var currentManifestPath = Path.Combine(installRoot, "current.json");
        var dataManifestPath = Path.Combine(dataRoot, "installation.json");
        var stableLauncher = Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe");
        var rootUpdater = Path.Combine(
            installRoot,
            "Client",
            "Updater",
            "1Salem.ServerManager.Updater.exe");
        var targetRoot = Path.Combine(installRoot, "Versions", target.ToString());
        var agentStopped = false;
        var switched = false;
        var installMutationStarted = false;
        var rollbackCreated = false;
        string? registryBefore = before.RegistryVersion;
        try
        {
            VerifyInstallRootWritable(installRoot, operationId);
            Directory.CreateDirectory(rollbackRoot);
            rollbackCreated = true;
            SnapshotFile(currentManifestPath, Path.Combine(rollbackRoot, "current.json"));
            SnapshotFile(dataManifestPath, Path.Combine(rollbackRoot, "installation.json"));
            SnapshotFile(
                Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"),
                Path.Combine(rollbackRoot, "Uninstall 1Salem Server Manager.exe"));
            SnapshotDirectory(
                Path.Combine(installRoot, "Client"),
                Path.Combine(rollbackRoot, "Client"));
            if (options.ActivateAgent)
            {
                SnapshotDirectory(
                    Path.Combine(installRoot, "Agent"),
                    Path.Combine(rollbackRoot, "Agent"));
            }

            installMutationStarted = true;
            PreserveLegacyVersion(installRoot, before, target.ToString());
            PrepareTargetDirectory(targetRoot, dataRoot, target.ToString());
            var versionClient = Path.Combine(targetRoot, "Client");
            var versionAgent = Path.Combine(targetRoot, "Agent");
            var versionUpdater = Path.Combine(targetRoot, "Updater");
            CopyDirectory(paths.ClientRoot, versionClient, relative =>
                !relative.StartsWith("Launcher" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            CopyDirectory(paths.AgentRoot, versionAgent);
            CopyDirectory(paths.UpdaterRoot, versionUpdater);

            Directory.CreateDirectory(Path.GetDirectoryName(stableLauncher)!);
            AtomicCopy(paths.LauncherPath, stableLauncher);
            CopyDirectory(paths.UpdaterRoot, Path.GetDirectoryName(rootUpdater)!);
            if (paths.MaintenanceExecutable is not null)
            {
                AtomicCopy(
                    paths.MaintenanceExecutable,
                    Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"));
            }

            var shortcutMigration = StableShortcutMigration.RetargetInstalledShortcuts(
                installRoot,
                stableLauncher);

            var activateAgent = options.ActivateAgent && !_managedGameProcessDetector();
            if (activateAgent)
            {
                await _serviceCommand("stop", options.ServiceName, true, cancellationToken);
                agentStopped = true;
                ReplaceDirectory(
                    Path.Combine(installRoot, "Agent"),
                    versionAgent,
                    Path.Combine(workRoot, "old-agent"));
                await _serviceCommand("start", options.ServiceName, false, cancellationToken);
                agentStopped = false;
                if (!options.SkipServiceHealthCheck)
                {
                    await _healthVerifier(
                        options.HealthUri ?? new Uri("http://127.0.0.1:5251/health"),
                        target.ToString(),
                        cancellationToken);
                }
            }

            var agentPath = Path.Combine(
                installRoot,
                "Agent",
                "1Salem.ServerManager.Agent.exe");
            var agentVersion = InstalledVersionDetector.ReadProductVersion(agentPath);
            var agentPending = !string.Equals(
                agentVersion,
                target.ToString(),
                StringComparison.OrdinalIgnoreCase);
            var previousVersion = comparison == 0
                ? before.PreviousVersion ?? before.RollbackVersion ?? currentVersion.ToString()
                : currentVersion.Major == 0
                    ? null
                    : currentVersion.ToString();
            var rollbackVersion = comparison == 0
                ? before.RollbackVersion ?? before.PreviousVersion ?? currentVersion.ToString()
                : currentVersion.Major == 0
                    ? null
                    : currentVersion.ToString();
            var manifest = new InstalledApplicationManifest(
                1,
                target.ToString(),
                previousVersion,
                rollbackVersion,
                ProductIdentity.StableChannel,
                stableLauncher,
                Path.Combine(versionClient, "1Salem.ServerManager.exe"),
                agentPath,
                rootUpdater,
                agentPending
                    ? Path.Combine(versionAgent, "1Salem.ServerManager.Agent.exe")
                    : null,
                agentPending ? "Agent update pending safe restart" : "Succeeded",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            WriteJsonAtomic(currentManifestPath, manifest);
            WriteJsonAtomic(dataManifestPath, manifest);
            switched = true;
            if (options.UpdateRegistry && OperatingSystem.IsWindows())
            {
                UpdateUninstallRegistration(target.ToString(), installRoot, stableLauncher);
            }

            VerifyVersion(manifest.ClientExecutablePath, target.ToString(), options.SkipBinaryVersionVerification);
            VerifyVersion(manifest.UpdaterExecutablePath, target.ToString(), options.SkipBinaryVersionVerification);
            VerifyVersion(stableLauncher, target.ToString(), options.SkipBinaryVersionVerification);
            var after = InstalledVersionDetector.Detect(new InstalledVersionDetectionOptions(
                installRoot,
                dataRoot,
                InspectRunningProcesses: true,
                InspectRegistry: options.UpdateRegistry));
            var result = new VersionedUpdateResult(
                true,
                false,
                agentPending,
                agentPending
                    ? $"Updated Client and Updater to {target}; Agent {agentVersion ?? "unknown"} is safely staged for a later restart."
                    : $"Updated successfully to {target}.",
                rollbackRoot,
                stableLauncher,
                after,
                shortcutMigration.Updated);
            await WriteResultAsync(dataRoot, result, cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            if (agentStopped)
            {
                try
                {
                    await _serviceCommand("start", options.ServiceName, false, cancellationToken);
                }
                catch (Exception startException)
                {
                    exception = new AggregateException(exception, startException);
                }
            }

            var rolledBack = installMutationStarted && RollBack(
                    installRoot,
                    dataRoot,
                    rollbackRoot,
                    switched,
                    options.ActivateAgent,
                    options.UpdateRegistry,
                    registryBefore);
            var result = new VersionedUpdateResult(
                false,
                rolledBack,
                false,
                !installMutationStarted
                    ? $"Update could not start; no installed files were changed: {exception.Message}"
                    : rolledBack
                    ? $"Update failed and the previous version was restored: {exception.Message}"
                    : $"Update failed and rollback is incomplete: {exception.Message}",
                rollbackCreated ? rollbackRoot : null,
                stableLauncher,
                before);
            await WriteResultAsync(dataRoot, result, CancellationToken.None);
            return result;
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private static void VerifyInstallRootWritable(string installRoot, string operationId)
    {
        Directory.CreateDirectory(installRoot);
        var probePath = Path.Combine(installRoot, $".update-write-test-{operationId}.tmp");
        try
        {
            File.WriteAllText(probePath, "1Salem updater write-access probe");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException(
                "The installed updater requires its elevated maintenance context to update this installation.",
                exception);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool ValidateRollbackSnapshot(string rollbackRoot)
    {
        if (!Directory.Exists(rollbackRoot))
        {
            return false;
        }

        var client = Path.Combine(rollbackRoot, "Client", "1Salem.ServerManager.exe");
        var current = Path.Combine(rollbackRoot, "current.json");
        return File.Exists(client) || File.Exists(current);
    }

    private static PayloadPaths ValidatePayload(
        string extracted,
        string targetVersion,
        bool skipVersionVerification)
    {
        var client = Path.Combine(extracted, "Client");
        var agent = Path.Combine(extracted, "Agent");
        var launcher = Path.Combine(
            client,
            "Launcher",
            "1Salem.ServerManager.Launcher.exe");
        var updater = Path.Combine(client, "Updater");
        foreach (var required in new[]
                 {
                     Path.Combine(client, "1Salem.ServerManager.exe"),
                     Path.Combine(agent, "1Salem.ServerManager.Agent.exe"),
                     launcher,
                     Path.Combine(updater, "1Salem.ServerManager.Updater.exe")
                 })
        {
            if (!File.Exists(required))
            {
                throw new InvalidDataException(
                    $"The update package is missing required file {Path.GetRelativePath(extracted, required)}.");
            }

            VerifyVersion(required, targetVersion, skipVersionVerification);
        }

        var maintenance = Path.Combine(
            extracted,
            "Maintenance",
            "Uninstall 1Salem Server Manager.exe");
        if (File.Exists(maintenance))
        {
            VerifyVersion(maintenance, targetVersion, skipVersionVerification);
        }

        return new PayloadPaths(
            client,
            agent,
            launcher,
            updater,
            File.Exists(maintenance) ? maintenance : null);
    }

    private static void ValidateOptions(VersionedUpdateOptions options)
    {
        if (!Path.IsPathFullyQualified(options.PackagePath) ||
            !Path.IsPathFullyQualified(options.InstallRoot) ||
            !Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException("Update paths must be absolute.");
        }

        if (!File.Exists(options.PackagePath))
        {
            throw new FileNotFoundException("The update package was not found.", options.PackagePath);
        }

        if (!options.ServiceName.Equals(ProductIdentity.ServiceName, StringComparison.Ordinal) &&
            !options.ServiceName.StartsWith("test-", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Agent service name is not approved.");
        }
    }

    private static SemanticVersion? HighestActiveVersion(InstalledVersionReport report)
    {
        var versions = new[]
            {
                report.Client.Version,
                report.Agent.Version,
                report.Updater.Version,
                report.RegistryVersion,
                report.ManifestVersion
            }
            .Where(value => SemanticVersion.TryParse(value, out _))
            .Select(value => SemanticVersion.Parse(value!))
            .ToArray();
        return versions.Length == 0 ? null : versions.Max();
    }

    private static void PreserveLegacyVersion(
        string installRoot,
        InstalledVersionReport before,
        string targetVersion)
    {
        var previous = before.Client.Version ?? before.RegistryVersion;
        if (string.IsNullOrWhiteSpace(previous) ||
            previous.Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previousRoot = Path.Combine(installRoot, "Versions", previous);
        var previousClient = Path.Combine(previousRoot, "Client");
        if (!Directory.Exists(previousClient))
        {
            CopyDirectory(Path.Combine(installRoot, "Client"), previousClient);
        }

        var previousAgent = Path.Combine(previousRoot, "Agent");
        if (!Directory.Exists(previousAgent) &&
            Directory.Exists(Path.Combine(installRoot, "Agent")))
        {
            CopyDirectory(Path.Combine(installRoot, "Agent"), previousAgent);
        }
    }

    private static void PrepareTargetDirectory(
        string targetRoot,
        string dataRoot,
        string targetVersion)
    {
        if (!Directory.Exists(targetRoot))
        {
            Directory.CreateDirectory(targetRoot);
            return;
        }

        var failed = Path.Combine(
            dataRoot,
            "updates",
            "failed",
            $"{targetVersion}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        Directory.CreateDirectory(Path.GetDirectoryName(failed)!);
        Directory.Move(targetRoot, failed);
        Directory.CreateDirectory(targetRoot);
    }

    private static bool RollBack(
        string installRoot,
        string dataRoot,
        string rollbackRoot,
        bool switched,
        bool restoreAgent,
        bool updateRegistry,
        string? registryVersion)
    {
        try
        {
            if (Directory.Exists(Path.Combine(rollbackRoot, "Client")))
            {
                CopyDirectory(
                    Path.Combine(rollbackRoot, "Client"),
                    Path.Combine(installRoot, "Client"));
            }

            if (restoreAgent && Directory.Exists(Path.Combine(rollbackRoot, "Agent")))
            {
                CopyDirectory(
                    Path.Combine(rollbackRoot, "Agent"),
                    Path.Combine(installRoot, "Agent"));
            }

            RestoreSnapshot(
                Path.Combine(rollbackRoot, "current.json"),
                Path.Combine(installRoot, "current.json"),
                switched);
            RestoreSnapshot(
                Path.Combine(rollbackRoot, "installation.json"),
                Path.Combine(dataRoot, "installation.json"),
                switched);
            RestoreSnapshot(
                Path.Combine(rollbackRoot, "Uninstall 1Salem Server Manager.exe"),
                Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"),
                switched);
            if (updateRegistry && OperatingSystem.IsWindows() &&
                !string.IsNullOrWhiteSpace(registryVersion))
            {
                RestoreRegistryVersion(registryVersion);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestoreSnapshot(string snapshot, string live, bool switched)
    {
        if (File.Exists(snapshot))
        {
            AtomicCopy(snapshot, live);
        }
        else if (switched && File.Exists(live))
        {
            File.Delete(live);
        }
    }

    private static void SnapshotFile(string source, string destination)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void SnapshotDirectory(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
    }

    private static void ReplaceDirectory(string live, string source, string old)
    {
        if (Directory.Exists(old))
        {
            throw new IOException($"Temporary update path already exists: {old}");
        }

        if (Directory.Exists(live))
        {
            Directory.Move(live, old);
        }

        CopyDirectory(source, live);
    }

    private static void CopyDirectory(
        string source,
        string destination,
        Func<string, bool>? include = null)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (include is not null && !include(relative))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".new-{Guid.NewGuid():N}";
        File.Copy(source, temporary, overwrite: false);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".new-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void VerifyVersion(string path, string expected, bool skip)
    {
        if (skip)
        {
            return;
        }

        var detected = InstalledVersionDetector.ReadProductVersion(path);
        if (!string.Equals(detected, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} reports {detected ?? "no version"}; expected {expected}.");
        }
    }

    private static void EnsureSeparateRoots(string installRoot, string dataRoot)
    {
        var install = installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var data = dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (install.StartsWith(data, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(install, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "ProgramData and the application installation must remain separate.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UpdateUninstallRegistration(
        string version,
        string installRoot,
        string launcher)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true) ??
            throw new UnauthorizedAccessException("The uninstall registration is unavailable.");
        key.SetValue("DisplayVersion", version);
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("DisplayIcon", $"{launcher},0");
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreRegistryVersion(string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true);
        key?.SetValue("DisplayVersion", version);
    }

    private static async Task RunServiceCommandAsync(
        string operation,
        string serviceName,
        bool allowAlreadyStopped,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(serviceName);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Windows service control could not start.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 && !(allowAlreadyStopped && process.ExitCode == 1062))
        {
            throw new IOException(
                $"Agent service {operation} failed with code {process.ExitCode}: " +
                await process.StandardError.ReadToEndAsync(cancellationToken));
        }
    }

    private static async Task VerifyHealthAsync(
        Uri uri,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode &&
                    content.Contains(expectedVersion, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"The updated Agent did not report healthy version {expectedVersion}.");
    }

    private static bool HasManagedGameProcesses() =>
        Process.GetProcesses().Any(process =>
        {
            using (process)
            {
                try
                {
                    return process.ProcessName.Equals("PalServer", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.StartsWith("PalServer-Win64", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        });

    private static Task WriteResultAsync(
        string dataRoot,
        VersionedUpdateResult result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, "updates", "last-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
    }

    private sealed record PayloadPaths(
        string ClientRoot,
        string AgentRoot,
        string LauncherPath,
        string UpdaterRoot,
        string? MaintenanceExecutable);
}
