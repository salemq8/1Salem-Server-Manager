using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ServerManager.Infrastructure.Connect;

public enum ConnectPolicyVerdictKind
{
    Safe,
    Unsafe,
    Unverifiable
}

public sealed record ConnectPolicyVerdict(ConnectPolicyVerdictKind Kind, IReadOnlyList<string> Reasons)
{
    public bool IsSafe => Kind == ConnectPolicyVerdictKind.Safe;

    public static ConnectPolicyVerdict Safe() => new(ConnectPolicyVerdictKind.Safe, []);
    public static ConnectPolicyVerdict Unsafe(params string[] reasons) => new(ConnectPolicyVerdictKind.Unsafe, reasons);
    public static ConnectPolicyVerdict Unverifiable(string reason) => new(ConnectPolicyVerdictKind.Unverifiable, [reason]);
}

/// <summary>
/// Conservatively proves that tagged friend nodes can reach only tagged host nodes on TCP 7780.
/// Anything whose source or destination meaning cannot be established is never called safe.
/// </summary>
public static class ConnectPolicyAnalyzer
{
    private const string FriendTag = TailscaleApiProvisioner.FriendTag;
    private const string HostTag = TailscaleApiProvisioner.HostTag;

    public static ConnectPolicyVerdict Analyze(ReadOnlySpan<byte> policyJson)
    {
        try
        {
            using var document = JsonDocument.Parse(policyJson.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ConnectPolicyVerdict.Unverifiable("The tailnet policy is not a JSON object.");
            }

            var root = document.RootElement;
            if (!TryReadTagOwners(root, out var tagError))
            {
                return ConnectPolicyVerdict.Unsafe(tagError!);
            }

            var unsafeReasons = new List<string>();
            var requiredGrant = false;
            if (!AnalyzeRules(root, "grants", modern: true, unsafeReasons, ref requiredGrant, out var error) ||
                !AnalyzeRules(root, "acls", modern: false, unsafeReasons, ref requiredGrant, out error))
            {
                return ConnectPolicyVerdict.Unverifiable(error!);
            }

            if (!requiredGrant)
            {
                unsafeReasons.Add("The policy does not grant tag:onesalem-client exactly TCP 7780 to tag:onesalem-host.");
            }

            return unsafeReasons.Count == 0
                ? ConnectPolicyVerdict.Safe()
                : new ConnectPolicyVerdict(ConnectPolicyVerdictKind.Unsafe, unsafeReasons);
        }
        catch (JsonException)
        {
            return ConnectPolicyVerdict.Unverifiable("The tailnet policy is not valid JSON.");
        }
    }

    private static bool TryReadTagOwners(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("tagOwners", out var owners) || owners.ValueKind != JsonValueKind.Object ||
            !owners.TryGetProperty(HostTag, out var hostOwners) || hostOwners.ValueKind != JsonValueKind.Array ||
            !owners.TryGetProperty(FriendTag, out var friendOwners) || friendOwners.ValueKind != JsonValueKind.Array)
        {
            error = "The policy must define owners for both 1Salem tags.";
            return false;
        }

        if (hostOwners.GetArrayLength() == 0 || friendOwners.GetArrayLength() == 0 ||
            hostOwners.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) ||
            friendOwners.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
        {
            error = "The policy must give both 1Salem tags valid owners.";
            return false;
        }

