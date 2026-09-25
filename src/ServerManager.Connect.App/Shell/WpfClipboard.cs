using System.Runtime.InteropServices;
using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.Shell;

/// <summary>
/// The Windows clipboard, retried briefly: another program (a clipboard manager, a remote
/// desktop client) often holds it open for a few milliseconds.
/// </summary>
public sealed class WpfClipboard : IClipboardService
{
    private const int Attempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        return false;
    }
}
