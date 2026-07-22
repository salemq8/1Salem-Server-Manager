using System.Net;
using Microsoft.Net.Http.Headers;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ApiAuthenticationMiddleware(
    RequestDelegate next,
    AgentOptions options)
{
    public async Task InvokeAsync(HttpContext context, IPairingService pairingService)
    {
        if (!options.LanEnabled)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path;
        var isLoopback = context.Connection.RemoteIpAddress is { } address &&
            IPAddress.IsLoopback(address);
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/pairing/complete", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (path.Equals("/api/v1/pairing/challenge", StringComparison.OrdinalIgnoreCase))
        {
            if (isLoopback)
            {
                await next(context);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }

            return;
        }

        if (isLoopback && options.TrustLocalhost)
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var credential = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : path.StartsWithSegments("/hubs/agent") &&
              context.Request.Query.TryGetValue("access_token", out var accessToken)
                ? accessToken.ToString()
                : string.Empty;
        if (string.IsNullOrWhiteSpace(credential))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return;
        }

        var client = await pairingService.ValidateCredentialAsync(
            credential,
            context.RequestAborted);
        if (client is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items["PairedClient"] = client;
        await next(context);
    }
}
