namespace ServerManager.Client.Shell;

public enum ClientLaunchMode
{
    Normal = 0,
    Administrator = 1
}

public static class ClientLaunchModeParser
{
    public static ClientLaunchMode Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(argument =>
            argument.Equals("--admin", StringComparison.OrdinalIgnoreCase))
                ? ClientLaunchMode.Administrator
                : ClientLaunchMode.Normal;
    }
}

