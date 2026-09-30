using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftGameplayServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-gameplay-" + Guid.NewGuid().ToString("N"));
    private readonly GameServerDefinition _server;
    private readonly FakeServers _servers = new();
    private readonly FakeConsole _console = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeWriter _writer;
    private readonly MinecraftGameplayService _service;

    public MinecraftGameplayServiceTests()
    {
        Directory.CreateDirectory(_root);
        _server = new GameServerDefinition(Guid.NewGuid(), GameType.Minecraft, "Test", _root, 25565, "1.21.8", DateTimeOffset.UtcNow);
        _servers.Items.Add(_server);
        File.WriteAllText(Path.Combine(_root, "server.properties"), "difficulty=easy\npvp=true\nmax-players=20\nlevel-name=world\n");
        _writer = new FakeWriter(_root);
        _service = new MinecraftGameplayService(
            _servers,
            _console,
            _settings,
            _audit,
            _writer,
            NullLogger<MinecraftGameplayService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Live_KeepInventoryIsTheRealGameruleAskedOfTheServer()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["keepInventory"] = "true";

        var snapshot = await _service.GetAsync(_server.Id);

        var rule = Assert.Single(snapshot.GameRules, rule => rule.Key == "keepInventory");
        Assert.Equal(MinecraftLiveControl.Live, snapshot.Control);
        Assert.True(rule.Supported);
        Assert.True(rule.Value);
        Assert.Equal(MinecraftValueSource.Live, rule.Source);
        Assert.Equal("keepInventory", rule.ServerName);
        Assert.Contains("gamerule keepInventory", _console.Sent);
    }

    [Fact]
    public async Task Live_SetKeepInventoryIsAppliedThenReadBackWithoutRestart()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["keepInventory"] = "false";

        var result = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", true));

        Assert.Equal(MinecraftChangeOutcome.AppliedLive, result.Outcome);
        Assert.True(result.VerifiedValue);
        Assert.Equal("true", _console.Rules["keepInventory"]);
        var set = _console.Sent.IndexOf("gamerule keepInventory true");
        Assert.True(set >= 0);
        Assert.Contains("gamerule keepInventory", _console.Sent.Skip(set + 1));
        Assert.DoesNotContain(_console.Sent, command => command is "stop" or "restart");
        Assert.Contains(_audit.Entries, entry => entry.Action == "MinecraftGameRuleChanged" && entry.Succeeded);
    }

    [Fact]
    public async Task Live_NewerServersKeepTheirOwnRuleNames()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["minecraft:keep_inventory"] = "false";
        _console.Rules["minecraft:fall_damage"] = "true";

        var snapshot = await _service.GetAsync(_server.Id);
        var result = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", true));

        var rule = Assert.Single(snapshot.GameRules, rule => rule.Key == "keepInventory");
        Assert.Equal("minecraft:keep_inventory", rule.ServerName);
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, result.Outcome);
        Assert.Contains("gamerule minecraft:keep_inventory true", _console.Sent);
        Assert.Equal("true", _console.Rules["minecraft:keep_inventory"]);
    }

    [Fact]
    public async Task Live_UnknownRuleIsNotSupportedAndNeverSet()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["keepInventory"] = "false";

        var snapshot = await _service.GetAsync(_server.Id);
        var result = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("freezeDamage", false));

        Assert.False(Assert.Single(snapshot.GameRules, rule => rule.Key == "freezeDamage").Supported);
        Assert.Equal(MinecraftChangeOutcome.Failed, result.Outcome);
        Assert.Equal("GameRuleNotSupported", result.ErrorCode);
        Assert.DoesNotContain(_console.Sent, command => command.StartsWith("gamerule freezeDamage ", StringComparison.Ordinal) &&
                                                        command.EndsWith(" false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopped_ShowsTheWorldFileValueAndKeepsTheChangeForTheNextStart()
    {
        _console.State = MinecraftConsoleState.NotRunning;
        WriteLevelDat(new Dictionary<string, string> { ["keepInventory"] = "false", ["fallDamage"] = "true", ["doFireTick"] = "true", ["mobGriefing"] = "true", ["doDaylightCycle"] = "true" });

        var result = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", true));
        var snapshot = await _service.GetAsync(_server.Id);

        Assert.Equal(MinecraftChangeOutcome.PendingNextStart, result.Outcome);
        Assert.Empty(_console.Sent);
        var rule = Assert.Single(snapshot.GameRules, rule => rule.Key == "keepInventory");
        Assert.False(rule.Value);
        Assert.Equal(MinecraftValueSource.WorldFile, rule.Source);
        Assert.True(rule.PendingValue);
        Assert.Equal(MinecraftLiveControl.Stopped, snapshot.Control);
    }

    [Fact]
    public async Task Stopped_ClassicWorldWithoutARuleMeansTheVersionDoesNotHaveIt()
    {
        WriteLevelDat(new Dictionary<string, string> { ["keepInventory"] = "false", ["fallDamage"] = "true", ["doFireTick"] = "true", ["mobGriefing"] = "true", ["doDaylightCycle"] = "true" });

        var snapshot = await _service.GetAsync(_server.Id);

        Assert.False(Assert.Single(snapshot.GameRules, rule => rule.Key == "freezeDamage").Supported);
    }

    [Fact]
    public async Task Ready_AppliesSavedChangesAndVerifiesThem()
    {
        WriteLevelDat(new Dictionary<string, string> { ["keepInventory"] = "false", ["fallDamage"] = "true", ["doFireTick"] = "true", ["mobGriefing"] = "true", ["doDaylightCycle"] = "true" });
        await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", true));

        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["keepInventory"] = "false";
        await _service.ApplyPendingAsync(_server.Id);

        Assert.Equal("true", _console.Rules["keepInventory"]);
        var pending = await _settings.GetAsync<Dictionary<string, bool>>(MinecraftGameplayService.PendingKey(_server.Id));
        Assert.NotNull(pending);
        Assert.Empty(pending);
        Assert.DoesNotContain(_console.Sent, command => command is "stop" or "restart");
    }

    [Fact]
    public async Task NoConsole_ChangesWaitAndPlayerActionsSaySo()
    {
        _console.State = MinecraftConsoleState.NoConsole;
        WriteLevelDat(new Dictionary<string, string> { ["keepInventory"] = "true" });

        var snapshot = await _service.GetAsync(_server.Id);
        var rule = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", false));
        var op = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Op, "Steve"));

        Assert.Equal(MinecraftLiveControl.NoConsole, snapshot.Control);
        Assert.True(Assert.Single(snapshot.GameRules, item => item.Key == "keepInventory").Value);
        Assert.Equal(MinecraftChangeOutcome.PendingNextStart, rule.Outcome);
        Assert.Equal(MinecraftChangeOutcome.Failed, op.Outcome);
        Assert.Equal("ConsoleUnavailable", op.ErrorCode);
        Assert.Empty(_console.Sent);
    }

    [Fact]
    public async Task FallDamage_OffersOnlyRealChoices()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["fallDamage"] = "true";
        var live = await _service.GetAsync(_server.Id);

        _console.State = MinecraftConsoleState.NotRunning;
        var unknown = await _service.GetAsync(_server.Id);

        Assert.True(live.FallDamage.Supported);
        Assert.Equal([100, 0], live.FallDamage.Percentages);
        Assert.DoesNotContain(live.FallDamage.Percentages, percent => percent is 75 or 50 or 25);
        Assert.False(unknown.FallDamage.Supported);
        Assert.Empty(unknown.FallDamage.Percentages);
    }

    [Fact]
    public async Task Properties_AreValidatedAllOrNothing()
    {
        var result = await _service.SetPropertiesAsync(
            _server.Id,
            new MinecraftPropertiesChangeRequest(new Dictionary<string, string> { ["pvp"] = "false", ["max-players"] = "5000" }));

        Assert.False(result.Success);
        Assert.Equal("InvalidValue", result.ErrorCode);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task Properties_PersistAndApplyLiveOnlyWhereARealCommandExists()
    {
        _console.State = MinecraftConsoleState.Ready;

        var result = await _service.SetPropertiesAsync(
            _server.Id,
            new MinecraftPropertiesChangeRequest(new Dictionary<string, string> { ["difficulty"] = "Hard", ["pvp"] = "False" }));

        Assert.True(result.Success);
        var written = Assert.Single(_writer.Writes);
        Assert.Equal("hard", written["difficulty"]);
        Assert.Equal("false", written["pvp"]);
        Assert.Equal(["difficulty"], result.AppliedLive);
        Assert.True(result.RestartRequired);
        Assert.Contains("difficulty hard", _console.Sent);
        Assert.DoesNotContain(_console.Sent, command => command is "stop" or "restart" || command.StartsWith("pvp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Properties_WhileStoppedNeedNoRestart()
    {
        var result = await _service.SetPropertiesAsync(
            _server.Id,
            new MinecraftPropertiesChangeRequest(new Dictionary<string, string> { ["op-permission-level"] = "3" }));

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        Assert.Empty(_console.Sent);
        Assert.Equal("3", Assert.Single(_writer.Writes)["op-permission-level"]);
    }

    [Theory]
    [InlineData("Steve; stop")]
    [InlineData("ab")]
    [InlineData("name with space")]
    [InlineData("seventeen_letters")]
    public async Task Players_InvalidNamesNeverReachTheConsole(string name)
    {
        _console.State = MinecraftConsoleState.Ready;

        var result = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Ban, name));

        Assert.Equal("InvalidPlayerName", result.ErrorCode);
        Assert.Empty(_console.Sent);
    }

    [Fact]
    public async Task Players_BanOpAndKickUseTheRealCommands()
    {
        _console.State = MinecraftConsoleState.Ready;
        _console.Online.Add("Alex");

        var ban = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Ban, "Steve"));
        var op = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Op, "Alex"));
        var kickOffline = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Kick, "Steve"));
        var kick = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Kick, "Alex"));

        Assert.Equal(MinecraftChangeOutcome.AppliedLive, ban.Outcome);
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, op.Outcome);
        Assert.Equal("PlayerNotOnline", kickOffline.ErrorCode);
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, kick.Outcome);
        Assert.Contains("ban Steve", _console.Sent);
        Assert.Contains("op Alex", _console.Sent);
        Assert.Contains("kick Alex", _console.Sent);
        Assert.DoesNotContain("kick Steve", _console.Sent);
    }

    [Fact]
    public async Task Players_UnknownPlayerIsReportedNotInvented()
    {
        _console.State = MinecraftConsoleState.Ready;

        var result = await _service.PlayerActionAsync(_server.Id, new MinecraftPlayerActionRequest(MinecraftPlayerAction.Op, "Nobody_Here"));

        Assert.Equal(MinecraftChangeOutcome.Failed, result.Outcome);
        Assert.Equal("UnknownPlayer", result.ErrorCode);
    }

    [Fact]
    public async Task Players_OnlineListIsLiveOrEmptyNeverGuessed()
    {
        File.WriteAllText(Path.Combine(_root, "ops.json"), """[{"uuid":"x","name":"Alex","level":4}]""");
        File.WriteAllText(Path.Combine(_root, "banned-players.json"), """[{"uuid":"y","name":"Griefer"}]""");
        _console.State = MinecraftConsoleState.Ready;
        _console.Online.AddRange(["Alex", "Steve"]);
        var live = await _service.GetPlayersAsync(_server.Id);

        _console.State = MinecraftConsoleState.NotRunning;
        var stopped = await _service.GetPlayersAsync(_server.Id);

        Assert.True(live.OnlineKnown);
        Assert.Equal(["Alex", "Steve"], live.Online);
        Assert.Equal(20, live.MaxPlayers);
        Assert.Equal(["Alex"], live.Operators);
        Assert.Equal(["Griefer"], live.Banned);
        Assert.False(stopped.OnlineKnown);
        Assert.Empty(stopped.Online);
        Assert.Equal(20, stopped.MaxPlayers);
    }

    [Fact]
    public void LevelDat_ReadsGameRulesAndSkipsOtherTags()
    {
        WriteLevelDat(new Dictionary<string, string> { ["keepInventory"] = "true", ["randomTickSpeed"] = "3" }, addTypedFallDamage: true);

        var rules = LevelDatGameRules.Read(Path.Combine(_root, "world", "level.dat"));

        Assert.NotNull(rules);
        Assert.Equal("true", rules["keepInventory"]);
        Assert.Equal("3", rules["randomTickSpeed"]);
        Assert.Equal("false", rules["minecraft:fall_damage"]);
    }

    [Fact]
    public async Task Stopped_ReadsTheMinecraft26WorldFormat()
    {
        // 26.x: data/minecraft/game_rules.dat, registry names, bytes; level.dat has no GameRules.
        WriteLevelDat(new Dictionary<string, string>(), includeGameRules: false);
        WriteRegistryRules(new Dictionary<string, object>
        {
            ["minecraft:keep_inventory"] = (byte)1,
            ["minecraft:advance_time"] = (byte)0,
            ["minecraft:advance_weather"] = (byte)1,
            ["minecraft:fall_damage"] = (byte)1,
            ["minecraft:pvp"] = (byte)0,
            ["minecraft:mob_griefing"] = (byte)1,
            ["minecraft:spawn_mobs"] = (byte)1,
            ["minecraft:fire_spread_radius_around_player"] = 128,
            ["minecraft:random_tick_speed"] = 3
        });

        var snapshot = await _service.GetAsync(_server.Id);
        MinecraftGameRuleState Rule(string key) => Assert.Single(snapshot.GameRules, rule => rule.Key == key);

        Assert.True(snapshot.GameRulesKnown);
        Assert.Equal((true, "minecraft:keep_inventory", MinecraftValueSource.WorldFile), (Rule("keepInventory").Value!.Value, Rule("keepInventory").ServerName, Rule("keepInventory").Source));
        Assert.Equal((false, "minecraft:advance_time"), (Rule("doDaylightCycle").Value!.Value, Rule("doDaylightCycle").ServerName));
        Assert.False(Rule("pvp").Value);
        Assert.False(Rule("doFireTick").Supported);
        Assert.False(Rule("freezeDamage").Supported);
        Assert.Equal([100, 0], snapshot.FallDamage.Percentages);
    }

    [Fact]
    public async Task Stopped_Minecraft26ChangeIsKeptUnderItsRegistryName()
    {
        WriteLevelDat(new Dictionary<string, string>(), includeGameRules: false);
        WriteRegistryRules(new Dictionary<string, object>
        {
            ["minecraft:keep_inventory"] = (byte)0,
            ["minecraft:advance_time"] = (byte)1,
            ["minecraft:fall_damage"] = (byte)1,
            ["minecraft:mob_griefing"] = (byte)1,
            ["minecraft:spawn_mobs"] = (byte)1
        });

        var saved = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("keepInventory", true));
        _console.State = MinecraftConsoleState.Ready;
        _console.Rules["minecraft:keep_inventory"] = "false";
        _console.Rules["minecraft:advance_time"] = "true";
        await _service.ApplyPendingAsync(_server.Id);
        var daylight = await _service.SetGameRuleAsync(_server.Id, new MinecraftGameRuleChangeRequest("doDaylightCycle", false));

        Assert.Equal(MinecraftChangeOutcome.PendingNextStart, saved.Outcome);
        Assert.Contains("gamerule minecraft:keep_inventory true", _console.Sent);
        Assert.Equal("true", _console.Rules["minecraft:keep_inventory"]);
        Assert.Equal(MinecraftChangeOutcome.AppliedLive, daylight.Outcome);
        Assert.Contains("gamerule minecraft:advance_time false", _console.Sent);
    }

    [Fact]
    public void LevelDat_MissingOrDamagedFilesReadAsNothing()
    {
        var path = Path.Combine(_root, "world", "level.dat");
        Assert.Null(LevelDatGameRules.Read(path));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x1f, 0x8b, 0x08, 0x00, 0x01, 0x02]);
        Assert.Null(LevelDatGameRules.Read(path));
    }

    [Fact]
    public void SetValues_ChangesOnlyTheGivenKeysInPlace()
    {
        const string existing = "#Minecraft server properties\nmotd=Hello\\: world\npvp=true\nunknown-key=kept\ndifficulty=easy\n";

        var updated = MinecraftPropertiesSerializer.SetValues(
            existing,
            new Dictionary<string, string> { ["pvp"] = "false", ["white-list"] = "true", ["op-permission-level"] = "3" });
        var parsed = MinecraftPropertiesSerializer.Parse(updated);
        var lines = updated.Split(Environment.NewLine);

        Assert.Equal("#Minecraft server properties", lines[0]);
        Assert.Equal("motd=Hello\\: world", lines[1]);
        Assert.Equal("pvp=false", lines[2]);
        Assert.Equal("kept", parsed["unknown-key"]);
        Assert.Equal("easy", parsed["difficulty"]);
        Assert.Equal("true", parsed["white-list"]);
        Assert.Equal("true", parsed["enforce-whitelist"]);
        Assert.Equal("3", parsed["op-permission-level"]);
    }

    private void WriteLevelDat(IReadOnlyDictionary<string, string> stringRules, bool addTypedFallDamage = false, bool includeGameRules = true)
    {
        var path = Path.Combine(_root, "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var raw = new MemoryStream();
        var nbt = new NbtWriter(raw);
        nbt.Begin(10, string.Empty);
        nbt.Begin(10, "Data");
        nbt.Begin(3, "version");
        nbt.Int(19133);
        nbt.Begin(9, "ServerBrands");
        raw.WriteByte(8);
        nbt.Int(1);
        nbt.String("vanilla");
        nbt.Begin(11, "Positions");
        nbt.Int(2);
        nbt.Int(1);
        nbt.Int(2);
        nbt.Begin(10, "WorldGenSettings");
        nbt.Begin(4, "seed");
        nbt.Long(42);
        raw.WriteByte(0);
        if (includeGameRules)
        {
            nbt.Begin(10, "GameRules");
            foreach (var (key, value) in stringRules)
            {
                nbt.Begin(8, key);
                nbt.String(value);
            }

            if (addTypedFallDamage)
            {
                nbt.Begin(1, "minecraft:fall_damage");
                raw.WriteByte(0);
            }

            raw.WriteByte(0);
        }

        raw.WriteByte(0);
        raw.WriteByte(0);
        WriteGzip(path, raw);
    }

    /// <summary>Minecraft 26.x: world/data/minecraft/game_rules.dat, root -> "data", registry names.</summary>
    private void WriteRegistryRules(IReadOnlyDictionary<string, object> rules)
    {
        var path = Path.Combine(_root, "world", "data", "minecraft", "game_rules.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var raw = new MemoryStream();
        var nbt = new NbtWriter(raw);
        nbt.Begin(10, string.Empty);
        nbt.Begin(10, "data");
        foreach (var (key, value) in rules)
        {
            if (value is byte flag)
            {
                nbt.Begin(1, key);
                raw.WriteByte(flag);
            }
            else
            {
                nbt.Begin(3, key);
                nbt.Int((int)value);
            }
        }

        raw.WriteByte(0);
        nbt.Begin(3, "DataVersion");
        nbt.Int(5023);
        raw.WriteByte(0);
        WriteGzip(path, raw);
    }

    private static void WriteGzip(string path, MemoryStream raw)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(raw.ToArray());
    }

    private sealed class NbtWriter(Stream stream)
    {
        public void Begin(byte type, string name)
        {
            stream.WriteByte(type);
            String(name);
        }

        public void String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        public void Int(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            stream.Write(buffer);
        }

        public void Long(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            stream.Write(buffer);
        }
    }

    /// <summary>Answers the way a vanilla server's console does.</summary>
    private sealed class FakeConsole : IMinecraftConsoleChannel
    {
        private const string Prefix = "[12:00:00] [Server thread/INFO]: ";

        public MinecraftConsoleState State { get; set; } = MinecraftConsoleState.NotRunning;

        public Dictionary<string, string> Rules { get; } = new(StringComparer.Ordinal);

        public List<string> Online { get; } = [];

        public List<string> Sent { get; } = [];

        public event EventHandler<Guid>? ServerReady;

        public MinecraftConsoleState GetState(Guid serverId) => State;

        public void RaiseReady(Guid serverId) => ServerReady?.Invoke(this, serverId);

        public Task<ConsoleExchangeResult> ExchangeAsync(
            Guid serverId,
            string command,
            Func<string, bool> isAnswer,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (State != MinecraftConsoleState.Ready)
            {
                return Task.FromResult(new ConsoleExchangeResult(OperationResult.Fail("ServerNotRunning", "Not running."), null, []));
            }

            Sent.Add(command);
            var line = Prefix + Answer(command.Split(' '));
            return Task.FromResult(isAnswer(line)
                ? new ConsoleExchangeResult(OperationResult.Ok(), line, [line])
                : new ConsoleExchangeResult(OperationResult.Fail("ConsoleTimeout", "No answer."), null, [line]));
        }

        private string Answer(string[] words) =>
            words switch
            {
            ["gamerule", var name] when Rules.TryGetValue(name, out var value) => $"Gamerule {name} is currently set to: {value}",
            ["gamerule", var name, var value] when Rules.ContainsKey(name) => Set(name, value),
            ["gamerule", ..] => "Incorrect argument for command",
            ["list"] => $"There are {Online.Count} of a max of 20 players online: {string.Join(", ", Online)}",
            ["op", "Nobody_Here"] => "That player does not exist",
            ["op", var player] => $"Made {player} a server operator",
            ["ban", var player] => $"Banned {player}: Banned by an operator.",
            ["kick", var player] => $"Kicked {player}: Kicked by an operator",
            ["difficulty", var level] => $"The difficulty has been set to {level}",
                _ => "Unknown or incomplete command, see below for error"
            };

        private string Set(string name, string value)
        {
            Rules[name] = value;
            return $"Gamerule {name} is now set to: {value}";
        }
    }

    private sealed class FakeWriter(string root) : IMinecraftPropertiesWriter
    {
        public List<IReadOnlyDictionary<string, string>> Writes { get; } = [];

        public Task<OperationResult> WriteAsync(
            Guid serverId,
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(new Dictionary<string, string>(values));
            var path = Path.Combine(root, "server.properties");
            File.WriteAllText(path, MinecraftPropertiesSerializer.SetValues(File.ReadAllText(path), values));
            return Task.FromResult(OperationResult.Ok());
        }
    }

    private sealed class FakeSettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _values[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(string Action, bool Succeeded, string? Detail);

    private sealed class FakeAudit : IAuditLogStore
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(
            string actor,
            string action,
            string target,
            bool succeeded,
            string? detail = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, succeeded, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServers : IGameServerStore
    {
        public List<GameServerDefinition> Items { get; } = [];

        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>(Items);

        public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(server => server.Id == serverId));

        public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
