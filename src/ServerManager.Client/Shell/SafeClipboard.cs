using System.Runtime.InteropServices;

namespace ServerManager.Client.Shell;

public static class SafeClipboard
{
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt == 9)
                {
                    return false;
                }

                Thread.Sleep(50);
            }
        }

        return false;
    }
}
