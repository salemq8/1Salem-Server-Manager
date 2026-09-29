using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ServerManager.Connect.UiReview;

/// <summary>Renders windows to PNG and records what a keyboard user reaches, in order.</summary>
internal sealed class Report(string outputRoot, string mode, string culture)
{
    private readonly string _directory = Path.Combine(outputRoot, mode + "-" + culture);
    private readonly List<object> _screens = [];
    private readonly List<string> _notes = [];

    public int Failures { get; private set; }

    public void Note(string text)
    {
        _notes.Add(text);
        Console.WriteLine(text);
    }

    public void Check(bool passed, string text)
    {
        if (!passed) Failures++;
        Note((passed ? "PASS " : "FAIL ") + text);
    }

    public static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
    }

    public static async Task SettleAsync(int milliseconds = 900)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    public async Task CaptureAsync(string name, Window window, bool keyboard = true)
    {
        await SettleAsync();
        window.UpdateLayout();
        Directory.CreateDirectory(_directory);
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(width, 1), Math.Max(height, 1), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var png = Path.Combine(_directory, name + ".png");
        await using (var stream = File.Create(png)) encoder.Save(stream);

        var order = keyboard ? await TabOrderAsync(window) : [];
        var clipped = Descendants(window).OfType<FrameworkElement>()
            .Where(element => element.IsVisible && element is TextBlock or ButtonBase or TextBox &&
                              LayoutInformation.GetLayoutClip(element) is not null &&
                              element.DesiredSize.Width > element.RenderSize.Width + 1)
            .Select(Describe).ToArray();
        var unnamed = order.Where(entry => entry.Name.Length == 0).Select(entry => entry.Control).ToArray();
        _screens.Add(new { name, png = Path.GetFileName(png), width, height, flowDirection = window.FlowDirection.ToString(), tabOrder = order, clipped, unnamed });
        Note($"captured {name} ({width}x{height}, {window.FlowDirection}, UI culture {System.Globalization.CultureInfo.CurrentUICulture.Name}); {order.Length} tab stops; {clipped.Length} clipped; {unnamed.Length} unnamed");
    }

    public void Write()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "report.json"), JsonSerializer.Serialize(
            new { mode, culture, failures = Failures, notes = _notes, screens = _screens }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The real Tab traversal: focus the first stop and move Next until it cycles.</summary>
    private static async Task<TabStop[]> TabOrderAsync(Window window)
    {
        window.Activate();
        await Dispatcher.Yield(DispatcherPriority.Input);
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        var stops = new List<TabStop>();
        var seen = new HashSet<IInputElement>();
        for (var step = 0; step < 80 && Keyboard.FocusedElement is UIElement focused && IsWithin(window, focused) && seen.Add(focused); step++)
        {
            stops.Add(Stop(focused));
            focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            await Dispatcher.Yield(DispatcherPriority.Input);
        }

        return [.. stops];
    }

    private static bool IsWithin(DependencyObject root, DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, root)) return true;
        return false;
    }

    private static TabStop Stop(UIElement element)
    {
        var name = AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(name) && element is ContentControl { Content: string content }) name = content;
        if (string.IsNullOrWhiteSpace(name) && AutomationProperties.GetLabeledBy(element) is TextBlock label) name = label.Text;
        return new TabStop(element.GetType().Name + ((element as FrameworkElement)?.Name is { Length: > 0 } id ? "#" + id : string.Empty), name ?? string.Empty);
    }

    private static string Describe(FrameworkElement element) => element.GetType().Name +
        (element.Name.Length > 0 ? "#" + element.Name : string.Empty) + ": " + element switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            TextBox box => box.Text,
            _ => string.Empty
        };

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    internal sealed record TabStop(string Control, string Name);
}
