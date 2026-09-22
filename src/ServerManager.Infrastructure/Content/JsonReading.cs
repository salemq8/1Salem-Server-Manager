using System.Text.Json;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Small readers for provider JSON. A field a provider did not send comes back null instead
/// of throwing, because "Unknown" is an honest thing to show and a crash is not.
/// </summary>
internal static class JsonReading
{
    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? String(this JsonElement element, string name)
    {
        var value = element.Property(name);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    public static long? Int64(this JsonElement element, string name)
    {
        var value = element.Property(name);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number)
            ? number
            : null;
    }

    public static bool Bool(this JsonElement element, string name, bool fallback = false)
    {
        var value = element.Property(name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    public static DateTimeOffset? Timestamp(this JsonElement element, string name)
    {
        var text = element.String(name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    public static Uri? Url(this JsonElement element, string name)
    {
        var text = element.String(name);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
    }

    public static IReadOnlyList<string> Strings(this JsonElement element, string name)
    {
        var value = element.Property(name);
        if (value?.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in value.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    public static IReadOnlyList<JsonElement> Array(this JsonElement element, string name)
    {
        var value = element.Property(name);
        return value?.ValueKind == JsonValueKind.Array
            ? value.Value.EnumerateArray().ToArray()
            : [];
    }
}
