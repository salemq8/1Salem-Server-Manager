using ServerManager.Client.Shell;
using ServerManager.Contracts;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// The Content destination's shell. It exists so Server Detail's navigation is final now and
/// the Content Hub can be dropped in later without moving anything. No catalogue, no
/// providers, and deliberately no placeholder items pretending to be real content.
/// </summary>
public partial class ServerContentTab : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;

    public ServerContentTab()
    {
        InitializeComponent();
        _context.Changed += (_, _) => Dispatcher.Invoke(Localize);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += (_, _) => Localize();
    }

    private void Localize()
    {
        if (Heading is null)
        {
            return;
        }

        Heading.Text = LocalizationService.Get("ServerTab.Content");

        // Word the subheading for the game in front of the person, without promising dates.
        Subheading.Text = _context.Source?.Game switch
        {
            GameType.Minecraft => LocalizationService.Get("Content.MinecraftSubtitle"),
            GameType.Palworld => LocalizationService.Get("Content.PalworldSubtitle"),
            _ => LocalizationService.Get("Content.Subtitle")
        };

        StateTitle.Text = LocalizationService.Get("Content.ComingTitle");
        StateMessage.Text = LocalizationService.Get("Content.ComingMessage");
    }
}
