using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Updates;

public static class ApplicationUpdateEndpointCatalog
{
    public const string StableManifest =
        "https://updates.1salem.app/server-manager/stable/version.json";
    public const string BetaManifest =
        "https://updates.1salem.app/server-manager/beta/version.json";

    public static Uri GetManifestUri(ApplicationUpdateChannel channel) =>
        new(channel == ApplicationUpdateChannel.Beta
            ? BetaManifest
            : StableManifest);
}
