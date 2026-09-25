using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ServerManager.Connect.Core.Json;

/// <summary>
/// A flat view of one JSON object that refuses duplicate member names. Parsers disagree about
/// which duplicate wins (System.Text.Json, Go's encoding/json and JavaScript each have their own
/// rule), so a ticket or envelope with <c>"aud"</c> twice could mean one thing to the broker and
/// another to a verifier. Rejecting duplicates outright removes that ambiguity.
/// </summary>
internal sealed class StrictJsonObject
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    private readonly Dictionary<string, JsonElement> _members;

    private StrictJsonObject(Dictionary<string, JsonElement> members)
    {
        _members = members;
    }

    public IEnumerable<string> Names => _members.Keys;

    public int Count => _members.Count;

    public static bool TryParse(ReadOnlyMemory<byte> utf8, [NotNullWhen(true)] out StrictJsonObject? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(utf8, Options);
            return TryCreate(document.RootElement, out value);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Raised when a string holds invalid UTF-8 and cannot be transcoded.
            return false;
        }
    }

    public static bool TryCreate(JsonElement element, [NotNullWhen(true)] out StrictJsonObject? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // Clone detaches each value from the source document so the caller may dispose it.
            if (!members.TryAdd(property.Name, property.Value.Clone()))
            {
                return false;
            }
        }

        value = new StrictJsonObject(members);
        return true;
    }

    public bool Contains(string name) => _members.ContainsKey(name);

    public bool TryGetString(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!_members.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    /// <summary>Integers only: 1.0, 1e3 and quoted numbers are all refused.</summary>
    public bool TryGetInt64(string name, out long value)
    {
        value = 0;
        return _members.TryGetValue(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            IsPlainInteger(element.GetRawText()) &&
            element.TryGetInt64(out value);
    }

    public bool TryGetObject(string name, [NotNullWhen(true)] out StrictJsonObject? value)
    {
        value = null;
        return _members.TryGetValue(name, out var element) && TryCreate(element, out value);
    }

    public bool TryGetArray(string name, out JsonElement.ArrayEnumerator value)
    {
        value = default;
        if (!_members.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        value = element.EnumerateArray();
        return true;
    }

    private static bool IsPlainInteger(string raw)
    {
        var digits = raw.AsSpan();
        if (digits.Length > 0 && digits[0] == '-')
        {
            digits = digits[1..];
        }

        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
