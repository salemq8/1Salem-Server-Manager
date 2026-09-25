using System.Text.RegularExpressions;

namespace ServerManager.Connect.App.Broker;

/// <summary>
/// Shapes of ids the app puts into broker paths or hands to the transport. They mirror the
/// broker's own validators, so a malformed value from any source never becomes part of a URL.
/// </summary>
public static partial class BrokerFormats
{
    public static bool IsMembershipId(string? value) => value is not null && MembershipId().IsMatch(value);

    /// <summary>Tailscale StableNodeIDs, or readable ids in fake mode.</summary>
    public static bool IsNodeId(string? value) => value is not null && NodeId().IsMatch(value);

    [GeneratedRegex("^mem_[a-z2-7]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex MembershipId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeId();
}
