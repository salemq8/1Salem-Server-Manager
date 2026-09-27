using System.Runtime.InteropServices;

namespace ServerManager.Client.Shell;

public static class SafeClipboard
{
    public static bool TrySetText(string text)
        => TrySetData(text, excludeFromHistory: false);

    /// <summary>
    /// Copies a one-time secret while asking Windows not to place it in clipboard history or
    /// cloud clipboard. The text remains available for the immediate paste operation.
    /// </summary>
    public static bool TrySetSensitiveText(string text)
        => TrySetData(text, excludeFromHistory: true);

    private static bool TrySetData(string text, bool excludeFromHistory)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (excludeFromHistory)
                {
                    var data = new System.Windows.DataObject();
                    data.SetData(System.Windows.DataFormats.UnicodeText, text);
                    data.SetData("CanIncludeInClipboardHistory", 0);
                    data.SetData("CanUploadToCloudClipboard", 0);
                    System.Windows.Clipboard.SetDataObject(data, true);
                }
                else
                {
                    System.Windows.Clipboard.SetText(text);
                }
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
