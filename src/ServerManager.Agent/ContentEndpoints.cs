using ServerManager.Contracts;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Agent;

/// <summary>
/// The Content Hub's HTTP surface. Every route resolves the server's profile first, so the
/// install target and the compatibility rules always come from the registered server rather
/// than from anything the caller sent.
/// </summary>
public static class ContentEndpoints
{
    public static void MapContentEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/profile",
            async (
                Guid serverId,
                ContentProfileService profiles,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                return profile is null ? Results.NotFound() : Results.Ok(profile);
            });

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/search",
            async (
                Guid serverId,
                string? query,
                string? sort,
                bool? compatibleOnly,
                string? provider,
                int? offset,
                int? limit,
                ContentProfileService profiles,
                ContentCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                var request = new ContentSearchRequest(
                    query,
                    Enum.TryParse<ContentSortOrder>(sort, true, out var parsedSort)
                        ? parsedSort
                        : ContentSortOrder.Relevance,
                    compatibleOnly ?? true,
                    Enum.TryParse<ContentProviderId>(provider, true, out var parsedProvider)
                        ? parsedProvider
                        : null,
                    offset ?? 0,
                    limit ?? 20);
                return Results.Ok(await catalog.SearchAsync(request, profile, cancellationToken));
            });

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/projects/{provider}/{projectId}",
            async (
                Guid serverId,
                string provider,
                string projectId,
                ContentProfileService profiles,
                ContentCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                if (!Enum.TryParse<ContentProviderId>(provider, true, out var providerId))
                {
                    return Results.NotFound();
                }

                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                try
                {
                    var (project, latest, versions) = await catalog.GetProjectAsync(
                        providerId,
                        projectId,
                        profile,
                        cancellationToken);
                    return project is null
                        ? Results.NotFound()
                        : Results.Ok(new ContentProjectDetail(project, latest, versions));
                }
                catch (ContentProviderException exception)
                {
                    return ProviderProblem(exception);
                }
            });

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/installed",
            async (
                Guid serverId,
                bool? identify,
                bool? updates,
                ContentProfileService profiles,
                InstalledContentService installed,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(await installed.ListAsync(
                    profile,
                    identify ?? false,
                    updates ?? false,
                    cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/plan",
            async (
                Guid serverId,
                ContentInstallRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(await installer.PlanAsync(
                    profile,
                    request with { ServerId = serverId },
                    cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/install",
            async (
                Guid serverId,
                ContentInstallRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                if (!profile.SupportsPlugins)
                {
                    return Results.Conflict(ContentOperationResult.Fail(
                        "UnsupportedServer",
                        "This server does not support plugins."));
                }

                var result = await installer.InstallAsync(
                    profile,
                    request with { ServerId = serverId },
                    null,
                    cancellationToken);
                return Respond(result);
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/update",
            async (
                Guid serverId,
                ContentFileRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Respond(await installer.UpdateAsync(
                    profile,
                    request.FileName,
                    request.VersionId,
                    null,
                    cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/rollback",
            async (
                Guid serverId,
                ContentFileRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Respond(await installer.RollbackAsync(
                    profile,
                    request.FileName,
                    cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/uninstall",
            async (
                Guid serverId,
                ContentFileRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Respond(await installer.UninstallAsync(
                    profile,
                    request.FileName,
                    cancellationToken));
            });
    }

    /// <summary>
    /// A failed operation answers with its code and a plain sentence, so the client can
    /// phrase it in the person's language instead of showing an exception.
    /// </summary>
    private static IResult Respond(ContentOperationResult result) =>
        result.Success
            ? Results.Ok(result)
            : result.ErrorCode switch
            {
                "ServerBusy" => Results.Conflict(result),
                "UnsupportedServer" or "NotManaged" or "AlreadyCurrent" => Results.Conflict(result),
                "AccessDenied" => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(result)
            };

    private static IResult ProviderProblem(ContentProviderException exception) =>
        Results.Json(
            ContentOperationResult.Fail(exception.ErrorCode, exception.Message),
            statusCode: exception.ErrorCode == ContentProviderException.NotFoundCode
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status502BadGateway);
}
