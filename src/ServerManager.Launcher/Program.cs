using System.Diagnostics;
using System.Text.Json;
using ServerManager.Contracts;

namespace ServerManager.Launcher;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The launcher itself never shows a window, but setting this before it does anything
        // else costs nothing and keeps every process in the Stable launch chain consistent.
        ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.AppUserModelId);
        try
        {
            var launcherPath = Environment.ProcessPath ??
                throw new InvalidOperationException("The launcher path is unavailable.");
            var clientDirectory = Directory.GetParent(launcherPath) ??
                throw new InvalidOperationException("The launcher directory is unavailable.");
            var installRoot = clientDirectory.Parent?.FullName ??
                throw new InvalidOperationException("The installation root is unavailable.");
            var manifestPath = Path.Combine(installRoot, "current.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    "The active-version record is missing. Use Setup.exe Repair once.");
            }

            var manifest = JsonSerializer.Deserialize<InstalledApplicationManifest>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = true
                    }) ??
                throw new InvalidDataException("The active-version record is invalid.");
            var target = StableLauncherPolicy.ResolveClientPath(installRoot, manifest);
            var start = new ProcessStartInfo
            {
                FileName = target,
                WorkingDirectory = Path.GetDirectoryName(target)!,
                UseShellExecute = true
            };
            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            _ = Process.Start(start) ??
                throw new InvalidOperationException("Windows did not start the dashboard.");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException)
        {
            System.Windows.Forms.MessageBox.Show(
                $"1Salem Server Manager could not start safely.\n\n{exception.Message}",
                ProductIdentity.ProductName,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 2;
        }
    }
}
