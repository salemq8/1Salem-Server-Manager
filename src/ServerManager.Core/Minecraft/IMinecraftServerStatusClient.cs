namespace ServerManager.Core.Minecraft;

public sealed record MinecraftStatusPlayer(Guid Uuid, string Name);
public sealed record MinecraftServerStatus(int Online, int Maximum, IReadOnlyList<MinecraftStatusPlayer> Sample);

/// <summary>Read-only Minecraft server-list status; its sample is not a complete online roster.</summary>
public interface IMinecraftServerStatusClient
{
    Task<MinecraftServerStatus?> QueryAsync(GameServerDefinition server, CancellationToken cancellationToken);
}
