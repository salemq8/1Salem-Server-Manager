using ServerManager.Contracts;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Agent;

public static class MinecraftPlayerEndpoints
{
    public static void MapMinecraftPlayerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/servers/{serverId:guid}/minecraft/players/dashboard",
            async (Guid serverId, MinecraftPlayerStateService players, CancellationToken cancellationToken) =>
                Results.Ok(await players.GetAsync(serverId, cancellationToken: cancellationToken)));
        app.MapPost("/api/v1/servers/{serverId:guid}/minecraft/players/administration",
            async (Guid serverId, MinecraftPlayerAdministrationRequest request, MinecraftPlayerStateService players, CancellationToken cancellationToken) =>
                Results.Ok(await players.AdministerAsync(serverId, request, cancellationToken)));
    }
}
