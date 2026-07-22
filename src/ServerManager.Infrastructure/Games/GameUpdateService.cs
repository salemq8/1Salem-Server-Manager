using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Games;

public sealed class GameUpdateService(
    MinecraftUpdateService minecraft,
    PalworldUpdateService palworld) : IUpdateService
{
    public Task<UpdateCheckResult> CheckAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default) =>
        server.Game switch
        {
            GameType.Minecraft => minecraft.CheckAsync(server, cancellationToken),
            GameType.Palworld => palworld.CheckAsync(server, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(server))
        };

    public Task<OperationResult> UpdateAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default) =>
        server.Game switch
        {
            GameType.Minecraft => minecraft.UpdateAsync(server, cancellationToken),
            GameType.Palworld => palworld.UpdateAsync(server, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(server))
        };
}
