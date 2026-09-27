using System.Text.Json;
using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// The memberships whose one-time enrollment blob this device has taken from the broker without
/// the membership being bound to a node yet. The broker deletes a blob when it is read, so for
/// these "nothing to take" means the blob is used up, not that the owner has yet to send it.
/// <para>
/// Kept across runs, because an app restart must not turn a failed enrollment back into
/// "Enrollment pending" for good. The file holds membership ids plus a flag saying that an old
/// owner node still had to be replaced, and nothing else (never anything from the blob, the auth key
/// above all), and is written whole to a temporary file that is then
/// moved over it, so a crash never leaves half a list. A file that cannot be read or written is
/// recorded in Diagnostics and the list carries on in memory for this run: the worst that can
/// come of it after a restart is "pending" where "failed" would be right.
/// </para>
/// </summary>
public sealed class ConsumedEnrollments
{
    public const string FileName = "consumed-enrollments.json";

    private readonly string? _path;
    private readonly DiagnosticsLog _log;
    private readonly object _gate = new();
    private PersistedState? _state;

    /// <param name="path">The file; null keeps the list for this run only.</param>
    public ConsumedEnrollments(string? path, DiagnosticsLog log)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>%LOCALAPPDATA%\1Salem Connect\consumed-enrollments.json, beside the identity and transport folders.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1Salem Connect", FileName);

    public bool Contains(string membershipId)
    {
        lock (_gate)
        {
            return State.Ids.Contains(membershipId);
        }
    }

    /// <summary>True only when a local node can be known to have come from this membership's blob.</summary>
    public bool CanReuseNode(string membershipId)
    {
        lock (_gate)
        {
            return State.Ids.Contains(membershipId) && !State.Replacements.Contains(membershipId);
        }
    }

    public void Add(string membershipId, bool replacingStaleNode = false)
    {
        if (!BrokerFormats.IsMembershipId(membershipId))
        {
            throw new ArgumentException("Unexpected membership id format.", nameof(membershipId));
        }

        lock (_gate)
        {
            var changed = State.Ids.Add(membershipId);
            if (replacingStaleNode)
            {
                changed |= State.Replacements.Add(membershipId);
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>The replacement enrollment returned a new node; a failed broker bind may safely retry it.</summary>
    public void MarkFreshNode(string membershipId)
    {
        lock (_gate)
        {
            if (State.Replacements.Remove(membershipId))
            {
                Save();
            }
        }
    }

    public void Remove(string membershipId)
    {
        lock (_gate)
        {
            var changed = State.Ids.Remove(membershipId);
            changed |= State.Replacements.Remove(membershipId);
            if (changed)
            {
                Save();
            }
        }
    }

    // Read on first use rather than at startup: a copy that never enrolls never touches the file.
    private PersistedState State => _state ??= Load();

    private PersistedState Load()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var replacements = new HashSet<string>(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            return new PersistedState(ids, replacements);
        }

        try
        {
            if (new FileInfo(_path).Length > 64 * 1024)
            {
                throw new JsonException("The consumed enrollment state is too large.");
            }

            var content = File.ReadAllBytes(_path);
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Build 7 compatibility: its file was an array of membership ids.
                AddValid(document.RootElement, ids);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                     document.RootElement.TryGetProperty("version", out var version) && version.GetInt32() == 2 &&
                     document.RootElement.TryGetProperty("memberships", out var savedIds) &&
                     document.RootElement.TryGetProperty("replacements", out var savedReplacements))
            {
                AddValid(savedIds, ids);
                AddValid(savedReplacements, replacements);
                replacements.IntersectWith(ids);
            }
            else
            {
                throw new JsonException("The consumed enrollment state has an unknown format.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            _log.Record("enrollment", exception);
        }

        return new PersistedState(ids, replacements);
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 2,
            memberships = State.Ids.Order(StringComparer.Ordinal).ToArray(),
            replacements = State.Replacements.Order(StringComparer.Ordinal).ToArray()
        });
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(content);
                }

                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                // Already gone once it has been moved into place.
                File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Record("enrollment", exception);
        }
    }

    private static void AddValid(JsonElement array, HashSet<string> target)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array of membership ids.");
        }

        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } id && BrokerFormats.IsMembershipId(id))
            {
                target.Add(id);
            }
        }
    }

    private sealed record PersistedState(HashSet<string> Ids, HashSet<string> Replacements);
}
