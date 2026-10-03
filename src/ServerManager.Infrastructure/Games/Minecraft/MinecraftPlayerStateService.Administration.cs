using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed partial class MinecraftPlayerStateService
{
    public async Task<MinecraftChangeResult> AdministerAsync(Guid serverId, MinecraftPlayerAdministrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Uuid == Guid.Empty) return Failed("InvalidPlayerUuid", "A known player UUID is required.");
        if (!Enum.IsDefined(request.Action)) return Failed("UnknownAction", "That player action is not offered.");
        if (MinecraftPlayerCommandPolicy.NeedsConfirmation(request.Action) && !request.Confirmed)
            return Failed("ConfirmationRequired", "Confirm this player action before sending it to Minecraft.");
        var server = await GetServerAsync(serverId, cancellationToken);
        var entry = _entries.GetOrAdd(serverId, _ => new Entry());
        await entry.ActionGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetAsync(serverId, true, cancellationToken);
            var player = snapshot.Players.FirstOrDefault(p => p.Uuid == request.Uuid);
            if (player is null || !MinecraftPlayerCommandPolicy.IsValidName(player.Username)) return Failed("UnknownPlayer", "Minecraft has not supplied a valid username for this UUID.");
            var name = player.Username!;
            if (snapshot.Players.Count(p => string.Equals(p.Username, name, StringComparison.OrdinalIgnoreCase)) != 1)
                return Failed("AmbiguousPlayer", "The saved username belongs to more than one UUID; refresh the server's player data first.");
            if (snapshot.Control != MinecraftLiveControl.Live)
                return Failed(snapshot.Control == MinecraftLiveControl.NoConsole ? "ConsoleUnavailable" : "ServerNotReady",
                    "The server has no live command channel. It has not been restarted or reconfigured.");
            if (request.Action == MinecraftPlayerAction.Kick && (!snapshot.OnlineIdentitiesKnown || snapshot.IsStale || player.IsOnline != true))
                return Failed("PlayerNotOnline", "The server has not verified this player is online.");
            var exchange = await _console.ExchangeAsync(serverId, MinecraftPlayerCommandPolicy.BuildCommand(request.Action, name),
                line => MinecraftConsoleReplies.ClassifyPlayerReply(request.Action, name, line) is not null, TimeSpan.FromSeconds(4), cancellationToken);
            var reply = MinecraftConsoleReplies.ClassifyPlayerReply(request.Action, name, exchange.Answer);
            var applied = exchange.Result.Success && reply is MinecraftPlayerReply.Applied or MinecraftPlayerReply.NoChange;
            var verified = false;
            if (applied)
            {
                // The console success line alone is insufficient: re-read Minecraft's own files or roster.
                for (var attempt = 0; attempt < 5 && !verified; attempt++)
                {
                    if (attempt != 0) await Task.Delay(100, cancellationToken);
                    var after = await GetAsync(serverId, true, cancellationToken);
                    var actual = after.Players.FirstOrDefault(p => p.Uuid == request.Uuid);
                    verified = request.Action switch
                    {
                        MinecraftPlayerAction.Kick => !after.IsStale && after.OnlineIdentitiesKnown && actual?.IsOnline == false,
                        MinecraftPlayerAction.Op => actual?.IsOperator == true,
                        MinecraftPlayerAction.Deop => actual?.IsOperator == false,
                        MinecraftPlayerAction.WhitelistAdd => actual?.IsWhitelisted == true,
                        MinecraftPlayerAction.WhitelistRemove => actual?.IsWhitelisted == false,
                        MinecraftPlayerAction.Ban => actual?.IsBanned == true,
                        MinecraftPlayerAction.Pardon => actual?.IsBanned == false,
                        _ => false
                    };
                }
            }
            await _audit.WriteAsync(MinecraftGameplayService.AuditActor, "MinecraftPlayerAdministration", server.Id.ToString(), verified,
                $"{request.Action}: {request.Uuid:D}", cancellationToken);
            return verified ? new MinecraftChangeResult(MinecraftChangeOutcome.AppliedLive) :
                Failed(applied ? "VerificationFailed" : "Refused", applied ? "Minecraft did not report the resulting player state." : "Minecraft did not confirm the player command.");
        }
        finally { entry.ActionGate.Release(); }
    }

    public async Task<MinecraftChangeResult> AdministerByNameAsync(Guid serverId, MinecraftPlayerActionRequest request, CancellationToken cancellationToken = default)
    {
        if (!MinecraftPlayerCommandPolicy.IsValidName(request.Player)) return Failed("InvalidPlayerName", "A Minecraft Java username must contain only 3-16 letters, digits or underscores.");
        var snapshot = await GetAsync(serverId, cancellationToken: cancellationToken);
        var matches = snapshot.Players.Where(p => string.Equals(p.Username, request.Player, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? await AdministerAsync(serverId, new(matches[0].Uuid, request.Action, request.Confirmed), cancellationToken)
            : Failed("UnknownPlayer", "A unique known UUID is required for player administration.");
    }

    private static MinecraftChangeResult Failed(string code, string message) => new(MinecraftChangeOutcome.Failed, code, message);
}
