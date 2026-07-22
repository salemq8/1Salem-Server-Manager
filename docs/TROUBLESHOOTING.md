# Troubleshooting

## Client shows “Agent unavailable”

What failed: the local named pipe is unavailable.

Likely reasons: the Agent service is stopped, the Agent failed database
initialization, or Client/Agent versions differ.

Fix:

1. Open the Administrator shortcut.
2. Select **Admin Tools > Query Status** and then **Start**.
3. Review `C:\ProgramData\1SalemServerManager\logs\agent.jsonl`.
4. Retry **Refresh**.

## Java missing

Minecraft 1.21.x requires Java 21. Install a supported x64 Java 21 runtime,
restart the dashboard, and retry. The installer does not substitute an
unverified Java executable.

## SteamCMD or Palworld install fails

Confirm HTTPS access to Steam, sufficient disk space, and write permission to
the chosen server root. App-managed SteamCMD is isolated under the Agent data
root. The failed staging folder is cleaned; an existing server folder is not
overwritten.

## Port in use

Stop or reconfigure the process using the selected port. Defaults are:

- Agent local HTTP: TCP 5251 (loopback only)
- Agent LAN HTTPS: TCP 5252
- Minecraft: TCP 25565
- Palworld: UDP 8211

Do not assign the same port to two listeners.

## LAN pairing fails

- Confirm the Agent started with `--lan`.
- Confirm TCP 5252 is allowed on a Private network.
- Generate a new code; codes expire after five minutes and are single-use.
- Wait one minute after repeated failures.
- Verify the IPv4 address in **Network Status**.
- If the pinned fingerprint changed, do not bypass the warning. Verify the
  Agent certificate was deliberately rotated, revoke the old client, and pair
  again.

## Backup is corrupt

Do not restore it. Select **Verify** to see the manifest/hash failure. Choose a
different completed backup. The restore workflow refuses corrupt archives
before replacing current data.

## Update or restore failed

Open the game control window and review the bounded log. Minecraft updates
attempt JAR rollback; restore attempts data rollback; Palworld update failure
keeps the server stopped so save/config recovery remains explicit.

## Low memory warning

Open **Resources** and lower the maximum server budget or use **One Game at a
Time**. Keep the Windows reserve. The default critical-memory action is warning
only.

## Diagnostic report

Open **About > Diagnostics > Export Diagnostic Report**. The ZIP includes
system/app details and a redacted Agent log tail. It excludes the database,
game files, passwords, and tokens.
