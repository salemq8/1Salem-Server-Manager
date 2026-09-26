using System.Net;
using System.Text.RegularExpressions;
using ServerManager.Infrastructure.Connect;

namespace TsnetSmoke;

/// <summary>
/// The driver's arguments. The broker must be on a loopback address, as for ConnectProof. Without
/// <c>--live</c> nothing else is required to exist and the driver stops after parsing; with it,
/// the stored OAuth client and the three executables must be there, or the run is refused before
/// anything starts.
/// </summary>
internal sealed partial record SmokeOptions(
    Uri Broker,
    string HostTransport,
    string FriendTransport,
    string Probe,
    string WorkDirectory,
    string ResultPath,
    string CredentialPath,
    string HostTag,
    string ClientTag,
    bool Live)
{
    /// <summary>The host tag of the production design. The owner may choose another name for the smoke test.</summary>
    public const string DefaultHostTag = "tag:onesalem-host";

    public static SmokeOptions Parse(string[] args)
    {
        string? Optional(string name)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"{name} needs a value.");
        }

        string Value(string name) => Optional(name) ?? throw new ArgumentException($"{name} is required.");

        if (!Uri.TryCreate(Value("--broker"), UriKind.Absolute, out var broker) ||
            !IPAddress.TryParse(broker.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The smoke driver only talks to a broker on a loopback address, such as http://127.0.0.1:8798.");
        }

        var options = new SmokeOptions(
            broker,
            Path.GetFullPath(Value("--host-transport")),
            Path.GetFullPath(Value("--friend-transport")),
            Path.GetFullPath(Value("--probe")),
            Path.GetFullPath(Value("--work")),
            Path.GetFullPath(Value("--result")),
            Path.GetFullPath(Value("--credential")),
            Optional("--host-tag") ?? DefaultHostTag,
            Optional("--client-tag") ?? TailscaleApiProvisioner.FriendTag,
            args.Contains("--live"));
        options.CheckTags();
        if (!options.Live)
        {
            return options;
        }

        if (!File.Exists(options.CredentialPath))
        {
            throw new ArgumentException(
                $"--live needs the OAuth client stored by Set-TsnetSmokeCredential.ps1, and there is none at {options.CredentialPath}.");
        }

        foreach (var executable in new[] { options.HostTransport, options.FriendTransport, options.Probe })
        {
            if (!File.Exists(executable))
            {
                throw new ArgumentException($"{executable} does not exist.");
            }
        }

        return options;
    }

    /// <summary>
    /// tailcfg.CheckTag (tailscale.com v1.102.4) refuses a tag whose name does not start with a
    /// letter, and the policy editor may do the same, so such a name gets a warning. The run can
    /// still try it: whether the control plane enforces the rule is not documented.
    /// </summary>
    public IEnumerable<string> Warnings =>
        new[] { HostTag, ClientTag }
            .Where(tag => !char.IsAsciiLetter(tag["tag:".Length]))
            .Select(tag => $"{tag} does not start with a letter after \"tag:\". tailcfg.CheckTag refuses such names and the tailnet policy editor may refuse them too; rename both tags before the live run if it does.");

    /// <summary>
    /// The friend and probe keys come from the production provisioner, which mints exactly
    /// <see cref="TailscaleApiProvisioner.FriendTag"/>. A different client tag would test a tag the
    /// production code never uses, so it is refused: renaming it is a separate, approved change to
    /// the production code, made before a live run.
    /// </summary>
    private void CheckTags()
    {
        foreach (var (name, tag) in new[] { ("--host-tag", HostTag), ("--client-tag", ClientTag) })
        {
            if (!TagPattern().IsMatch(tag))
            {
                throw new ArgumentException($"{name} must look like tag:<letters, digits and hyphens>.");
            }
        }

        if (ClientTag != TailscaleApiProvisioner.FriendTag)
        {
            throw new ArgumentException(
                $"--client-tag {ClientTag} differs from {TailscaleApiProvisioner.FriendTag}, the only tag the production provisioner (TailscaleApiProvisioner.FriendTag) mints for friends. " +
                "Renaming it is a separate, approved production change, made before the live run.");
        }

        if (HostTag == ClientTag)
        {
            throw new ArgumentException("--host-tag and --client-tag must differ: the tailnet policy lets the client tag reach the host tag on the bridge port only.");
        }
    }

    [GeneratedRegex("^tag:[A-Za-z0-9-]{1,100}$")]
    private static partial Regex TagPattern();
}
