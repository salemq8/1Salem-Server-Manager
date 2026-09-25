using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Connect.App.Identity;

/// <summary>
/// This Windows user's 1Salem Connect device (<c>dev_…</c>, contract §5). The private key never
/// leaves the implementation: callers get signatures and opened enrollment blobs, not key bytes.
/// </summary>
public interface IDeviceIdentity
{
    string DeviceId { get; }

    string PublicKeySpkiBase64Url { get; }

    /// <summary>Adds the X-1S-* headers over the request exactly as it will be sent.</summary>
    Task SignAsync(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an enrollment blob addressed to this device for <paramref name="membershipId"/> and
    /// <paramref name="ownerId"/>. Every failure is <see cref="EnrollmentDecryptionException"/>.
    /// </summary>
    EnrollmentSecret OpenEnrollment(string ciphertext, string membershipId, string ownerId);
}
