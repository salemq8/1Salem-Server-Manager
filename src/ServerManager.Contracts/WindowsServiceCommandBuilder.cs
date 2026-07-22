namespace ServerManager.Contracts;

public static class WindowsServiceCommandBuilder
{
    public static string BuildBinaryPath(string agentExecutablePath, string dataRoot)
    {
        if (!Path.IsPathFullyQualified(agentExecutablePath))
        {
            throw new ArgumentException(
                "The Agent executable path must be absolute.",
                nameof(agentExecutablePath));
        }

        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException(
                "The Agent data root must be absolute.",
                nameof(dataRoot));
        }

        return
            $"\"{Path.GetFullPath(agentExecutablePath)}\" --service " +
            $"--data-root \"{Path.GetFullPath(dataRoot)}\"";
    }

    public static IReadOnlyList<string> CreateArguments(
        string serviceName,
        string displayName,
        string binaryPath,
        string startType = "auto")
    {
        Validate(serviceName, displayName, binaryPath, startType);
        return
        [
            "create",
            serviceName,
            "binPath=",
            binaryPath,
            "start=",
            startType,
            "DisplayName=",
            displayName
        ];
    }

    public static IReadOnlyList<string> ConfigureArguments(
        string serviceName,
        string binaryPath,
        string startType = "auto")
    {
        Validate(serviceName, "display name", binaryPath, startType);
        return
        [
            "config",
            serviceName,
            "binPath=",
            binaryPath,
            "start=",
            startType
        ];
    }

    private static void Validate(
        string serviceName,
        string displayName,
        string binaryPath,
        string startType)
    {
        if (string.IsNullOrWhiteSpace(serviceName) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(binaryPath) ||
            string.IsNullOrWhiteSpace(startType))
        {
            throw new ArgumentException("Windows Service command values cannot be empty.");
        }
    }
}
