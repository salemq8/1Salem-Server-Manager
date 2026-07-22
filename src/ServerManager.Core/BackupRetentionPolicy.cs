namespace ServerManager.Core;

public static class BackupRetentionPolicy
{
    public static IReadOnlyList<BackupRecord> SelectForDeletion(
        IReadOnlyList<BackupRecord> backups,
        BackupRetentionSettings settings,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumCount < 1 ||
            settings.MaximumAge <= TimeSpan.Zero ||
            settings.MaximumTotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        var ordered = backups
            .Where(backup =>
                backup.Status == Contracts.BackupStatus.Completed &&
                !backup.IsProtected)
            .OrderByDescending(backup => backup.CreatedAtUtc)
            .ToArray();
        var retainedCalendar = new HashSet<Guid>();
        if (settings.KeepDaily)
        {
            foreach (var backup in ordered
                         .GroupBy(item => item.CreatedAtUtc.UtcDateTime.Date)
                         .Select(group => group.First()))
            {
                retainedCalendar.Add(backup.Id);
            }
        }

        if (settings.KeepWeekly)
        {
            foreach (var backup in ordered
                         .GroupBy(item =>
                             $"{System.Globalization.ISOWeek.GetYear(item.CreatedAtUtc.UtcDateTime)}-{System.Globalization.ISOWeek.GetWeekOfYear(item.CreatedAtUtc.UtcDateTime):00}")
                         .Select(group => group.First()))
            {
                retainedCalendar.Add(backup.Id);
            }
        }

        var deletion = new HashSet<Guid>();
        long retainedBytes = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var backup = ordered[index];
            var mustKeepCalendar = retainedCalendar.Contains(backup.Id);
            var overCount = index >= settings.MaximumCount;
            var overAge = nowUtc - backup.CreatedAtUtc > settings.MaximumAge;
            var overStorage =
                retainedBytes > settings.MaximumTotalBytes - backup.SizeBytes;
            if (!mustKeepCalendar && (overCount || overAge || overStorage))
            {
                deletion.Add(backup.Id);
                continue;
            }

            retainedBytes = retainedBytes > long.MaxValue - backup.SizeBytes
                ? long.MaxValue
                : retainedBytes + backup.SizeBytes;
        }

        return ordered.Where(backup => deletion.Contains(backup.Id)).ToArray();
    }
}
