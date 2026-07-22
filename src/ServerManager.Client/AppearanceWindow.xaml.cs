using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ServerManager.Client.Shell;

namespace ServerManager.Client;

public partial class AppearanceWindow : Window
{
    private readonly UiPreferencesStore _store = new();

    public AppearanceWindow()
    {
        InitializeComponent();
        var preferences = _store.Load();
        SelectByTag(LanguageBox, preferences.Language);
        SelectByTag(ThemeBox, preferences.Theme.ToString());
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var language = SelectedTag(LanguageBox);
        if (!Enum.TryParse<AppTheme>(SelectedTag(ThemeBox), out var theme))
        {
            theme = AppTheme.Dark;
        }

        var preferences = new UiPreferences(language, theme);
        _store.Save(preferences);
        LocalizationService.Apply(language);
        ThemeService.Apply(theme);
        FlowDirection = LayoutDirectionService.ForCulture(
            CultureInfo.GetCultureInfo(language));
        StatusText.Text =
            language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? "تم حفظ اللغة والمظهر. أعد فتح لوحة التحكم لتطبيق اتجاه جميع صفحات التنقل."
                : "Language and theme saved. Reopen the dashboard to apply the navigation direction everywhere.";
    }

    private static string SelectedTag(Selector selector) =>
        (selector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SelectByTag(Selector selector, string tag)
    {
        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        selector.SelectedIndex = selector.SelectedIndex < 0 ? 0 : selector.SelectedIndex;
    }
}
