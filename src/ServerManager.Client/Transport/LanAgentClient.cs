using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Security;

namespace ServerManager.Client.Transport;

public sealed record LanClientProfile(
    string Host,
    int Port,
    Guid ClientId,
    string ClientName,
    string CertificateFingerprint,
    string ProtectedCredential);

public sealed class LanAgentClient
{
    public async Task<PairingResult> PairAsync(
        string host,
        int port,
        string code,
        string clientName,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(host, port);
        string? observedFingerprint = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                observedFingerprint = Convert.ToHexString(
                    SHA256.HashData(certificate.GetRawCertData()));
                return true;
            }
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{port}"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var response = await client.PostAsJsonAsync(
            "/api/v1/pairing/complete",
            new PairingCompleteRequest(code, clientName),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var pairing = await response.Content.ReadFromJsonAsync<PairingResult>(
            cancellationToken: cancellationToken) ??
            throw new HttpRequestException("The Agent returned an empty pairing response.");
        if (observedFingerprint is null ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(observedFingerprint),
                Convert.FromHexString(pairing.CertificateFingerprint)))
        {
            throw new AuthenticationException(
                "The certificate fingerprint changed during pairing.");
        }

        return pairing;
    }

    public HttpClient CreateAuthenticatedClient(LanClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateEndpoint(profile.Host, profile.Port);
        var expectedFingerprint = profile.CertificateFingerprint;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedFingerprint),
                    SHA256.HashData(certificate.GetRawCertData()))
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{profile.Host}:{profile.Port}"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        var credential = new WindowsDpapiSecretStore().Unprotect(
            profile.ProtectedCredential);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static void ValidateEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Enter a valid Agent IPv4 address or host name.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }
}

public sealed class LanClientProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly ISecretStore _secretStore;

    public LanClientProfileStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "1SalemServerManager",
                "lan-client.json"),
            new WindowsDpapiSecretStore())
    {
    }

    internal LanClientProfileStore(string path, ISecretStore secretStore)
    {
        _path = Path.GetFullPath(path);
        _secretStore = secretStore;
    }

    public void Save(string host, int port, PairingResult pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var profile = new LanClientProfile(
            host.Trim(),
            port,
            pairing.ClientId,
            pairing.ClientName,
            pairing.CertificateFingerprint,
            _secretStore.Protect(pairing.ProtectedCredential));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".new";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(profile, SerializerOptions));
        File.Move(temporaryPath, _path, true);
    }

    public LanClientProfile? Load() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<LanClientProfile>(
                File.ReadAllText(_path),
                SerializerOptions)
            : null;
}
