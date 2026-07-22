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
            var buildText = Value(args, "--build-revision") ??
                throw new ArgumentException(
                    "preflight requires --build-revision <positive integer>.");
            if (!int.TryParse(buildText, out var buildRevision) || buildRevision <= 0)
            {
                throw new ArgumentException(
                    "--build-revision must contain a positive integer.");
            }

            var decision = ReleaseVersionPolicy.EvaluateBuild(
                target,
                buildRevision,
                audit.Sources.Select(source => new ProductBuildIdentity(
                    source.Version,
                    source.BuildRevision)),
                args.Contains("--repair-same-build", StringComparer.OrdinalIgnoreCase));
            Console.WriteLine(decision.Message);
            return decision.CanInstall ? 0 : 5;
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
        Console.WriteLine($"Source BUILD_REVISION: {audit.SourceBuildRevision}");
        Console.WriteLine($"Highest known version: {audit.HighestKnownVersion ?? "none"}");
        Console.WriteLine($"Highest known build: {audit.HighestKnownBuildRevision}");
        foreach (var source in audit.Sources)
        {
            Console.WriteLine(
                $"{source.Kind}: {source.Version} Build {source.BuildRevision} ({source.Path})");
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

public sealed record VersionSource(
    string Kind,
    string Version,
    string Path,
    int BuildRevision = 0);

public sealed record VersionAudit(
    string SourceVersion,
    int SourceBuildRevision,
    string? HighestKnownVersion,
    int HighestKnownBuildRevision,
    InstalledVersionReport Installed,
    IReadOnlyList<VersionSource> Sources);

public static partial class VersionAuditor
{
    public static VersionAudit Audit(string repository)
    {
        var sourceVersion = File.ReadAllText(Path.Combine(repository, "VERSION")).Trim();
        _ = SemanticVersion.Parse(sourceVersion);
        var buildText = File.ReadAllText(Path.Combine(repository, "BUILD_REVISION")).Trim();
        if (!int.TryParse(buildText, out var sourceBuildRevision) ||
            sourceBuildRevision <= 0)
        {
            throw new FormatException("BUILD_REVISION must contain a positive integer.");
        }
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

                var releaseBuildRevision = 0;
                var manifest = Path.Combine(directory, "version.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (document.RootElement.TryGetProperty("version", out var value) &&
                            SemanticVersion.TryParse(value.GetString(), out var parsed))
                        {
                            releaseBuildRevision = document.RootElement.TryGetProperty(
                                    "buildRevision",
                                    out var buildValue) &&
                                buildValue.TryGetInt32(out var parsedBuild)
                                ? parsedBuild
                                : 0;
                            sources.Add(new VersionSource(
                                "release manifest",
                                parsed.ToString(),
                                manifest,
                                releaseBuildRevision));
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                sources.Add(new VersionSource(
                    "release folder",
                    name,
                    directory,
                    releaseBuildRevision));

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
                            package,
                            releaseBuildRevision));
                    }
                }
            }
        }

        var highest = sources
            .OrderByDescending(source => SemanticVersion.Parse(source.Version))
            .ThenByDescending(source => source.BuildRevision)
            .FirstOrDefault();
        return new VersionAudit(
            sourceVersion,
            sourceBuildRevision,
            highest?.Version,
            highest?.BuildRevision ?? 0,
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
                    component.ExecutablePath ?? component.Source,
                    component.BuildRevision));
            }

            if (!string.IsNullOrWhiteSpace(component.StagedVersion))
            {
                sources.Add(new VersionSource(
                    $"staged {component.Name}",
                    component.StagedVersion,
                    component.StagedPath ?? component.Source,
                    component.StagedBuildRevision ?? 0));
            }
        }

        foreach (var component in installed.RunningComponents)
        {
            if (!string.IsNullOrWhiteSpace(component.Version))
            {
                sources.Add(new VersionSource(
                    $"running {component.Name}",
                    component.Version,
                    component.ExecutablePath ?? component.Source,
                    component.BuildRevision));
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

    [GeneratedRegex(@"^1SalemServerManager-Update-(\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?)\.zip$")]
    private static partial Regex UpdatePackageVersion();
}
