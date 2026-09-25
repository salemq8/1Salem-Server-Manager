using System.Reflection;

namespace ServerManager.Connect.App.Services;

/// <summary>The product version and build revision stamped by Directory.Build.props.</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var revision = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildRevision")?.Value;
        return revision is null ? version : $"{version} (build {revision})";
    }
}
