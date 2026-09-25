using System.Collections;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The environment a Connect sidecar starts with (contract §11): the Agent's own, minus every
/// variable tsnet would read as an auth key, a client secret, a control server, a forced
/// re-login or a workload-identity login. Auth keys reach a sidecar only over its pipe, never
/// through its environment or command line. Windows variable names ignore case, so the match
/// does too.
/// </summary>
public static class ConnectSidecarEnvironment
{
    public static readonly IReadOnlyList<string> RemovedVariables =
    [
        "TS_AUTHKEY",
        "TS_AUTH_KEY",
        "TS_CLIENT_SECRET",
        "TS_CONTROL_URL",
        "TSNET_FORCE_LOGIN",

        // tsnet's workload-identity inputs, with which it could obtain a login by itself. The
        // contract lists only the five above; the sidecars scrub these too, and so does the
        // Agent, so the list is the same on both sides of the process boundary.
        "TS_CLIENT_ID",
        "TS_ID_TOKEN",
        "TS_AUDIENCE"
    ];

    private static readonly HashSet<string> Removed = new(RemovedVariables, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Scrub(IEnumerable<KeyValuePair<string, string>> parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parent)
        {
            if (!Removed.Contains(name))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>This process's environment, as the starting point for <see cref="Scrub"/>.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Current() =>
        Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string>((string)entry.Key, entry.Value as string ?? string.Empty));
}
