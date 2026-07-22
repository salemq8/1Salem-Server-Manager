using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed class PalworldRestClient(
    HttpClient httpClient,
    ISecretStore secretStore)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<PalworldRestOperationResult> SaveWorldAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default) =>
        SendOperationAsync(
            server,
            "save",
            null,
            "WorldSaved",
            "Palworld confirmed that the world was saved.",
            cancellationToken);

    public Task<PalworldRestOperationResult> AnnounceAsync(
        GameServerDefinition server,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > 512 ||
            message.Contains('\r') ||
            message.Contains('\n'))
        {
            return Task.FromResult(new PalworldRestOperationResult(
                false,
                "InvalidAnnouncement",
                "Announcements must contain 1 to 512 characters on one line.",
                DateTimeOffset.UtcNow));
        }

        return SendOperationAsync(
            server,
            "announce",
            JsonSerializer.Serialize(new { message }),
            "AnnouncementSent",
            "Palworld accepted the player announcement.",
            cancellationToken);
    }

    public async Task<PalworldRestOperationResult> TestConnectionAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await ReadContextAsync(server, cancellationToken);
            if (context.Error is not null)
            {
                return context.Error;
            }

            using var document = await GetJsonAsync(
                context.BaseAddress!,
                "info",
                context.Password!,
                cancellationToken);
            return new PalworldRestOperationResult(
                true,
                "Connected",
                "Connected to Palworld /info through localhost.",
                DateTimeOffset.UtcNow);
        }
        catch (PalworldRestException exception)
        {
            return new PalworldRestOperationResult(
                false,
                exception.Code,
                exception.Message,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            IOException or
            InvalidOperationException)
        {
            return Failure(exception);
        }
    }

    public async Task<PalworldRestSettingsResult> GetSettingsAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await ReadContextAsync(server, cancellationToken);
            if (context.Error is not null)
            {
                return new PalworldRestSettingsResult(
                    false,
                    context.Error.Code,
                    context.Error.Message,
                    new Dictionary<string, string>(),
                    DateTimeOffset.UtcNow);
            }

            using var document = await GetJsonAsync(
                context.BaseAddress!,
                "settings",
                context.Password!,
                cancellationToken);
            var settings = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => RenderJsonValue(property.Value),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
            return new PalworldRestSettingsResult(
                true,
                "SettingsLoaded",
                "Palworld returned its active server settings.",
                settings,
                DateTimeOffset.UtcNow);
        }
        catch (PalworldRestException exception)
        {
            return new PalworldRestSettingsResult(
                false,
                exception.Code,
                exception.Message,
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            IOException or
            InvalidOperationException)
        {
            var failure = Failure(exception);
            return new PalworldRestSettingsResult(
                false,
                failure.Code,
                failure.Message,
                new Dictionary<string, string>(),
                failure.CompletedAtUtc);
        }
    }

    public async Task<PalworldManagementSnapshot> GetSnapshotAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException(
                "The selected server is not Palworld.",
                nameof(server));
        }

        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken);
        if (!metadata.Settings.RestApiEnabled)
        {
            return Disabled(
                server.Id,
                metadata.Settings.RestApiPort,
                "Palworld management is disabled. Enable it to show live players, FPS, and server details.");
        }

        var port = metadata.Settings.RestApiPort;
        if (port is < 1 or > 65_535)
        {
            return Unavailable(
                server.Id,
                port,
                "The configured Palworld management port is invalid.");
        }

        var password = secretStore.Unprotect(metadata.ProtectedAdminPassword);
        if (string.IsNullOrEmpty(password))
        {
            return Unavailable(
                server.Id,
                port,
                "An admin password is required for Palworld management.");
        }

        try
        {
            var baseAddress = new Uri($"http://127.0.0.1:{port}/v1/api/");
            EnsureLoopback(baseAddress);
            using var info = await GetJsonAsync(
                baseAddress,
                "info",
                password,
                cancellationToken);
            using var metrics = await GetJsonAsync(
                baseAddress,
                "metrics",
                password,
                cancellationToken);
            using var players = await GetJsonAsync(
                baseAddress,
                "players",
                password,
                cancellationToken);

            return new PalworldManagementSnapshot(
                server.Id,
                PalworldManagementState.Online,
                true,
                port,
                "Connected to the local Palworld management API.",
                ReadString(info.RootElement, "servername"),
                ReadString(info.RootElement, "description"),
                ReadString(info.RootElement, "version"),
                ReadString(info.RootElement, "worldguid"),
                ReadDouble(metrics.RootElement, "serverfps"),
                ReadDouble(metrics.RootElement, "serverframetime"),
                ReadInt(metrics.RootElement, "currentplayernum"),
                ReadInt(metrics.RootElement, "maxplayernum"),
                ReadLong(metrics.RootElement, "uptime"),
                ReadPlayers(players.RootElement),
                DateTimeOffset.UtcNow);
        }
        catch (PalworldRestException exception)
        {
            return Unavailable(server.Id, port, exception.Message);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            IOException or
            InvalidOperationException)
        {
            return Unavailable(
                server.Id,
                port,
                $"Local Palworld management is unavailable: {FriendlyMessage(exception)}");
        }
    }

    public static PalworldManagementSnapshot Disabled(
        Guid serverId,
        int port,
        string message) =>
        new(
            serverId,
            PalworldManagementState.Disabled,
            false,
            port,
            message,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            DateTimeOffset.UtcNow);

    public static PalworldManagementSnapshot Unavailable(
        Guid serverId,
        int port,
        string message) =>
        new(
            serverId,
            PalworldManagementState.Unavailable,
            true,
            port,
            message,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            DateTimeOffset.UtcNow);

    private async Task<JsonDocument> GetJsonAsync(
        Uri baseAddress,
        string endpoint,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseAddress, endpoint));
        request.Headers.Authorization = CreateAuthorization(password);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PalworldRestException(
                "Unauthorized",
                "Palworld rejected the protected admin credentials. Save a valid admin password and restart the server.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PalworldRestException(
                "HttpError",
                $"Palworld management returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
    }

    private static AuthenticationHeaderValue CreateAuthorization(string password)
    {
        var bytes = Encoding.UTF8.GetBytes($"admin:{password}");
        try
        {
            return new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void EnsureLoopback(Uri address)
    {
        if (!address.IsLoopback ||
            !IPAddress.TryParse(address.Host, out var ipAddress) ||
            !IPAddress.IsLoopback(ipAddress))
        {
            throw new InvalidOperationException(
                "Palworld management connections are restricted to this computer.");
        }
    }

    private static IReadOnlyList<PalworldPlayerInfo> ReadPlayers(JsonElement root)
    {
        if (!TryGet(root, "players", out var players) ||
            players.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PalworldPlayerInfo>();
        foreach (var player in players.EnumerateArray())
        {
            TryGet(player, "location", out var location);
            result.Add(new PalworldPlayerInfo(
                ReadString(player, "name") ?? "Unknown player",
                ReadString(player, "accountName"),
                ReadString(player, "playerId"),
                ReadString(player, "userId"),
                ReadString(player, "ip"),
                ReadDouble(player, "ping"),
                ReadInt(player, "level"),
                location.ValueKind == JsonValueKind.Object
                    ? ReadDouble(location, "x")
                    : null,
                location.ValueKind == JsonValueKind.Object
                    ? ReadDouble(location, "y")
                    : null));
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? ReadLong(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.TryGetInt64(out var result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool TryGet(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static async Task<PalworldServerMetadata> ReadMetadataAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, ".1salem", "metadata.json");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Managed Palworld metadata is invalid.");
    }

    private static string FriendlyMessage(Exception exception) =>
        exception is TaskCanceledException
            ? "the local request timed out"
            : exception.Message;

    private async Task<PalworldRestOperationResult> SendOperationAsync(
        GameServerDefinition server,
        string endpoint,
        string? jsonBody,
        string successCode,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await ReadContextAsync(server, cancellationToken);
            if (context.Error is not null)
            {
                return context.Error;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(context.BaseAddress!, endpoint));
            request.Headers.Authorization = CreateAuthorization(context.Password!);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(
                    jsonBody,
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new PalworldRestOperationResult(
                    false,
                    "Unauthorized",
                    "Palworld rejected the protected admin credentials.",
                    DateTimeOffset.UtcNow);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PalworldRestOperationResult(
                    false,
                    "HttpError",
                    $"Palworld returned HTTP {(int)response.StatusCode} for /{endpoint}.",
                    DateTimeOffset.UtcNow);
            }

            return new PalworldRestOperationResult(
                true,
                successCode,
                successMessage,
                DateTimeOffset.UtcNow);
        }
        catch (PalworldRestException exception)
        {
            return new PalworldRestOperationResult(
                false,
                exception.Code,
                exception.Message,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            IOException or
            InvalidOperationException)
        {
            return Failure(exception);
        }
    }

    private async Task<RestContext> ReadContextAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException(
                "The selected server is not Palworld.",
                nameof(server));
        }

        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken);
        if (!metadata.Settings.RestApiEnabled)
        {
            return new RestContext(
                null,
                null,
                new PalworldRestOperationResult(
                    false,
                    "RestDisabled",
                    "Palworld local management is disabled.",
                    DateTimeOffset.UtcNow));
        }

        if (metadata.Settings.RestApiPort is < 1 or > 65_535)
        {
            return new RestContext(
                null,
                null,
                new PalworldRestOperationResult(
                    false,
                    "InvalidRestPort",
                    "The configured Palworld management port is invalid.",
                    DateTimeOffset.UtcNow));
        }

        var password = secretStore.Unprotect(metadata.ProtectedAdminPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            return new RestContext(
                null,
                null,
                new PalworldRestOperationResult(
                    false,
                    "AdminPasswordMissing",
                    "A protected Palworld admin password is required.",
                    DateTimeOffset.UtcNow));
        }

        var address = new Uri(
            $"http://127.0.0.1:{metadata.Settings.RestApiPort}/v1/api/");
        EnsureLoopback(address);
        return new RestContext(address, password, null);
    }

    private static PalworldRestOperationResult Failure(Exception exception)
    {
        var code = exception switch
        {
            TaskCanceledException => "RestStartupTimeout",
            HttpRequestException { InnerException: System.Net.Sockets.SocketException } =>
                "ConnectionRefused",
            HttpRequestException => "RestUnreachable",
            JsonException => "InvalidRestResponse",
            IOException => "ConfigurationReadFailed",
            _ => "RestUnavailable"
        };
        return new PalworldRestOperationResult(
            false,
            code,
            $"Local Palworld management is unavailable: {FriendlyMessage(exception)}",
            DateTimeOffset.UtcNow);
    }

    private static string RenderJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => $"({string.Join(',', value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.GetRawText()))})",
            _ => value.GetRawText()
        };

    private sealed record RestContext(
        Uri? BaseAddress,
        string? Password,
        PalworldRestOperationResult? Error);

    private sealed class PalworldRestException(string code, string message) :
        Exception(message)
    {
        public string Code { get; } = code;
    }
}
