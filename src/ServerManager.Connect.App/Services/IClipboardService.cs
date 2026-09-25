namespace ServerManager.Connect.App.Services;

public interface IClipboardService
{
    /// <summary>False when the clipboard stayed locked by another program.</summary>
    bool TrySetText(string text);
}
