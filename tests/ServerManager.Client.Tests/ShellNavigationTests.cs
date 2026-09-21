using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

/// <summary>
/// Behavioural cover for the Build 6 shell. The old navigation tests asserted that specific
/// strings appeared in MainViewModel.cs, which said nothing about what the shell does; these
/// exercise the view model itself. Titles are only checked for being present, never for exact
/// wording, because another test class applies Arabic globally and xUnit runs classes in
/// parallel.
/// </summary>
public sealed class ShellNavigationTests
{
    [Fact]
    public void Destinations_AreTheFiveTaskShapedOnes_InSidebarOrder()
    {
        Assert.Equal(
            ["Home", "Servers", "Backups", "Network", "Settings"],
            MainViewModel.DestinationKeys);
    }

    [Fact]
    public void Destinations_NoLongerExposeTheTwelveTechnicalSections()
    {
        // Games, files, logs and the rest moved inside a server, Settings or Advanced. If one
        // reappears as a top-level destination the simplification has been undone.
        string[] retired =
        [
            "Minecraft",
            "Palworld",
            "RemoteAccess",
            "Updates",
            "Resources",
            "Files",
            "Logs",
            "About"
        ];

        Assert.Empty(MainViewModel.DestinationKeys.Intersect(retired, StringComparer.Ordinal));
    }

    [Fact]
    public void PrimarySections_ExcludeSettings_SoItCanPinToTheBottom()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);

        Assert.DoesNotContain(
            viewModel.PrimarySections,
            section => section.Key.Equals("Settings", StringComparison.Ordinal));
        Assert.Equal("Settings", viewModel.SettingsSection.Key);
        Assert.Equal(
            MainViewModel.DestinationKeys.Count - 1,
            viewModel.PrimarySections.Count);
    }

    [Fact]
    public void EveryDestination_HasATitleAndAnIcon()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);

        foreach (var section in viewModel.Sections)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(section.Title),
                $"Destination '{section.Key}' has no title to render.");
            Assert.False(
                string.IsNullOrWhiteSpace(section.Glyph),
                $"Destination '{section.Key}' has no icon, so the collapsed rail is blank.");
        }
    }

    /// <summary>
    /// The shell shows pages by binding each one's visibility to its own Is*Selected flag. If
    /// two are ever true at once the pages render stacked on top of each other, which is
    /// exactly what a null written back by the primary list's selection binding used to cause.
    /// </summary>
    [Fact]
    public void SelectSection_LeavesExactlyOneDestinationSelected()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);

        foreach (var key in MainViewModel.DestinationKeys)
        {
            viewModel.SelectSection(key);

            Assert.Equal(key, viewModel.SelectedSection.Key);
            Assert.Equal(1, CountSelected(viewModel));
        }
    }

    [Fact]
    public void SelectedSection_RejectsNull_SoVisibilityNeverGoesStale()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);
        viewModel.SelectSection("Settings");

        // The primary list cannot hold Settings, so its TwoWay binding writes null here.
        viewModel.SelectedSection = null!;

        Assert.Equal("Settings", viewModel.SelectedSection.Key);
        Assert.Equal(1, CountSelected(viewModel));
    }

    [Fact]
    public void SelectSection_IgnoresAnUnknownKey()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);
        viewModel.SelectSection("Home");

        viewModel.SelectSection("Palworld");

        Assert.Equal("Home", viewModel.SelectedSection.Key);
    }

    [Fact]
    public void PageTitleAndSubtitle_FollowTheSelectedDestination()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);

        foreach (var key in MainViewModel.DestinationKeys)
        {
            viewModel.SelectSection(key);

            Assert.Equal(viewModel.SelectedSection.Title, viewModel.PageTitle);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.PageSubtitle));
        }
    }

    private static int CountSelected(MainViewModel viewModel) =>
        new[]
        {
            viewModel.IsHomeSelected,
            viewModel.IsServersSelected,
            viewModel.IsBackupsSelected,
            viewModel.IsNetworkSelected,
            viewModel.IsSettingsSelected
        }.Count(selected => selected);
}
