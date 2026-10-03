using ServerManager.Contracts;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Agent;

public static class MinecraftSoftwareEndpoints
{
    public static void MapMinecraftSoftwareEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/servers/{serverId:guid}/minecraft/software",
            async (Guid serverId, MinecraftSoftwareService software, CancellationToken cancellationToken) =>
                Results.Ok(await software.GetAsync(serverId, cancellationToken)));
        app.MapPost("/api/v1/servers/{serverId:guid}/minecraft/software",
            async (Guid serverId, MinecraftSoftwareMigrationRequest request, MinecraftSoftwareService software,
                CancellationToken cancellationToken) => Results.Ok(await software.MigrateAsync(serverId, request, cancellationToken)));
        app.MapPost("/api/v1/servers/{serverId:guid}/minecraft/software/prepare",
            async (Guid serverId, MinecraftSoftwareService software, CancellationToken cancellationToken) =>
                Results.Ok(await software.PrepareAsync(serverId, cancellationToken)));
    }
}
