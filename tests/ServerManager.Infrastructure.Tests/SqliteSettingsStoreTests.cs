using Microsoft.Data.Sqlite;
using System.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Persistence;

namespace ServerManager.Infrastructure.Tests;

public sealed class SqliteSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SetAndGetAsync_RoundTripsTypedJson()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();
        var store = new SqliteSettingsStore(factory);
        var expected = new TestSetting("Balanced", 6);

        await store.SetAsync("resources.profile", expected);
        var actual = await store.GetAsync<TestSetting>("resources.profile");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingValue()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();
        var store = new SqliteSettingsStore(factory);

        await store.SetAsync("ui.language", "en");
        await store.SetAsync("ui.language", "ar");

        Assert.Equal("ar", await store.GetAsync<string>("ui.language"));
    }

    [Fact]
    public async Task ResourcePolicy_PersistsCustomValuesAcrossStoreRestart()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();
        var expected = new ResourcePolicy(
            ResourceMode.Custom,
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.Normal,
            6 * ResourcePolicyCatalog.Gibibyte,
            15 * ResourcePolicyCatalog.Gibibyte / 2,
            PalworldWarningThresholdBytes:
                5 * ResourcePolicyCatalog.Gibibyte,
            PalworldCriticalThresholdBytes:
                7 * ResourcePolicyCatalog.Gibibyte,
            AllowUnsafeStartupOverride: true);

        await new SqliteSettingsStore(factory).SetAsync(
            "resources.activePolicy",
            expected);
        var restartedStore = new SqliteSettingsStore(
            new SqliteConnectionFactory(options));
        var actual = await restartedStore.GetAsync<ResourcePolicy>(
            "resources.activePolicy");

        Assert.Equal(expected, actual);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed record TestSetting(string Mode, int PollSeconds);
}
