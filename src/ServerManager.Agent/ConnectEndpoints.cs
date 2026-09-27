using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Agent;

/// <summary>Local-desktop-only owner API. Paired LAN clients are refused on every route.</summary>
public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/connect/status", (HttpContext context, ConnectOwnerWorkflow workflow) =>
            Local(context, () => Results.Ok(workflow.Status)));

        app.MapPut("/api/v1/connect/credential", (HttpContext context, ConnectCredentialRequest request,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalAsync(context, async () =>
            {
                await workflow.SetCredentialAsync(request, cancellationToken);
                return Results.Ok(workflow.Status);
            }));

        app.MapDelete("/api/v1/connect/credential", (HttpContext context, ConnectOwnerWorkflow workflow,
            CancellationToken cancellationToken) =>
            LocalAsync(context, async () =>
            {
                await workflow.RemoveCredentialAsync(cancellationToken);
                return Results.Ok(OperationResult.Ok());
            }));

        app.MapPost("/api/v1/connect/check", (HttpContext context, ConnectOwnerWorkflow workflow,
            CancellationToken cancellationToken) =>
            LocalAsync(context, async () =>
            {
                await workflow.CheckAsync(cancellationToken);
                return Results.Ok(workflow.Status);
            }));

        app.MapGet("/api/v1/servers/{serverId:guid}/connect", (HttpContext context, Guid serverId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalAsync(context, async () => Results.Ok(await workflow.GetServerAsync(serverId, cancellationToken))));

        app.MapPost("/api/v1/servers/{serverId:guid}/connect/enable", (HttpContext context, Guid serverId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.EnableAsync(serverId, cancellationToken)));

        app.MapPost("/api/v1/servers/{serverId:guid}/connect/disable", (HttpContext context, Guid serverId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.DisableAsync(serverId, cancellationToken)));

        app.MapPost("/api/v1/servers/{serverId:guid}/connect/invites", (HttpContext context, Guid serverId,
            ConnectInviteRequest request, ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalAsync(context, async () => Results.Ok(
                await workflow.CreateInviteAsync(serverId, request.TtlSeconds, cancellationToken))));

        app.MapPost("/api/v1/connect/invites/{inviteId}/revoke", (HttpContext context, string inviteId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.RevokeInviteAsync(inviteId, cancellationToken)));

        app.MapPost("/api/v1/connect/memberships/{membershipId}/approve", (HttpContext context, string membershipId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.ApproveAsync(membershipId, cancellationToken)));

        app.MapPost("/api/v1/connect/memberships/{membershipId}/reject", (HttpContext context, string membershipId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.RejectAsync(membershipId, cancellationToken)));

        app.MapPost("/api/v1/connect/memberships/{membershipId}/revoke", (HttpContext context, string membershipId,
            ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalAsync(context, async () => Results.Ok(await workflow.RevokeAsync(membershipId, cancellationToken))));

        app.MapPut("/api/v1/connect/memberships/{membershipId}/nickname", (HttpContext context, string membershipId,
            ConnectNicknameRequest request, ConnectOwnerWorkflow workflow, CancellationToken cancellationToken) =>
            LocalActionAsync(context, () => workflow.SetNicknameAsync(membershipId, request.Nickname, cancellationToken)));
    }

    public static bool IsLocalClient(HttpContext context) =>
        context.Items.TryGetValue("LocalClient", out var value) && value is true;

    private static IResult Local(HttpContext context, Func<IResult> action) =>
        IsLocalClient(context) ? action() : LocalOnly();

    private static Task<IResult> LocalActionAsync(HttpContext context, Func<Task> action) =>
        LocalAsync(context, async () =>
        {
            await action();
            return Results.Ok(OperationResult.Ok());
        });

    private static async Task<IResult> LocalAsync(HttpContext context, Func<Task<IResult>> action)
    {
        if (!IsLocalClient(context))
        {
            return LocalOnly();
        }

        try
        {
            return await action();
        }
        catch (ConnectHostOperationException exception)
        {
            var result = OperationResult.Fail(exception.ErrorCode, exception.Message);
            return exception.ErrorCode switch
            {
                ConnectErrorCodes.NotFound => Results.NotFound(result),
                ConnectErrorCodes.InvalidRequest => Results.BadRequest(result),
                _ => Results.Conflict(result)
            };
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(OperationResult.Fail(ConnectErrorCodes.InvalidRequest, exception.Message));
        }
        catch (ConnectPolicyNotPermittedException exception)
        {
            return Results.Json(OperationResult.Fail(ConnectErrorCodes.PolicyNotPermitted, exception.Message), statusCode: 403);
        }
        catch (ConnectOwnerBrokerException exception)
        {
            var rejected = exception.Failure is ConnectOwnerBrokerFailure.Conflict or ConnectOwnerBrokerFailure.Rejected;
            var code = rejected ? ConnectErrorCodes.BrokerRejected : ConnectErrorCodes.BrokerUnavailable;
            return Results.Json(OperationResult.Fail(code, exception.Message),
                statusCode: rejected ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable);
        }
        catch (ConnectProvisioningException exception)
        {
            return Results.Json(OperationResult.Fail(ConnectErrorCodes.TailnetUnavailable, exception.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult LocalOnly() => Results.Json(
        OperationResult.Fail(ConnectErrorCodes.LocalClientOnly, "1Salem Connect owner settings are available only on this PC."),
        statusCode: StatusCodes.Status403Forbidden);
}
