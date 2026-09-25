using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ServerManager.Connect.Core.Diagnostics;

/// <summary>
/// A read-only picture of the host network settings that 1Salem Connect promises never to
/// change (contract §15): adapters (including down, loopback and tunnel ones, as
/// <see cref="NetworkInterface"/> reports them), their addresses, default gateways and DNS
/// servers, the current user's WinINet proxy values, the WinHTTP proxy, and proxy environment
/// variables. Two snapshots taken before and after a Connect session must have identical
/// <see cref="Text"/>.
/// Nothing here writes. The only external command is <c>netsh winhttp show …</c>, run from
/// System32 by full path without a shell; <see cref="WinHttpQueries"/> is the complete list of
/// argument vectors, and every one is a <c>show</c>. Registry keys are opened read-only.
/// Counters, link speeds and lease times are left out on purpose: they change on their own
/// and would make an unchanged system look changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkConfigSnapshot
{
    /// <summary>
    /// <c>show advproxy</c> exists on current Windows 11 and Server builds and also covers
    /// auto-config settings. Older builds only know <c>show proxy</c>, so it is the fallback.
    /// </summary>
    internal static readonly IReadOnlyList<string[]> WinHttpQueries =
    [
        ["winhttp", "show", "advproxy"],
        ["winhttp", "show", "proxy"]
    ];

    private static readonly string[] ProxyEnvironmentVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "FTP_PROXY", "ALL_PROXY", "NO_PROXY"];

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly string[] InternetSettingsValues =
        ["ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect"];

    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(15);

    // Proxy URLs may carry credentials (http://user:password@proxy:8080). The snapshot is for
    // diffing and for Diagnostics, so the user-info part is masked before it is stored.
    private static readonly Regex UrlCredentials = new(
        @"(://)[^/@\s""]+@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private NetworkConfigSnapshot(string text)
    {
        Text = text;
    }

    /// <summary>Normalized, sorted, line-oriented text. Equal text means no observed change.</summary>
    public string Text { get; }

    public static async Task<NetworkConfigSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        AppendAdapters(builder);
        AppendInternetSettings(builder);
        builder.Append(await ReadWinHttpProxyAsync(cancellationToken).ConfigureAwait(false));
        AppendEnvironment(builder);
        return new NetworkConfigSnapshot(builder.ToString());
    }

    private static void AppendAdapters(StringBuilder builder)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(adapter => adapter.Id, StringComparer.Ordinal))
        {
            builder.Append("[adapter ").Append(adapter.Id).Append("]\n");
            AppendLine(builder, "name", adapter.Name);
            AppendLine(builder, "description", adapter.Description);
            AppendLine(builder, "type", adapter.NetworkInterfaceType.ToString());
            AppendLine(builder, "status", adapter.OperationalStatus.ToString());
            AppendLine(builder, "mac", adapter.GetPhysicalAddress().ToString());

            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (NetworkInformationException exception)
            {
                AppendLine(builder, "ip-properties", $"unavailable ({exception.ErrorCode.ToString(CultureInfo.InvariantCulture)})");
                continue;
            }

            AppendSorted(builder, "unicast", properties.UnicastAddresses.Select(address =>
                $"{address.Address}/{address.PrefixLength.ToString(CultureInfo.InvariantCulture)}"));
            AppendSorted(builder, "gateway", properties.GatewayAddresses.Select(gateway => gateway.Address.ToString()));
            AppendSorted(builder, "dns", properties.DnsAddresses.Select(address => address.ToString()));
            AppendLine(builder, "dns-suffix", properties.DnsSuffix);
        }
    }

    private static void AppendInternetSettings(StringBuilder builder)
    {
        builder.Append("[wininet HKCU]\n");
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        foreach (var name in InternetSettingsValues)
        {
            AppendLine(builder, name, key?.GetValue(name) switch
            {
                null => "(not set)",
                string text => Mask(text),
                int number => number.ToString(CultureInfo.InvariantCulture),
                byte[] bytes => Convert.ToHexString(bytes),
                var other => Mask(Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty)
            });
        }
    }

    private static async Task<string> ReadWinHttpProxyAsync(CancellationToken cancellationToken)
    {
        foreach (var arguments in WinHttpQueries)
        {
            var output = await RunNetshAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (output is not null)
            {
                var builder = new StringBuilder();
                builder.Append("[winhttp ").Append(arguments[^1]).Append("]\n");
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        builder.Append(Mask(trimmed)).Append('\n');
                    }
                }

                return builder.ToString();
            }
        }

        return "[winhttp]\nunavailable\n";
    }

    /// <summary>Null when the command is unknown on this Windows build or fails.</summary>
    private static async Task<string?> RunNetshAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("netsh could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetshTimeout);
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            await standardError.ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            // netsh only reads, so stopping it early cannot leave anything half-changed.
            process.Kill();
            throw;
        }
    }

    private static void AppendEnvironment(StringBuilder builder)
    {
        builder.Append("[environment]\n");
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var name in ProxyEnvironmentVariables)
            {
                var value = Environment.GetEnvironmentVariable(name, target);
                AppendLine(builder, $"{target}:{name}", value is null ? "(not set)" : Mask(value));
            }
        }
    }

    private static void AppendSorted(StringBuilder builder, string name, IEnumerable<string> values)
    {
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            AppendLine(builder, name, value);
        }
    }

    private static void AppendLine(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append('=').Append(value.ReplaceLineEndings(" ").Trim()).Append('\n');

    internal static string Mask(string value) => UrlCredentials.Replace(value, "$1" + SecretRedactor.Marker + "@");
}
