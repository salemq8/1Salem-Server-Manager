using ServerManager.Client.Shell;
using ServerManager.Contracts;
using Window = System.Windows.Window;

namespace ServerManager.Client;

/// <summary>
/// The legacy editors, reachable from Server Detail. These are large, proven surfaces whose
/// backend logic must not be rewritten just to look newer, so Server Detail links to them
/// until each is ported. Opening one is always an explicit "Configure…" choice.
/// </summary>
public partial class LegacyServerEditorWindow : Window
{
    /// <summary>Tab indexes inside the legacy page, matching its declaration order.</summary>
    public const int SettingsTab = 2;
    public const int FilesTab = 4;
    public const int UpdatesTab = 6;
    public const int PerformanceTab = 7;
    public const int NetworkTab = 8;

    public LegacyServerEditorWindow()
    {
        InitializeComponent();
        Closed += (_, _) => LegacyPage.Dispose();
        // The legacy overview's "Open Remote Access" had no subscriber once the sidebar
        // destination was removed, so the menu item silently did nothing.
        LegacyPage.RemoteAccessRequested += (_, _) => RemoteAccessWindow.Open(this);
    }

    public void ShowFor(
        GameType game,
        int tabIndex,
        string title,
        string subtitle,
        Guid? serverId = null)
    {
        HeaderTitle.Text = title;
        HeaderSubtitle.Text = subtitle;
        LegacyPage.Configure(game, serverId);
        LegacyPage.SelectTab(tabIndex);
        _ = LegacyPage.RefreshNowAsync();
    }

    /// <summary>
    /// Opens one of the legacy editors as a modal owned by the main window, for the server it
    /// was opened from. Without an id it falls back to the first server of that game, which is
    /// what the create flow wants.
    /// </summary>
    public static void Open(
        Window? owner,
        GameType game,
        int tabIndex,
        string titleKey,
        string subtitleKey,
        Guid? serverId = null)
    {
        var window = new LegacyServerEditorWindow { Owner = owner };
        window.ShowFor(
            game,
            tabIndex,
            LocalizationService.Get(titleKey),
            LocalizationService.Get(subtitleKey),
            serverId);
        window.ShowDialog();
    }
}
