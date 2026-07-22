using System.Reflection;
using ServerManager.Contracts;

namespace ServerManager.Client.Shell;

public static class ProductInfo
{
    public static string Version { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0]
        ?? typeof(ProductInfo).Assembly.GetName().Version?.ToString(3)
        ?? "Unknown";

    public static string VersionLabel => $"Version {Version}";

    public static int BuildRevision { get; } =
        ProductIdentity.BuildRevisionOf(typeof(ProductInfo).Assembly);

    public static string DiagnosticsVersionLabel =>
        $"Version {Version}{Environment.NewLine}Build {BuildRevision}";
}
