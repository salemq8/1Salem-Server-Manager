using System.Net;
using System.Text.Json;

namespace ServerManager.Contracts;

public static class AgentTransportDefaults
{
    public const string PipeName = "1Salem.ServerManager.Agent.v1";
    public const string LoopbackApiUrl = "http://127.0.0.1:5251";
    public const int MaximumPipeMessageBytes = 1024 * 1024;

    public static string ResolvePipeName() =>
        string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ONE_SALEM_AGENT_PIPE"))
            ? PipeName
            : Environment.GetEnvironmentVariable("ONE_SALEM_AGENT_PIPE")!.Trim();

    public static string ResolveLoopbackApiUrl()
    {
        var configured = Environment.GetEnvironmentVariable(
            "ONE_SALEM_AGENT_URL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return LoopbackApiUrl;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.IsLoopback(
                Dns.GetHostAddresses(uri.Host)
                    .FirstOrDefault() ?? IPAddress.None))
        {
            throw new InvalidOperationException(
                "ONE_SALEM_AGENT_URL must be an HTTP loopback address.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public static class NamedPipeOperations
{
    public const string Ping = "agent.ping";
    public const string GetStatus = "agent.status";

    public static bool IsPhaseOneAllowed(string operation) =>
        operation is Ping or GetStatus;
}

public sealed record PipeRequest(
    string Id,
    string Operation,
    JsonElement? Payload = null);

public sealed record PipeResponse(
    string Id,
    bool Success,
    JsonElement? Payload,
    ApiErrorResponse? Error)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static PipeResponse Ok<T>(string id, T payload) =>
        new(id, true, JsonSerializer.SerializeToElement(payload, SerializerOptions), null);

    public static PipeResponse Fail(string id, ApiErrorResponse error) =>
        new(id, false, null, error);
}
