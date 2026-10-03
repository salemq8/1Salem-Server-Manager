using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftServerProvider : IGameServerProvider
{
    public GameType Game => GameType.Minecraft;

    public async Task<InstallationDetection> DetectInstallationAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return new InstallationDetection(false, root, null, [], []);
        }

        var detected = new List<string>();
        foreach (var name in new[] { "server.jar", "server.properties", "eula.txt" })
        {
            if (File.Exists(Path.Combine(root, name)))
            {
                detected.Add(name);
            }
        }

        if (Directory.Exists(Path.Combine(root, "world")))
        {
            detected.Add("world");
        }

        var version = await ReadVersionAsync(root, cancellationToken);
        var installed = detected.Contains("server.jar", StringComparer.OrdinalIgnoreCase) &&
                        detected.Contains("server.properties", StringComparer.OrdinalIgnoreCase);
        var warnings = installed
            ? Array.Empty<string>()
            : ["A server JAR and server.properties were not both detected."];
        return new InstallationDetection(installed, root, version, detected, warnings);
    }

    public ProcessLaunchSpec CreateLaunchSpec(GameServerDefinition server)
    {
        if (MinecraftSoftwareService.HasPendingMigration(server.RootPath))
            throw new InvalidOperationException("An interrupted server software migration requires runtime/configuration recovery. Starting an unverified runtime is blocked; world data must not be restored automatically.");
        return CreateSoftwareMigrationLaunchSpec(server);
    }

    internal ProcessLaunchSpec CreateSoftwareMigrationLaunchSpec(GameServerDefinition server)
    {
        if (server.Game != GameType.Minecraft)
        {
            throw new ArgumentException("The server definition is not Minecraft.", nameof(server));
        }

        var javaPathFile = Path.Combine(server.RootPath, ".1salem", "java-path.txt");
        var javaPath = !string.IsNullOrWhiteSpace(server.JavaExecutablePath)
            ? server.JavaExecutablePath
            : File.Exists(javaPathFile)
                ? File.ReadAllText(javaPathFile).Trim()
                : throw new FileNotFoundException(
                    "The managed Minecraft Java path is missing.",
                    javaPathFile);
        if (!File.Exists(javaPath))
        {
            throw new FileNotFoundException("The configured Java executable is missing.", javaPath);
        }

        var jar = Path.Combine(server.RootPath, "server.jar");
        if (!File.Exists(jar))
        {
            throw new FileNotFoundException("The Minecraft server JAR is missing.", jar);
        }

        var (minimumMemoryMb, maximumMemoryMb) = ResolveMemory(server);
        return new ProcessLaunchSpec(
            javaPath,
            "@user_jvm_args.txt -jar server.jar nogui",
            server.RootPath,
            new Dictionary<string, string>(),
            ArgumentList:
            [
                $"-Xms{minimumMemoryMb}M",
                $"-Xmx{maximumMemoryMb}M",
                "-jar",
                "server.jar",
                "nogui"
            ]);
    }

    private static (int MinimumMemoryMb, int MaximumMemoryMb) ResolveMemory(
        GameServerDefinition server)
    {
        if (server.MinimumMemoryMb is > 0 &&
            server.MaximumMemoryMb >= server.MinimumMemoryMb)
        {
            return (server.MinimumMemoryMb.Value, server.MaximumMemoryMb.Value);
        }

        var argumentsPath = Path.Combine(server.RootPath, "user_jvm_args.txt");
        if (File.Exists(argumentsPath))
        {
            var minimum = ReadMemoryArgument(argumentsPath, "-Xms");
            var maximum = ReadMemoryArgument(argumentsPath, "-Xmx");
            if (minimum is > 0 && maximum >= minimum)
            {
                return (minimum.Value, maximum.Value);
            }
        }

        return (1024, 4096);
    }

    private static int? ReadMemoryArgument(string path, string prefix)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();
            if (value.EndsWith('M') &&
                int.TryParse(
                    value[..^1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var mebibytes))
            {
                return mebibytes;
            }

            if (value.EndsWith('G') &&
                int.TryParse(
                    value[..^1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var gibibytes))
            {
                return checked(gibibytes * 1024);
            }
        }

        return null;
    }

    private static async Task<string?> ReadVersionAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(root, ".1salem", "metadata.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(metadataPath);
        var metadata = await JsonSerializer.DeserializeAsync<MinecraftServerMetadata>(
            stream,
            cancellationToken: cancellationToken);
        return metadata?.Version;
    }
}
