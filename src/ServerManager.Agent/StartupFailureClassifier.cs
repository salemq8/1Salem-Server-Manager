using ServerManager.Contracts;

namespace ServerManager.Agent;

public static class StartupFailureClassifier
{
    public static StartupFailureDetails Classify(
        MinecraftCreationStage stage,
        MinecraftInstallRequest request,
        MinecraftInstallResult? result,
        Exception exception,
        IReadOnlyList<string> consoleLines)
    {
        var combined =
            $"{exception.Message} {string.Join(" ", consoleLines.TakeLast(20))}";
        var (code, fix) = combined.ToLowerInvariant() switch
        {
            var text when text.Contains("java") &&
                              (text.Contains("required") ||
                               text.Contains("not found") ||
                               text.Contains("missing")) =>
                ("JavaMissingOrIncompatible",
                    "Choose a compatible java.exe or approve the verified automatic Java installation."),
            var text when text.Contains("eula") =>
                ("EulaNotAccepted",
                    "Review the Minecraft EULA, explicitly accept it, and retry."),
            var text when text.Contains("port") &&
                              (text.Contains("use") ||
                               text.Contains("bind") ||
                               text.Contains("failed")) =>
                ("PortAlreadyInUse",
                    "Choose an unused TCP port or stop the process currently using this port."),
            var text when text.Contains("heap") ||
                              text.Contains("memory") ||
                              text.Contains("xms") ||
                              text.Contains("xmx") =>
                ("InvalidMemory",
                    "Lower Xmx, keep Xmx greater than or equal to Xms, and preserve the Windows reserve."),
            var text when text.Contains("hash") ||
                              text.Contains("jar") &&
                              text.Contains("invalid") =>
                ("InvalidServerJar",
                    "Retry the verified official download. If it repeats, inspect Agent networking and antivirus logs."),
            var text when text.Contains("access") ||
                              text.Contains("permission") =>
                ("FolderPermission",
                    "Choose a folder writable by the Agent service or repair its folder permissions."),
            var text when text.Contains("lock") ||
                              text.Contains("already running") =>
                ("WorldLockOrExistingProcess",
                    "Stop the existing server process and ensure the world folder is not locked."),
            var text when text.Contains("download") ||
                              text.Contains("http") ||
                              text.Contains("network") =>
                ("DownloadFailure",
                    "Check internet access to the official metadata/download service and retry."),
            var text when text.Contains("timeout") ||
                              text.Contains("startup completion") =>
                ("StartupTimeout",
                    "Open the last console lines, correct the reported server error, and retry."),
            _ =>
                ("MinecraftCreationFailed",
                    "Open the Agent log and console details, correct the failed stage, and retry.")
        };

        return new StartupFailureDetails(
            stage.ToString(),
            exception.Message,
            code,
            result?.JavaExecutablePath ?? request.JavaExecutablePath,
            result?.RootPath ?? Path.GetFullPath(request.DestinationPath),
            TryReadExitCode(consoleLines),
            code == "PortAlreadyInUse",
            result is null
                ? $"Requested Java for Minecraft {request.Version}"
                : $"Java {result.JavaMajorVersion}: {result.JavaExecutablePath}",
            $"Xms={request.MinimumMemoryMb} MB; Xmx={request.MaximumMemoryMb} MB",
            consoleLines.TakeLast(50).ToArray(),
            fix,
            true);
    }

    private static int? TryReadExitCode(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            const string marker = "code ";
            var index = line.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 &&
                int.TryParse(
                    line[(index + marker.Length)..].TrimEnd('.', ' '),
                    out var code))
            {
                return code;
            }
        }

        return null;
    }
}
