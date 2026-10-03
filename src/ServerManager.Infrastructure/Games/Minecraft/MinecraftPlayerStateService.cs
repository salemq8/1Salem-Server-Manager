using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>
/// One per-server player truth shared by every page. Failed reads retain verified counts with a
/// stale marker. Console events update it without reloading pages; status samples never imply a
/// complete roster. History belongs to ServerId, not a mutable username or world folder name.
/// </summary>
public sealed partial class MinecraftPlayerStateService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly IGameServerStore _servers;
    private readonly IMinecraftConsoleChannel _console;
    private readonly ISettingsStore _settings;
    private readonly IAuditLogStore _audit;
    private readonly IMinecraftServerStatusClient _status;
    private readonly ILogStreamService? _logs;
    private readonly ILogger<MinecraftPlayerStateService> _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly CancellationTokenSource _lifetime = new();

    public MinecraftPlayerStateService(IGameServerStore servers, IMinecraftConsoleChannel console,
        ISettingsStore settings, IAuditLogStore audit, IMinecraftServerStatusClient status,
        ILogger<MinecraftPlayerStateService> logger, ILogStreamService? logs = null)
    {
        _servers = servers; _console = console; _settings = settings; _audit = audit;
        _status = status; _logger = logger; _logs = logs;
        _console.ServerReady += OnServerReady;
    }

    public static string HistoryKey(Guid serverId) => $"minecraft.players.history.{serverId:N}";

    public MinecraftPlayerDashboardSnapshot? GetCached(Guid serverId) =>
        _entries.TryGetValue(serverId, out var entry) ? Volatile.Read(ref entry.Snapshot) : null;

    public async Task<MinecraftPlayerDashboardSnapshot> GetAsync(Guid serverId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var entry = _entries.GetOrAdd(serverId, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(serverId, entry, cancellationToken);
            var control = Control(_console.GetState(serverId));
            var now = DateTimeOffset.UtcNow;
            if (!forceRefresh && entry.Snapshot is { } cached && entry.LastAttempt + RefreshInterval > now && cached.Control == control)
                return cached;
            entry.LastAttempt = now;
            if (forceRefresh || entry.LastFilesRead + TimeSpan.FromSeconds(30) <= now)
            {
                ReadFiles(server, entry);
                entry.LastFilesRead = now;
            }
            var properties = MinecraftPlayerFiles.ReadProperties(server.RootPath);
            if (entry.Maximum is null && properties.TryGetValue("max-players", out var text) && int.TryParse(text, out var maximum) && maximum >= 0)
                entry.Maximum = maximum;
            var verified = false;
            entry.IdentitiesKnown = false;
            if (control == MinecraftLiveControl.Stopped)
            {
                ApplyRoster(entry, [], now);
                entry.Online = 0;
                verified = true;
            }
            else if (control == MinecraftLiveControl.Live)
            {
                var exchange = await _console.ExchangeAsync(serverId, "list",
                    line => MinecraftConsoleReplies.TryParseList(line, out _, out _, out _), TimeSpan.FromSeconds(3), cancellationToken);
                if (exchange.Result.Success && MinecraftConsoleReplies.TryParseList(exchange.Answer, out var online, out var max, out var names))
                {
                    entry.Online = online; entry.Maximum = max;
                    // An incomplete or redacted response gives a genuine count, not guessed identities.
                    if (online == names.Count && names.All(MinecraftPlayerCommandPolicy.IsValidName)) ApplyRoster(entry, names, now);
                    verified = true;
                }
            }
            // A re-adopted server cannot regain anonymous redirected stdin. Server-list status is
            // independently safe and needs no config change/restart; commands remain unavailable.
            if (!verified && control != MinecraftLiveControl.Stopped)
            {
                var status = await _status.QueryAsync(server, cancellationToken);
                if (status is not null)
                {
                    entry.Online = status.Online; entry.Maximum = status.Maximum;
                    foreach (var player in entry.Players.Values.ToArray()) entry.Players[player.Uuid] = player with { IsOnline = null, SessionStartedAtUtc = null };
                    foreach (var sample in status.Sample)
                    {
                        var player = Ensure(entry, sample.Uuid, sample.Name);
                        entry.Players[sample.Uuid] = player with { IsOnline = true, LastSeenAtUtc = now };
                    }
                    if (status.Online == 0) ApplyRoster(entry, [], now);
                    verified = true;
                }
            }
            if (verified) entry.LastVerified = now;
            entry.Stale = !verified;
            var snapshot = Publish(serverId, entry, control, now);
            await SaveAsync(serverId, entry, cancellationToken);
            if (!entry.Watching && _logs is not null)
            {
                entry.Watching = true;
                _ = ObserveLogsAsync(serverId, entry, _lifetime.Token);
            }
            return snapshot;
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<GameServerDefinition> GetServerAsync(Guid serverId, CancellationToken ct)
    {
        var server = await _servers.GetAsync(serverId, ct) ?? throw new KeyNotFoundException("The server is not registered.");
        if (server.Game != GameType.Minecraft) throw new ArgumentException("The selected server is not Minecraft.", nameof(serverId));
        return server;
    }

    private async Task LoadAsync(Guid serverId, Entry entry, CancellationToken ct)
    {
        if (entry.Loaded) return;
        try
        {
            var history = await _settings.GetAsync<MinecraftPlayerDashboardSnapshot>(HistoryKey(serverId), ct);
            if (history?.ServerId == serverId)
            {
                entry.Online = history.OnlinePlayers; entry.Maximum = history.MaxPlayers; entry.LastVerified = history.LastVerifiedAtUtc;
                foreach (var player in history.Players.Where(p => p.Uuid != Guid.Empty))
                    entry.Players[player.Uuid] = player with { IsOnline = null, SessionStartedAtUtc = null };
            }
        }
        catch (JsonException) { /* Broken manager history never changes Minecraft files. */ }
        entry.Loaded = true;
    }

    private Task SaveAsync(Guid id, Entry entry, CancellationToken ct) => _settings.SetAsync(HistoryKey(id), entry.Snapshot, ct);

    private static MinecraftPlayerDashboardSnapshot Publish(Guid id, Entry entry, MinecraftLiveControl control, DateTimeOffset now)
    {
        var snapshot = new MinecraftPlayerDashboardSnapshot(id, control, entry.Online, entry.Maximum, entry.Stale,
            entry.LastVerified, entry.IdentitiesKnown, entry.Players.Values.OrderBy(p => p.Username, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Uuid).ToArray(), now);
        Volatile.Write(ref entry.Snapshot, snapshot);
        return snapshot;
    }

    private static MinecraftPlayerProfile Ensure(Entry entry, Guid uuid, string? name = null)
    {
        if (!entry.Players.TryGetValue(uuid, out var player))
            player = new MinecraftPlayerProfile(uuid, null, null, null, null, null, null, null, null, null);
        if (MinecraftPlayerCommandPolicy.IsValidName(name)) player = player with { Username = name };
        entry.Players[uuid] = player;
        return player;
    }

    private static void ApplyRoster(Entry entry, IReadOnlyList<string> names, DateTimeOffset now)
    {
        var onlineNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var player in entry.Players.Values.ToArray())
        {
            var online = player.Username is { } name && onlineNames.Contains(name);
            entry.Players[player.Uuid] = player with { IsOnline = online,
                LastSeenAtUtc = online ? now : player.LastSeenAtUtc,
                // A list response proves presence, not when that player joined.
                SessionStartedAtUtc = online ? player.SessionStartedAtUtc : null };
        }
        entry.IdentitiesKnown = true;
    }

    private async Task ObserveLogsAsync(Guid id, Entry entry, CancellationToken ct)
    {
        try
        {
            await foreach (var log in _logs!.StreamAsync(id, ct))
            {
                if (log.Source is not ("stdout" or "stderr" or "Minecraft" or "Server")) continue;
                await RecordLogAsync(id, log, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Player event stream ended for {ServerId}.", id); }
        finally { entry.Watching = false; }
    }

    // Kept independently testable: only explicit server-generated UUID/join/leave lines are used.
    internal async Task RecordLogAsync(Guid id, LogEntry log, CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd(id, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            await LoadAsync(id, entry, ct);
            var message = MinecraftConsoleReplies.Message(log.Message);
            var identity = IdentityRegex().Match(message);
            if (identity.Success && Guid.TryParse(identity.Groups["id"].Value, out var uuid) && uuid != Guid.Empty)
            {
                Ensure(entry, uuid, identity.Groups["name"].Value);
            }
            else
            {
                var match = PresenceRegex().Match(message);
                if (!match.Success) return;
                var matches = entry.Players.Values.Where(p => string.Equals(p.Username, match.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) { entry.LastAttempt = DateTimeOffset.MinValue; return; }
                var player = matches[0];
                var joined = match.Groups["kind"].Value == "joined";
                // Replayed log-buffer history must not overwrite a newer list/status observation.
                var recent = entry.LastVerified is null || log.TimestampUtc >= entry.LastVerified;
                var first = joined && (player.FirstJoinedAtUtc is null || log.TimestampUtc < player.FirstJoinedAtUtc) ? log.TimestampUtc : player.FirstJoinedAtUtc;
                var last = player.LastSeenAtUtc is null || log.TimestampUtc > player.LastSeenAtUtc ? log.TimestampUtc : player.LastSeenAtUtc;
                entry.Players[player.Uuid] = player with { FirstJoinedAtUtc = first, LastSeenAtUtc = last,
                    IsOnline = recent ? joined : player.IsOnline, SessionStartedAtUtc = recent ? (joined ? log.TimestampUtc : null) : player.SessionStartedAtUtc };
                if (recent && entry.Online is not null && player.IsOnline != joined)
                {
                    entry.Online = Math.Max(0, entry.Online.Value + (joined ? 1 : -1));
                    entry.LastVerified = log.TimestampUtc;
                }
                entry.LastAttempt = DateTimeOffset.MinValue;
            }
            Publish(id, entry, Control(_console.GetState(id)), DateTimeOffset.UtcNow);
            await SaveAsync(id, entry, ct);
        }
        finally { entry.Gate.Release(); }
    }

    private void OnServerReady(object? sender, Guid id)
    {
        if (_entries.TryGetValue(id, out var entry)) entry.LastAttempt = DateTimeOffset.MinValue;
    }

    internal static MinecraftLiveControl Control(MinecraftConsoleState state) => state switch
    {
        MinecraftConsoleState.Ready => MinecraftLiveControl.Live,
        MinecraftConsoleState.Starting => MinecraftLiveControl.Starting,
        MinecraftConsoleState.NoConsole => MinecraftLiveControl.NoConsole,
        _ => MinecraftLiveControl.Stopped
    };

    public void Dispose() { _console.ServerReady -= OnServerReady; _lifetime.Cancel(); _lifetime.Dispose(); }

    [GeneratedRegex("^UUID of player (?<name>[A-Za-z0-9_]{3,16}) is (?<id>[A-Fa-f0-9-]{36})$")]
    private static partial Regex IdentityRegex();
    [GeneratedRegex("^(?<name>[A-Za-z0-9_]{3,16}) (?<kind>joined|left) the game$")]
    private static partial Regex PresenceRegex();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly SemaphoreSlim ActionGate = new(1, 1);
        public readonly Dictionary<Guid, MinecraftPlayerProfile> Players = [];
        public bool Loaded, Watching, IdentitiesKnown;
        public bool Stale = true;
        public int? Online, Maximum;
        public DateTimeOffset? LastVerified;
        public DateTimeOffset LastAttempt;
        public DateTimeOffset LastFilesRead;
        public MinecraftPlayerDashboardSnapshot? Snapshot;
    }
}
