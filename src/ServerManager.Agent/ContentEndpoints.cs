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
                string? kind,
                string? platform,
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
                    limit ?? 20,
                    Enum.TryParse<ContentKind>(kind, true, out var parsedKind)
                        ? parsedKind
                        : ContentKind.Plugin,
                    platform);
                return Results.Ok(await catalog.SearchAsync(request, profile, cancellationToken));
            });

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/projects/{provider}/{projectId}",
            async (
                Guid serverId,
                string provider,
                string projectId,
                string? kind,
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
                        Enum.TryParse<ContentKind>(kind, true, out var parsed)
                            ? parsed
                            : ContentKind.Plugin,
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
                PackInstallService packs,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                if (!ContentTypePolicy.IsSupportedBy(request.Kind, profile))
                {
                    return Results.Conflict(ContentOperationResult.Fail(
                        "UnsupportedServer",
                        "This server cannot take that content."));
                }

                // The content type decides the pipeline: a data pack is not installed the way
                // a plugin is, and neither is decided by which provider it came from.
                var result = request.Kind == ContentKind.Plugin
                    ? await installer.InstallAsync(
                        profile,
                        request with { ServerId = serverId },
                        null,
                        cancellationToken)
                    : await packs.InstallAsync(
                        profile,
                        request with { ServerId = serverId },
                        null,
                        cancellationToken);
                return Respond(result);
            });

        app.MapGet(
            "/api/v1/servers/{serverId:guid}/content/modpacks/plan",
            async (
                Guid serverId,
                string provider,
                string projectId,
                string? versionId,
                ContentProfileService profiles,
                ModpackService modpacks,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                if (!Enum.TryParse<ContentProviderId>(provider, true, out var providerId))
                {
                    return Results.NotFound();
                }

                return Results.Ok(await modpacks.PlanAsync(
                    profile,
                    providerId,
                    projectId,
                    versionId,
                    cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/modpacks/install",
            async (
                Guid serverId,
                ModpackInstallRequest request,
                ContentProfileService profiles,
                ModpackService modpacks,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                // This always builds a NEW server; the server in the route is only the one
                // being browsed from and is never modified.
                var result = await modpacks.InstallAsync(profile, request, null, cancellationToken);
                return result.Success
                    ? Results.Ok(result)
                    : Results.BadRequest(result);
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/resource-pack/distribute",
            async (
                Guid serverId,
                ResourcePackDistributionRequest request,
                ContentProfileService profiles,
                PackInstallService packs,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Respond(await packs.DistributeAsync(profile, request, cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/resource-pack/withdraw",
            async (
                Guid serverId,
                ContentProfileService profiles,
                PackInstallService packs,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                return Respond(await packs.StopDistributingAsync(profile, cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/update",
            async (
                Guid serverId,
                ContentFileRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                PackInstallService packs,
                IInstalledContentStore store,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                var kind = await KindOfAsync(store, serverId, request.FileName, cancellationToken);
                return Respond(kind == ContentKind.Plugin
                    ? await installer.UpdateAsync(
                        profile,
                        request.FileName,
                        request.VersionId,
                        null,
                        cancellationToken)
                    : await packs.UpdateAsync(
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
                PackInstallService packs,
                IInstalledContentStore store,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                var kind = await KindOfAsync(store, serverId, request.FileName, cancellationToken);
                return Respond(kind == ContentKind.Plugin
                    ? await installer.RollbackAsync(profile, request.FileName, cancellationToken)
                    : await packs.RollbackAsync(profile, request.FileName, cancellationToken));
            });

        app.MapPost(
            "/api/v1/servers/{serverId:guid}/content/uninstall",
            async (
                Guid serverId,
                ContentFileRequest request,
                ContentProfileService profiles,
                PluginInstallService installer,
                PackInstallService packs,
                IInstalledContentStore store,
                CancellationToken cancellationToken) =>
            {
                var profile = await profiles.GetAsync(serverId, cancellationToken);
                if (profile is null)
                {
                    return Results.NotFound();
                }

                var kind = await KindOfAsync(store, serverId, request.FileName, cancellationToken);
                return Respond(kind == ContentKind.Plugin
                    ? await installer.UninstallAsync(profile, request.FileName, cancellationToken)
                    : await packs.UninstallAsync(profile, request.FileName, cancellationToken));
            });
    }

    /// <summary>
    /// What kind of content a managed file is, so update, rollback and uninstall go through
    /// the right pipeline. An unrecorded file is treated as a plugin, the only kind whose
    /// folder is scanned without a record.
    /// </summary>
    private static async Task<ContentKind> KindOfAsync(
        IInstalledContentStore store,
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(serverId, fileName, cancellationToken);
        return record?.Kind ?? ContentKind.Plugin;
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

