using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ServerManager.Connect.App.Shell;

public enum ThemeChoice
{
    /// <summary>Light or dark as Windows' "app mode" says.</summary>
    System,
    Dark,
    Light
}

public sealed record ConnectPreferences(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ThemeChoice Theme = ThemeChoice.System);

/// <summary>
/// <c>%LOCALAPPDATA%\1Salem Connect\preferences.json</c>, the friend's own choices. Like the rest
/// of that folder it is left alone by Setup and by updates. Unreadable means defaults.
/// </summary>
public sealed class ConnectPreferencesStore
{
    public const string FileName = "preferences.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? _path;
    private ConnectPreferences _memory = new();

    /// <summary>A null path keeps preferences for this run only.</summary>
    public ConnectPreferencesStore(string? path)
    {
        _path = path is null ? null : Path.GetFullPath(path);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1Salem Connect", FileName);

    public ConnectPreferences Load()
    {
        if (_path is null)
        {
            return _memory;
        }

        try
        {
            var info = new FileInfo(_path);
            return info.Exists && info.Length <= 4096
                ? JsonSerializer.Deserialize<ConnectPreferences>(File.ReadAllBytes(_path), Json) ?? new ConnectPreferences()
                : new ConnectPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ConnectPreferences();
        }
    }

    public void Save(ConnectPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (_path is null)
        {
            _memory = preferences;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(preferences, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

/// <summary>
/// The two palettes of the Server Manager client (src/ServerManager.Client/Shell/UiPreferences.cs),
/// reduced to the keys this app uses. Applying a theme replaces the brushes in the application's
/// resources; every page binds them with DynamicResource, so the whole window follows.
/// </summary>
public static class ConnectTheme
{
    internal static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SurfaceBrush"] = "#08131F",
        ["SurfaceRaisedBrush"] = "#101F2E",
        ["SurfaceOverlayBrush"] = "#16283A",
        ["SurfaceSunkenBrush"] = "#050D16",
        ["PanelAltBrush"] = "#16283A",
        ["AccentBrush"] = "#2CB8B3",
        ["AccentSoftBrush"] = "#123B3E",
        ["WarningBrush"] = "#E0A83B",
        ["WarningSoftBrush"] = "#33290F",
        ["DangerBrush"] = "#D85B63",
        ["DangerSoftBrush"] = "#351D27",
        ["DisabledBrush"] = "#405064",
        ["TextBrush"] = "#F1F6FA",
        ["TextPrimaryBrush"] = "#F1F6FA",
        ["TextSecondaryBrush"] = "#9FB0C2",
        ["TextTertiaryBrush"] = "#7D91A8",
        ["MutedTextBrush"] = "#9FB0C2",
        ["ControlSurfaceBrush"] = "#102235",
        ["ControlBorderBrush"] = "#3B566D",
        ["FocusBrush"] = "#72E2DE",
        ["SelectionBrush"] = "#087F7B",
        ["BorderSubtleBrush"] = "#1E3448",
        ["BorderStrongBrush"] = "#2C4459",
        ["AccentForegroundBrush"] = "#031719",
        ["AccentHoverBrush"] = "#58D1CD",
        ["AccentPressedBrush"] = "#168A87",
        ["AccentPressedForegroundBrush"] = "#FFFFFF",
        ["DisabledBorderBrush"] = "#526174",
        ["DisabledForegroundBrush"] = "#D1D7E0"
    };

    internal static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SurfaceBrush"] = "#F4F6F9",
        ["SurfaceRaisedBrush"] = "#FFFFFF",
        ["SurfaceOverlayBrush"] = "#EDF2F7",
        ["SurfaceSunkenBrush"] = "#E8EDF3",
        ["PanelAltBrush"] = "#E5EAF1",
        ["AccentBrush"] = "#087F7B",
        ["AccentSoftBrush"] = "#DCF0EF",
        ["WarningBrush"] = "#A96F00",
        ["WarningSoftBrush"] = "#FBEFD8",
        ["DangerBrush"] = "#B62D3A",
        ["DangerSoftBrush"] = "#FBEAEC",
        ["DisabledBrush"] = "#C7D0DB",
        ["TextBrush"] = "#16202D",
        ["TextPrimaryBrush"] = "#16202D",
        ["TextSecondaryBrush"] = "#526173",
        ["TextTertiaryBrush"] = "#5F6E7F",
        ["MutedTextBrush"] = "#526173",
        ["ControlSurfaceBrush"] = "#FFFFFF",
        ["ControlBorderBrush"] = "#91A0B3",
        ["FocusBrush"] = "#087F7B",
        ["SelectionBrush"] = "#087F7B",
        ["BorderSubtleBrush"] = "#D8E0E9",
        ["BorderStrongBrush"] = "#B9C5D3",
        ["AccentForegroundBrush"] = "#FFFFFF",
        ["AccentHoverBrush"] = "#0A9590",
        ["AccentPressedBrush"] = "#066662",
        ["AccentPressedForegroundBrush"] = "#FFFFFF",
        ["DisabledBorderBrush"] = "#AEB9C6",
        ["DisabledForegroundBrush"] = "#4A5868"
    };

    /// <summary>The palette a choice means right now.</summary>
    public static bool IsLight(ThemeChoice choice) =>
        choice switch
        {
            ThemeChoice.Light => true,
            ThemeChoice.Dark => false,
            _ => WindowsUsesLightApps()
        };

    public static void Apply(ResourceDictionary resources, ThemeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var (key, color) in IsLight(choice) ? Light : Dark)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
