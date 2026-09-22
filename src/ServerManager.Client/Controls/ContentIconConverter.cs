using System.Globalization;
using System.Net.Cache;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ServerManager.Client.Controls;

/// <summary>
/// Loads a provider's project icon without letting one broken image spoil the list. A failed
/// or malformed icon simply returns nothing and the card shows its placeholder glyph.
/// Images are cached by the platform's own HTTP cache rather than re-fetched per keystroke.
/// </summary>
public sealed class ContentIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriCachePolicy = new RequestCachePolicy(RequestCacheLevel.CacheIfAvailable);
            image.DecodePixelWidth = 96;
            image.UriSource = uri;
            image.EndInit();

            // Download and decode failures are events, not exceptions, so swallow them here
            // instead of letting them reach the dispatcher.
            image.DownloadFailed += (_, _) => { };
            image.DecodeFailed += (_, _) => { };
            return image;
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or UriFormatException
                or System.IO.IOException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
