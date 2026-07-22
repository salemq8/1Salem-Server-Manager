using ServerManager.Contracts;
using ServerManager.Infrastructure.Playit;

namespace ServerManager.Agent;

public sealed class PlayitStartupGate
{
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    public void MarkReady() => _ready.TrySetResult();
}

public sealed class PlayitRecoveryHostedService(
    OfficialPlayitSupervisor supervisor,
    PlayitStartupGate startupGate,
    ILogger<PlayitRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await supervisor.InitializeAsync(stoppingToken);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await supervisor.GetStatusAsync(stoppingToken);
                if (!status.IsEnabled ||
                    status.State is PlayitRuntimeState.Online or
                        PlayitRuntimeState.RunningExternally or
                        PlayitRuntimeState.NotInstalled or
                        PlayitRuntimeState.Error)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Playit startup was unavailable; local game management will continue.");
        }
        finally
        {
            startupGate.MarkReady();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await supervisor.RecoverIfNeededAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Playit automatic recovery check failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await supervisor.StopAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Playit did not stop cleanly with the Agent.");
        }

        await base.StopAsync(cancellationToken);
    }
}
