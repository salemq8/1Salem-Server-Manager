using System.Globalization;
using System.Windows.Data;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ServerManager.Client.Controls;

/// <summary>
/// Resolves a <see cref="UiStatusTone"/> to a theme brush. Converting here keeps status colour
/// in the token system: no page inlines a hex, and a theme switch recolours every badge.
/// Pass ConverterParameter="Soft" for the tinted background variant.
/// </summary>
public sealed class StatusToneConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        var soft = string.Equals(parameter as string, "Soft", StringComparison.Ordinal);
        var key = value is UiStatusTone tone
            ? tone switch
            {
                UiStatusTone.Positive => soft ? "SuccessSoftBrush" : "SuccessBrush",
                UiStatusTone.Caution => soft ? "WarningSoftBrush" : "WarningBrush",
                UiStatusTone.Negative => soft ? "DangerSoftBrush" : "DangerBrush",
                _ => soft ? "SurfaceOverlayBrush" : "TextSecondaryBrush"
            }
            : soft ? "SurfaceOverlayBrush" : "TextSecondaryBrush";

        return Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Transparent;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
