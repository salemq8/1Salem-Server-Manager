using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly GameServerDefinition _server;
    private readonly FakeConsole _console = new();
    private readonly MinecraftInventoryService _service;

    public MinecraftInventoryTests()
    {
        Directory.CreateDirectory(_root);
        _server = new(Guid.NewGuid(), GameType.Minecraft, "Fixture", _root, 25565, "26.3", DateTimeOffset.UtcNow);
        _service = new(new FakeServers(_server), _console);
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=custom-world\n");
    }

    [Fact]
    public void LegacySlotCountDamageEnchantmentsAndNameArePreserved()
    {
        var nbt = MinecraftPlayerNbt.ReadSnbt("{Inventory:[{Slot:0b,id:'minecraft:diamond_sword',Count:1b,tag:{Damage:17,Enchantments:[{id:'minecraft:sharpness',lvl:3s}],display:{Name:'{\"text\":\"Blade\"}'}}},{Slot:-106b,id:'minecraft:shield',Count:1b}]}");
        Assert.True(MinecraftInventoryService.TryItems(nbt, out var items));
        var sword = Assert.Single(items, item => item.Slot == 0);
        Assert.Equal("Blade", sword.CustomName);
        Assert.Equal(17, sword.Damage);
        Assert.Null(sword.MaximumDamage);
        Assert.Equal(3, sword.Enchantments["minecraft:sharpness"]);
        Assert.Contains(items, item => item.Slot == 150 && item.ItemId == "minecraft:shield");
    }

    [Fact]
    public void ModernComponentsEquipmentAndSimplifiedEnchantmentsArePreserved()
    {
        var nbt = MinecraftPlayerNbt.ReadSnbt("{Inventory:[{Slot:9b,id:'minecraft:stone',count:64}],equipment:{head:{id:'minecraft:diamond_helmet',components:{'minecraft:damage':12,'minecraft:max_damage':363,'minecraft:enchantments':{'minecraft:protection':4},'minecraft:custom_name':{text:'خوذة'}}},offhand:{id:'minecraft:shield'}}}");
        Assert.True(MinecraftInventoryService.TryItems(nbt, out var items));
        Assert.Equal(64, Assert.Single(items, item => item.Slot == 9).Count);
        var helmet = Assert.Single(items, item => item.Slot == 103);
        Assert.Equal(1, helmet.Count); // The modern on-disk default, not an assumed stack size.
        Assert.Equal(363, helmet.MaximumDamage);
        Assert.Equal(4, helmet.Enchantments["minecraft:protection"]);
        Assert.Equal("خوذة", helmet.CustomName);
        Assert.Equal("minecraft:shield", Assert.Single(items, item => item.Slot == 150).ItemId);
    }

    [Fact]
    public void EarlierComponentLevelsAndStoredEnchantmentsAreRead()
    {
        Assert.True(MinecraftInventoryService.TryItems(MinecraftPlayerNbt.ReadSnbt("{Inventory:[{Slot:1b,id:'minecraft:enchanted_book',components:{'minecraft:stored_enchantments':{levels:{'minecraft:mending':1}}}}]}"), out var items));
        Assert.Equal(1, Assert.Single(items).Enchantments["minecraft:mending"]);
    }

    [Fact]
    public void ModernEquipmentWinsOverLegacyEquipmentSlot()
    {
        Assert.True(MinecraftInventoryService.TryItems(MinecraftPlayerNbt.ReadSnbt("{Inventory:[{Slot:103b,id:'minecraft:iron_helmet'}],equipment:{head:{id:'minecraft:diamond_helmet'}}}"), out var items));
        Assert.Equal("minecraft:diamond_helmet", Assert.Single(items).ItemId);
    }

    [Fact]
    public async Task Modern26PlayerDirectoryUsesConfiguredWorldAndSharedReadWithoutMutation()
    {
        var path = Save("players/data", "{Inventory:[{Slot:1b,id:'minecraft:apple',count:3}],playerGameType:1,Dimension:'minecraft:the_nether'}");
        var before = File.ReadAllBytes(path);
        using var gameHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var snapshot = await _service.GetAsync(_server.Id, _uuid);
        Assert.Equal(MinecraftInventorySource.LastSaved, snapshot.Source);
        Assert.Equal(3, Assert.Single(snapshot.Items).Count);
        Assert.NotNull(snapshot.SavedAtUtc);
        Assert.Empty(_console.Commands);
        var metadata = MinecraftPlayerNbt.ReadMetadata(path);
        Assert.Equal("Creative", metadata.GameMode);
        Assert.Equal("minecraft:the_nether", metadata.Dimension);
        gameHandle.Dispose();
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LegacyDirectoryIsSupportedButModernSaveTakesPrecedence()
    {
        Save("playerdata", "{Inventory:[{Slot:0b,id:'minecraft:apple',Count:1b}]}");
        Assert.Equal("minecraft:apple", Assert.Single((await _service.GetAsync(_server.Id, _uuid)).Items).ItemId);
        Save("players/data", "{Inventory:[{Slot:0b,id:'minecraft:stone',count:2}]}");
        Assert.Equal("minecraft:stone", Assert.Single((await _service.GetAsync(_server.Id, _uuid)).Items).ItemId);
    }

    [Fact]
    public async Task GenuineUuidBoundLiveReplyIsPreferredAndCommandIsReadOnly()
    {
        Save("players/data", "{Inventory:[{Slot:0b,id:'minecraft:dirt'}]}");
        _console.State = MinecraftConsoleState.Ready;
        _console.Reply = LiveReply(_uuid, "{Slot:0b,id:'minecraft:diamond',count:2}");
        var result = await _service.GetAsync(_server.Id, _uuid);
        Assert.Equal(MinecraftInventorySource.Live, result.Source);
        Assert.Null(result.SavedAtUtc);
        Assert.Equal("minecraft:diamond", Assert.Single(result.Items).ItemId);
        Assert.Equal($"data get entity {_uuid:D}", Assert.Single(_console.Commands));
    }

    [Fact]
    public async Task WrongUuidLiveReplyFallsBackToExplicitLastSaved()
    {
        Save("players/data", "{Inventory:[]}");
        _console.State = MinecraftConsoleState.Ready;
        _console.Reply = LiveReply(Guid.NewGuid(), "{Slot:0b,id:'minecraft:diamond'}");
        Assert.Equal(MinecraftInventorySource.LastSaved, (await _service.GetAsync(_server.Id, _uuid)).Source);
    }

    [Fact]
    public void ChatCannotImpersonateAValidLiveInventoryReply()
    {
        var reply = LiveReply(_uuid, "{Slot:0b,id:'minecraft:diamond'}");
        Assert.False(MinecraftInventoryService.TryLive(reply.Replace("Alex has", "<Eve> Alex has", StringComparison.Ordinal), _uuid, out _));
        Assert.False(MinecraftInventoryService.TryLive(reply[..^2], _uuid, out _));
    }

    [Fact]
    public async Task ReadoptedServerNeverClaimsDiskDataIsLiveOrSendsACommand()
    {
        _console.State = MinecraftConsoleState.NoConsole;
        Save("players/data", "{Inventory:[]}");
        Assert.Equal(MinecraftInventorySource.LastSaved, (await _service.GetAsync(_server.Id, _uuid)).Source);
        Assert.Empty(_console.Commands);
    }

    [Fact]
    public async Task MissingDataIsUnavailableNotAnEmptyVerifiedInventory()
    {
        Assert.Equal(MinecraftInventorySource.Unavailable, (await _service.GetAsync(_server.Id, _uuid)).Source);
        Assert.False(MinecraftInventoryService.TryItems(MinecraftPlayerNbt.ReadSnbt("{Health:20f}"), out _));
    }

    [Fact]
    public async Task WorldTraversalAndEmptyUuidAreRejected()
    {
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=../outside\n");
        var result = await _service.GetAsync(_server.Id, _uuid);
        Assert.Equal("UnsafeWorldPath", result.UnavailableReason);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetAsync(_server.Id, Guid.Empty));
    }

    [Fact]
    public async Task CorruptSaveNeverTurnsIntoVerifiedEmptyInventory()
    {
        var path = Save("players/data", "{Inventory:[]}");
        File.WriteAllBytes(path, [10, 0, 0, 9, 0, 1, 65, 10, 127, 255, 255, 255]);
        Assert.Equal(MinecraftInventorySource.Unavailable, (await _service.GetAsync(_server.Id, _uuid)).Source);
    }

    [Fact]
    public void DecompressionBombIsBounded()
    {
        var path = Path.Combine(_root, "bomb.dat");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(new byte[MinecraftPlayerNbt.MaximumBytes + 1]);
        Assert.Throws<InvalidDataException>(() => MinecraftPlayerNbt.ReadFile(path));
    }

    [Theory]
    [InlineData("{Inventory:[{Slot:0b,id:'x'},{Slot:0b,id:'y'}]}")]
    [InlineData("{Inventory:[{id:'x'}]}")]
    public void AmbiguousInventoryIsNotAccepted(string input) => Assert.False(MinecraftInventoryService.TryItems(MinecraftPlayerNbt.ReadSnbt(input), out _));

    [Fact]
    public void ExcessiveDepthDuplicateKeysAndHugeCollectionsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MinecraftPlayerNbt.ReadSnbt(string.Concat(Enumerable.Repeat("{x:", 70)) + "1" + new string('}', 70)));
        Assert.Throws<InvalidDataException>(() => MinecraftPlayerNbt.ReadSnbt("{x:1,x:2}"));
        Assert.Throws<InvalidDataException>(() => MinecraftPlayerNbt.Read([10, 0, 0, 9, 0, 1, 65, 10, 127, 255, 255, 255]));
    }

    [Fact]
    public void BinaryModifiedUtf8PreservesArabicSupplementaryCharactersAndNul()
    {
        var payload = new Dictionary<string, object?> { ["name"] = "خوذة 😀\0" };
        Assert.Equal(payload["name"], MinecraftPlayerNbt.Read(Encode(payload))["name"]);
    }

    private string Save(string folder, string snbt)
    {
        var directory = Path.Combine(_root, "custom-world", folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{_uuid:D}.dat");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        gzip.Write(Encode(MinecraftPlayerNbt.ReadSnbt(snbt)));
        return path;
    }

    private static string LiveReply(Guid uuid, string item)
    {
        var bytes = uuid.ToByteArray(bigEndian: true);
        var ints = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4, 4)));
        return $"[12:00:00] [Server thread/INFO]: Alex has the following entity data: {{UUID:[I;{string.Join(',', ints)}],Inventory:[{item}]}}";
    }

    private static byte[] Encode(IReadOnlyDictionary<string, object?> root)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Integer(long value, int size) { for (var i = size - 1; i >= 0; i--) writer.Write((byte)((ulong)value >> (i * 8))); }
        void String(string value)
        {
            var encoded = new List<byte>();
            foreach (var character in value)
            {
                if (character is > '\0' and <= '\x7f') encoded.Add((byte)character);
                else if (character <= '\x7ff') { encoded.Add((byte)(0xc0 | (character >> 6))); encoded.Add((byte)(0x80 | (character & 63))); }
                else { encoded.Add((byte)(0xe0 | (character >> 12))); encoded.Add((byte)(0x80 | ((character >> 6) & 63))); encoded.Add((byte)(0x80 | (character & 63))); }
            }
            Integer(encoded.Count, 2); writer.Write(encoded.ToArray());
        }
        byte Kind(object? value) => value switch { string => 8, IReadOnlyDictionary<string, object?> => 10, List<object?> => 9, double => 6, _ => 4 };
        void Value(object? value)
        {
            switch (value)
            {
                case string text: String(text); break;
                case IReadOnlyDictionary<string, object?> map:
                    foreach (var (key, child) in map) { writer.Write(Kind(child)); String(key); Value(child); }
                    writer.Write((byte)0); break;
                case List<object?> list:
                    writer.Write(list.Count == 0 ? (byte)0 : Kind(list[0])); Integer(list.Count, 4);
                    foreach (var child in list) Value(child); break;
                case double number: Integer(BitConverter.DoubleToInt64Bits(number), 8); break;
                default: Integer(Convert.ToInt64(value), 8); break;
            }
        }
        writer.Write((byte)10); String(string.Empty); Value(root);
        return stream.ToArray();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class FakeServers(GameServerDefinition server) : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);
        public Task<GameServerDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == server.Id ? server : null);
        public Task UpsertAsync(GameServerDefinition value, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStateAsync(Guid id, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeConsole : IMinecraftConsoleChannel
    {
        public MinecraftConsoleState State { get; set; }
        public string? Reply { get; set; }
        public List<string> Commands { get; } = [];
        public event EventHandler<Guid>? ServerReady { add { } remove { } }
        public MinecraftConsoleState GetState(Guid serverId) => State;
        public Task<ConsoleExchangeResult> ExchangeAsync(Guid serverId, string command, Func<string, bool> isAnswer, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var answer = Reply is not null && isAnswer(Reply) ? Reply : null;
            return Task.FromResult(new ConsoleExchangeResult(answer is null ? OperationResult.Fail("Timeout", "No answer") : OperationResult.Ok(), answer, []));
        }
    }
}
