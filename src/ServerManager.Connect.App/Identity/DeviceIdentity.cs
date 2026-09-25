using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.App.Identity;

/// <summary>
/// The device identity backed by Connect.Core: DPAPI-protected under
/// <c>%LOCALAPPDATA%\1Salem Connect\identity</c>. A saved identity that cannot be opened raises
/// <see cref="ConnectIdentityUnavailableException"/> from <see cref="LoadOrCreateDefault"/> and
/// is never replaced here (§16: re-pair, never treat it as corruption).
/// </summary>
public sealed class DeviceIdentity : IDeviceIdentity, IDisposable
{
    private readonly ConnectIdentity _identity;
    private readonly SignedRequestSigner _signer;

    public DeviceIdentity(ConnectIdentity identity, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != ConnectIdentityKind.Device)
        {
            throw new ArgumentException("1Salem Connect signs with a device identity.", nameof(identity));
        }

        _identity = identity;
        _signer = new SignedRequestSigner(identity, clock);
    }

    public string DeviceId => _identity.KeyId;

    public string PublicKeySpkiBase64Url => _identity.PublicKeySpkiBase64Url;

    public static DeviceIdentity LoadOrCreateDefault() =>
        new(
            new ConnectIdentityStore(ConnectIdentityStore.DefaultDeviceDirectory(), ConnectIdentityKind.Device).LoadOrCreate(),
            TimeProvider.System);

    public Task SignAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _signer.SignAsync(request, cancellationToken);

    public EnrollmentSecret OpenEnrollment(string ciphertext, string membershipId, string ownerId)
    {
        EnrollmentBinding binding;
        try
        {
            binding = new EnrollmentBinding(membershipId, _identity.KeyId, ownerId);
        }
        catch (ArgumentException exception)
        {
            // A malformed id from the broker is one more way the blob is not for us; it fails the
            // same way as a wrong key so the cases stay indistinguishable.
            throw new EnrollmentDecryptionException(exception);
        }

        return EnrollmentCrypto.Decrypt(EnrollmentEnvelope.Parse(ciphertext), _identity, binding);
    }

    public void Dispose() => _identity.Dispose();
}
