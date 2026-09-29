using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App.Tests;

public sealed class DiagnosticsAndTextTests
{
    [Fact]
    public async Task Diagnostics_show_technical_detail_with_every_secret_redacted()
    {
        using var app = new AppHarness();
        var secret = TestIds.NewInviteSecret();
        app.Transport.Nodes.Add(new TransportNode(app.OwnerId, "nFAKE1CNTRL", "fake"));
        app.Transport.Log.Add("2026-09-24T10:00:00Z enroll failed for tskey-auth-kABCDEF123CNTRL-secretpart");
        app.Transport.Log.Add("2026-09-24T10:00:01Z ticket eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiIxc2FsZW0ifQ.c2lnbmF0dXJlLXNlY3JldA");
        app.Log.Record("invite", new InvalidOperationException($"could not use https://connect.1salem.app/i#{secret}"));
        app.Main.ShowDiagnostics();
        var diagnostics = Assert.IsType<DiagnosticsViewModel>(app.Main.CurrentPage);

        await diagnostics.RefreshAsync(CancellationToken.None);
        var report = diagnostics.ReportText;

        Assert.Contains(app.Identity.DeviceId, report, StringComparison.Ordinal);
        Assert.Contains("nFAKE1CNTRL", report, StringComparison.Ordinal);
        Assert.Contains("fake test", report, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secretpart", report, StringComparison.Ordinal);
        Assert.DoesNotContain("c2lnbmF0dXJlLXNlY3JldA", report, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report, StringComparison.Ordinal);

        diagnostics.CopyCommand.Execute(null);
        Assert.Equal(report, app.Clipboard.Text);
    }

    [Fact]
    public async Task Diagnostics_say_when_the_transport_is_not_running()
    {
        using var app = new AppHarness();
        app.Transport.DiagnosticsFailure = new TransportException(TransportErrorCodes.Unavailable);
        var diagnostics = new DiagnosticsViewModel(app.Context);

        await diagnostics.RefreshAsync(CancellationToken.None);

        Assert.Contains("Transport: not running", diagnostics.ReportText, StringComparison.Ordinal);
    }

    [Fact]
    public void User_facing_states_are_exactly_the_agreed_words()
    {
        Assert.Equal("Connecting…", ConnectionStateText.For(ConnectionState.Connecting));
        Assert.Equal("Connected", ConnectionStateText.For(ConnectionState.Connected));
        Assert.Equal("Disconnected", ConnectionStateText.For(ConnectionState.Disconnected));
        Assert.Equal("Server offline", ConnectionStateText.For(ConnectionState.ServerOffline));
        Assert.Equal("Access expired", ConnectionStateText.For(ConnectionState.AccessExpired));
        Assert.Equal("Access revoked", ConnectionStateText.For(ConnectionState.AccessRevoked));
        Assert.Equal("1Salem Connect is not configured yet", Text.NotConfigured);
    }

    [Fact]
    public void String_table_and_typed_accessors_match_exactly()
    {
        var properties = typeof(Text)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .ToDictionary(property => property.Name, property => (string?)property.GetValue(null));

        var entries = Entries().Keys.ToHashSet();

        Assert.Equal(entries.OrderBy(key => key, StringComparer.Ordinal), properties.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(properties, property => Assert.False(string.IsNullOrWhiteSpace(property.Value), property.Key));
    }

    [Fact]
    public void Network_vocabulary_appears_only_in_diagnostics_strings()
    {
        var forbidden = new Regex(@"\b(DERP|WireGuard|OAuth|ACL|node ?keys?|tailnet|Tailscale|tsnet|ticket)\b", RegexOptions.IgnoreCase);

        var leaks = Entries().Concat(Entries(Arabic))
            .Where(entry => !entry.Key.StartsWith("Diagnostics", StringComparison.Ordinal) && forbidden.IsMatch(entry.Value))
            .Select(entry => entry.Key)
            .ToList();

        Assert.Empty(leaks);
    }

    [Fact]
    public void Arabic_table_translates_every_string_with_the_same_placeholders()
    {
        var english = Entries();
        var arabic = Entries(Arabic);
        var placeholders = new Regex(@"\{\d+\}");
        static string[] Slots(Regex pattern, string text) => [.. pattern.Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal)];

        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), arabic.Keys.Order(StringComparer.Ordinal));
        Assert.All(english, entry => Assert.Equal(Slots(placeholders, entry.Value), Slots(placeholders, arabic[entry.Key])));
    }

    [Fact]
    public void Only_a_translated_right_to_left_language_turns_the_window_right_to_left()
    {
        Assert.True(Text.HasOwnTable(CultureInfo.GetCultureInfo("ar-SA")));
        // No Hebrew table: the app shows English, which must stay left to right.
        Assert.False(Text.HasOwnTable(CultureInfo.GetCultureInfo("he-IL")));
    }

    private static readonly CultureInfo Arabic = CultureInfo.GetCultureInfo("ar");

    private static Dictionary<string, string> Entries(CultureInfo? culture = null)
    {
        var set = Text.Table.GetResourceSet(culture ?? CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: culture is null)
            ?? throw new MissingManifestResourceException("The string table is missing.");
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
    }
}
