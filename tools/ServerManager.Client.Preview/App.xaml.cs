using System.Globalization;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Preview;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // A distinct identity from the real Stable app (ProductIdentity.AppUserModelId), so
        // this developer-only design/screenshot harness can never visually merge with, or be
        // mistaken for, the real running application in the taskbar.
        ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.PreviewAppUserModelId);
        base.OnStartup(e);
        var options = PreviewOptions.Parse(e.Args);
        LocalizationService.Apply(options.Language);
        ThemeService.Apply(options.Theme);
        var window = new PreviewWindow(options);
        MainWindow = window;
        if (options.ExportAll)
        {
            window.ShowInTaskbar = false;
            window.Opacity = 0;
        }

        window.Show();
        if (options.ExportAll)
        {
            await window.ExportAllScreenshotsAsync();
            window.Close();
            Shutdown();
        }
    }
}

public sealed record PreviewOptions(
    string State,
    AppTheme Theme,
    string Language,
    double Width,
    double Height,
    bool ExportAll)
{
    public static PreviewOptions Parse(IReadOnlyList<string> args)
    {
        static string Value(IReadOnlyList<string> values, string name, string fallback)
        {
            var index = values.ToList().FindIndex(value =>
                value.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < values.Count ? values[index + 1] : fallback;
        }

        var state = Value(args, "--state", "running");
        var theme = Value(args, "--theme", "dark").Equals("light", StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Light
            : AppTheme.Dark;
        var language = Value(args, "--language", "en-US");
        var width = double.Parse(Value(args, "--width", "1920"), CultureInfo.InvariantCulture);
        var height = double.Parse(Value(args, "--height", "1080"), CultureInfo.InvariantCulture);
        var exportAll = args.Any(argument =>
            argument.Equals("--export-all", StringComparison.OrdinalIgnoreCase));
        return new PreviewOptions(state, theme, language, width, height, exportAll);
    }
}
