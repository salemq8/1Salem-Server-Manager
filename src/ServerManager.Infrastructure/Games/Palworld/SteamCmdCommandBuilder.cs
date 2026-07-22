namespace ServerManager.Infrastructure.Games.Palworld;

public static class SteamCmdCommandBuilder
{
    public static string BuildInstallOrUpdateArguments(string destinationPath, int appId)
    {
        if (appId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(appId));
        }

        var destination = Path.GetFullPath(destinationPath);
        if (destination.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("SteamCMD destinations cannot contain quote characters.", nameof(destinationPath));
        }

        return $"+force_install_dir \"{destination}\" +login anonymous +app_update {appId} validate +quit";
    }
}
