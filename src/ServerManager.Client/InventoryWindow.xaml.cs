using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Automation;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;
using TextBlock = System.Windows.Controls.TextBlock;
using static ServerManager.Client.Controls.MinecraftInventoryPresentation;

namespace ServerManager.Client;

/// <summary>Read-only inventory. Clicking a slot only selects its inert metadata text.</summary>
public partial class InventoryWindow : Window
{
    private readonly Guid _serverId;
    private readonly Guid _playerUuid;
    private readonly HttpClient _http = ServerDetailContext.Shared.CreateClient(TimeSpan.FromSeconds(20));
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    private InventoryWindow(Guid serverId, Guid playerUuid, string displayName)
    {
        _serverId = serverId;
        _playerUuid = playerUuid;
        InitializeComponent();
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        Title = TitleText.Text = Text("Inventory — read-only", "المخزون — للقراءة فقط");
        PlayerText.Text = $"{displayName} · {playerUuid:D}";
        ArmorHeading.Text = Text("Armor", "الدروع");
        OffhandHeading.Text = Text("Offhand", "اليد الثانوية");
        ReadOnlyHeading.Text = Text("A view, never an editor", "عرض فقط دون تعديل");
        ReadOnlyText.Text = Text("Select a slot to inspect its real item data. Nothing here moves, replaces or deletes items.", "اختر خانة لعرض بيانات العنصر الحقيقية. لا يتم نقل العناصر أو استبدالها أو حذفها هنا.");
        MainHeading.Text = Text("Inventory · 27 slots", "المخزون · ٢٧ خانة");
        HotbarHeading.Text = Text("Hotbar · 9 slots", "الشريط السريع · ٩ خانات");
        DetailsHeading.Text = Text("Item details", "تفاصيل العنصر");
        RefreshButton.Content = Text("Refresh", "تحديث");
        CloseButton.Content = Text("Close", "إغلاق");
        RenderSlots([]);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _http.Dispose(); _lifetime.Dispose(); };
    }

    public static void Open(Window? owner, Guid serverId, Guid playerUuid, string displayName)
    {
        var window = new InventoryWindow(serverId, playerUuid, displayName) { Owner = owner };
        window.ShowDialog();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        try
        {
            SourceText.Text = Text("Reading inventory…", "جارٍ قراءة المخزون…");
            var result = await _http.GetFromJsonAsync<MinecraftInventorySnapshot>(
                $"/api/v1/servers/{_serverId:D}/minecraft/players/{_playerUuid:D}/inventory", _lifetime.Token);
            if (result is null) throw new HttpRequestException("Empty inventory response.");
            SourceText.Text = SourceLabel(result.Source);
            HintText.Text = result.Source switch
            {
                MinecraftInventorySource.Live => Text("Verified from the running server just now.", "تم التحقق من الخادم قيد التشغيل الآن."),
                MinecraftInventorySource.LastSaved => Text("Read from the player's saved data. This may differ from their current live inventory. Saved: ", "تمت القراءة من بيانات اللاعب المحفوظة وقد تختلف عن المخزون المباشر. وقت الحفظ: ") + result.SavedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture),
                _ => Text("No verified inventory is available. Blank slots below do not mean the player has no items.", "لا يتوفر مخزون تم التحقق منه. الخانات الفارغة أدناه لا تعني أن اللاعب ليس لديه عناصر.")
            };
            RenderSlots(result.Items);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            if (_lifetime.IsCancellationRequested) return;
            SourceText.Text = SourceLabel(MinecraftInventorySource.Unavailable);
            HintText.Text = Text("The Agent could not read a verified inventory. Try Refresh.", "لم يتمكن الوكيل من قراءة مخزون موثوق. حاول التحديث.");
            RenderSlots([]);
        }
        finally { _busy = false; RefreshButton.IsEnabled = true; }
    }

    private void RenderSlots(IReadOnlyList<MinecraftInventoryItem> items)
    {
        DetailsText.Text = Text("Select a filled slot to inspect it.", "اختر خانة تحتوي على عنصر لعرضه.");
        Fill(MainSlots, MinecraftInventoryPresentation.MainSlots, items);
        Fill(HotbarSlots, MinecraftInventoryPresentation.HotbarSlots, items);
        Fill(ArmorPanel, ArmorSlots, items);
        Fill(OffhandPanel, [150], items);
    }

    private void Fill(Panel panel, IReadOnlyList<int> slots, IReadOnlyList<MinecraftInventoryItem> items)
    {
        panel.Children.Clear();
        foreach (var slot in slots)
        {
            var item = items.FirstOrDefault(value => value.Slot == slot);
            var button = new Button { Height = 72, Margin = new Thickness(2), Padding = new Thickness(5), BorderThickness = new Thickness(2) };
            button.SetResourceReference(BackgroundProperty, "SurfaceSunkenBrush");
            button.SetResourceReference(BorderBrushProperty, "BorderStrongBrush");
            button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var grid = new Grid();
            var name = new TextBlock { Text = item is null ? SlotName(slot) : ItemName(item), FontSize = item is null ? 10 : 11,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44, VerticalAlignment = VerticalAlignment.Top };
            name.SetResourceReference(TextBlock.ForegroundProperty, item is null ? "TextTertiaryBrush" : "TextPrimaryBrush");
            grid.Children.Add(name);
            if (item is not null)
            {
                grid.Children.Add(new TextBlock { Text = item.Count.ToString(CultureInfo.CurrentUICulture), FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
                button.Click += (_, _) => DetailsText.Text = Details(item);
                button.ToolTip = Details(item);
            }
            button.Content = grid;
            AutomationProperties.SetName(button, SlotName(slot) + (item is null ? Text(" — empty or unavailable", " — فارغة أو غير متاحة") : " — " + ItemName(item) + " × " + item.Count));
            panel.Children.Add(button);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
