using System.Globalization;
using System.Windows.Data;

namespace ServerManager.Client.Controls;

/// <summary>
/// Wraps a single bound object in a one-element array so it can be used as an ItemsSource.
/// The pinned Settings entry at the bottom of the sidebar reuses the same list styling and
/// item template as the primary destinations rather than duplicating that markup.
/// </summary>
public sealed class SingleItemConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is null ? Array.Empty<object>() : new[] { value };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
