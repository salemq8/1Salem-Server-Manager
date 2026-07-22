using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Updates;

public sealed record UpdateApplyOptions(
    string PackagePath,
    string InstallRoot,
    string DataRoot,
    string TargetVersion,
    bool RequiresServiceRestart,
    string ServiceName,
    bool SkipServiceControl = false,
    bool SkipHealthCheck = false,
    Uri? HealthUri = null,
    string? ClientExecutablePath = null,
    bool SkipVersionVerification = false);

public sealed record UpdateApplyResult(
    bool Success,
    bool RolledBack,
    string Message,
    string? RollbackPath);

public sealed class UpdatePackageApplier
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<CancellationToken, Task>? _afterSwap;
    private readonly Func<string, string, bool, CancellationToken, Task> _serviceCommand;
    private readonly Func<Uri, string, CancellationToken, Task> _healthVerifier;

    public UpdatePackageApplier(
        Func<CancellationToken, Task>? afterSwap = null,
        Func<string, string, bool, CancellationToken, Task>? serviceCommand = null,
        Func<Uri, string, CancellationToken, Task>? healthVerifier = null)
    {
        _afterSwap = afterSwap;
        _serviceCommand = serviceCommand ?? RunServiceCommandAsync;
        _healthVerifier = healthVerifier ?? VerifyHealthAsync;
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        _ = UpdatePackageSecurity.ValidateArchive(options.PackagePath);
        var installRoot = Path.GetFullPath(options.InstallRoot);
        var dataRoot = Path.GetFullPath(options.DataRoot);
        EnsureSeparateDataRoot(installRoot, dataRoot);
        var operationId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var workRoot = Path.Combine(dataRoot, "updates", "work", operationId);
        var extractedRoot = Path.Combine(workRoot, "payload");
        var rollbackRoot = Path.Combine(
            dataRoot,
            "updates",
            "rollback",
            $"{options.TargetVersion}-{operationId}");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(rollbackRoot);
        ProtectRollbackDirectory(rollbackRoot);
        UpdatePackageSecurity.ExtractSafe(options.PackagePath, extractedRoot);

        var movedOld = new List<(string Live, string Temporary)>();
        var installedNew = new List<string>();
        var serviceStopped = false;
        try
        {
            CopyDirectory(Path.Combine(installRoot, "Client"), Path.Combine(rollbackRoot, "Client"));
            CopyDirectory(Path.Combine(installRoot, "Agent"), Path.Combine(rollbackRoot, "Agent"));

            if (options.RequiresServiceRestart && !options.SkipServiceControl)
            {
                await _serviceCommand(
                    "stop",
                    options.ServiceName,
                    true,
                    cancellationToken);
                serviceStopped = true;
            }

            foreach (var directoryName in new[] { "Client", "Agent" })
            {
                var live = EnsureChildPath(installRoot, Path.Combine(installRoot, directoryName));
                var incoming = EnsureChildPath(
                    extractedRoot,
                    Path.Combine(extractedRoot, directoryName));
                var temporary = EnsureChildPath(
                    installRoot,
                    Path.Combine(installRoot, $".update-old-{directoryName}-{operationId}"));
                if (!Directory.Exists(incoming))
                {
                    throw new InvalidDataException(
                        $"Update payload is missing the {directoryName} directory.");
                }

                if (Directory.Exists(live))
                {
                    Directory.Move(live, temporary);
                    movedOld.Add((live, temporary));
                }

                Directory.Move(incoming, live);
                installedNew.Add(live);
            }

            if (_afterSwap is not null)
            {
                await _afterSwap(cancellationToken);
            }

            if (options.RequiresServiceRestart && !options.SkipServiceControl)
            {
                await _serviceCommand(
                    "start",
                    options.ServiceName,
                    false,
                    cancellationToken);
                serviceStopped = false;
            }

            if (!options.SkipHealthCheck)
            {
                await _healthVerifier(
                    options.HealthUri ?? new Uri("http://127.0.0.1:5251/health"),
                    options.TargetVersion,
                    cancellationToken);
            }

            if (!options.SkipVersionVerification)
            {
                VerifyInstalledVersion(
                    Path.Combine(
                        installRoot,
                        "Agent",
                        "1Salem.ServerManager.Agent.exe"),
                    options.TargetVersion);
                VerifyInstalledVersion(
                    Path.Combine(
                        installRoot,
                        "Client",
                        "1Salem.ServerManager.exe"),
                    options.TargetVersion);
            }

            foreach (var (_, temporary) in movedOld)
            {
                Directory.Delete(temporary, recursive: true);
            }

            Directory.Delete(workRoot, recursive: true);
            await WriteResultAsync(
                dataRoot,
                new UpdateApplyResult(
                    true,
                    false,
                    $"Updated successfully to {options.TargetVersion}.",
                    rollbackRoot),
                cancellationToken);
            return new UpdateApplyResult(
                true,
                false,
                $"Updated successfully to {options.TargetVersion}.",
                rollbackRoot);
        }
        catch (Exception exception)
        {
            if (options.RequiresServiceRestart &&
                !options.SkipServiceControl &&
                !serviceStopped)
            {
                try
                {
                    await _serviceCommand(
                        "stop",
                        options.ServiceName,
                        true,
                        cancellationToken);
                    serviceStopped = true;
                }
                catch (Exception stopException)
                {
                    exception = new AggregateException(exception, stopException);
                }
            }

            var rolledBack = RollBack(movedOld, installedNew);
            if (serviceStopped && !options.SkipServiceControl)
            {
                try
                {
                    await _serviceCommand(
                        "start",
                        options.ServiceName,
                        false,
                        cancellationToken);
                }
                catch (Exception serviceException)
                {
                    exception = new AggregateException(exception, serviceException);
                }
            }

            var result = new UpdateApplyResult(
                false,
                rolledBack,
                rolledBack
                    ? $"Update failed and was rolled back: {exception.Message}"
                    : $"Update failed and rollback was incomplete: {exception.Message}",
                rollbackRoot);
            await WriteResultAsync(dataRoot, result, CancellationToken.None);
            return result;
        }
    }

    public static string EnsureChildPath(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update path is outside its permitted root.");
        }

        return normalizedCandidate;
    }

    private static bool RollBack(
        IEnumerable<(string Live, string Temporary)> movedOld,
        IEnumerable<string> installedNew)
    {
        try
        {
            foreach (var path in installedNew.Reverse())
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }

            foreach (var (live, temporary) in movedOld.Reverse())
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Move(temporary, live);
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void ValidateOptions(UpdateApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.PackagePath) ||
            !Path.IsPathFullyQualified(options.InstallRoot) ||
            !Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException("Updater paths must be absolute.");
        }

        if (!File.Exists(options.PackagePath))
        {
            throw new FileNotFoundException("The update package does not exist.", options.PackagePath);
        }

        _ = ServerManager.Core.SemanticVersion.Parse(options.TargetVersion);
    }

    private static void EnsureSeparateDataRoot(string installRoot, string dataRoot)
    {
        var installPrefix = installRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (dataRoot.Equals(installRoot, StringComparison.OrdinalIgnoreCase) ||
            dataRoot.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The protected data root must be separate from the application install root.");
        }
    }

    private static void ProtectRollbackDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var rights = FileSystemRights.FullControl;
        var inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
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
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows service control could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 &&
            !(allowAlreadyStopped &&
              (output.Contains("1062", StringComparison.Ordinal) ||
               error.Contains("1062", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                $"Agent service {operation} failed with code {process.ExitCode}.");
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
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (json.Contains(
                            $"\"version\":\"{expectedVersion}\"",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException(
            $"The updated Agent did not report healthy version {expectedVersion}.");
    }

    private static void VerifyInstalledVersion(string executable, string expected)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Updated application binary is missing.", executable);
        }

        var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion
            ?.Split('+', '-')[0];
        if (!string.Equals(version, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Updated binary version mismatch at {executable}. Expected {expected}, found {version ?? "unknown"}.");
        }
    }

    private static async Task WriteResultAsync(
        string dataRoot,
        UpdateApplyResult result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, "updates", "last-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
    }
}
