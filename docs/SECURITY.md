# Security

## Defaults

- HTTP is loopback-only at `127.0.0.1:5251`.
- LAN HTTPS is disabled unless `--lan` is supplied.
- Firewall rules are opt-in and restricted to Private profiles.
- No public tunnel, public-IP discovery, router automation, or port forwarding.
- No arbitrary shell command endpoint.

## Secrets

Palworld passwords, the Agent certificate PFX envelope, and LAN client
credentials use Windows DPAPI. SQLite stores only high-entropy credential
hashes for paired clients. Pairing responses travel over HTTPS. Secret-like
values are redacted from SignalR log events and diagnostic exports.

## Pairing

- Cryptographically generated six-digit code.
- Five-minute expiry.
- Single use.
- Five attempts per address per minute.
- SHA-256 Agent certificate fingerprint pinning.
- Client rename, last connection, and revocation.
- Remote API and SignalR requests require a valid bearer credential.
- Localhost trust is available only when explicitly configured for the
  All-in-One scenario.

## Files and processes

Server processes use absolute executable/working-directory paths and Windows
Job Objects. Console input blocks newlines and is limited to 512 characters.
The file manager is restricted to SQLite-registered roots, rejects traversal
and reparse points, limits text size, and backs up files before write/delete.
Folder deletion is not exposed.

## Backups and updates

Backup ZIPs have archive and per-file SHA-256 verification. Restore extracts to
staging, rejects escaping archive paths, preserves the current state, and rolls
back on verification failure. Updates stop gracefully, back up, stage/verify,
restart, and report success only after verification.

## Resource safety

Realtime process priority is rejected. Profiles reserve at least 1 GiB for
Windows, never allocate all physical RAM, and default to warnings on critical
memory. High CPU alone never triggers a hard kill.

## Reporting a concern

Export a redacted diagnostic report from **About > Diagnostics**. Review it
before sharing because machine names and operational paths may still be useful
for troubleshooting and private to your environment.

## Playit and application update trust

Only the official Playit executable is launched. Server Manager never requests
Playit credentials, reimplements its protocol, scrapes its website, or calls
undocumented account APIs. Existing secret files are referenced without being
read, captured output is bounded, and secret-like values and claim links are
redacted.

Production update endpoints are compiled into the application; the UI accepts
no arbitrary URL. Manifests, release notes, and packages require HTTPS.
Packages must match the declared size and SHA-256 and may contain only safe
Client/Agent paths—never traversal, symbolic links, or executable scripts.
Current local binaries are unsigned; Authenticode state is shown and future
signature enforcement is prepared.
