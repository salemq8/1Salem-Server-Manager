using System.Text.RegularExpressions;
using ServerManager.Contracts;

namespace ServerManager.Core;

public static partial class PlayitOutputParser
{
    public static string? FindClaimUrl(string? line) =>
        line is null ? null : ClaimUrlRegex().Match(line) is { Success: true } match
            ? match.Value
            : null;

    public static bool IndicatesLinked(string? line) =>
        ContainsAny(line, "secret key is valid", "account linked", "agent registered");

    public static bool IndicatesVerified(string? line) =>
        ContainsAny(line, "tunnel running", "agent registered", "got pong");

    public static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var redacted = ClaimUrlRegex().Replace(line, "[PLAYIT CLAIM LINK]");
        redacted = SecretAssignmentRegex().Replace(redacted, "$1[REDACTED]");
        redacted = LongSecretRegex().Replace(redacted, "[REDACTED]");
        return redacted.Length <= 2_000 ? redacted : redacted[..2_000];
    }

    private static bool ContainsAny(string? line, params string[] values) =>
        line is not null && values.Any(value =>
            line.Contains(value, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(
        @"https://playit\.gg/claim/[A-Za-z0-9_-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimUrlRegex();

    [GeneratedRegex(
        @"(?i)\b(secret|token|key)\s*[:=]\s*(\S+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])[A-Za-z0-9_-]{40,}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretRegex();
}

public static class PlayitTunnelPolicy
{
    public const string LoopbackHost = "127.0.0.1";

    public static string? Validate(
        GameType game,
        PlayitTunnelProtocol protocol,
        string localHost,
        int localPort,
        int configuredPort,
        IEnumerable<PlayitTunnelStatus>? existing = null)
    {
        if (!localHost.Equals(LoopbackHost, StringComparison.Ordinal))
        {
            return "Playit game tunnels must target 127.0.0.1.";
        }

        if (localPort is < 1 or > 65535 || configuredPort is < 1 or > 65535)
        {
            return "The local port must be between 1 and 65535.";
        }

        var requiredProtocol = game == GameType.Minecraft
            ? PlayitTunnelProtocol.Tcp
            : PlayitTunnelProtocol.Udp;
        if (protocol != requiredProtocol)
        {
            return game == GameType.Minecraft
                ? "Minecraft Java requires a TCP tunnel."
                : "Palworld requires a UDP tunnel.";
        }

        if (localPort != configuredPort)
        {
            return $"The tunnel targets port {localPort}, but the configured {game} port is {configuredPort}.";
        }

        if (game == GameType.Palworld && localPort == 25565)
        {
            return "Palworld must not target the Minecraft port 25565.";
        }

        if (existing?.Any(item =>
                item.Game != game &&
                item.LocalHost.Equals(localHost, StringComparison.OrdinalIgnoreCase) &&
                item.LocalPort == localPort) == true)
        {
            return "Another game tunnel already targets this local server instance.";
        }

        return null;
    }
}

public static class ApplicationUpdatePolicy
{
    public static bool CanInstall(
        bool gameServerBusy,
        bool approvedWhileBusy,
        bool requiresServiceRestart) =>
        !gameServerBusy || approvedWhileBusy || !requiresServiceRestart;
}
