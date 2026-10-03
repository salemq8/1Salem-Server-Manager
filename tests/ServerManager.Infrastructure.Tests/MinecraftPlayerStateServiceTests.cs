using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftPlayerStateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-players-test-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _alex = Guid.NewGuid();
    private readonly Servers _servers = new();
    private readonly ConsoleChannel _console = new();
    private readonly Settings _settings = new();
    private readonly Status _status = new();
    private readonly Audit _audit = new();
    private readonly GameServerDefinition _server;
    private readonly MinecraftPlayerStateService _service;

    public MinecraftPlayerStateServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=kept-world\nmax-players=20\n");
        _server = new(Guid.NewGuid(), GameType.Minecraft, "Fixture", _root, 25565, "26.3", DateTimeOffset.UtcNow);
        _servers.Items.Add(_server);
        WriteIdentities("usercache.json", (_alex, "Alex"));
        WriteIdentities("ops.json"); WriteIdentities("whitelist.json"); WriteIdentities("banned-players.json");
        _service = CreateService();
    }

    private MinecraftPlayerStateService CreateService() => new(_servers, _console, _settings, _audit, _status, NullLogger<MinecraftPlayerStateService>.Instance);

    [Fact]
    public async Task AllCallersShareOneVerifiedCount_RefreshFailureKeepsItStale()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Names = ["Alex"];
        var first = await _service.GetAsync(_server.Id);
        var second = await _service.GetAsync(_server.Id);
        Assert.Same(first, second);
        Assert.Single(_console.Commands);
        Assert.Equal(1, first.OnlinePlayers); Assert.Equal(20, first.MaxPlayers);
        Assert.False(first.IsStale); Assert.True(first.OnlineIdentitiesKnown);
        Assert.True(Assert.Single(first.Players).IsOnline);
        _console.Timeout = true;
        var failed = await _service.GetAsync(_server.Id, true);
        Assert.Equal(1, failed.OnlinePlayers); Assert.Equal(20, failed.MaxPlayers);
        Assert.Equal(first.LastVerifiedAtUtc, failed.LastVerifiedAtUtc);
        Assert.True(failed.IsStale); Assert.False(failed.OnlineIdentitiesKnown);
        Assert.Same(failed, _service.GetCached(_server.Id));
    }

    [Fact]
    public async Task NoObservationDoesNotInventZero_StoppedProcessDoesProveZero()
    {
        _console.State = MinecraftConsoleState.NoConsole;
        var missing = await _service.GetAsync(_server.Id);
        Assert.Null(missing.OnlinePlayers); Assert.True(missing.IsStale);
        Assert.Null(Assert.Single(missing.Players).IsOnline);
        _console.State = MinecraftConsoleState.NotRunning;
        var stopped = await _service.GetAsync(_server.Id);
        Assert.Equal(0, stopped.OnlinePlayers); Assert.False(stopped.IsStale);
        Assert.False(Assert.Single(stopped.Players).IsOnline);
    }

    [Fact]
    public async Task ReadoptedStatusCountsAreRealButPartialSampleIsNotFullRoster()
    {
        _console.State = MinecraftConsoleState.NoConsole;
        _status.Value = new(3, 80, [new(_alex, "Alex")]);
        var other = Guid.NewGuid();
        WriteIdentities("ops.json", (other, "Steve"));
        var snapshot = await _service.GetAsync(_server.Id);
        Assert.Equal(3, snapshot.OnlinePlayers); Assert.Equal(80, snapshot.MaxPlayers);
        Assert.False(snapshot.IsStale); Assert.False(snapshot.OnlineIdentitiesKnown);
        Assert.True(snapshot.Players.Single(p => p.Uuid == _alex).IsOnline);
        Assert.Null(snapshot.Players.Single(p => p.Uuid == other).IsOnline);
        Assert.Empty(_console.Commands);
        var result = await _service.AdministerAsync(_server.Id, new(_alex, MinecraftPlayerAction.Op));
        Assert.Equal("ConsoleUnavailable", result.ErrorCode);
        Assert.Empty(_console.Commands);
    }

    [Fact]
    public async Task PersistedCountsSurviveAgentRestartWithoutPretendingToBeFresh()
    {
        _console.State = MinecraftConsoleState.Ready; _console.Names = ["Alex"];
        var before = await _service.GetAsync(_server.Id);
        _console.State = MinecraftConsoleState.NoConsole;
        using var restarted = CreateService();
        var after = await restarted.GetAsync(_server.Id);
        Assert.Equal(before.OnlinePlayers, after.OnlinePlayers);
        Assert.Equal(before.LastVerifiedAtUtc, after.LastVerifiedAtUtc);
        Assert.True(after.IsStale); Assert.Null(Assert.Single(after.Players).IsOnline);
    }

    [Fact]
    public async Task UuidHistoryMergesRealSources_26ModernStatsAndUnknownDatesStayHonest()
    {
        var second = Guid.NewGuid();
        WriteIdentities("ops.json", (_alex, "OldName"));
        WriteIdentities("whitelist.json", (second, "Steve"));
        WriteIdentities("banned-players.json", (second, "Steve"));
        var stats = Path.Combine(_root, "kept-world", "players", "stats");
        Directory.CreateDirectory(stats);
        File.WriteAllText(Path.Combine(stats, _alex + ".json"), "{\"stats\":{\"minecraft:custom\":{\"minecraft:play_time\":1200}}}");
        var snapshot = await _service.GetAsync(_server.Id);
        var alex = snapshot.Players.Single(p => p.Uuid == _alex);
        Assert.Equal("Alex", alex.Username); Assert.Equal(TimeSpan.FromMinutes(1), alex.TotalPlayTime);
        Assert.True(alex.IsOperator); Assert.False(alex.IsWhitelisted); Assert.False(alex.IsBanned);
        Assert.Null(alex.FirstJoinedAtUtc); Assert.Null(alex.LastSeenAtUtc); Assert.Null(alex.SessionStartedAtUtc);
        Assert.Null(alex.PingMilliseconds); Assert.Null(alex.GameMode); Assert.Null(alex.Dimension);
        Assert.True(snapshot.Players.Single(p => p.Uuid == second).IsWhitelisted);
        Assert.True(snapshot.Players.Single(p => p.Uuid == second).IsBanned);
    }

    [Fact]
    public async Task HistoriesArePartitionedByServerId()
    {
        _console.State = MinecraftConsoleState.NoConsole;
        _status.Value = new(5, 20, []);
        await _service.GetAsync(_server.Id);
        var other = _server with { Id = Guid.NewGuid() }; _servers.Items.Add(other);
        _status.Value = null;
        var snapshot = await _service.GetAsync(other.Id);
        Assert.Null(snapshot.OnlinePlayers);
        Assert.Contains(MinecraftPlayerStateService.HistoryKey(_server.Id), _settings.Values.Keys);
        Assert.Contains(MinecraftPlayerStateService.HistoryKey(other.Id), _settings.Values.Keys);
    }

    [Fact]
    public async Task JoinLeaveEventsUseUuidUpdateCountAndPersistWithoutAnyConsolePoll()
    {
        _console.State = MinecraftConsoleState.Ready;
        await _service.GetAsync(_server.Id);
        var joined = DateTimeOffset.UtcNow.AddSeconds(1);
        await _service.RecordLogAsync(_server.Id, new(joined, "Information", "Minecraft", "[Server thread/INFO]: Alex joined the game"));
        var online = _service.GetCached(_server.Id)!;
        Assert.Equal(1, online.OnlinePlayers);
        Assert.Equal(joined, Assert.Single(online.Players).FirstJoinedAtUtc);
        Assert.Equal(joined, Assert.Single(online.Players).SessionStartedAtUtc);
        Assert.Single(_console.Commands);
        await _service.RecordLogAsync(_server.Id, new(joined.AddSeconds(10), "Information", "Minecraft", "[Server thread/INFO]: Alex left the game"));
        var offline = _service.GetCached(_server.Id)!;
        Assert.Equal(0, offline.OnlinePlayers); Assert.False(Assert.Single(offline.Players).IsOnline);
        Assert.Null(Assert.Single(offline.Players).SessionStartedAtUtc);
        Assert.Single(_console.Commands);
    }

    [Fact]
    public async Task HistoricalJoinCannotOverrideNewerVerifiedOfflineRoster_ChatCannotInventEvents()
    {
        _console.State = MinecraftConsoleState.Ready;
        var before = await _service.GetAsync(_server.Id);
        await _service.RecordLogAsync(_server.Id, new(DateTimeOffset.UtcNow.AddDays(-2), "Information", "Minecraft", "[Server thread/INFO]: Alex joined the game"));
        await _service.RecordLogAsync(_server.Id, new(DateTimeOffset.UtcNow.AddSeconds(1), "Information", "Minecraft", "[Server thread/INFO]: <Steve> Alex joined the game"));
        var after = _service.GetCached(_server.Id)!;
        Assert.Equal(0, after.OnlinePlayers); Assert.False(Assert.Single(after.Players).IsOnline);
        Assert.Equal(before.LastVerifiedAtUtc, after.LastVerifiedAtUtc);
    }

    [Fact]
    public async Task MalformedFilesDoNotTurnUnknownFlagsIntoFalseOrInventDates()
    {
        File.WriteAllText(Path.Combine(_root, "ops.json"), "broken");
        var snapshot = await _service.GetAsync(_server.Id);
        Assert.Null(Assert.Single(snapshot.Players).IsOperator);
        Assert.Null(Assert.Single(snapshot.Players).FirstJoinedAtUtc);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/outside")]
    [InlineData("world:stream")]
    public void WorldResolutionRejectsEscapeAndAlternateDataStreams(string levelName)
    {
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=" + levelName);
        Assert.Null(MinecraftPlayerFiles.ResolveWorld(_root));
    }

    [Theory]
    [InlineData(MinecraftPlayerAction.Kick)]
    [InlineData(MinecraftPlayerAction.Ban)]
    [InlineData(MinecraftPlayerAction.Deop)]
    [InlineData(MinecraftPlayerAction.WhitelistRemove)]
    public async Task DestructiveAdministrationNeedsExplicitConfirmation(MinecraftPlayerAction action)
    {
        var result = await _service.AdministerAsync(_server.Id, new(_alex, action));
        Assert.Equal("ConfirmationRequired", result.ErrorCode); Assert.Empty(_console.Commands);
    }

    [Theory]
    [InlineData("Alex\nstop")]
    [InlineData("@a")]
    [InlineData("Alex; op Steve")]
    [InlineData("Alex\r")]
    public async Task UntrustedNameNeverBecomesCommandText(string name)
    {
        var result = await _service.AdministerByNameAsync(_server.Id, new(MinecraftPlayerAction.Op, name));
        Assert.Equal("InvalidPlayerName", result.ErrorCode); Assert.Empty(_console.Commands);
    }

    [Fact]
    public async Task EmptyAndUnknownUuidsCannotAdministerAnyone()
    {
        Assert.Equal("InvalidPlayerUuid", (await _service.AdministerAsync(_server.Id, new(Guid.Empty, MinecraftPlayerAction.Op))).ErrorCode);
        Assert.Equal("UnknownPlayer", (await _service.AdministerAsync(_server.Id, new(Guid.NewGuid(), MinecraftPlayerAction.Op))).ErrorCode);
        Assert.Empty(_console.Commands);
    }

    [Fact]
    public async Task ConsoleSuccessWithoutActualStateChangeIsNotSuccess()
    {
        _console.State = MinecraftConsoleState.Ready;
        var result = await _service.AdministerAsync(_server.Id, new(_alex, MinecraftPlayerAction.Op));
        Assert.Equal("VerificationFailed", result.ErrorCode);
        Assert.Contains("op Alex", _console.Commands);
        Assert.DoesNotContain(_console.Commands, c => c is "stop" or "restart");
        Assert.False(_audit.Succeeded);
    }

    [Theory]
    [InlineData(MinecraftPlayerAction.Op, "ops.json", true)]
    [InlineData(MinecraftPlayerAction.Deop, "ops.json", false)]
    [InlineData(MinecraftPlayerAction.WhitelistAdd, "whitelist.json", true)]
    [InlineData(MinecraftPlayerAction.WhitelistRemove, "whitelist.json", false)]
    [InlineData(MinecraftPlayerAction.Ban, "banned-players.json", true)]
    [InlineData(MinecraftPlayerAction.Pardon, "banned-players.json", false)]
    public async Task ModerationRequiresRealUuidFileReadback_NoRestart(MinecraftPlayerAction action, string file, bool desired)
    {
        _console.State = MinecraftConsoleState.Ready;
        if (!desired) WriteIdentities(file, (_alex, "Alex"));
        _console.OnAction = () => WriteIdentities(file, desired ? [(_alex, "Alex")] : []);
        var result = await _service.AdministerAsync(_server.Id, new(_alex, action, Confirmed: true));
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, result.Outcome);
        Assert.True(_audit.Succeeded);
        Assert.Contains(MinecraftPlayerCommandPolicy.BuildCommand(action, "Alex"), _console.Commands);
        Assert.DoesNotContain(_console.Commands, c => c is "stop" or "restart");
    }

    [Fact]
    public async Task KickReadsBackActualOnlineRoster()
    {
        _console.State = MinecraftConsoleState.Ready; _console.Names = ["Alex"];
        _console.OnAction = () => _console.Names = [];
        var result = await _service.AdministerAsync(_server.Id, new(_alex, MinecraftPlayerAction.Kick, true));
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, result.Outcome);
        Assert.Equal(new[] { "list", "kick Alex", "list" }, _console.Commands);
    }

    [Fact]
    public async Task StatusClientUsesReadOnlyLoopbackProtocolAndParsesRealSample()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fixture = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            var buffer = new byte[1024];
            Assert.True(await stream.ReadAsync(buffer, deadline.Token) > 0);
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { players = new { online = 2, max = 30, sample = new[] { new { id = _alex, name = "Alex" } } } }));
            using var response = new MemoryStream();
            response.WriteByte(0); VarInt(response, json.Length); response.Write(json);
            using var framed = new MemoryStream(); VarInt(framed, (int)response.Length); framed.Write(response.ToArray());
            await stream.WriteAsync(framed.ToArray(), deadline.Token);
        }, deadline.Token);
        var status = await new MinecraftServerStatusClient().QueryAsync(_server with { Port = ((IPEndPoint)listener.LocalEndpoint).Port }, deadline.Token);
        await fixture;
        Assert.NotNull(status); Assert.Equal(2, status.Online); Assert.Equal(30, status.Maximum);
        Assert.Equal(_alex, Assert.Single(status.Sample).Uuid);
    }

    [Fact]
    public async Task StatusClientRejectsRemoteConfiguredHostWithoutConnecting()
    {
        File.WriteAllText(Path.Combine(_root, "server.properties"), "server-ip=203.0.113.1");
        Assert.Null(await new MinecraftServerStatusClient().QueryAsync(_server, CancellationToken.None));
    }

    private static void VarInt(Stream stream, int value)
    {
        do { var b = value & 127; value >>= 7; stream.WriteByte((byte)(value == 0 ? b : b | 128)); } while (value != 0);
    }

    private void WriteIdentities(string name, params (Guid Uuid, string Name)[] players) =>
        File.WriteAllText(Path.Combine(_root, name), JsonSerializer.Serialize(players.Select(p => new { uuid = p.Uuid, name = p.Name, expiresOn = "2030-01-01 00:00:00 +0000" })));

    public void Dispose() { _service.Dispose(); Directory.Delete(_root, true); }

    private sealed class ConsoleChannel : IMinecraftConsoleChannel
    {
        public MinecraftConsoleState State;
        public string[] Names = [];
        public bool Timeout;
        public Action? OnAction;
        public List<string> Commands { get; } = [];
        public event EventHandler<Guid>? ServerReady { add { } remove { } }
        public MinecraftConsoleState GetState(Guid serverId) => State;
        public Task<ConsoleExchangeResult> ExchangeAsync(Guid serverId, string command, Func<string, bool> isAnswer, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (Timeout) return Task.FromResult(new ConsoleExchangeResult(OperationResult.Fail("ConsoleTimeout", "No answer"), null, []));
            if (command != "list") OnAction?.Invoke();
            var reply = command switch
            {
                "list" => $"There are {Names.Length} of a max of 20 players online: {string.Join(", ", Names)}",
                "op Alex" => "Made Alex a server operator", "deop Alex" => "Made Alex no longer a server operator",
                "whitelist add Alex" => "Added Alex to the whitelist", "whitelist remove Alex" => "Removed Alex from the whitelist",
                "ban Alex" => "Banned Alex: Banned by an operator.", "pardon Alex" => "Unbanned Alex",
                "kick Alex" => "Kicked Alex: Kicked by an operator", _ => "Unknown command"
            };
            Assert.True(isAnswer(reply));
            return Task.FromResult(new ConsoleExchangeResult(OperationResult.Ok(), reply, [reply]));
        }
    }
    private sealed class Status : IMinecraftServerStatusClient
    {
        public MinecraftServerStatus? Value;
        public Task<MinecraftServerStatus?> QueryAsync(GameServerDefinition server, CancellationToken cancellationToken) => Task.FromResult(Value);
    }
    private sealed class Settings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) { Values[key] = JsonSerializer.Serialize(value); return Task.CompletedTask; }
    }
    private sealed class Audit : IAuditLogStore
    {
        public bool Succeeded;
        public Task WriteAsync(string actor, string action, string target, bool succeeded, string? detail = null, CancellationToken cancellationToken = default) { Succeeded = succeeded; return Task.CompletedTask; }
    }
    private sealed class Servers : IGameServerStore
    {
        public List<GameServerDefinition> Items { get; } = [];
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameServerDefinition>>(Items);
        public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(s => s.Id == serverId));
        public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
