using System.IO;
using System.Text;

namespace ServerManager.Client;

/// <summary>
/// Picks the install folder for a new Minecraft server. Every server gets its own folder named
/// after the server, made unique with -2, -3… so a second server never lands on an existing
/// one. New servers go under <c>ProgramData\1SalemServerManager\MinecraftServers</c>, not inside
/// <c>…\Minecraft</c>: that folder is the first server's own root, where a new folder could be
/// swept into its backups (<c>world*</c>, <c>logs</c>) or share its <c>mods</c> or
/// <c>plugins</c>. The folder follows the server name until the user picks one with Browse or
/// types their own.
/// </summary>
public sealed class MinecraftInstallFolder(string defaultParent, Func<string, bool>? exists = null)
{
    private const int MaxNameLength = 64;
    private const int MaxSuffix = 999;
    private const string FallbackName = "minecraft-server";

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL",
         "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, bool> _exists = exists ?? (path => Directory.Exists(path) || File.Exists(path));
    private string? _suggested;

    public static string DefaultParentFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "1SalemServerManager",
        "MinecraftServers");

    public string DefaultParent { get; } = defaultParent;

    /// <summary>True once the user chose the folder (Browse or typing); it then no longer follows the name.</summary>
    public bool IsCustom { get; private set; }

    /// <summary>The automatic folder for <paramref name="serverName"/>, or null when the user chose one.</summary>
    public string? Suggest(string? serverName)
    {
        if (IsCustom)
        {
            return null;
        }

        _suggested = Unique(DefaultParent, serverName, _exists);
        return _suggested;
    }

    /// <summary>
    /// Reports the path box's text. Anything other than the last suggestion is the user's own
    /// choice; clearing the box hands the folder back to the automatic suggestion.
    /// </summary>
    public void PathChanged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            IsCustom = false;
        }
        else if (!string.Equals(path, _suggested, StringComparison.Ordinal))
        {
            IsCustom = true;
        }
    }

    /// <summary>Browse: a unique folder for this server inside <paramref name="parent"/>, kept as the user's choice.</summary>
    public string ChooseParent(string parent, string? serverName)
    {
        IsCustom = true;
        return Unique(parent, serverName, _exists);
    }

    public static string FolderName(string? serverName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = (serverName ?? string.Empty).Trim();
        var builder = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[++index]);
                continue;
            }

            // An unpaired surrogate would reach the Agent as U+FFFD, a different folder name.
            builder.Append(char.IsControl(character) || char.IsSurrogate(character) ||
                Array.IndexOf(invalid, character) >= 0 ? '-' : character);
        }

        // Windows drops trailing dots and spaces from folder names.
        var name = builder.ToString().TrimEnd('.', ' ');
        if (name.Length > MaxNameLength)
        {
            // Never cut an emoji or other surrogate pair in half.
            var length = char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            name = name[..length].TrimEnd('.', ' ');
        }

        if (name.Trim('.', '-', ' ').Length == 0)
        {
            return FallbackName;
        }

        // CON, NUL, COM1… are device names with or without an extension, so the stem changes.
        var stem = name.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? stem + "-server" + name[stem.Length..] : name;
    }

    public static string Unique(string parent, string? serverName, Func<string, bool> exists)
    {
        var name = FolderName(serverName);
        var candidate = Path.Combine(parent, name);
        for (var suffix = 2; exists(candidate); suffix++)
        {
            candidate = suffix <= MaxSuffix
                ? Path.Combine(parent, $"{name}-{suffix}")
                : Path.Combine(parent, $"{name}-{Guid.NewGuid():N}");
        }

        return candidate;
    }
}
