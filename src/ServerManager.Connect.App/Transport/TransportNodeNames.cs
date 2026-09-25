using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Names the app gives the transport. There is one node, and so one tsnet state directory, per
/// owner tailnet (§7): tsnet ignores a new auth key once a directory has state, so two owners must
/// never share a node. The owner id already fits the transport's node rule (<c>[A-Za-z0-9_-]{1,64}</c>).
/// </summary>
public static class TransportNodeNames
{
    private const string HostnamePrefix = "1salem-";

    public static string ForOwner(string ownerId) =>
        ConnectKeyIds.IsOwnerId(ownerId)
            ? ownerId
            : throw new ArgumentException("Expected an own_ owner id.", nameof(ownerId));

    /// <summary>
    /// What the owner sees for this friend in their device list: stable for this device and
    /// free of the Windows user or PC name. A lower-case DNS label, as the transport requires.
    /// </summary>
    public static string Hostname(string deviceId) =>
        ConnectKeyIds.IsDeviceId(deviceId)
            ? HostnamePrefix + deviceId[ConnectKeyIds.DevicePrefix.Length..].ToLowerInvariant()
            : throw new ArgumentException("Expected a dev_ device id.", nameof(deviceId));
}
