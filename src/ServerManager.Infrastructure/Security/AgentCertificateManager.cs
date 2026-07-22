using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Security;

public sealed record AgentCertificateIdentity(
    X509Certificate2 Certificate,
    string Sha256Fingerprint);

public static class AgentCertificateManager
{
    private const string CertificateFileName = "agent-certificate.pfx.dpapi";

    public static AgentCertificateIdentity LoadOrCreate(
        string dataRoot,
        ISecretStore secretStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(secretStore);

        var securityRoot = Path.Combine(Path.GetFullPath(dataRoot), "security");
        Directory.CreateDirectory(securityRoot);
        var path = Path.Combine(securityRoot, CertificateFileName);
        X509Certificate2 certificate;
        if (File.Exists(path))
        {
            var protectedValue = File.ReadAllText(path);
            var pfx = Convert.FromBase64String(secretStore.Unprotect(protectedValue));
            try
            {
                certificate = new X509Certificate2(
                    pfx,
                    (string?)null,
                    X509KeyStorageFlags.EphemeralKeySet |
                    X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }

            if (!certificate.HasPrivateKey ||
                certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                certificate.Dispose();
                throw new CryptographicException(
                    "The Agent HTTPS certificate is invalid or expired. " +
                    "Rotate it explicitly and re-pair LAN clients.");
            }
        }
        else
        {
            certificate = CreateCertificate();
            Persist(path, certificate, secretStore);
        }

        return new AgentCertificateIdentity(
            certificate,
            Convert.ToHexString(SHA256.HashData(certificate.RawData)));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(3072);
        var machineName = Environment.MachineName;
        var request = new CertificateRequest(
            $"CN={machineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                true));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(machineName);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var address in GetLanAddresses())
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.EphemeralKeySet |
            X509KeyStorageFlags.Exportable);
    }

    private static IReadOnlyList<IPAddress> GetLanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item =>
                item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(item.Address))
            .Select(item => item.Address)
            .Distinct()
            .ToArray();

    private static void Persist(
        string path,
        X509Certificate2 certificate,
        ISecretStore secretStore)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            var protectedValue = secretStore.Protect(Convert.ToBase64String(pfx));
            var temporaryPath = path + ".new";
            File.WriteAllText(temporaryPath, protectedValue);
            File.Move(temporaryPath, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
