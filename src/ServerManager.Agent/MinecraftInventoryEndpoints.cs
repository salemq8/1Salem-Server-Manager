using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Agent;

public static class MinecraftInventoryEndpoints
{
    public static void MapMinecraftInventoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/servers/{serverId:guid}/minecraft/players/{playerUuid:guid}/inventory",
            async (Guid serverId, Guid playerUuid, MinecraftInventoryService inventory, CancellationToken cancellationToken) =>
            {
                if (playerUuid == Guid.Empty) return Results.BadRequest();
                try { return Results.Ok(await inventory.GetAsync(serverId, playerUuid, cancellationToken)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException) { return Results.BadRequest(); }
            });
    }
}
