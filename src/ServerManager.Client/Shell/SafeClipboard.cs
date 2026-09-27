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
                    // Windows expects raw serialized DWORDs, not managed Int32 objects.
                    using var history = new System.IO.MemoryStream(new byte[sizeof(uint)]);
                    using var cloud = new System.IO.MemoryStream(new byte[sizeof(uint)]);
                    data.SetData("CanIncludeInClipboardHistory", history, autoConvert: false);
                    data.SetData("CanUploadToCloudClipboard", cloud, autoConvert: false);
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
