using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>
/// The Gameplay page's backend. Gamerules are real: asked of the running server through its
/// console and read back after every change, or read from the world's level.dat while the server
/// is not answering. A change made while the server cannot take it is kept and applied, then
/// verified, the next time 1Salem starts the server, so nothing here ever restarts a server.
/// server.properties values go through the safe writer and apply at the next start, except the
/// few that have a real live command.
/// </summary>
public sealed class MinecraftGameplayService : IDisposable
{
    public const string AuditActor = "LocalAdministrator";

    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(4);

    private readonly IGameServerStore _servers;
    private readonly IMinecraftConsoleChannel _console;
    private readonly ISettingsStore _settings;
    private readonly IAuditLogStore _audit;
    private readonly IMinecraftPropertiesWriter _propertiesWriter;
    private readonly ILogger<MinecraftGameplayService> _logger;
    private readonly MinecraftPlayerStateService? _playerState;

    // The name each rule has on a running server, learned from its answers. Cleared when the
    // server becomes ready again, since a restart can mean a different Minecraft version.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, string?>> _liveNames = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _pendingLocks = new();

    public MinecraftGameplayService(
        IGameServerStore servers,
        IMinecraftConsoleChannel console,
        ISettingsStore settings,
        IAuditLogStore audit,
        IMinecraftPropertiesWriter propertiesWriter,
        ILogger<MinecraftGameplayService> logger,
        MinecraftPlayerStateService? playerState = null)
    {
        _servers = servers;
        _console = console;
        _settings = settings;
        _audit = audit;
        _propertiesWriter = propertiesWriter;
        _logger = logger;
        _playerState = playerState;
        _console.ServerReady += OnServerReady;
    }

    public static string PendingKey(Guid serverId) => $"minecraft.gamerules.pending.{serverId:N}";

    public async Task<MinecraftGameplaySnapshot> GetAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var control = ToControl(_console.GetState(serverId));
        var properties = ReadProperties(server.RootPath);
        var worldRules = ReadWorldRules(server.RootPath, properties);
        var pending = await ReadPendingAsync(serverId, cancellationToken);
        var rules = new List<MinecraftGameRuleState>(MinecraftGameRuleCatalog.All.Count);
        foreach (var rule in MinecraftGameRuleCatalog.All)
        {
            var state = control == MinecraftLiveControl.Live
                ? await QueryLiveAsync(serverId, rule, worldRules, cancellationToken) ?? FromWorld(rule, worldRules)
                : FromWorld(rule, worldRules);
            rules.Add(pending.TryGetValue(rule.Key, out var pendingValue) && state.Value != pendingValue
                ? state with { PendingValue = pendingValue }
                : state);
        }

