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
        // Agent host shutdown -- a binary update, a Windows Service restart, or a crash -- must
        // never imply "stop Playit". Only an explicit user/admin action (the real Stop Playit
        // button, calling OfficialPlayitSupervisor.StopAsync directly) may terminate the real
        // process. This deliberately does NOT stop it: the OS process keeps running
        // independently of this Agent instance's own lifetime, and the next Agent instance's
        // PlayitRecoveryHostedService.ExecuteAsync re-adopts it via TryAdoptExistingAsync
        // instead of starting a new one. Previously this called supervisor.StopAsync(), which
        // genuinely killed a live tunnel on every graceful Agent restart -- a real
        // production-safety defect fixed in Version 1.5 Build 3.
        supervisor.ReleaseWithoutStopping();
        await base.StopAsync(cancellationToken);
    }
}
