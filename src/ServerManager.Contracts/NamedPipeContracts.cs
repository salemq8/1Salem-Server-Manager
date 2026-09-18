using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>
    /// Resolves the Agent's data root the same way for both the Agent (its default, absent
    /// an explicit `--data-root`) and the Client (which has no other way to discover it,
    /// since both processes run on the same machine and normally agree on this default).
    /// </summary>
    public static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ONE_SALEM_AGENT_DATA_ROOT");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "1SalemServerManager")
            : Path.GetFullPath(configured.Trim());
    }

    /// <summary>
    /// The high-entropy local bearer credential both the Agent and the Client use to
    /// authenticate loopback API calls. The file is ACL-restricted (Administrators/SYSTEM
    /// only) by the Agent when it creates it -- loopback origin alone is never treated as
    /// proof of authorization.
    /// </summary>
    public static string ResolveLocalAgentKeyPath(string? dataRoot = null) =>
        Path.Combine(
            Path.GetFullPath(dataRoot ?? ResolveDataRoot()),
            "security",
            "local-agent.key");

    /// <summary>
    /// Reads the local bearer credential the Agent generated, or null if it hasn't been
    /// created yet (e.g., the Agent has never started) or can't be read (e.g., this account
    /// isn't a local Administrator). Never throws.
    /// </summary>
    public static string? TryReadLocalAgentKey()
    {
        try
        {
            var path = ResolveLocalAgentKeyPath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> pointed at the Agent's loopback API with the local
    /// bearer credential already attached, if one could be read. Every Client call site that
    /// talks to the Agent over loopback should be built this way instead of constructing an
    /// <see cref="HttpClient"/> directly, so authentication stays centralized in one place.
    /// </summary>
    public static HttpClient CreateLoopbackHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(ResolveLoopbackApiUrl()),
            Timeout = timeout
        };
        var credential = TryReadLocalAgentKey();
        if (!string.IsNullOrEmpty(credential))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);
        }

        return client;
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
