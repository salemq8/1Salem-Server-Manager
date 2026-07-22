using System.Reflection;

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
}
