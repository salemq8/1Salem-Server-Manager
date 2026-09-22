using System.IO.Compression;

namespace ServerManager.Infrastructure.Content;

public sealed record PluginJarValidation(bool IsValid, string? Reason, string? DeclaredName = null);

/// <summary>
/// Checks that a downloaded file really is a plugin JAR before it is allowed near the
/// server's plugins folder. This reads the archive's structure only: no class is loaded and
/// no plugin code runs.
/// </summary>
public static class PluginJarValidator
{
    private const long MaximumUncompressedBytes = 1024L * 1024 * 1024;
    private const int SuspiciousCompressionRatio = 1000;

    public static PluginJarValidation Validate(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new PluginJarValidation(false, "The downloaded file is missing.");
        }

        if (new FileInfo(filePath).Length == 0)
        {
            return new PluginJarValidation(false, "The download was empty.");
        }

        ZipArchive archive;
        try
        {
            // An error page saved as .jar fails right here, which is the point.
            archive = ZipFile.OpenRead(filePath);
        }
        catch (InvalidDataException)
        {
            return new PluginJarValidation(false, "The download is not a valid JAR archive.");
        }
        catch (IOException exception)
        {
            return new PluginJarValidation(false, $"The download could not be read: {exception.Message}");
        }

        using (archive)
        {
            if (archive.Entries.Count == 0)
            {
                return new PluginJarValidation(false, "The JAR archive is empty.");
            }

            long uncompressed = 0;
            var hasClasses = false;
            string? descriptor = null;

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains("../", StringComparison.Ordinal) ||
                    Path.IsPathRooted(name) || name.Contains(':'))
                {
                    return new PluginJarValidation(
                        false,
                        "The JAR contains an unsafe entry path.");
                }

                uncompressed += entry.Length;
                if (uncompressed > MaximumUncompressedBytes)
                {
                    return new PluginJarValidation(
                        false,
                        "The JAR expands to an unreasonable size.");
                }

                if (entry.CompressedLength > 0 && entry.Length > 0 &&
                    entry.Length / entry.CompressedLength > SuspiciousCompressionRatio)
                {
                    return new PluginJarValidation(
                        false,
                        "The JAR contains a suspiciously compressed entry.");
                }

                if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                {
                    hasClasses = true;
                }

                if (name is "plugin.yml" or "paper-plugin.yml" or "bungee.yml" or "velocity-plugin.json")
                {
                    descriptor = name;
                }
            }

            if (descriptor is null)
            {
                return new PluginJarValidation(
                    false,
                    "The JAR has no plugin descriptor, so it is not a server plugin.");
            }

            if (!hasClasses)
            {
                return new PluginJarValidation(false, "The JAR contains no plugin code.");
            }

            return new PluginJarValidation(true, null, descriptor);
        }
    }
}
