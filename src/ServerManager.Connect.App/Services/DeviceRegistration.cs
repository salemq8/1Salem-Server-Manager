using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// Registers this device's public key with the broker once per run, before the first signed
/// call that needs it. Registration is idempotent on the broker side (the id derives from the
/// key), so repeating it after a failed attempt is always safe.
/// </summary>
public sealed class DeviceRegistration
{
    private readonly IBrokerClient _broker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _registered;

    public DeviceRegistration(IBrokerClient broker)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registered)
            {
                await _broker.RegisterDeviceAsync(cancellationToken).ConfigureAwait(false);
                _registered = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
