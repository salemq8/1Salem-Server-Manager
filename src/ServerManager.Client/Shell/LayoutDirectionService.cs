using System.Globalization;
using System.Windows;

namespace ServerManager.Client.Shell;

public static class LayoutDirectionService
{
    public static System.Windows.FlowDirection ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.IsRightToLeft
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
    }
}
