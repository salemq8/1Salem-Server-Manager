using ServerManager.Contracts;
using System.Globalization;
using System.Text;

namespace ServerManager.Infrastructure.Games.Palworld;

public static class PalworldSettingsSerializer
{
    public static string Serialize(PalworldServerSettings settings)
    {
        Validate(settings);
        return string.Join(
            Environment.NewLine,
            "[/Script/Pal.PalGameWorldSettings]",
            "OptionSettings=(" +
            $"ServerName=\"{Escape(settings.ServerName)}\"," +
            $"ServerDescription=\"{Escape(settings.Description)}\"," +
            $"AdminPassword=\"{Escape(settings.AdminPassword)}\"," +
            $"ServerPassword=\"{Escape(settings.ServerPassword)}\"," +
            $"ServerPlayerMaxNum={settings.MaxPlayers}," +
            $"PublicPort={settings.Port}," +
            $"RCONEnabled={settings.RconEnabled.ToString().ToLowerInvariant()}," +
            $"RCONPort={settings.RconPort}," +
            $"RESTAPIEnabled={settings.RestApiEnabled.ToString().ToLowerInvariant()}," +
            $"RESTAPIPort={settings.RestApiPort}," +
            "PublicIP=\"\")",
            string.Empty);
    }

    public static string Merge(string existing, PalworldServerSettings settings)
    {
        Validate(settings);
        if (string.IsNullOrWhiteSpace(existing) ||
            !existing.Contains("OptionSettings=(", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(settings);
        }

        var valuesToMerge = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["ServerName"] = Quote(settings.ServerName),
            ["ServerDescription"] = Quote(settings.Description),
            ["AdminPassword"] = Quote(settings.AdminPassword),
            ["ServerPassword"] = Quote(settings.ServerPassword),
            ["ServerPlayerMaxNum"] =
                settings.MaxPlayers.ToString(CultureInfo.InvariantCulture),
            ["PublicPort"] = settings.Port.ToString(CultureInfo.InvariantCulture),
            ["RCONEnabled"] = settings.RconEnabled.ToString().ToLowerInvariant(),
            ["RCONPort"] = settings.RconPort.ToString(CultureInfo.InvariantCulture),
            ["RESTAPIEnabled"] =
                settings.RestApiEnabled.ToString().ToLowerInvariant(),
            ["RESTAPIPort"] =
                settings.RestApiPort.ToString(CultureInfo.InvariantCulture)
        };
        return MergeValues(existing, valuesToMerge);
    }

    public static string MergeValues(
        string existing,
        IReadOnlyDictionary<string, string> valuesToMerge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existing);
        ArgumentNullException.ThrowIfNull(valuesToMerge);
        const string marker = "OptionSettings=(";
        var firstMarker = existing.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (firstMarker < 0)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini does not contain OptionSettings.");
        }

        if (existing.IndexOf(
                marker,
                firstMarker + marker.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini contains duplicate OptionSettings values.");
        }

        var start = firstMarker + marker.Length;
        var end = FindClosingParenthesis(existing, start);
        if (end < start)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini contains an invalid OptionSettings value.");
        }

        var values = SplitValues(existing[start..end]);
        foreach (var (key, value) in valuesToMerge)
        {
            ValidateKey(key);
            Set(values, key, value);
        }

        return string.Concat(
            existing.AsSpan(0, start),
            string.Join(',', values),
            existing.AsSpan(end));
    }

    public static IReadOnlyDictionary<string, string> ParseValues(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        const string marker = "OptionSettings=(";
        var firstMarker = content.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (firstMarker < 0)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini does not contain OptionSettings.");
        }

        if (content.IndexOf(
                marker,
                firstMarker + marker.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini contains duplicate OptionSettings values.");
        }

        var start = firstMarker + marker.Length;
        var end = FindClosingParenthesis(content, start);
        if (end < start)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini contains an invalid OptionSettings value.");
        }

        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in SplitValues(content[start..end]))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidDataException(
                    $"PalWorldSettings.ini contains an invalid setting: {item}");
            }

            var key = item[..separator].Trim();
            ValidateKey(key);
            if (!result.TryAdd(key, item[(separator + 1)..].Trim()))
            {
                throw new InvalidDataException(
                    $"PalWorldSettings.ini contains duplicate setting '{key}'.");
            }
        }

        return result;
    }

    public static string RenderText(string value) => Quote(value);

    public static string ReadText(string rawValue)
    {
        if (rawValue.Length >= 2 &&
            rawValue[0] == '"' &&
            rawValue[^1] == '"')
        {
            return rawValue[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return rawValue;
    }

    public static void Validate(PalworldServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ServerName) || settings.ServerName.Length > 80)
        {
            throw new ArgumentException("Palworld server names must contain 1 to 80 characters.", nameof(settings));
        }

        if (settings.Description.Length > 500)
        {
            throw new ArgumentException("Palworld descriptions cannot exceed 500 characters.", nameof(settings));
        }

        if (settings.MaxPlayers is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Palworld supports 1 to 32 players.");
        }

        if (settings.Port is < 1 or > 65_535 ||
            settings.RconPort is < 1 or > 65_535 ||
            settings.RestApiPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Ports must be between 1 and 65535.");
        }

        ValidateSecret(settings.ServerPassword, nameof(settings.ServerPassword));
        ValidateSecret(settings.AdminPassword, nameof(settings.AdminPassword));
    }

    private static void ValidateSecret(string value, string name)
    {
        if (value.Length > 128 ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Secrets cannot exceed 128 characters or contain newlines.", name);
        }
    }

    private static string Escape(string value)
    {
        if (value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Palworld text values cannot contain newlines.", nameof(value));
        }

        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string Quote(string value) => $"\"{Escape(value)}\"";

    private static int FindClosingParenthesis(string value, int start)
    {
        var quoted = false;
        var escaped = false;
        var nestedDepth = 0;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\' && quoted)
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == '(' && !quoted)
            {
                nestedDepth++;
            }
            else if (character == ')' && !quoted && nestedDepth > 0)
            {
                nestedDepth--;
            }
            else if (character == ')' && !quoted)
            {
                return index;
            }
        }

        return -1;
    }

    private static List<string> SplitValues(string value)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        var nestedDepth = 0;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\' && quoted)
            {
                current.Append(character);
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                current.Append(character);
            }
            else if (character == '(' && !quoted)
            {
                nestedDepth++;
                current.Append(character);
            }
            else if (character == ')' && !quoted && nestedDepth > 0)
            {
                nestedDepth--;
                current.Append(character);
            }
            else if (character == ',' && !quoted && nestedDepth == 0)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted || nestedDepth != 0)
        {
            throw new InvalidDataException(
                "PalWorldSettings.ini contains an unterminated quoted or list value.");
        }

        values.Add(current.ToString());
        return values;
    }

    private static void Set(List<string> values, string key, string value)
    {
        var prefix = $"{key}=";
        var index = values.FindIndex(item =>
            item.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var rendered = $"{key}={value}";
        if (index >= 0)
        {
            values[index] = rendered;
        }
        else
        {
            values.Add(rendered);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '_'))
        {
            throw new InvalidDataException(
                $"PalWorldSettings.ini contains invalid setting name '{key}'.");
        }
    }
}
