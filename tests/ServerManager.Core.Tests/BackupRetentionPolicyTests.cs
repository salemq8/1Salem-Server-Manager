using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class BackupRetentionPolicyTests
{
    [Fact]
    public void SelectForDeletion_AppliesCountAndAgeWithoutDeletingNewest()
    {
        var now = DateTimeOffset.UtcNow;
        var serverId = Guid.NewGuid();
        var backups = Enumerable.Range(0, 5)
            .Select(index => new BackupRecord(
                Guid.NewGuid(),
                serverId,
                $"backup-{index}.zip",
                "1",
                "hash",
                1,
                1,
                BackupStatus.Completed,
                now.AddDays(-index)))
            .ToArray();

        var deletion = BackupRetentionPolicy.SelectForDeletion(
            backups,
            new BackupRetentionSettings(3, TimeSpan.FromDays(30)),
            now);

        Assert.Equal(2, deletion.Count);
        Assert.DoesNotContain(deletion, backup => backup.Id == backups[0].Id);
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesProtectedBackup()
    {
        var now = DateTimeOffset.UtcNow;
        var protectedBackup = CreateBackup(
            now.AddYears(-1),
            10_000,
            isProtected: true);
        var ordinary = CreateBackup(now.AddDays(-30), 10_000);

        var deletion = BackupRetentionPolicy.SelectForDeletion(
            [protectedBackup, ordinary],
            new BackupRetentionSettings(
                1,
                TimeSpan.FromDays(1),
                1),
            now);

        Assert.DoesNotContain(
            deletion,
            backup => backup.Id == protectedBackup.Id);
        Assert.Contains(deletion, backup => backup.Id == ordinary.Id);
    }

    [Fact]
    public void SelectForDeletion_EnforcesMaximumTotalStorage()
    {
        var now = DateTimeOffset.UtcNow;
        var backups = new[]
        {
            CreateBackup(now, 75),
            CreateBackup(now.AddMinutes(-1), 75),
            CreateBackup(now.AddMinutes(-2), 75)
        };

        var deletion = BackupRetentionPolicy.SelectForDeletion(
            backups,
            new BackupRetentionSettings(
                10,
                TimeSpan.FromDays(30),
                150),
            now);

        Assert.Single(deletion);
        Assert.Equal(backups[2].Id, deletion[0].Id);
    }

    private static BackupRecord CreateBackup(
        DateTimeOffset created,
        long size,
        bool isProtected = false) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"backup-{Guid.NewGuid():N}.zip",
            "1",
            "hash",
            size,
            1,
            BackupStatus.Completed,
            created,
            IsProtected: isProtected);
}
