namespace ServerManager.Contracts;

public sealed record PairingCompleteRequest(
    string Code,
    string ClientName);

public sealed record PairingRenameRequest(string Name);

public sealed record PairedClientResponse(
    Guid Id,
    string Name,
    string CertificateFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? RevokedAtUtc);
