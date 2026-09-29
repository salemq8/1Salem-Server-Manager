using System.Runtime.Versioning;
using System.Text.Json;

[assembly: SupportedOSPlatform("windows")]

namespace Phase2Acceptance;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AcceptanceOptions options;
        try { options = AcceptanceOptions.Parse(args); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine("REFUSED acceptance arguments (" + exception.GetType().Name + ").");
            return 2;
        }
        if (!options.Live)
        {
            Console.WriteLine("Arguments accepted. No credential opened, process launched, or API contacted without --live.");
            return 0;
        }
        try { options.VerifyPrecheckAndCredentialBoundary(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine("REFUSED acceptance precheck/credential boundary (" + exception.GetType().Name + ").");
            return 2;
        }
        Directory.CreateDirectory(options.Work);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var watch = new CancellationTokenSource();
        var watcher = WatchAsync(Path.Combine(options.Work, "stop.request"), stop, watch.Token);
        using var run = new AcceptanceRun(options, stop.Token);
        try { await run.ExecuteAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Exceptions can contain upstream response bodies. Evidence records type only.
            try { await run.CaptureFailureDiagnosticsAsync(); } catch { }
            var frames = new System.Diagnostics.StackTrace(exception, false).GetFrames();
            var location = frames?.Select(frame => frame.GetMethod()).FirstOrDefault(method =>
                method?.DeclaringType?.FullName?.StartsWith("Phase2Acceptance.", StringComparison.Ordinal) == true);
            run.Record("run completed", false, exception.GetType().Name + " at " +
                (location?.DeclaringType?.Name ?? "unknown") + "." + (location?.Name ?? "unknown"));
        }
        finally
        {
            try { await run.CleanupAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { run.Record("cleanup complete", false, exception.GetType().Name); }
            run.WriteResult();
            watch.Cancel();
            await watcher;
        }
        return run.Passed ? 0 : 1;
    }

    private static async Task WatchAsync(string path, CancellationTokenSource stop, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(path)) { stop.Cancel(); return; }
                await Task.Delay(1000, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