        if (!friendOwners.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String && value.GetString() == HostTag))
        {
            error = "tag:onesalem-client must be owned by tag:onesalem-host.";
            return false;
        }

        return true;
    }

    private static bool AnalyzeRules(
        JsonElement root,
        string property,
        bool modern,
        List<string> unsafeReasons,
        ref bool requiredGrant,
        out string? error)
    {
        error = null;
        if (!root.TryGetProperty(property, out var rules))
        {
            return true;
        }

        if (rules.ValueKind != JsonValueKind.Array)
        {
            error = $"The policy's {property} member is not an array.";
            return false;
        }

        var index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            index++;
            if (rule.ValueKind != JsonValueKind.Object || !TryStringsEither(rule, "src", "users", out var sources))
            {
                error = $"Policy {property} rule {index} has an unsupported source.";
                return false;
            }

            if (!sources.Any(SourceMayContainFriend))
            {
                continue;
            }

            JsonElement action = default;
            if (!modern && (!rule.TryGetProperty("action", out action) || action.ValueKind != JsonValueKind.String))
            {
                error = $"Policy {property} rule {index} has no usable action.";
                return false;
            }

            if (!modern && action.GetString() != "accept")
            {
                continue;
            }

            bool exact;
            if (modern)
            {
                exact = TryStringsEither(rule, "dst", "ports", out var destinations) &&
                    destinations.Count > 0 && destinations.All(value => value == HostTag) &&
                    TryStrings(rule, "ip", out var protocols) &&
                    protocols.Count > 0 && protocols.All(IsTcp7780) &&
                    !rule.TryGetProperty("app", out _);
            }
            else
            {
                exact = TryStringsEither(rule, "dst", "ports", out var destinations) &&
                    destinations.Count > 0 && destinations.All(value => value == HostTag + ":7780");
            }

            if (!exact)
            {
                unsafeReasons.Add($"Policy {property} rule {index} may grant a friend node access beyond tag:onesalem-host TCP 7780.");
            }
            else
            {
                requiredGrant = true;
            }
        }

        return true;
    }

    private static bool TryStrings(JsonElement owner, string name, out IReadOnlyList<string> values)
    {
        values = [];
        if (!owner.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            {
                return false;
            }

            list.Add(text);
        }

        values = list;
        return true;
    }

    private static bool TryStringsEither(JsonElement owner, string preferred, string legacy, out IReadOnlyList<string> values) =>
        owner.TryGetProperty(preferred, out _) ? TryStrings(owner, preferred, out values) : TryStrings(owner, legacy, out values);

    private static bool IsTcp7780(string value) =>
        value == "tcp:7780" || value == "tcp:7780-7780";

    private static bool SourceMayContainFriend(string source)
    {
        if (source is "*" or FriendTag or "autogroup:tagged")
        {
            return true;
        }

        if (source.StartsWith("tag:", StringComparison.Ordinal) ||
            source.StartsWith("group:", StringComparison.Ordinal) ||
            source.StartsWith("autogroup:", StringComparison.Ordinal) ||
            source.Contains('@'))
        {
            return false;
        }

        if (TryNetwork(source, out var network, out var prefixLength))
        {
            return Overlaps(network, prefixLength, IPAddress.Parse("100.64.0.0"), 10) ||
                Overlaps(network, prefixLength, IPAddress.Parse("fd7a:115c:a1e0::"), 48);
        }

        // A host alias can resolve to a tailnet address, so it is potentially a friend source.
        return true;
    }

    private static bool TryNetwork(string text, out IPAddress address, out int prefixLength)
    {
        var slash = text.LastIndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out address!))
        {
            prefixLength = 0;
            return false;
        }

        var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        prefixLength = maximum;
        return slash < 0 ||
            (int.TryParse(text.AsSpan(slash + 1), out prefixLength) && prefixLength is >= 0 && prefixLength <= maximum);
    }

    private static bool Overlaps(IPAddress left, int leftPrefix, IPAddress right, int rightPrefix)
    {
        if (left.AddressFamily != right.AddressFamily)
        {
            return false;
        }

        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        var bits = Math.Min(leftPrefix, rightPrefix);
        var fullBytes = bits / 8;
        if (!leftBytes.AsSpan(0, fullBytes).SequenceEqual(rightBytes.AsSpan(0, fullBytes)))
        {
            return false;
        }

        var remaining = bits % 8;
        return remaining == 0 ||
            (leftBytes[fullBytes] & (0xff << (8 - remaining))) ==
            (rightBytes[fullBytes] & (0xff << (8 - remaining)));
    }
}
