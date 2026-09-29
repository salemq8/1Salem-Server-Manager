using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// Loads an application's own App.xaml resources from source. WPF refuses to load one
/// assembly's App.xaml into another type, and instantiating the real App would run its startup.
/// </summary>
internal static class AppResources
{
    public static ResourceDictionary Load(string repositoryRelativePath)
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), repositoryRelativePath));
        var root = Regex.Match(xaml, @"<Application\s[^>]*>", RegexOptions.Singleline).Value;
        var namespaces = string.Join(' ', Regex.Matches(root, @"xmlns(:\w+)?=""[^""]*""").Select(match => match.Value));
        var inner = Regex.Match(xaml, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline).Groups[1].Value.Trim();
        var dictionary = inner.StartsWith("<ResourceDictionary", StringComparison.Ordinal)
            ? "<ResourceDictionary " + namespaces + inner["<ResourceDictionary".Length..]
            : "<ResourceDictionary " + namespaces + ">" + inner + "</ResourceDictionary>";
        return (ResourceDictionary)XamlReader.Parse(dictionary);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
