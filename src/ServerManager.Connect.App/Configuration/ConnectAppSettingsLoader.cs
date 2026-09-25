using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerManager.Connect.App.Configuration;

/// <summary>The settings plus, when the file was unusable, why (for Diagnostics).</summary>
public sealed record SettingsLoadResult(ConnectAppSettings Settings, string? Problem);

/// <summary>
/// Reads <c>1Salem.Connect.settings.json</c> from the app directory:
/// <code>
/// { "brokerUrl": "https://…", "developmentMode": false, "transportMode": "tsnet",
///   "fakeNodeId": "…", "transportPath": "1Salem.Connect.Transport.exe" }
/// </code>
/// A missing file means "not configured". A file that is present but wrong also leaves the app
/// unconfigured, with the reason recorded: running against a half-understood configuration would
/// be worse than refusing to run. Unknown members are refused so a typo cannot silently drop a
/// setting such as <c>developmentMode</c>.
/// </summary>
public static partial class ConnectAppSettingsLoader
{
    public const string FileName = "1Salem.Connect.settings.json";

    private const int MaxFileBytes = 16 * 1024;

    private static readonly HashSet<string> KnownMembers =
        ["brokerUrl", "developmentMode", "transportMode", "fakeNodeId", "transportPath"];

    public static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, FileName);

    public static SettingsLoadResult Load(string path)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        var unconfigured = ConnectAppSettings.Unconfigured(baseDirectory);
        byte[] content;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new SettingsLoadResult(unconfigured, null);
            }

            if (file.Length > MaxFileBytes)
            {
                return new SettingsLoadResult(unconfigured, "The settings file is larger than 16 KiB.");
            }

            content = File.ReadAllBytes(file.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(unconfigured, $"The settings file could not be read: {exception.Message}");
        }

        return Parse(content, baseDirectory, unconfigured);
    }

    private static SettingsLoadResult Parse(byte[] content, string baseDirectory, ConnectAppSettings unconfigured)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new SettingsLoadResult(unconfigured, "The settings file is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingsLoadResult(unconfigured, "The settings file must contain one JSON object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!KnownMembers.Contains(member.Name))
                {
                    return new SettingsLoadResult(unconfigured, $"Unknown setting '{member.Name}'.");
                }

                // A repeated member would let two readers of the same file disagree on its value.
                if (!seen.Add(member.Name))
                {
                    return new SettingsLoadResult(unconfigured, $"The setting '{member.Name}' appears twice.");
                }
            }

            return Interpret(root, baseDirectory, unconfigured);
        }
    }

    private static SettingsLoadResult Interpret(JsonElement root, string baseDirectory, ConnectAppSettings unconfigured)
    {
        if (!TryReadBoolean(root, "developmentMode", out var developmentMode) ||
            !TryReadString(root, "brokerUrl", out var brokerText) ||
            !TryReadString(root, "transportMode", out var modeText) ||
            !TryReadString(root, "fakeNodeId", out var fakeNodeId) ||
            !TryReadString(root, "transportPath", out var transportPath))
        {
            return new SettingsLoadResult(unconfigured, "A setting has the wrong JSON type.");
        }

        if (string.IsNullOrEmpty(brokerText))
        {
            return new SettingsLoadResult(unconfigured, null);
        }

        if (!Uri.TryCreate(brokerText, UriKind.Absolute, out var brokerUrl))
        {
            return new SettingsLoadResult(unconfigured, "brokerUrl refused: not an absolute URL.");
        }

        if (!BrokerEndpointPolicy.IsAllowed(brokerUrl, developmentMode, out var reason))
        {
            return new SettingsLoadResult(unconfigured, "brokerUrl refused: " + reason);
        }

        TransportMode mode;
        switch (modeText)
        {
            case null or "tsnet":
                mode = TransportMode.Tsnet;
                if (fakeNodeId is not null)
                {
                    return new SettingsLoadResult(unconfigured, "fakeNodeId applies to the fake transport only.");
                }

                break;
            case "fake":
                // The fake transport's node id is self-asserted. It must never be reachable from a
                // release configuration.
                if (!developmentMode)
                {
                    return new SettingsLoadResult(unconfigured, "The fake transport requires developmentMode.");
                }

                if (fakeNodeId is null || !FakeNodeIdPattern().IsMatch(fakeNodeId))
                {
                    return new SettingsLoadResult(unconfigured, "The fake transport requires a fakeNodeId of 1-128 characters [A-Za-z0-9._:-].");
                }

                mode = TransportMode.Fake;
                break;
            default:
                return new SettingsLoadResult(unconfigured, "transportMode must be \"tsnet\" or \"fake\".");
        }

        return new SettingsLoadResult(
            new ConnectAppSettings
            {
                BrokerUrl = brokerUrl,
                DevelopmentMode = developmentMode,
                TransportMode = mode,
                FakeNodeId = mode == TransportMode.Fake ? fakeNodeId : null,
                TransportExecutablePath = string.IsNullOrEmpty(transportPath)
                    ? ConnectAppSettings.DefaultTransportPath(baseDirectory)
                    : Path.GetFullPath(transportPath, baseDirectory)
            },
            null);
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    // Matches the fake transport's own rule for --fake-node-id.
    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex FakeNodeIdPattern();
}
