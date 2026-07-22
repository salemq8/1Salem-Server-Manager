using ServerManager.Contracts;

namespace ServerManager.Agent;

public sealed record AgentOptions(
    string DataRoot,
    string PipeName,
    string ApiUrl,
    bool LanEnabled = false,
    int LanPort = 5252,
    bool TrustLocalhost = true)
{
    public static AgentOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "1SalemServerManager");
        var lanEnabled = false;
        var lanPort = 5252;
        var trustLocalhost = true;
        var pipeName = AgentTransportDefaults.ResolvePipeName();
        var apiUrl = AgentTransportDefaults.ResolveLoopbackApiUrl();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
            {
                dataRoot = argument["--data-root=".Length..].Trim('"');
            }
            else if (argument.Equals("--data-root", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--data-root requires a path.", nameof(args));
                }

                dataRoot = args[++index];
            }
            else if (argument.Equals("--lan", StringComparison.OrdinalIgnoreCase))
            {
                lanEnabled = true;
            }
            else if (argument.StartsWith("--lan-port=", StringComparison.OrdinalIgnoreCase))
            {
                lanPort = ParsePort(argument["--lan-port=".Length..]);
            }
            else if (argument.Equals("--lan-port", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--lan-port requires a port.", nameof(args));
                }

                lanPort = ParsePort(args[++index]);
            }
            else if (argument.Equals(
                         "--lan-no-localhost-trust",
                         StringComparison.OrdinalIgnoreCase))
            {
                trustLocalhost = false;
            }
            else if (argument.StartsWith("--api-url=", StringComparison.OrdinalIgnoreCase))
            {
                apiUrl = ValidateLoopbackUrl(argument["--api-url=".Length..]);
            }
            else if (argument.Equals("--api-url", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--api-url requires a URL.", nameof(args));
                }

                apiUrl = ValidateLoopbackUrl(args[++index]);
            }
            else if (argument.StartsWith("--pipe-name=", StringComparison.OrdinalIgnoreCase))
            {
                pipeName = ValidatePipeName(argument["--pipe-name=".Length..]);
            }
            else if (argument.Equals("--pipe-name", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--pipe-name requires a value.", nameof(args));
                }

                pipeName = ValidatePipeName(args[++index]);
            }
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("The Agent data root cannot be empty.", nameof(args));
        }

        return new AgentOptions(
            Path.GetFullPath(dataRoot),
            pipeName,
            apiUrl,
            lanEnabled,
            lanPort,
            trustLocalhost);
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                "LAN port must be between 1 and 65535.");

    private static string ValidateLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host is not ("127.0.0.1" or "localhost"))
        {
            throw new ArgumentException(
                "The API URL must use HTTP on localhost or 127.0.0.1.",
                nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string ValidatePipeName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 200 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? value.Trim()
            : throw new ArgumentException("The named-pipe name is invalid.", nameof(value));
}
