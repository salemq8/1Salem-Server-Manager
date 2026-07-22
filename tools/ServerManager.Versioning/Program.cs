using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Versioning;

public static partial class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "audit";
            var repository = Path.GetFullPath(Value(args, "--repo") ??
                FindRepositoryRoot(AppContext.BaseDirectory));
            var audit = VersionAuditor.Audit(repository);
            if (command == "audit")
            {
                WriteAudit(audit, args.Contains("--json", StringComparer.OrdinalIgnoreCase));
                return 0;
            }

            if (command != "preflight")
            {
                throw new ArgumentException("Command must be audit or preflight.");
            }

            var target = Value(args, "--target") ??
                throw new ArgumentException("preflight requires --target <version>.");
            var decision = ReleaseVersionPolicy.Evaluate(
                target,
                audit.Sources.Select(source => source.Version),
                args.Contains("--rebuild-same-version", StringComparer.OrdinalIgnoreCase));
            Console.WriteLine(decision.Message);
            return decision.Allowed ? 0 : 5;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or IOException or
                UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Version audit failed safely: {exception.Message}");
            return 2;
        }
    }

    private static void WriteAudit(VersionAudit audit, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                audit,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                }));
            return;
        }

        Console.WriteLine($"Source VERSION: {audit.SourceVersion}");
        Console.WriteLine($"Highest known version: {audit.HighestKnownVersion ?? "none"}");
        foreach (var source in audit.Sources)
        {
            Console.WriteLine($"{source.Kind}: {source.Version} ({source.Path})");
        }
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

public sealed record VersionSource(string Kind, string Version, string Path);

public sealed record VersionAudit(
    string SourceVersion,
    string? HighestKnownVersion,
    InstalledVersionReport Installed,
    IReadOnlyList<VersionSource> Sources);

public static partial class VersionAuditor
{
    public static VersionAudit Audit(string repository)
    {
        var sourceVersion = File.ReadAllText(Path.Combine(repository, "VERSION")).Trim();
        _ = SemanticVersion.Parse(sourceVersion);
        var installed = InstalledVersionDetector.Detect();
        var sources = new List<VersionSource>();
        AddInstalled(sources, installed);
        var releaseRoot = Path.Combine(repository, "artifacts", "release");
        if (Directory.Exists(releaseRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(releaseRoot))
            {
                var name = Path.GetFileName(directory);
                if (!SemanticVersion.TryParse(name, out _))
                {
                    continue;
                }

                sources.Add(new VersionSource("release folder", name, directory));
                var manifest = Path.Combine(directory, "version.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (document.RootElement.TryGetProperty("version", out var value) &&
                            SemanticVersion.TryParse(value.GetString(), out var parsed))
                        {
                            sources.Add(new VersionSource(
                                "release manifest",
                                parsed.ToString(),
                                manifest));
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                foreach (var package in Directory.EnumerateFiles(
                             directory,
                             "1SalemServerManager-Update-*.zip",
                             SearchOption.TopDirectoryOnly))
                {
                    var match = UpdatePackageVersion().Match(Path.GetFileName(package));
                    if (match.Success &&
                        SemanticVersion.TryParse(match.Groups[1].Value, out var parsed))
                    {
                        sources.Add(new VersionSource(
                            "update package",
                            parsed.ToString(),
                            package));
                    }
                }
            }
        }

        var highest = sources
            .Select(source => SemanticVersion.Parse(source.Version))
            .OrderDescending()
            .FirstOrDefault();
        return new VersionAudit(
            sourceVersion,
            sources.Count == 0 ? null : highest.ToString(),
            installed,
            sources
                .DistinctBy(source => (source.Kind, source.Version, source.Path))
                .OrderBy(source => source.Kind, StringComparer.Ordinal)
                .ThenBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static void AddInstalled(
        ICollection<VersionSource> sources,
        InstalledVersionReport installed)
    {
        foreach (var component in new[]
                 {
                     installed.Client,
                     installed.Agent,
                     installed.Updater
                 })
        {
            if (!string.IsNullOrWhiteSpace(component.Version))
            {
                sources.Add(new VersionSource(
                    $"installed {component.Name}",
                    component.Version,
                    component.ExecutablePath ?? component.Source));
            }

            if (!string.IsNullOrWhiteSpace(component.StagedVersion))
            {
                sources.Add(new VersionSource(
                    $"staged {component.Name}",
                    component.StagedVersion,
                    component.StagedPath ?? component.Source));
            }
        }

        foreach (var component in installed.RunningComponents)
        {
            if (!string.IsNullOrWhiteSpace(component.Version))
            {
                sources.Add(new VersionSource(
                    $"running {component.Name}",
                    component.Version,
                    component.ExecutablePath ?? component.Source));
            }
        }

        if (!string.IsNullOrWhiteSpace(installed.RegistryVersion))
        {
            sources.Add(new VersionSource(
                "uninstall registry",
                installed.RegistryVersion,
                "HKLM uninstall registration"));
        }
    }

    [GeneratedRegex(@"^1SalemServerManager-Update-(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\.zip$")]
    private static partial Regex UpdatePackageVersion();
}
