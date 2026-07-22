using System.Diagnostics;
using System.Reflection;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Updates;

namespace ServerManager.Updater;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--package", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"1Salem Server Manager Updater {ProductIdentity.VersionOf(Assembly.GetExecutingAssembly())}");
            return 0;
        }

        var rollbackIndex = Array.FindIndex(
            args,
            value => value.Equals("--validate-rollback", StringComparison.OrdinalIgnoreCase));
        if (rollbackIndex >= 0)
        {
            if (rollbackIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("--validate-rollback requires an absolute snapshot path.");
                return 2;
            }

            var valid = VersionedUpdateInstaller.ValidateRollbackSnapshot(
                Path.GetFullPath(args[rollbackIndex + 1]));
            Console.WriteLine(valid
                ? "Rollback snapshot is complete and readable."
                : "Rollback snapshot is incomplete.");
            return valid ? 0 : 4;
        }

        try
        {
            var arguments = UpdateArguments.Parse(args);
            if (arguments.WaitProcessId is { } processId)
            {
                if (arguments.CloseDashboard)
                {
                    await DashboardProcessShutdown.CloseOnlyDashboardAsync(
                        processId,
                        arguments.InstallRoot,
                        arguments.ClientPath);
                }
                else
                {
                    await WaitForProcessAsync(processId, TimeSpan.FromSeconds(30));
                }
            }

            string launchPath;
            bool success;
            bool rolledBack;
            if (arguments.VersionedInstall)
            {
                var installer = new VersionedUpdateInstaller();
                var result = await installer.ApplyAsync(new VersionedUpdateOptions(
                    arguments.PackagePath,
                    arguments.InstallRoot,
                    arguments.DataRoot,
                    arguments.Version,
                    arguments.ServiceName,
                    ActivateAgent: arguments.RestartAgent &&
                        !arguments.DeferAgent &&
                        !arguments.SkipServiceControl,
                    AllowSameVersion: arguments.AllowSameVersion,
                    SkipServiceHealthCheck: arguments.SkipHealthCheck,
                    HealthUri: new Uri("http://127.0.0.1:5251/health"),
                    VerifyOnly: arguments.VerifyOnly,
                    TargetBuildRevision: arguments.BuildRevision,
                    TargetPackageSha256: arguments.PackageSha256,
                    AllowManagedGameAgentRestart: arguments.AllowManagedGameAgentRestart));
                Console.WriteLine(result.Message);
                success = result.Success;
                rolledBack = result.RolledBack;
                launchPath = result.StableLauncherPath ?? arguments.ClientPath;
            }
            else
            {
                var applier = new UpdatePackageApplier();
                var result = await applier.ApplyAsync(new UpdateApplyOptions(
                    arguments.PackagePath,
                    arguments.InstallRoot,
                    arguments.DataRoot,
                    arguments.Version,
                    arguments.RestartAgent,
                    arguments.ServiceName,
                    arguments.SkipServiceControl,
                    arguments.SkipHealthCheck,
                    new Uri("http://127.0.0.1:5251/health"),
                    arguments.ClientPath));
                Console.WriteLine(result.Message);
                success = result.Success;
                rolledBack = result.RolledBack;
                launchPath = arguments.ClientPath;
            }

            if (!success)
            {
                return rolledBack ? 3 : 4;
            }

            if (!arguments.SkipClientStart &&
                !arguments.VerifyOnly &&
                File.Exists(launchPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = launchPath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(launchPath)!
                });
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            FileNotFoundException or
            UnauthorizedAccessException or
            IOException)
        {
            Console.Error.WriteLine($"Update failed safely: {exception.Message}");
            return 2;
        }
    }

    private static async Task WaitForProcessAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (ArgumentException)
        {
        }
        catch (OperationCanceledException)
        {
            throw new IOException(
                "The dashboard did not close in time; no application files were changed.");
        }
    }

    private sealed record UpdateArguments(
        string PackagePath,
        string InstallRoot,
        string DataRoot,
        string Version,
        string ServiceName,
        string ClientPath,
        bool RestartAgent,
        int? WaitProcessId,
        bool SkipServiceControl,
        bool SkipHealthCheck,
        bool SkipClientStart,
        bool VersionedInstall,
        bool DeferAgent,
        bool AllowSameVersion,
        bool VerifyOnly,
        bool CloseDashboard,
        int BuildRevision,
        string? PackageSha256,
        bool AllowManagedGameAgentRestart)
    {
        public static UpdateArguments Parse(IReadOnlyList<string> args)
        {
            string Required(string name)
            {
                var index = Find(name);
                if (index < 0 || index + 1 >= args.Count)
                {
                    throw new ArgumentException($"{name} is required.");
                }

                return args[index + 1];
            }

            int Find(string name)
            {
                for (var index = 0; index < args.Count; index++)
                {
                    if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                return -1;
            }

            int? waitProcessId = null;
            var waitIndex = Find("--wait-pid");
            if (waitIndex >= 0 &&
                (waitIndex + 1 >= args.Count ||
                 !int.TryParse(args[waitIndex + 1], out var parsedPid) ||
                 parsedPid <= 0))
            {
                throw new ArgumentException("--wait-pid must contain a positive process ID.");
            }

            if (waitIndex >= 0)
            {
                waitProcessId = int.Parse(
                    args[waitIndex + 1],
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            var buildRevision = 0;
            var buildIndex = Find("--build-revision");
            if (buildIndex >= 0 &&
                (buildIndex + 1 >= args.Count ||
                 !int.TryParse(args[buildIndex + 1], out buildRevision) ||
                 buildRevision <= 0))
            {
                throw new ArgumentException(
                    "--build-revision must contain a positive integer.");
            }

            var hashIndex = Find("--package-sha256");
            var packageSha256 = hashIndex >= 0 && hashIndex + 1 < args.Count
                ? args[hashIndex + 1]
                : null;

            return new UpdateArguments(
                Path.GetFullPath(Required("--package")),
                Path.GetFullPath(Required("--install-root")),
                Path.GetFullPath(Required("--data-root")),
                Required("--version"),
                Required("--service-name"),
                Path.GetFullPath(Required("--client")),
                Find("--restart-agent") >= 0,
                waitProcessId,
                Find("--skip-service-control") >= 0,
                Find("--skip-health-check") >= 0,
                Find("--skip-client-start") >= 0,
                Find("--versioned-install") >= 0,
                Find("--defer-agent") >= 0,
                Find("--allow-same-version") >= 0,
                Find("--verify-only") >= 0,
                Find("--close-dashboard") >= 0,
                buildRevision,
                packageSha256,
                Find("--allow-managed-game-agent-restart") >= 0);
        }
    }
}
