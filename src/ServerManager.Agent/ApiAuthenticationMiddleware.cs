using System.Net;
using Microsoft.Net.Http.Headers;
using ServerManager.Core;
using ServerManager.Infrastructure.Security;

namespace ServerManager.Agent;

/// <summary>
/// Every privileged Agent HTTP endpoint requires authentication, including loopback callers.
/// Loopback origin is never treated as sufficient on its own -- a loopback caller must present
/// the local bearer credential (a high-entropy value read from a file that is ACL-restricted to
/// local Administrators/SYSTEM), and a LAN caller must present a paired-client bearer
/// credential. The only exceptions are the liveness probe (reveals no server state) and
/// completing a pairing handshake (secured separately by a short-lived, rate-limited, one-use
/// code -- that is the one endpoint a not-yet-trusted device is expected to call anonymously).
/// </summary>
public sealed class ApiAuthenticationMiddleware(
    RequestDelegate next,
    AgentOptions options)
{
    public async Task InvokeAsync(
        HttpContext context,
        IPairingService pairingService,
        ILocalAgentCredential localCredential)
    {
        var path = context.Request.Path;
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (path.Equals("/api/v1/pairing/complete", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var isLoopback = context.Connection.RemoteIpAddress is { } address &&
            IPAddress.IsLoopback(address);
        var credential = ExtractBearerCredential(context, path);

        if (isLoopback && localCredential.Validate(credential))
        {
            context.Items["LocalClient"] = true;
            await next(context);
            return;
        }

        // Minting a pairing code is itself a privileged action -- it is the seed for granting
        // a brand-new device full remote control -- so it requires the same local credential
        // as every other privileged loopback operation. Loopback origin alone is not enough.
        if (path.Equals("/api/v1/pairing/challenge", StringComparison.OrdinalIgnoreCase))
        {
            RejectUnauthenticated(context, credential);
            return;
        }

        if (!options.LanEnabled)
        {
            RejectUnauthenticated(context, credential);
            return;
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            RejectUnauthenticated(context, credential);
            return;
        }

        var client = await pairingService.ValidateCredentialAsync(
            credential,
            context.RequestAborted);
        if (client is null)
        {
            // credential is guaranteed non-empty here (checked above), so this is a
            // present-but-wrong LAN credential -- 403, not 401, per RejectUnauthenticated's
            // documented 401-for-none/403-for-wrong contract.
            RejectUnauthenticated(context, credential);
            return;
        }

        context.Items["PairedClient"] = client;
        await next(context);
    }

    private static void RejectUnauthenticated(HttpContext context, string credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }

    private static string ExtractBearerCredential(HttpContext context, PathString path)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return path.StartsWithSegments("/hubs/agent") &&
               context.Request.Query.TryGetValue("access_token", out var accessToken)
            ? accessToken.ToString()
            : string.Empty;
    }
}
