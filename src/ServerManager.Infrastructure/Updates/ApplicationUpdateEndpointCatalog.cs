using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Updates;

public static class ApplicationUpdateEndpointCatalog
{
    public const string StableManifest =
        "https://updates.1salem.app/server-manager/stable/version.json";
    public const string PreviewManifest =
        "https://updates.1salem.app/server-manager/preview/version.json";
    public const string DevelopmentManifest =
        "https://updates.1salem.app/server-manager/development/version.json";

    public static Uri GetManifestUri(ApplicationUpdateChannel channel) =>
        new(channel switch
        {
            ApplicationUpdateChannel.Preview => PreviewManifest,
            ApplicationUpdateChannel.Development => DevelopmentManifest,
            _ => StableManifest
        });
}
