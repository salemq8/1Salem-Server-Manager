using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ServerManager.Core;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>Bounded Java server-list handshake, no RCON, authentication, commands or configuration writes.</summary>
public sealed class MinecraftServerStatusClient : IMinecraftServerStatusClient
{
    public async Task<MinecraftServerStatus?> QueryAsync(GameServerDefinition server, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var properties = MinecraftPlayerFiles.ReadProperties(server.RootPath);
            var address = IPAddress.Loopback;
            var configured = properties.GetValueOrDefault("server-ip");
            // Never use a configured remote hostname as a status/SSRF target. Local bindings only.
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!IPAddress.TryParse(configured, out var parsed)) return null;
                if (parsed.Equals(IPAddress.Any)) parsed = IPAddress.Loopback;
                if (parsed.Equals(IPAddress.IPv6Any)) parsed = IPAddress.IPv6Loopback;
                if (!IPAddress.IsLoopback(parsed) && !System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(parsed))) return null;
                address = parsed;
            }
            var port = server.Port;
            if (properties.TryGetValue("server-port", out var portText) && int.TryParse(portText, out var configuredPort) && configuredPort is > 0 and <= 65535)
                port = configuredPort;
            if (port is <= 0 or > 65535) return null;
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, deadline.Token);
            using var stream = client.GetStream();
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, -1); // Status negotiation does not require matching game protocol.
            var host = Encoding.UTF8.GetBytes(address.ToString());
            WriteVarInt(handshake, host.Length);
            handshake.Write(host);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);
            using var packet = new MemoryStream();
            WriteVarInt(packet, checked((int)handshake.Length));
            handshake.Position = 0;
            handshake.CopyTo(packet);
            packet.Write([1, 0]); // Empty status request.
            await stream.WriteAsync(packet.ToArray(), deadline.Token);
            var length = await ReadVarIntAsync(stream, deadline.Token);
            if (length is <= 0 or > 65536) return null;
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, deadline.Token);
            using var response = new MemoryStream(bytes, writable: false);
            if (await ReadVarIntAsync(response, deadline.Token) != 0) return null;
            var jsonLength = await ReadVarIntAsync(response, deadline.Token);
            if (jsonLength <= 0 || jsonLength != response.Length - response.Position) return null;
            using var json = JsonDocument.Parse(bytes.AsMemory((int)response.Position, jsonLength));
            var players = json.RootElement.GetProperty("players");
            var online = players.GetProperty("online").GetInt32();
            var maximum = players.GetProperty("max").GetInt32();
            if (online < 0 || maximum < 0) return null;
            var sample = new List<MinecraftStatusPlayer>();
            if (players.TryGetProperty("sample", out var roster) && roster.ValueKind == JsonValueKind.Array)
                foreach (var item in roster.EnumerateArray().Take(1000))
                    if (item.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var uuid) && uuid != Guid.Empty &&
                        item.TryGetProperty("name", out var name) && MinecraftPlayerCommandPolicy.IsValidName(name.GetString()))
                        sample.Add(new MinecraftStatusPlayer(uuid, name.GetString()!));
            return new MinecraftServerStatus(online, maximum, sample);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var number = (uint)value;
        do { var next = (byte)(number & 0x7f); number >>= 7; stream.WriteByte(number != 0 ? (byte)(next | 0x80) : next); } while (number != 0);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var one = new byte[1];
        for (var i = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(one, ct);
            result |= (one[0] & 0x7f) << (7 * i);
            if ((one[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Invalid Minecraft VarInt.");
    }
}
