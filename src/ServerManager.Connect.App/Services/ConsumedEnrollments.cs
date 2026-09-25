using System.Text.Json;
using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// The memberships whose one-time enrollment blob this device has taken from the broker without
/// the membership being bound to a node yet. The broker deletes a blob when it is read, so for
/// these "nothing to take" means the blob is used up, not that the owner has yet to send it.
/// <para>
/// Kept across runs, because an app restart must not turn a failed enrollment back into
/// "Enrollment pending" for good. The file holds membership ids and nothing else (never anything
/// from the blob, the auth key above all), and is written whole to a temporary file that is then
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
    private HashSet<string>? _ids;

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
            return Ids.Contains(membershipId);
        }
    }

    public void Add(string membershipId)
    {
        if (!BrokerFormats.IsMembershipId(membershipId))
        {
            throw new ArgumentException("Unexpected membership id format.", nameof(membershipId));
        }

        lock (_gate)
        {
            if (Ids.Add(membershipId))
            {
                Save();
            }
        }
    }

    public void Remove(string membershipId)
    {
        lock (_gate)
        {
            if (Ids.Remove(membershipId))
            {
                Save();
            }
        }
    }

    // Read on first use rather than at startup: a copy that never enrolls never touches the file.
    private HashSet<string> Ids => _ids ??= Load();

    private HashSet<string> Load()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            return ids;
        }

        try
        {
            // Well-formed ids only: anything else in the file means nothing here.
            var saved = JsonSerializer.Deserialize<string?[]>(File.ReadAllBytes(_path)) ?? [];
            ids.UnionWith(saved.Where(BrokerFormats.IsMembershipId).Select(id => id!));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Record("enrollment", exception);
        }

        return ids;
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(_ids!.Order(StringComparer.Ordinal).ToArray());
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
}
