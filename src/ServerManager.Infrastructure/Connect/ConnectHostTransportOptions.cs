using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Infrastructure.Connect;

public enum ConnectTransportMode
{
    /// <summary>Loopback stands in for the tailnet. For tests and the disposable proof only.</summary>
    Fake,

    /// <summary>A real tsnet node in the owner's tailnet.</summary>
    Tsnet
}

/// <summary>
/// How the Agent starts <c>1Salem.Connect.Host.Transport.exe</c>. The rules mirror the
/// sidecar's own flag checks, so a bad configuration fails here with a clear message instead of
/// in a restart loop:
/// <list type="bullet">
/// <item>fake mode: the bridge listens on a loopback <c>ip:port</c>, and there is no state
/// directory;</item>
/// <item>tsnet mode: a fully qualified state directory, and a bridge address that is either
/// <c>:port</c> (every tailnet address of the node) or an explicit Tailscale address.</item>
/// </list>
/// A wildcard such as 0.0.0.0 is refused in both modes.
/// </summary>
public sealed class ConnectHostTransportOptions
{
    /// <summary>The sidecar accepts only <c>\\.\pipe\</c> followed by 1-200 of these characters.</summary>
    private const int MaxPipeNameLength = 200;

    public ConnectHostTransportOptions(
        string executablePath,
        ConnectTransportMode mode,
        string bridgeListen,
        string? stateDirectory = null,
        string authorizationPipeName = ConnectPipeNames.HostAuthorization,
        string? controlPipeName = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            // Never resolved through PATH: the Agent runs the binary it shipped, nothing else.
            throw new ArgumentException("The host transport path must be fully qualified.", nameof(executablePath));
        }

        if (!IsSidecarPipeName(authorizationPipeName))
        {
            throw new ArgumentException(
                "Expected a bare pipe name of letters, digits, '.', '_' and '-'.",
                nameof(authorizationPipeName));
        }

        controlPipeName ??= ConnectPipeNames.HostTransportAgent;
        if (!IsSidecarPipeName(controlPipeName))
        {
            throw new ArgumentException(
                "Expected a bare pipe name of letters, digits, '.', '_' and '-'.",
                nameof(controlPipeName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeListen);
        switch (mode)
        {
            case ConnectTransportMode.Fake:
                if (stateDirectory is not null)
                {
                    throw new ArgumentException("Fake mode keeps no node state.", nameof(stateDirectory));
                }

                if (!TryParseCanonicalEndpoint(bridgeListen, out var loopback) ||
                    !IPAddress.IsLoopback(loopback.Address))
                {
                    throw new ArgumentException("Fake mode must listen on a loopback ip:port.", nameof(bridgeListen));
                }

                break;
            case ConnectTransportMode.Tsnet:
                if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory))
                {
                    throw new ArgumentException("Tsnet mode needs a fully qualified state directory.", nameof(stateDirectory));
                }

                if (!IsTailnetListenAddress(bridgeListen))
                {
                    throw new ArgumentException("Tsnet mode must listen on :port or a Tailscale ip:port.", nameof(bridgeListen));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ExecutablePath = executablePath;
        Mode = mode;
        BridgeListen = bridgeListen;
        StateDirectory = stateDirectory;
        AuthorizationPipeName = authorizationPipeName;
        ControlPipeName = controlPipeName;
    }

    public string ExecutablePath { get; }

    public ConnectTransportMode Mode { get; }

    public string BridgeListen { get; }

    public string? StateDirectory { get; }

    /// <summary>The bare name of the Agent's authorization pipe.</summary>
    public string AuthorizationPipeName { get; }

    /// <summary>The bare control pipe name the sidecar must create.</summary>
    public string ControlPipeName { get; }

    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumRestartDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A run at least this long counts as healthy and resets the restart delay.</summary>
    public TimeSpan StableRunTime { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private static readonly (IPAddress Network, int PrefixLength)[] TailscaleRanges =
    [
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("fd7a:115c:a1e0::"), 48)
    ];

    private static bool IsSidecarPipeName(string? value) =>
        value is { Length: > 0 and <= MaxPipeNameLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsTailnetListenAddress(string value)
    {
        if (value.StartsWith(':'))
        {
            return ushort.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port != 0;
        }

        return TryParseCanonicalEndpoint(value, out var endpoint) &&
            TailscaleRanges.Any(range => IsInRange(endpoint.Address, range.Network, range.PrefixLength));
    }

    /// <summary>
    /// <see cref="IPEndPoint.TryParse(string, out IPEndPoint?)"/> also accepts shorthand such as
    /// <c>127.1:80</c>, which the sidecar's parser refuses. Requiring the canonical spelling keeps
    /// the two in agreement, so an address accepted here is never one the sidecar exits on.
    /// </summary>
    private static bool TryParseCanonicalEndpoint(string value, [NotNullWhen(true)] out IPEndPoint? endpoint) =>
        IPEndPoint.TryParse(value, out endpoint) &&
        endpoint.Port != 0 &&
        string.Equals(endpoint.ToString(), value, StringComparison.OrdinalIgnoreCase);

    private static bool IsInRange(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        if (!addressBytes.AsSpan(0, fullBytes).SequenceEqual(networkBytes.AsSpan(0, fullBytes)))
        {
            return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
