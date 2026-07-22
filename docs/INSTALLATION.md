# Installation

## Setup.exe

1. Run `Setup.exe`.
2. Approve the Windows UAC prompt.
3. Choose:
   - **Client + Agent (All-in-One)** for the server PC;
   - **Client only** for another Windows PC on the LAN;
   - **Agent only** for a headless server PC.
4. Choose the application folder.
5. Select shortcuts, Agent service, Private-network firewall rules, dashboard
   startup, and launch-after-install options.
6. Select **Install**.

The installer validates the payload, free space, and target path. It writes an
ownership marker only to the application directory. Game servers, backups, and
Agent data remain outside that directory.

## Shortcuts and elevation

- **1Salem Server Manager** starts the normal non-elevated dashboard.
- **1Salem Server Manager (Administrator)** passes `--admin`; the Client then
  requests UAC and exposes service/firewall/repair tools.

Daily start/stop/backup/update operations go through the Agent and do not
require the dashboard itself to remain elevated.

## Portable.zip

Extract the ZIP to a local, writable folder. Start the Agent first:

```cmd
Agent\1Salem.ServerManager.Agent.exe --data-root "%CD%\Data"
```

Then start:

```cmd
Client\1Salem.ServerManager.exe
```

Do not use Downloads, Temp, OneDrive, or an existing game folder as a permanent
application or data root.

## LAN Agent

LAN is opt-in. Start the Agent with:

```cmd
1Salem.ServerManager.Agent.exe --data-root "C:\ProgramData\1SalemServerManager" --lan --lan-port 5252
```

Allow TCP 5252 only on a Private Windows network. On the Agent PC, open
**Network > LAN Pairing** and generate a code. On the client PC, enter the
Agent IPv4 address, HTTPS port, code, and client name.

No router port forwarding or public tunnel should be configured.

## Upgrade

Run a newer Setup.exe over the existing application folder. The Agent service
is stopped while application binaries are replaced. ProgramData, registered
game roots, saves, worlds, configs, and backups are preserved.

## Uninstall

Use **Uninstall 1Salem Server Manager** from the Start Menu. It removes:

- Client and Agent application components;
- Agent service registration;
- 1Salem Private-network firewall rules;
- dashboard startup registration;
- application shortcuts.

It intentionally preserves `C:\ProgramData\1SalemServerManager`, registered
Minecraft/Palworld roots, worlds, saves, configs, and backups. Delete preserved
data manually only after verifying that it is no longer needed.

## Normal application updates

After the first All-in-One installation, open **Updates** in the dashboard.
Choose Stable or Beta, select **Check Now**, then download and apply the
verified package. The dashboard runs the updater outside the install folder,
closes itself, restarts the Agent only when required, validates health and
version, and reopens automatically.

`Setup.exe` remains for first installation, repair, service installation, or a
release whose manifest says `requiresFullSetup: true`. Ordinary compatible
releases do not require Setup.exe.
