# User Guide (English)

## First start

Install **Client + Agent** on the server PC. The Agent runs as a Windows
Service; the dashboard can close without stopping games. Use the normal
shortcut for daily work and the Administrator shortcut only for service,
firewall, startup, shortcut, or repair actions.

## Minecraft

1. Open **Minecraft**.
2. Select **Install Minecraft Server**.
3. Choose the official version, name, empty destination, port, players,
   difficulty, online mode, Xms, and Xmx.
4. Read the EULA and explicitly accept it.
5. Install. The app verifies the official server JAR before finalizing.
6. Select **Manage Minecraft** for Start, Stop, Restart, Backup, Update,
   Prioritize, folder, console, and logs.

Use **Import Existing Minecraft Server** only for an existing folder. Copy is
the default and leaves the source unchanged. Move requires exact typed
confirmation.

## Palworld

1. Open **Palworld > Install Palworld Server**.
2. Choose an empty destination, server details, passwords, players, and port.
3. App-managed SteamCMD installs official app `2394010`.
4. Passwords are stored with DPAPI and materialized only when the server starts.
5. Use **Manage Palworld** for daily operations.

## Backups

Open **Backups > Open Backup Center**. **Backup Now** defaults to safe offline:
the server stops, the ZIP and per-file hashes are created, and a previously
running server restarts. Verify before restore. Restore creates a safety backup
of current data and uses staging/rollback.

## Resources

Choose Balanced, Minecraft Priority, Palworld Priority, One Game at a Time, or
Custom. Realtime is unavailable. Keep enough Windows reserve. Optional
lower-priority stopping is off by default.

## Network and LAN

**Network Status** shows the preferred IPv4 and game addresses. LAN Agent
access is disabled by default. When enabled by an administrator, generate a
five-minute pairing code on the Agent PC and enter it on the LAN Client. Never
expose the Agent directly to the internet.

## Files and logs

The file manager can see only registered server roots. It blocks path escape
and reparse points. Saving text creates a safety copy. File deletion requires
the exact displayed `DELETE filename` text and also creates a safety copy.

Game control windows show a bounded, redacted log and accept only a single
server command up to 512 characters.

## Language, theme, and diagnostics

Open **Settings > Language and Theme**. Choose English or Arabic and Dark or
Light. Reopen the dashboard after a language change to apply navigation
direction everywhere.

Open **About > Diagnostics** to export a redacted support ZIP.

## Remote Access and application updates

Open **Remote Access** to detect the official Playit installation. Start it,
use **Link Account** only when the official claim link appears, and finish in
the browser. The existing Minecraft tunnel stays TCP
`127.0.0.1:25565`. Create a separate Palworld UDP tunnel to
`127.0.0.1:8211` using the in-app guide and official Playit portal.

Open **Updates** for Stable/Preview/Development checks, verified downloads, release notes,
history, and rollback status. Downloading does not stop games. If installation
needs an Agent restart while a game is active, approve explicitly or choose
Update Later. Ordinary verified releases install without Setup.exe.