        var fallDamage = rules.First(rule => rule.Key == "fallDamage");
        var detected = MinecraftPlatformDetector.Detect(server.RootPath);
        return new MinecraftGameplaySnapshot(
            server.Id,
            detected.MinecraftVersion ?? server.InstalledVersion,
            detected.Platform == ServerPlatform.Unknown ? ServerPlatform.Vanilla : detected.Platform,
            control,
            rules.Any(rule => rule.Source != MinecraftValueSource.Unknown),
            rules,
            MinecraftGameplayPropertyPolicy.All
                .Select(definition => new MinecraftPropertyState(
                    definition.Key,
                    properties.TryGetValue(definition.Key, out var value) ? value : null,
                    MinecraftGameplayPropertyPolicy.CanApplyLive(definition.Key)))
                .ToArray(),
            MinecraftFallDamagePolicy.For(fallDamage.Supported && fallDamage.Source != MinecraftValueSource.Unknown),
            DateTimeOffset.UtcNow);
    }

    public async Task<MinecraftChangeResult> SetGameRuleAsync(
        Guid serverId,
        MinecraftGameRuleChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = MinecraftGameRuleCatalog.Find(request.Key);
        if (rule is null)
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "UnknownGameRule", "That gamerule is not offered.");
        }

        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var result = _console.GetState(serverId) == MinecraftConsoleState.Ready
            ? await ApplyLiveAsync(server, rule, request.Value, cancellationToken)
            : await SavePendingAsync(server, rule, request.Value, cancellationToken);
        await _audit.WriteAsync(
            AuditActor,
            "MinecraftGameRuleChanged",
            serverId.ToString(),
            result.Outcome != MinecraftChangeOutcome.Failed,
            $"{rule.Key}={MinecraftGameRuleCatalog.Format(request.Value)} ({result.Outcome}{(result.ErrorCode is null ? string.Empty : ", " + result.ErrorCode)})",
            cancellationToken);
        return result;
    }

    public async Task<MinecraftPropertiesChangeResult> SetPropertiesAsync(
        Guid serverId,
        MinecraftPropertiesChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Values is null || request.Values.Count == 0)
        {
            return new MinecraftPropertiesChangeResult(false, "NothingToSave", "No settings were changed.", false, []);
        }

        // All or nothing: one bad value and nothing is written.
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Values)
        {
            if (!MinecraftGameplayPropertyPolicy.TryNormalize(key, value, out var clean, out var error))
            {
                return new MinecraftPropertiesChangeResult(false, "InvalidValue", error, false, []);
            }

            normalized[key] = clean;
        }

        await GetMinecraftServerAsync(serverId, cancellationToken);
        var written = await _propertiesWriter.WriteAsync(serverId, normalized, cancellationToken);
        if (!written.Success)
        {
            await AuditPropertiesAsync(serverId, normalized, false, written.ErrorCode, cancellationToken);
            return new MinecraftPropertiesChangeResult(false, written.ErrorCode, written.Message, false, []);
        }

        var state = _console.GetState(serverId);
        var appliedLive = new List<string>();
        if (state == MinecraftConsoleState.Ready)
        {
            foreach (var (key, value) in normalized)
            {
                var command = MinecraftGameplayPropertyPolicy.LiveCommand(key, value);
                if (command is null)
                {
                    continue;
                }

                var exchange = await _console.ExchangeAsync(
                    serverId,
                    command,
                    line => MinecraftConsoleReplies.ClassifyPropertyReply(key, line) is not null,
                    AnswerTimeout,
                    cancellationToken);
                if (exchange.Result.Success && MinecraftConsoleReplies.ClassifyPropertyReply(key, exchange.Answer) == true)
                {
                    appliedLive.Add(key);
                }
            }
        }

        // Saved values reach a running server at its next start; a stopped one simply starts with them.
        var restartRequired = state != MinecraftConsoleState.NotRunning &&
                              normalized.Keys.Any(key => !appliedLive.Contains(key));
        await AuditPropertiesAsync(serverId, normalized, true, null, cancellationToken);
        return new MinecraftPropertiesChangeResult(true, null, null, restartRequired, appliedLive);
    }

    public async Task<MinecraftPlayersState> GetPlayersAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (_playerState is not null)
        {
            var snapshot = await _playerState.GetAsync(serverId, cancellationToken: cancellationToken);
            return new MinecraftPlayersState(snapshot.Control, snapshot.OnlineIdentitiesKnown && !snapshot.IsStale,
                snapshot.Players.Where(p => p.IsOnline == true && p.Username is not null).Select(p => p.Username!).ToArray(), snapshot.MaxPlayers,
                snapshot.Players.Where(p => p.IsOperator == true && p.Username is not null).Select(p => p.Username!).ToArray(),
                snapshot.Players.Where(p => p.IsWhitelisted == true && p.Username is not null).Select(p => p.Username!).ToArray(),
                snapshot.Players.Where(p => p.IsBanned == true && p.Username is not null).Select(p => p.Username!).ToArray(), snapshot.CapturedAtUtc);
        }
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var control = ToControl(_console.GetState(serverId));
        var properties = ReadProperties(server.RootPath);
        int? max = properties.TryGetValue("max-players", out var maxText) &&
                   int.TryParse(maxText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedMax)
            ? parsedMax
            : null;
        var onlineKnown = false;
        IReadOnlyList<string> online = [];
        if (control == MinecraftLiveControl.Live)
        {
            var exchange = await _console.ExchangeAsync(
                serverId,
                "list",
                line => MinecraftConsoleReplies.TryParseList(line, out _, out _, out _),
                AnswerTimeout,
                cancellationToken);
            if (exchange.Result.Success &&
                MinecraftConsoleReplies.TryParseList(exchange.Answer, out _, out var liveMax, out var names))
            {
                onlineKnown = true;
                online = names.Where(MinecraftPlayerCommandPolicy.IsValidName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                max = liveMax;
            }
        }

        return new MinecraftPlayersState(
            control,
            onlineKnown,
            online,
            max,
            ReadNames(server.RootPath, "ops.json"),
            ReadNames(server.RootPath, "whitelist.json"),
            ReadNames(server.RootPath, "banned-players.json"),
            DateTimeOffset.UtcNow);
    }

    public async Task<MinecraftChangeResult> PlayerActionAsync(
        Guid serverId,
        MinecraftPlayerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_playerState is not null) return await _playerState.AdministerByNameAsync(serverId, request, cancellationToken);
        if (!Enum.IsDefined(request.Action))
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "UnknownAction", "That player action is not offered.");
        }

        var player = request.Player?.Trim() ?? string.Empty;
        if (!MinecraftPlayerCommandPolicy.IsValidName(player))
        {
            return new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "InvalidPlayerName",
                "A Minecraft Java player name has 3-16 letters, digits or underscores.");
        }

        await GetMinecraftServerAsync(serverId, cancellationToken);
        var result = await RunPlayerActionAsync(serverId, request.Action, player, cancellationToken);
        await _audit.WriteAsync(
            AuditActor,
            request.Action switch
            {
                MinecraftPlayerAction.Op => "MinecraftPlayerOpped",
                MinecraftPlayerAction.Deop => "MinecraftPlayerDeopped",
                MinecraftPlayerAction.WhitelistAdd => "MinecraftPlayerWhitelisted",
                MinecraftPlayerAction.WhitelistRemove => "MinecraftPlayerUnwhitelisted",
                MinecraftPlayerAction.Kick => "MinecraftPlayerKicked",
                MinecraftPlayerAction.Ban => "MinecraftPlayerBanned",
                _ => "MinecraftPlayerPardoned"
            },
            serverId.ToString(),
            result.Outcome == MinecraftChangeOutcome.AppliedLive,
            result.ErrorCode is null ? player : $"{player} ({result.ErrorCode})",
            cancellationToken);
        return result;
    }

    public void Dispose() => _console.ServerReady -= OnServerReady;

    private async Task<MinecraftChangeResult> RunPlayerActionAsync(
        Guid serverId,
        MinecraftPlayerAction action,
        string player,
        CancellationToken cancellationToken)
    {
        var state = _console.GetState(serverId);
        if (state != MinecraftConsoleState.Ready)
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, NotLiveCode(state), NotLiveMessage(state));
        }

        if (MinecraftPlayerCommandPolicy.NeedsPlayerOnline(action))
        {
            var players = await GetPlayersAsync(serverId, cancellationToken);
            if (!players.OnlineKnown)
            {
                return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "ConsoleTimeout", "The server did not say who is online.");
            }

            if (!players.Online.Contains(player, StringComparer.OrdinalIgnoreCase))
            {
                return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "PlayerNotOnline", $"{player} is not on the server.");
            }
        }

        var exchange = await _console.ExchangeAsync(
            serverId,
            MinecraftPlayerCommandPolicy.BuildCommand(action, player),
            line => MinecraftConsoleReplies.ClassifyPlayerReply(action, player, line) is not null,
            AnswerTimeout,
            cancellationToken);
        if (!exchange.Result.Success)
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, exchange.Result.ErrorCode, exchange.Result.Message);
        }

        return MinecraftConsoleReplies.ClassifyPlayerReply(action, player, exchange.Answer) switch
        {
            MinecraftPlayerReply.Applied => new MinecraftChangeResult(MinecraftChangeOutcome.AppliedLive),
            MinecraftPlayerReply.NoChange => new MinecraftChangeResult(
                MinecraftChangeOutcome.AppliedLive,
                "NoChange",
                MinecraftConsoleReplies.Message(exchange.Answer)),
            MinecraftPlayerReply.UnknownPlayer => new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "UnknownPlayer",
                MinecraftConsoleReplies.Message(exchange.Answer)),
            _ => new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "Refused",
                MinecraftConsoleReplies.Message(exchange.Answer))
        };
    }

    private async Task<MinecraftChangeResult> ApplyLiveAsync(
        GameServerDefinition server,
        MinecraftGameRuleDefinition rule,
        bool value,
        CancellationToken cancellationToken)
    {
        var worldRules = ReadWorldRules(server.RootPath, ReadProperties(server.RootPath));
        var current = await QueryLiveAsync(server.Id, rule, worldRules, cancellationToken);
        if (current is null)
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "ConsoleTimeout", "The server did not answer in time.");
        }

        if (!current.Supported || current.ServerName is not { } serverName)
        {
            return new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "GameRuleNotSupported",
                "This server's Minecraft version does not have that gamerule.");
        }

        var set = await _console.ExchangeAsync(
            server.Id,
            $"gamerule {serverName} {MinecraftGameRuleCatalog.Format(value)}",
            line => MinecraftConsoleReplies.AnswersGameRule(line, serverName, set: true),
            AnswerTimeout,
            cancellationToken);
        if (!set.Result.Success)
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, set.Result.ErrorCode, set.Result.Message);
        }

        if (MinecraftConsoleReplies.IsCommandError(set.Answer))
        {
            return new MinecraftChangeResult(MinecraftChangeOutcome.Failed, "Refused", MinecraftConsoleReplies.Message(set.Answer));
        }

        // Read it back rather than trusting the set message.
        var verified = await QueryLiveAsync(server.Id, rule, worldRules, cancellationToken);
        await RemovePendingAsync(server.Id, rule.Key, cancellationToken);
        return verified?.Value == value
            ? new MinecraftChangeResult(MinecraftChangeOutcome.AppliedLive, VerifiedValue: value)
            : new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "VerificationFailed",
                "The server did not report the new value when asked again.",
                verified?.Value);
    }

    private async Task<MinecraftChangeResult> SavePendingAsync(
        GameServerDefinition server,
        MinecraftGameRuleDefinition rule,
        bool value,
        CancellationToken cancellationToken)
    {
        var worldRules = ReadWorldRules(server.RootPath, ReadProperties(server.RootPath));
        var fromWorld = FromWorld(rule, worldRules);
        if (!fromWorld.Supported)
        {
            return new MinecraftChangeResult(
                MinecraftChangeOutcome.Failed,
                "GameRuleNotSupported",
                "This server's Minecraft version does not have that gamerule.");
        }

        var gate = _pendingLocks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var pending = new Dictionary<string, bool>(await ReadPendingAsync(server.Id, cancellationToken), StringComparer.Ordinal);
            if (fromWorld.Value == value)
            {
                // Back to what the world already has: nothing left to apply.
                pending.Remove(rule.Key);
            }
            else
            {
                pending[rule.Key] = value;
            }

            await _settings.SetAsync(PendingKey(server.Id), pending, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return new MinecraftChangeResult(MinecraftChangeOutcome.PendingNextStart);
    }

    private async Task RemovePendingAsync(Guid serverId, string key, CancellationToken cancellationToken)
    {
        var gate = _pendingLocks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var pending = new Dictionary<string, bool>(await ReadPendingAsync(serverId, cancellationToken), StringComparer.Ordinal);
            if (pending.Remove(key))
            {
                await _settings.SetAsync(PendingKey(serverId), pending, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void OnServerReady(object? sender, Guid serverId)
    {
        _liveNames.TryRemove(serverId, out _);
        _ = ApplyPendingAsync(serverId);
    }

    /// <summary>Applies the gamerule changes saved while the server could not take them, then verifies each.</summary>
    internal async Task ApplyPendingAsync(Guid serverId)
    {
        try
        {
            var pending = await ReadPendingAsync(serverId, CancellationToken.None);
            if (pending.Count == 0)
            {
                return;
            }

            var server = await _servers.GetAsync(serverId, CancellationToken.None);
            if (server is null || server.Game != GameType.Minecraft)
            {
                return;
            }

            foreach (var (key, value) in pending)
            {
                var rule = MinecraftGameRuleCatalog.Find(key);
                if (rule is null)
                {
                    await RemovePendingAsync(serverId, key, CancellationToken.None);
                    continue;
                }

                var result = await ApplyLiveAsync(server, rule, value, CancellationToken.None);
                if (result.Outcome != MinecraftChangeOutcome.AppliedLive && result.ErrorCode is "GameRuleNotSupported" or "Refused")
                {
                    // The server will never take it; keeping it would retry forever.
                    await RemovePendingAsync(serverId, key, CancellationToken.None);
                }

                await _audit.WriteAsync(
                    AuditActor,
                    "MinecraftGameRuleChanged",
                    serverId.ToString(),
                    result.Outcome == MinecraftChangeOutcome.AppliedLive,
                    $"{key}={MinecraftGameRuleCatalog.Format(value)} (applied at start{(result.ErrorCode is null ? string.Empty : ", " + result.ErrorCode)})",
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Applying saved gamerules failed for server {ServerId}.", serverId);
        }
    }

    /// <summary>
    /// Asks the running server for one rule. Null means it did not answer in time (the caller
    /// then shows the saved value); an unsupported rule comes back with Supported false.
    /// </summary>
    private async Task<MinecraftGameRuleState?> QueryLiveAsync(
        Guid serverId,
        MinecraftGameRuleDefinition rule,
        IReadOnlyDictionary<string, string>? worldRules,
        CancellationToken cancellationToken)
    {
        var names = _liveNames.GetOrAdd(serverId, _ => new ConcurrentDictionary<string, string?>(StringComparer.Ordinal));
        IEnumerable<string> candidates;
        if (names.TryGetValue(rule.Key, out var known))
        {
            if (known is null)
            {
                return new MinecraftGameRuleState(rule.Key, null, false, null, MinecraftValueSource.Live, null);
            }

            candidates = [known];
        }
        else
        {
            // The world's own name for the rule first, then the other spellings.
            var fromWorld = worldRules is null ? null : MinecraftGameRuleCatalog.ResolveName(rule, worldRules.Keys.ToArray());
            candidates = fromWorld is null ? rule.Names : [fromWorld, .. rule.Names.Where(name => name != fromWorld)];
        }

        foreach (var name in candidates)
        {
            var exchange = await _console.ExchangeAsync(
                serverId,
                $"gamerule {name}",
                line => MinecraftConsoleReplies.AnswersGameRule(line, name, set: false),
                AnswerTimeout,
                cancellationToken);
            if (!exchange.Result.Success)
            {
                return null;
            }

            if (MinecraftConsoleReplies.TryParseGameRuleQuery(exchange.Answer, out _, out var text) &&
                MinecraftGameRuleCatalog.TryParseValue(text, out var value))
            {
                names[rule.Key] = name;
                return new MinecraftGameRuleState(rule.Key, name, true, value, MinecraftValueSource.Live, null);
            }
        }

        names[rule.Key] = null;
        return new MinecraftGameRuleState(rule.Key, null, false, null, MinecraftValueSource.Live, null);
    }

    /// <summary>
    /// The rule as the world last saved it. A world's saved rules list every rule its version has
    /// (level.dat's GameRules in older versions, game_rules.dat in 26.x), so once the list is
    /// recognisably complete a missing rule is not supported there. A rule saved as a number rather
    /// than on/off is not offered as a switch. With too little to go on, the rule is only unknown.
    /// </summary>
    internal static MinecraftGameRuleState FromWorld(MinecraftGameRuleDefinition rule, IReadOnlyDictionary<string, string>? worldRules)
    {
        if (worldRules is null)
        {
            return new MinecraftGameRuleState(rule.Key, null, true, null, MinecraftValueSource.Unknown, null);
        }

        var name = MinecraftGameRuleCatalog.ResolveName(rule, worldRules.Keys.ToArray());
        if (name is not null)
        {
            return MinecraftGameRuleCatalog.TryParseValue(worldRules[name], out var value)
                ? new MinecraftGameRuleState(rule.Key, name, true, value, MinecraftValueSource.WorldFile, null)
                : new MinecraftGameRuleState(rule.Key, name, false, null, MinecraftValueSource.WorldFile, null);
        }

        var completeList = MinecraftGameRuleCatalog.All.Count(known => known.Names.Any(worldRules.ContainsKey)) >= 5;
        return completeList
            ? new MinecraftGameRuleState(rule.Key, null, false, null, MinecraftValueSource.WorldFile, null)
            : new MinecraftGameRuleState(rule.Key, null, true, null, MinecraftValueSource.Unknown, null);
    }

    private static IReadOnlyDictionary<string, string>? ReadWorldRules(
        string rootPath,
        IReadOnlyDictionary<string, string> properties)
    {
        var levelName = properties.TryGetValue("level-name", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : "world";
        if (levelName.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(levelName) || levelName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return LevelDatGameRules.ReadWorld(Path.Combine(rootPath, levelName));
    }

    private async Task<IReadOnlyDictionary<string, bool>> ReadPendingAsync(Guid serverId, CancellationToken cancellationToken)
    {
        try
        {
            return await _settings.GetAsync<Dictionary<string, bool>>(PendingKey(serverId), cancellationToken) ??
                   new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    private Task AuditPropertiesAsync(
        Guid serverId,
        IReadOnlyDictionary<string, string> values,
        bool succeeded,
        string? errorCode,
        CancellationToken cancellationToken) =>
        _audit.WriteAsync(
            AuditActor,
            "MinecraftGameplaySettingsChanged",
            serverId.ToString(),
            succeeded,
            string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}")) + (errorCode is null ? string.Empty : $" ({errorCode})"),
            cancellationToken);

    private async Task<GameServerDefinition> GetMinecraftServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await _servers.GetAsync(serverId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Server {serverId} is not registered.");
        if (server.Game != GameType.Minecraft)
        {
            throw new ArgumentException("The selected server is not Minecraft.", nameof(serverId));
        }

        return server;
    }

    private static MinecraftLiveControl ToControl(MinecraftConsoleState state) =>
        state switch
        {
            MinecraftConsoleState.Ready => MinecraftLiveControl.Live,
            MinecraftConsoleState.Starting => MinecraftLiveControl.Starting,
            MinecraftConsoleState.NoConsole => MinecraftLiveControl.NoConsole,
            _ => MinecraftLiveControl.Stopped
        };

    private static string NotLiveCode(MinecraftConsoleState state) =>
        state switch
        {
            MinecraftConsoleState.NoConsole => "ConsoleUnavailable",
            MinecraftConsoleState.Starting => "ServerStarting",
            _ => "ServerNotRunning"
        };

    private static string NotLiveMessage(MinecraftConsoleState state) =>
        state switch
        {
            MinecraftConsoleState.NoConsole =>
                "This server was started before the Agent last restarted. Restart it from 1Salem to manage players live.",
            MinecraftConsoleState.Starting => "The server is still starting.",
            _ => "Start the server to manage players."
        };

    private static IReadOnlyDictionary<string, string> ReadProperties(string rootPath)
    {
        var path = Path.Combine(rootPath, "server.properties");
        try
        {
            return File.Exists(path)
                ? MinecraftPropertiesSerializer.Parse(File.ReadAllText(path))
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static IReadOnlyList<string> ReadNames(string rootPath, string fileName)
    {
        var path = Path.Combine(rootPath, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object &&
                                   item.TryGetProperty("name", out var name) &&
                                   name.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("name").GetString()!)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
