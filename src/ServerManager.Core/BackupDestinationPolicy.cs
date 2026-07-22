using ServerManager.Contracts;

namespace ServerManager.Core;

public static class BackupDestinationPolicy
{
    public const long MinimumFreeSpaceBytes = 256L * 1024 * 1024;

    public static string GetDefaultRoot(GameServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.GetTempPath();
        }

        return Path.Combine(
            programData,
            "1Salem Server Manager",
            "Backups",
            server.Game.ToString(),
            server.Id.ToString("N"));
    }

    public static BackupDestinationValidationResult Validate(
        GameServerDefinition server,
        string destinationRoot,
        long estimatedRequiredBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        string destination;
        try
        {
            destination = Path.GetFullPath(destinationRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return Invalid(
                "InvalidDestinationPath",
                exception.Message,
                destinationRoot);
        }

        var liveSave = Path.Combine(server.RootPath, "Pal", "Saved");
        if (SafePathPolicy.IsWithinRoot(destination, liveSave) ||
            SafePathPolicy.IsWithinRoot(liveSave, destination))
        {
            return Invalid(
                "DestinationInsideLiveSave",
                "The backup destination cannot be inside, or contain, the live Palworld save folder.",
                destination);
        }

        if (SafePathPolicy.IsWithinRoot(destination, server.RootPath))
        {
            return Invalid(
                "DestinationInsideServerRoot",
                "The backup destination cannot be inside the live game-server installation.",
                destination);
        }

        var applicationRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (SafePathPolicy.IsWithinRoot(destination, applicationRoot))
        {
            return Invalid(
                "DestinationInsideApplication",
                "The backup destination cannot be inside the application installation.",
                destination);
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (SafePathPolicy.IsWithinRoot(destination, temporaryRoot))
        {
            return Invalid(
                "TemporaryDestinationRejected",
                "Temporary folders cannot be used for persistent backups.",
                destination);
        }

        try
        {
            Directory.CreateDirectory(destination);
            var probe = Path.Combine(
                destination,
                $".1salem-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            var root = Path.GetPathRoot(destination);
            var available = string.IsNullOrWhiteSpace(root)
                ? 0
                : new DriveInfo(root).AvailableFreeSpace;
            var required = Math.Max(MinimumFreeSpaceBytes, estimatedRequiredBytes);
            if (available < required)
            {
                return new BackupDestinationValidationResult(
                    false,
                    "InsufficientFreeSpace",
                    $"The destination has {available} bytes available but at least {required} bytes are required.",
                    destination,
                    available);
            }

            return new BackupDestinationValidationResult(
                true,
                "DestinationReady",
                "The backup destination exists, is writable, and has sufficient free space.",
                destination,
                available);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return Invalid(
                exception is UnauthorizedAccessException
                    ? "DestinationNotWritable"
                    : "DestinationUnavailable",
                exception.Message,
                destination);
        }
    }

    private static BackupDestinationValidationResult Invalid(
        string code,
        string message,
        string path) =>
        new(false, code, message, path, 0);
}
