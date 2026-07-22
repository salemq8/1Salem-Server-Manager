using System.Reflection;

namespace ServerManager.Contracts;

public static class ProductIdentity
{
    public const string ProductName = "1Salem Server Manager";
    public const string StableLauncherRelativePath = @"Client\1Salem.ServerManager.exe";
    public const string ServiceName = "1SalemServerManagerAgent";
    public const string StableChannel = "Stable";

    public static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', 2)[0]
        ?? assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
