using System.IO;

namespace ServerManager.Setup;

public static class InstallerPreflight
{
    public static void ValidateRequest(InstallRequest request, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode is not ("AllInOne" or "ClientOnly" or "AgentOnly"))
        {
            throw new ArgumentException("Select a supported install mode.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InstallRoot) ||
            !Path.IsPathFullyQualified(request.InstallRoot))
        {
            throw new ArgumentException("Choose an absolute application folder.", nameof(request));
        }

        if (request.Mode == "ClientOnly" && request.InstallAgentService)
        {
            throw new ArgumentException(
                "Agent service installation is not available in Client-only mode.",
                nameof(request));
        }

        if ((request.InstallAgentService || request.AddFirewallRules) && !isElevated)
        {
            throw new InstallerFailureException(
                "Administrator preflight",
                "Setup must be run as Administrator for the selected service or firewall options.",
                "The Setup executable manifest requests UAC elevation. Close Setup, " +
                "right-click Setup.exe, and choose Run as administrator.");
        }
    }

    public static void ValidateDriveSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(path) ??
            throw new ArgumentException("The installation drive is invalid.", nameof(path));
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"At least {requiredBytes / (1024 * 1024)} MiB of free space is required.");
        }
    }

    public static void ValidatePayload(
        string contentRoot,
        bool includesClient,
        bool includesAgent)
    {
        if (includesClient)
        {
            RequireFile(
                Path.Combine(contentRoot, "Client", "1Salem.ServerManager.exe"),
                "The Setup payload is missing the dashboard executable.");
        }

        if (includesAgent)
        {
            RequireFile(
                Path.Combine(
                    contentRoot,
                    "Agent",
                    "1Salem.ServerManager.Agent.exe"),
                "The Setup payload is missing the Agent executable.");
        }
    }

    public static void VerifyFolderWritable(string path)
    {
        Directory.CreateDirectory(path);
        var testPath = Path.Combine(path, $".1salem-write-test-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(
                testPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
        }
        finally
        {
            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new InstallerFailureException(
                "Payload preflight",
                message,
                $"Expected file: {path}");
        }
    }
}
