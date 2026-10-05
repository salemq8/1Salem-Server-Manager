using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public static class MinecraftSoftwareSafety
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool RequiresWorldReset(ServerPlatform current, ServerPlatform target) =>
        current == ServerPlatform.Vanilla && target is ServerPlatform.Paper or ServerPlatform.Purpur;

    public static string LevelName(string root)
    {
        var file = SafePathPolicy.ResolveWithinRoot(root, "server.properties");
        var properties = MinecraftPropertiesSerializer.Parse(File.Exists(file) ? MinecraftPropertiesSerializer.ReadFile(file) : "");
        var name = properties.GetValueOrDefault("level-name") ?? "world";
        // Fail closed for Java-properties escapes, absolute paths, aliases, or traversal.
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') ||
            name.Contains(':') || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.'))
            throw new InvalidDataException("The level-name is not a safe single directory name.");
        _ = SafePathPolicy.ResolveWithinRoot(root, name);
        return name;
    }

    public static string? MigrationBlock(ServerPlatform current, ServerPlatform target, string version,
        string root, string levelName)
    {
        if (current == ServerPlatform.Unknown) return "CurrentSoftwareUnknown";
        if (current == target) return "AlreadyInstalled";
        if (target == ServerPlatform.Spigot) return "SpigotBuildToolsRequired";
        if (target is not (ServerPlatform.Vanilla or ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Folia))
            return "UnsupportedSoftware";
        // The preserving path never converts layouts. Vanilla -> Paper/Purpur uses the
        // separate, explicitly confirmed fresh-world reset path instead.
        if (current == ServerPlatform.Vanilla || target == ServerPlatform.Vanilla)
            return "WorldLayoutChangeRequired";
        if (current == ServerPlatform.Folia || target == ServerPlatform.Folia)
            return "FoliaCompatibilityNotVerified";
        if (current is not (ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Spigot or ServerPlatform.Bukkit))
            return "UnsupportedSoftware";
        var modern = int.TryParse(version.Split('.')[0], out var major) && major >= 26;
        if (modern && current is ServerPlatform.Spigot or ServerPlatform.Bukkit)
            return "WorldLayoutChangeRequired";
        var world = SafePathPolicy.ResolveWithinRoot(root, levelName);
        if (!Directory.Exists(world) || !File.Exists(Path.Combine(world, "level.dat")))
            return "WorldLayoutUnverified";
        if (modern && (Directory.Exists(Path.Combine(root, levelName + "_nether")) ||
                       Directory.Exists(Path.Combine(root, levelName + "_the_end")) ||
                       Directory.Exists(Path.Combine(world, "DIM-1")) ||
                       Directory.Exists(Path.Combine(world, "DIM1"))))
            return "WorldLayoutChangeRequired";
        if (!modern && (Directory.Exists(Path.Combine(world, "DIM-1")) ||
                        Directory.Exists(Path.Combine(world, "DIM1"))))
            return "WorldLayoutChangeRequired";
        return null;
    }

    public static string Message(string code) => code switch
    {
        "Ready" => "Same Minecraft version; automatic verified backup and runtime-only migration.",
        "WorldResetRequired" => "Requires deleting the current world and starting fresh. Existing world/player progress will not carry over. The server must stop safely before your explicit confirmation. Existing backups and server settings are preserved.",
        "AlreadyInstalled" => "This software is already installed.",
        "WorldLayoutChangeRequired" => "This transition requires the server software to move world or gamerule data. Blocked to preserve exact world paths; no files were changed.",
        "WorldLayoutUnverified" => "The existing world layout could not be verified. Migration is blocked.",
        "FoliaCompatibilityNotVerified" => "Folia requires separately verified region-threading and plugin compatibility; automatic migration is unavailable.",
        "SpigotBuildToolsRequired" => "Spigot requires official BuildTools; no official prebuilt runtime is available for automatic download.",
        "ExactVersionUnavailable" => "No official verified build exists for this exact Minecraft version. No version substitution or downgrade is allowed.",
        "ProviderUnavailable" => "The official software provider could not be reached; availability is unknown.",
        "CurrentSoftwareUnknown" => "The active server.jar could not be identified safely.",
        "InterruptedMigration" => "An interrupted software migration requires runtime/configuration recovery; another migration is blocked.",
        _ => "This software transition is not supported safely."
    };

    public static (ServerPlatform Platform, string? Version) Inspect(string root, string? registeredVersion)
    {
        var jar = SafePathPolicy.ResolveWithinRoot(root, "server.jar");
        if (!File.Exists(jar)) return (ServerPlatform.Unknown, registeredVersion);
        using var input = File.OpenRead(jar);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        var marker = SafePathPolicy.ResolveWithinRoot(root, ".1salem/software.json");
        if (File.Exists(marker))
        {
            try
            {
                var value = JsonSerializer.Deserialize<InstalledSoftware>(File.ReadAllText(marker), Json);
                if (value is not null && value.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return (value.Platform, value.MinecraftVersion);
            }
            catch (JsonException) { }
        }
        input.Position = 0;
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var manifest = ReadSmall(zip.GetEntry("META-INF/MANIFEST.MF"));
            var versionText = ReadSmall(zip.GetEntry("version.json"));
            string? version = null;
            if (!string.IsNullOrWhiteSpace(versionText))
            {
                using var versionDocument = JsonDocument.Parse(versionText);
                if (versionDocument.RootElement.TryGetProperty("id", out var id)) version = id.GetString();
            }
            if (manifest.Contains("Main-Class: net.minecraft.bundler.Main", StringComparison.Ordinal) ||
                manifest.Contains("Main-Class: net.minecraft.server.Main", StringComparison.Ordinal))
                return (ServerPlatform.Vanilla, version);
            // Only active-JAR branding counts: an old version_history.json or another JAR in
            // the root can describe a different runtime and must never override this one.
            var branding = string.Join('\n', manifest.Split('\n').Where(line => !line.StartsWith("Main-Class:", StringComparison.Ordinal))) +
                "\n" + ReadSmall(zip.GetEntry("patch.properties")) + "\n" + ReadSmall(zip.GetEntry("META-INF/versions.list")) +
                "\n" + ReadSmall(zip.GetEntry("META-INF/patches.list"));
            foreach (var (name, platform) in new[] { ("purpur", ServerPlatform.Purpur),
                         ("folia", ServerPlatform.Folia), ("spigot", ServerPlatform.Spigot) })
                if (branding.Contains(name, StringComparison.OrdinalIgnoreCase)) return (platform, version);
            // Paperclip is shared by multiple forks: its bootstrap Main-Class alone is not
            // evidence of Paper. Require actual artifact/implementation branding.
            if (branding.Contains("Paper", StringComparison.OrdinalIgnoreCase)) return (ServerPlatform.Paper, version);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        return (ServerPlatform.Unknown, registeredVersion);
    }

    public static void ValidateJar(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (!ReadSmall(zip.GetEntry("META-INF/MANIFEST.MF")).Contains("Main-Class:", StringComparison.Ordinal))
            throw new InvalidDataException("The staged file is not an executable server JAR.");
    }

    private static string ReadSmall(ZipArchiveEntry? entry)
    {
        if (entry is null) return "";
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Runtime metadata is too large.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public sealed record InstalledSoftware(ServerPlatform Platform, string MinecraftVersion, string Build, string Sha256);
}
