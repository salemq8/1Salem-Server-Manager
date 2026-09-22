namespace ServerManager.Infrastructure.Content;

/// <summary>
/// How the Content Hub identifies itself to providers. Modrinth requires a uniquely
/// identifying User-Agent and Hangar expects requests to be attributable, so the app names
/// itself and its version. Nothing about the person or their machine is sent.
/// </summary>
public static class ContentClientDefaults
{
    public const string UserAgent = "1SalemServerManager/1.5 (Windows game server manager)";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Downloads need longer than metadata calls, but still finish or fail.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
}
