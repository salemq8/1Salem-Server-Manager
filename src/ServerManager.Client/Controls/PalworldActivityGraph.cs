using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace ServerManager.Client.Controls;

public sealed class PalworldActivityGraph : FrameworkElement
{
    private IReadOnlyList<PalworldMetricSample> _samples = [];
    private string _emptyMessage = string.Empty;

    public PalworldActivityGraph()
    {
        MinHeight = 190;
        AutomationProperties.SetName(this, "Server activity history chart");
    }

    public void SetSamples(
        IReadOnlyList<PalworldMetricSample> samples,
        string emptyMessage)
    {
        _samples = samples ?? [];
        _emptyMessage = emptyMessage ?? string.Empty;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        var background = ResolveBrush("PanelAltBrush", Brushes.Transparent);
        var grid = ResolveBrush("ControlBorderBrush", Brushes.SlateGray);
        var muted = ResolveBrush("MutedTextBrush", Brushes.Gray);
        drawingContext.DrawRoundedRectangle(background, null, bounds, 8, 8);

        const double inset = 18;
        var plot = new Rect(
            inset,
            inset,
            Math.Max(1, bounds.Width - inset * 2),
            Math.Max(1, bounds.Height - inset * 2));
        var gridPen = new Pen(grid, 1) { DashStyle = DashStyles.Dot };
        for (var index = 1; index < 4; index++)
        {
            var y = plot.Top + plot.Height * index / 4d;
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (_samples.Count < 2)
        {
            var text = new FormattedText(
                _emptyMessage,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection,
                new Typeface("Segoe UI"),
                13,
                muted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, plot.Width),
                TextAlignment = TextAlignment.Center
            };
            drawingContext.DrawText(
                text,
                new Point(plot.Left, plot.Top + (plot.Height - text.Height) / 2));
            return;
        }

        var maximumPlayers = Math.Max(1, _samples.Max(sample => sample.Players ?? 0));
        DrawSeries(
            drawingContext,
            plot,
            _samples.Select(sample => sample.Players is { } players
                ? players * 100d / maximumPlayers
                : (double?)null).ToArray(),
            ResolveBrush("SuccessBrush", Brushes.MediumSeaGreen),
            2.4);
        DrawSeries(
            drawingContext,
            plot,
            _samples.Select(sample => sample.CpuPercent).ToArray(),
            ResolveBrush("AccentBrush", Brushes.DeepSkyBlue),
            1.8);
        DrawSeries(
            drawingContext,
            plot,
            _samples.Select(sample => sample.RamPercent).ToArray(),
            ResolveBrush("WarningBrush", Brushes.Goldenrod),
            1.8);
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        Rect plot,
        IReadOnlyList<double?> values,
        Brush brush,
        double thickness)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] is not { } value)
                {
                    open = false;
                    continue;
                }

                var x = plot.Left + plot.Width * index / Math.Max(1, values.Count - 1d);
                var y = plot.Bottom - plot.Height * Math.Clamp(value, 0, 100) / 100d;
                var point = new Point(x, y);
                if (!open)
                {
                    context.BeginFigure(point, false, false);
                    open = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }

    private Brush ResolveBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
