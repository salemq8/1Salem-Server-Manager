namespace ServerManager.Client.Tests;

/// <summary>
/// Create Minecraft Server: each new server gets its own unique folder named after it, instead of
/// the first server's ProgramData\1SalemServerManager\Minecraft root (found in Build 8, fixed in Build 9).
/// </summary>
public sealed class MinecraftInstallFolderTests
{
    private const string Parent = @"C:\ProgramData\1SalemServerManager\MinecraftServers";

    [Fact]
    public void DefaultParent_IsItsOwnFolder_NotTheFirstServersRoot()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var legacyRoot = Path.Combine(programData, "1SalemServerManager", "Minecraft");

        Assert.Equal(Path.Combine(programData, "1SalemServerManager", "MinecraftServers"), MinecraftInstallFolder.DefaultParentFolder);
        Assert.False(MinecraftInstallFolder.DefaultParentFolder.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("alsrabet2", "alsrabet2")]
    [InlineData("  My Server  ", "My Server")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("tab\there", "tab-here")]
    [InlineData("name. . ", "name")]
    [InlineData("CON", "CON-server")]
    [InlineData("nul.txt", "nul-server.txt")]
    [InlineData("com1", "com1-server")]
    [InlineData("", "minecraft-server")]
    [InlineData("   ", "minecraft-server")]
    [InlineData("???", "minecraft-server")]
    [InlineData("..", "minecraft-server")]
    [InlineData("سيرفر الأصدقاء", "سيرفر الأصدقاء")]
    public void FolderName_KeepsTheServerName_AndReplacesWhatWindowsRefuses(string serverName, string expected) =>
        Assert.Equal(expected, MinecraftInstallFolder.FolderName(serverName));

    [Fact]
    public void FolderName_IsCappedAt64Characters()
    {
        var name = MinecraftInstallFolder.FolderName(new string('x', 200));

        Assert.Equal(64, name.Length);
    }

    [Fact]
    public void FolderName_NeverSplitsOrKeepsALoneSurrogate()
    {
        var cut = MinecraftInstallFolder.FolderName(new string('a', 63) + "\U0001F525tail");
        var lone = MinecraftInstallFolder.FolderName("ab\uD83Dcd");
        var pair = MinecraftInstallFolder.FolderName("fire \U0001F525");

        Assert.Equal(new string('a', 63), cut);
        Assert.Equal("ab-cd", lone);
        Assert.Equal("fire \U0001F525", pair);
    }

    [Fact]
    public void Unique_AddsTheFirstFreeSuffix_WhenTheFolderExists()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Parent, "alsrabet2"),
            Path.Combine(Parent, "alsrabet2-2"),
        };

        Assert.Equal(Path.Combine(Parent, "survival"), MinecraftInstallFolder.Unique(Parent, "survival", taken.Contains));
        Assert.Equal(Path.Combine(Parent, "alsrabet2-3"), MinecraftInstallFolder.Unique(Parent, "alsrabet2", taken.Contains));
    }

    [Fact]
    public void Suggestion_FollowsTheServerName_UntilTheUserChoosesAFolder()
    {
        var folder = new MinecraftInstallFolder(Parent, _ => false);

        var first = folder.Suggest("alsrabet2");
        folder.PathChanged(first);
        var renamed = folder.Suggest("alsrabet3");
        folder.PathChanged(renamed);

        Assert.Equal(Path.Combine(Parent, "alsrabet2"), first);
        Assert.Equal(Path.Combine(Parent, "alsrabet3"), renamed);
        Assert.False(folder.IsCustom);

        folder.PathChanged(@"D:\Games\My Minecraft");

        Assert.True(folder.IsCustom);
        Assert.Null(folder.Suggest("another name"));
    }

    [Fact]
    public void Browse_ChoosesAUniqueFolderInsideTheParent_AndStopsFollowingTheName()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Servers\alsrabet2" };
        var folder = new MinecraftInstallFolder(Parent, taken.Contains);
        folder.PathChanged(folder.Suggest("alsrabet2"));

        var chosen = folder.ChooseParent(@"D:\Servers", "alsrabet2");
        folder.PathChanged(chosen);

        Assert.Equal(@"D:\Servers\alsrabet2-2", chosen);
        Assert.True(folder.IsCustom);
        Assert.Null(folder.Suggest("renamed"));
    }

    [Fact]
    public void Browse_ToTheSuggestedFolder_StillStopsFollowingTheName()
    {
        var folder = new MinecraftInstallFolder(Parent, _ => false);
        var suggested = folder.Suggest("alsrabet2");

        var chosen = folder.ChooseParent(Parent, "alsrabet2");

        Assert.Equal(suggested, chosen);
        Assert.True(folder.IsCustom);
        Assert.Null(folder.Suggest("renamed"));
    }

    [Fact]
    public void ClearingThePath_ReturnsToTheAutomaticFolder()
    {
        var folder = new MinecraftInstallFolder(Parent, _ => false);
        folder.PathChanged(@"D:\Custom");

        folder.PathChanged(string.Empty);

        Assert.False(folder.IsCustom);
        Assert.Equal(Path.Combine(Parent, "survival"), folder.Suggest("survival"));
    }

    [Fact]
    public void Suggestion_ReChecksTheFolder_SoANewlyCreatedFolderGetsASuffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = new MinecraftInstallFolder(Parent, taken.Contains);
        folder.PathChanged(folder.Suggest("alsrabet2"));

        taken.Add(Path.Combine(Parent, "alsrabet2"));
        var refreshed = folder.Suggest("alsrabet2");

        Assert.Equal(Path.Combine(Parent, "alsrabet2-2"), refreshed);
    }

    [Fact]
    public void Wizard_UsesTheGeneratedFolder_InsteadOfTheFirstServersRoot()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "MinecraftInstallWindow.xaml");
        var code = ReadSource("src", "ServerManager.Client", "MinecraftInstallWindow.xaml.cs");

        Assert.DoesNotContain(@"1SalemServerManager\Minecraft""", xaml, StringComparison.Ordinal);
        Assert.Contains("ServerNameBox.TextChanged += (_, _) => RefreshFolder();", code, StringComparison.Ordinal);
        Assert.Contains("FolderBox.TextChanged += (_, _) => _folder.PathChanged(FolderBox.Text);", code, StringComparison.Ordinal);
        Assert.Contains("_folder.ChooseParent(dialog.SelectedPath, ServerNameBox.Text)", code, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Path.Combine(dialog.SelectedPath, ""Minecraft"")", code, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    }
}
