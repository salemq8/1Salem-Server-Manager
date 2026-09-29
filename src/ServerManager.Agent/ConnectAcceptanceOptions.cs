using Microsoft.Extensions.DependencyInjection.Extensions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Agent;

/// <summary>Explicit, Debug-only isolation for the production Agent-hosted Connect acceptance.</summary>
public sealed record ConnectAcceptanceOptions(
    Uri BrokerOrigin,
    string AuthorizationPipeName,
    string ControlPipeName,
    string TransportExecutablePath)
{
    private const string PipePrefix = "1Salem.Connect.Acceptance.";

    internal static AgentOptions Parse(IReadOnlyList<string> args)
    {
#if !DEBUG
        throw new ArgumentException("Connect acceptance mode is available only in a Debug Agent build.", nameof(args));
#else
        string[] required = ["--data-root", "--api-url", "--pipe-name", "--connect-broker-url",
            "--connect-authz-pipe", "--connect-control-pipe", "--connect-transport"];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enabled = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals("--connect-acceptance", StringComparison.OrdinalIgnoreCase))
            {
                if (enabled) throw new ArgumentException("Duplicate Connect acceptance flag.", nameof(args));
                enabled = true;
                continue;
            }

            var equals = argument.IndexOf('=');
            var name = equals < 0 ? argument : argument[..equals];
            if (!required.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unknown or unsafe Connect acceptance option: " + name, nameof(args));
            }

            var value = equals >= 0 ? argument[(equals + 1)..] :
                index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++index] : string.Empty;
            if (string.IsNullOrWhiteSpace(value) || !values.TryAdd(name, value))
            {
                throw new ArgumentException("Missing or duplicate Connect acceptance option: " + name, nameof(args));
            }
        }

        if (!enabled || required.Any(name => !values.ContainsKey(name)))
        {
            throw new ArgumentException("Connect acceptance requires its explicit flag and every isolation option.", nameof(args));
        }

        var dataRoot = ValidateDataRoot(values["--data-root"]);
        var api = ValidateLoopbackOrigin(values["--api-url"]);
        var broker = ValidateLoopbackOrigin(values["--connect-broker-url"]);
        if (api.Port == broker.Port) throw new ArgumentException("Acceptance Agent and broker ports must differ.", nameof(args));
        var pipes = new[] { values["--pipe-name"], values["--connect-authz-pipe"], values["--connect-control-pipe"] };
        if (pipes.Any(pipe => !pipe.StartsWith(PipePrefix, StringComparison.Ordinal) ||
                pipe.Length <= PipePrefix.Length || pipe.Length > 200 ||
                !pipe.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')) ||
            pipes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pipes.Length)
        {
            throw new ArgumentException("Acceptance requires three distinct, acceptance-prefixed pipe names.", nameof(args));
        }

        var executable = values["--connect-transport"];
        ValidatePlainLocalPath(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("1Salem.Connect.Host.Transport.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Acceptance requires the absolute, existing host transport executable.", nameof(args));
        }

        executable = Path.GetFullPath(executable);
        RejectReparsePoints(executable);
        return new AgentOptions(dataRoot, pipes[0], api.GetLeftPart(UriPartial.Authority), ConnectAcceptance:
            new ConnectAcceptanceOptions(broker, pipes[1], pipes[2], executable));
#endif
    }

    /// <summary>Keep only the two actual production services needed by this isolated host.</summary>
    public static void ConfigureHostedServices(IServiceCollection services)
    {
        services.RemoveAll<IHostedService>();
        services.AddHostedService<ConnectHostService>();
        services.AddHostedService<NamedPipeAgentServer>();
    }

    public static async Task InitializeDatabaseAsync(IServiceProvider services, string dataRoot, CancellationToken cancellationToken)
    {
        ValidateDataRoot(dataRoot);
        await services.GetRequiredService<IApplicationDatabase>().InitializeAsync(cancellationToken);
        var servers = await services.GetRequiredService<IGameServerStore>().ListAsync(cancellationToken);
        foreach (var server in servers)
        {
            if (server.Game != GameType.Minecraft || !Path.IsPathFullyQualified(server.RootPath) ||
                !IsWithin(Path.GetFullPath(server.RootPath), dataRoot))
            {
                throw new InvalidOperationException("Acceptance may contain only Minecraft test servers beneath its own data root.");
            }

            RejectReparsePoints(server.RootPath);
        }

        services.GetRequiredService<AgentRuntimeState>().MarkDatabaseReady();
    }

    internal static string ValidateDataRoot(string value)
    {
        ValidatePlainLocalPath(value);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var productionRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1SalemServerManager"),
            Environment.GetEnvironmentVariable("ONE_SALEM_AGENT_DATA_ROOT")
        };
        foreach (var production in productionRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(production!));
            if (root.Equals(path, StringComparison.OrdinalIgnoreCase) || IsWithin(root, path) || IsWithin(path, root))
            {
                throw new ArgumentException("Acceptance data root must not overlap a production Agent data root.", nameof(value));
            }
        }

        RejectReparsePoints(root);
        // Before certificate, credential, logging, or SQLite startup can touch any child path,
        // reject links within a preseeded/restarted acceptance directory as well as its parents.
        if (Directory.Exists(root))
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ArgumentException("Acceptance data root must not contain links or junctions.", nameof(value));
                    }

                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            }
        }

        return root;
    }

    private static void ValidatePlainLocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' ||
            value[2] is not ('\\' or '/') || value[3..].Split(['\\', '/']).Any(segment =>
                segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':') || segment.Contains('~')))
        {
            throw new ArgumentException("Acceptance paths must be absolute local paths without aliases or alternate streams.", nameof(value));
        }
    }

    private static Uri ValidateLoopbackOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttp ||
            origin.Host != "127.0.0.1" || origin.Port is < 1024 or 5251 or 5252 ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            throw new ArgumentException("Acceptance endpoints require an explicit nonproduction HTTP port on 127.0.0.1.", nameof(value));
        }

        return origin;
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Acceptance paths must not contain links or junctions.", nameof(path));
            }
        }
    }
}
