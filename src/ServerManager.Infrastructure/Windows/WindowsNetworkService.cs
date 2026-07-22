using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Windows;

public sealed class WindowsNetworkService : INetworkService
{
    private const string PreferredAdapterKey = "network.preferredAdapterId";
    private readonly ISettingsStore? _settingsStore;

    public WindowsNetworkService()
    {
    }

    public WindowsNetworkService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<NetworkSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preferredId = _settingsStore is null
            ? null
            : await _settingsStore.GetAsync<string>(
                PreferredAdapterKey,
                cancellationToken);
        var adapters = EnumerateAdapters(preferredId);
        var selected = NetworkAdapterSelection.Select(adapters, preferredId);

        return new NetworkSnapshot(
            Environment.MachineName,
            selected?.Ipv4,
            await IsPublicProfileAsync(cancellationToken),
            DateTimeOffset.UtcNow,
            adapters,
            selected?.Id);
    }

    public async Task<OperationResult> SetPreferredAdapterAsync(
        string? adapterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settingsStore is null)
        {
            return OperationResult.Fail(
                "PreferredAdapterUnsupported",
                "Preferred adapter persistence is unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(adapterId) &&
            EnumerateAdapters(null).All(
                adapter => !adapter.Id.Equals(
                    adapterId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Fail(
                "AdapterNotFound",
                "The selected active IPv4 adapter is no longer available.");
        }

        await _settingsStore.SetAsync(
            PreferredAdapterKey,
            adapterId ?? string.Empty,
            cancellationToken);
        return OperationResult.Ok();
    }

    public Task<PortTestResponse> TestPortAsync(
        int port,
        string protocol,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "The port must be between 1 and 65535.");
        }

        var normalizedProtocol = protocol.Equals(
            "UDP",
            StringComparison.OrdinalIgnoreCase)
            ? "UDP"
            : "TCP";
        try
        {
            if (normalizedProtocol == "UDP")
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp)
                {
                    ExclusiveAddressUse = true
                };
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            else
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp)
                {
                    ExclusiveAddressUse = true
                };
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }

            return Task.FromResult(new PortTestResponse(
                port,
                normalizedProtocol,
                true,
                $"{normalizedProtocol} port {port} is available."));
        }
        catch (SocketException exception)
        {
            return Task.FromResult(new PortTestResponse(
                port,
                normalizedProtocol,
                false,
                $"{normalizedProtocol} port {port} is already in use: {exception.Message}"));
        }
    }

    private static IReadOnlyList<NetworkAdapterSnapshot> EnumerateAdapters(
        string? preferredId)
    {
        var candidates = new List<NetworkAdapterSnapshot>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsable(networkInterface))
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            var address = properties.UnicastAddresses
                .Select(item => item.Address)
                .FirstOrDefault(item =>
                    item.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(item) &&
                    !item.ToString().StartsWith(
                        "169.254.",
                        StringComparison.Ordinal));
            if (address is null)
            {
                continue;
            }

            candidates.Add(new NetworkAdapterSnapshot(
                networkInterface.Id,
                networkInterface.Name,
                networkInterface.Description,
                address.ToString(),
                properties.GatewayAddresses.Any(
                    gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(IPAddress.Any)),
                !string.IsNullOrWhiteSpace(preferredId) &&
                networkInterface.Id.Equals(
                    preferredId,
                    StringComparison.OrdinalIgnoreCase)));
        }

        return candidates
            .OrderByDescending(adapter => adapter.IsPreferred)
            .ThenByDescending(adapter => adapter.HasDefaultGateway)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool IsUsable(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is
                NetworkInterfaceType.Loopback or
                NetworkInterfaceType.Tunnel or
                NetworkInterfaceType.Unknown)
        {
            return false;
        }

        var identity =
            $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
        return !identity.Contains("virtual", StringComparison.Ordinal) &&
               !identity.Contains("hyper-v", StringComparison.Ordinal) &&
               !identity.Contains("vpn", StringComparison.Ordinal) &&
               !identity.Contains("tunnel", StringComparison.Ordinal) &&
               !identity.Contains("loopback", StringComparison.Ordinal);
    }

    private static async Task<bool> IsPublicProfileAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = await WindowsCommandRunner.RunAsync(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "(Get-NetConnectionProfile | Where-Object IPv4Connectivity -ne 'Disconnected' | Select-Object -ExpandProperty NetworkCategory) -contains 'Public'"
            ],
            cancellationToken);
        return result.Success &&
               result.Message?.Contains(
                   "True",
                   StringComparison.OrdinalIgnoreCase) == true;
    }
}
