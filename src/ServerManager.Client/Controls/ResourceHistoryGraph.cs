using System.Windows;
using System.Windows.Media;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace ServerManager.Client.Controls;

public sealed class ResourceHistoryGraph : FrameworkElement
{
    private const int MaximumSamples = 150;
    private readonly Queue<(double Cpu, double Memory)> _samples = new();

    public void AddSample(double cpuPercent, double memoryPercent)
    {
        _samples.Enqueue((
            Math.Clamp(cpuPercent, 0, 100),
            Math.Clamp(memoryPercent, 0, 100)));
        while (_samples.Count > MaximumSamples)
        {
            _samples.Dequeue();
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 29, 39)),
            new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 65, 82)), 1),
            bounds,
            8,
            8);
        if (_samples.Count < 2 || ActualWidth <= 12 || ActualHeight <= 12)
        {
            return;
        }

        for (var line = 1; line < 4; line++)
        {
            var y = ActualHeight * line / 4;
            drawingContext.DrawLine(
                new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 74, 88, 107)), 1),
                new Point(8, y),
                new Point(ActualWidth - 8, y));
        }

        var samples = _samples.ToArray();
        DrawSeries(
            drawingContext,
            samples.Select(sample => sample.Cpu).ToArray(),
            System.Windows.Media.Color.FromRgb(69, 191, 147));
        DrawSeries(
            drawingContext,
            samples.Select(sample => sample.Memory).ToArray(),
            System.Windows.Media.Color.FromRgb(77, 166, 255));
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        IReadOnlyList<double> values,
        System.Windows.Media.Color color)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var x = 8 + index * (ActualWidth - 16) / Math.Max(1, MaximumSamples - 1);
                var y = 8 + (100 - values[index]) * (ActualHeight - 16) / 100;
                if (index == 0)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    context.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(
            null,
            new Pen(new SolidColorBrush(color), 2),
            geometry);
    }
}
