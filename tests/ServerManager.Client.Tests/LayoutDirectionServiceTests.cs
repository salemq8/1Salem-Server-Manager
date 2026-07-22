using System.Globalization;
using System.Windows;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

public sealed class LayoutDirectionServiceTests
{
    [Fact]
    public void ForCulture_UsesRightToLeftForArabic()
    {
        Assert.Equal(
            FlowDirection.RightToLeft,
            LayoutDirectionService.ForCulture(CultureInfo.GetCultureInfo("ar-SA")));
    }

    [Fact]
    public void ForCulture_UsesLeftToRightForEnglish()
    {
        Assert.Equal(
            FlowDirection.LeftToRight,
            LayoutDirectionService.ForCulture(CultureInfo.GetCultureInfo("en-US")));
    }
}
