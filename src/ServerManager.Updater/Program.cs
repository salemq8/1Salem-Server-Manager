using System.Diagnostics;
using ServerManager.Infrastructure.Updates;

namespace ServerManager.Updater;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--package", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("1Salem Server Manager Updater 1.3.1");
            return 0;
        }

        try
        {
            var arguments = UpdateArguments.Parse(args);
            if (arguments.WaitProcessId is { } processId)
            {
                await WaitForProcessAsync(processId, TimeSpan.FromSeconds(30));
            }

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
            if (!result.Success)
            {
                return result.RolledBack ? 3 : 4;
            }

            if (!arguments.SkipClientStart && File.Exists(arguments.ClientPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = arguments.ClientPath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(arguments.ClientPath)!
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
        bool SkipClientStart)
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
                Find("--skip-client-start") >= 0);
        }
    }
}
