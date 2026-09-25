using System.Globalization;
using System.Text.RegularExpressions;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

/// <summary>
/// The 1Salem Connect card in Server Detail's Settings is a development-preview shell. It must
/// say so in both languages and offer no action that could look as if it provisioned,
/// invited or revoked anyone.
/// </summary>
public sealed class ConnectPreviewCardTests
{
    private static readonly string[] CardKeys =
    [
        "ServerSettings.Connect",
        "ServerSettings.ConnectPreview",
        "ServerSettings.ConnectBody",
        "ServerSettings.ConnectEnable",
        "ServerSettings.ConnectInvite",
        "ServerSettings.ConnectFriends",
        "ServerSettings.ConnectRevoke"
    ];

    private static readonly string[] ActionButtons =
    [
        "ConnectEnableButton",
        "ConnectInviteButton",
        "ConnectFriendsButton",
        "ConnectRevokeButton"
    ];

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public void EveryCardString_ResolvesInBothLanguages(string culture)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach (var key in CardKeys)
            {
                Assert.True(LocalizationService.HasKey(key), $"{key} has no {culture} text.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void TheCard_SaysItIsNotConfiguredInThisBuild()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Contains("development preview", LocalizationService.Get("ServerSettings.ConnectBody"), StringComparison.Ordinal);
            Assert.Contains("not configured in this build", LocalizationService.Get("ServerSettings.ConnectBody"), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void EveryConnectAction_IsDisabled_AndWiredToNothing()
    {
        var markup = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml");
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");

        foreach (var name in ActionButtons)
        {
            var declaration = Regex.Match(markup, $"<Button x:Name=\"{name}\"[^>]*/>", RegexOptions.Singleline);
            Assert.True(declaration.Success, $"{name} is not declared.");
            Assert.Contains("IsEnabled=\"False\"", declaration.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("Click=", declaration.Value, StringComparison.Ordinal);
            Assert.DoesNotContain($"{name}.IsEnabled", code, StringComparison.Ordinal);
            Assert.DoesNotContain($"{name}.Click", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCard_BelongsToTheNetworkGroup()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");

        Assert.Contains("ConnectCard.Visibility = group == \"Network\"", code, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    }
}
