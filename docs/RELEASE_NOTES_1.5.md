# 1Salem Server Manager 1.5 — Build 8

## 1Salem Connect: private friend access (Build 8)

Friends can now join a Minecraft server privately, without a public address, port forwarding
or a VPN on their PC.

- **For the owner**, in Server Manager: set up private friend access once with your own
  Tailscale account (an OAuth client in your own tailnet; the secret is protected on this PC and
  never shown again). Then, per Minecraft server, turn **Private friend access** on, **Invite
  friend** to create a one-time invitation link or code, and approve, rename or revoke friends
  from **Friends**.
- **For the friend**, the separate **1Salem Connect** app (`1SalemConnect-1.5.zip`): paste the
  invitation, wait for the owner's approval, then **Connect**. Minecraft connects to a local
  address such as `127.0.0.1:18211`. Only that game connection goes through 1Salem Connect;
  the rest of the friend's internet traffic, routes, DNS and proxy settings are left alone.
- Access is checked twice: by the owner's tailnet policy, which lets friend devices reach only
  the Connect port, and by this app, which lets each approved friend reach only the server they
  were invited to. Friends never reach the Agent, RDP, file sharing, other servers or your LAN.
- An invitation works once, expires, and can be revoked. Revoking a friend closes their live
  connection, stops new ones, and removes their device from your tailnet when nothing else of
  theirs still uses it; the app says exactly which step is still pending otherwise.
- Invitation links are copied without entering Windows clipboard history or cloud clipboard.
- Both apps are in English and Arabic, right to left in Arabic, and reachable by keyboard and
  screen reader.

Private friend access supports Minecraft Java servers. Palworld is not supported yet.
The Connect service (broker) runs on Cloudflare workers.dev at
`https://onesalem-connect-broker-production.onesalemconnect.workers.dev`. Server Manager and the
1Salem Connect app use that address, and invitation links look like
`https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#…`.


## Content Hub (Build 7)

A server's **Content** tab now installs add-ons from Modrinth and Hangar — the sites'
own APIs, with no scraping and nothing hosted by this app.

- **Plugins** for Paper, Purpur, Spigot, Bukkit and Folia servers. Only releases that fit
  your server's Minecraft version and platform are shown, and every download is checked
  against the provider's own hash before it is installed.
- **Data packs** go into the world the server actually runs, taken from `level-name`, and
  ask for a reload rather than a restart.
- **Resource packs** are downloaded and kept until you choose **Send to players**, which
  points your server at the provider's own address with its hash. **Stop sending** clears it
  again. Nothing is hosted here and no ports are opened.
- **Modpacks** build a **new** server, never changing an existing one. You see what the pack
  would do first — the Minecraft version, the loader, how many files and how large — and a
  pack that needs an installer this app cannot run is refused by name rather than half-built.
  A build that fails leaves nothing behind.
- **Installed** lists what is on each server with where it came from, and **Updates** offers
  a newer release only when it actually fits the server. Updating keeps the previous file so
  it can be rolled back, and removing an add-on keeps its data folder.
- Everything is in English and Arabic, works in Dark and Light, and is reachable by keyboard
  and screen reader.

Mods are not included in this release.

## Several Minecraft servers on one PC (Build 7)

- More than one Minecraft server can now be set up on the same computer. Each has its own
  page, backups, schedules, content and settings, and appears on its own card on Home and on
  the Servers page.
- Existing servers, backups, schedules and installed add-ons are carried over exactly as they
  are. Nothing is renamed, re-pointed or recreated.
- Two servers can no longer be set up for the same port by accident: the app says which
  server already uses it and offers a free one.
- A server built from a modpack tracks the pack it came from. When a newer release fits the
  same Minecraft version and loader, it is offered as an update. When it needs a different
  Minecraft version or loader, it says **Requires server migration** and is not applied,
  because that would break the world the server already has.
- Remote access still provides one tunnel per game. A second Minecraft server is reachable on
  your network, but not through the managed tunnel yet.

## A simpler, clearer app (Build 6)

The whole app has been redesigned around five places: Home, Servers, Backups, Network and
Settings. Everything a server can do now lives inside that server, and technical details
sit behind "Advanced" instead of competing for attention.

- **Home** answers "is everything OK?" at a glance, and one dropped reading no longer
  flashes a false "can't connect".
- **Servers** lists your servers and adds new ones. A server's own page has five tabs:
  Overview, Console, Backups, Content and Settings. The **Start** button on a server card
  now really starts the server.
- **Backups** shows every server's protection at once, and never calls the whole set
  healthy while one server is overdue.
- **Network** shows the address to share and whether people can reach you. Setting up
  remote access (installing and linking Playit, the public address, starting it with the
  PC, stopping it, turning it off) is one click away in **Manage Remote Access**.
- **Settings** holds language, theme, starting with Windows, updates, troubleshooting tools
  and About. Updates tell you honestly whether a check worked, and **Update Now** closes
  the app so the update can finish.
- Dark and Light themes, English and Arabic (right-to-left), keyboard navigation and screen
  readers are supported throughout.
- If 1Salem is opened without administrator rights it now says so and offers **Restart as
  Administrator**, instead of reporting that it cannot reach its background service.

## Delete Server (Build 6)

- **Delete Server** is in a server's **…** menu, on the Servers page and on the server's own
  page. It always asks first, naming the server.
- It removes the server from 1Salem Server Manager only. **No files are deleted**: the
  server folder, its worlds and its backup files stay on disk, and the confirmation shows
  where.
- A running or busy server is never deleted and never stopped for you: stop it first.

## Update reliability repair (Build 5)

Found when a real Build 4 installation failed partway through and rolled itself back. No
game server or tunnel was affected, and the installation recovered to its previous Build,
but the update could not complete:

- Installing a new Agent now waits for the Windows service to genuinely finish stopping
  before any Agent file is replaced. Previously the installer only waited for the stop
  *command* to return, which happens while the service is still shutting down and still
  has its own files open — so the update could fail with "access denied" through no fault
  of the installation.
- If the service cannot be confirmed stopped within 60 seconds, the update now stops
  cleanly before touching anything, leaving the installed Build completely intact. A
  service that is merely slow to stop no longer costs you a rollback.
- Starting the Agent back up waits for it to actually reach Running, and never issues a
  second start to a service that is already starting or already running — the condition
  that previously surfaced as a confusing "code 1056" failure.
- Recovery after a failed update now restores files first and starts the Agent afterwards.
  The previous order could start the Agent on top of files the recovery was still
  replacing.

## Palworld process re-adoption repair (Build 4)

Found during real local acceptance testing of Build 3, by comparing what the Agent
reported against what Windows actually showed — an internal accuracy fix, with no change
to the visible Version 1.5 identity:

- After the Agent restarts and re-adopts an already-running Palworld server, it now
  correctly identifies the real game process again. Previously the thin `PalServer.exe`
  launcher was reported as both the root and the game process: the game process ID
  collapsed onto the launcher's, the child count read zero, and the thread and memory
  figures described the launcher instead of the actual server. The game itself was never
  disrupted by this — only the Agent's view of it, until the next full server restart.
- The cause was that the process tree was read from the Windows Job Object the Agent
  creates when it adopts a server. A Job Object only knows the processes assigned to it,
  and a game process started before that Agent instance existed was never one of them.
  The tree is now read from real Windows parent-child process relationships instead, so it
  is correct whether the Agent started the server itself or inherited it.
- Process priority and CPU affinity now apply to every process in a re-adopted server's
  tree. A hard memory limit is enforced by Windows at the Job level, so it can only cover
  processes that actually joined the Job; the Agent now attempts to bring an inherited
  game process in, and records plainly when it could not, instead of reporting a limit as
  covering more than it does. Nothing is ever restarted just to make that membership work.
- Agent restarts still never stop a running game server or the Playit tunnel.

## Playit tunnel continuity repair (Build 3)

Found during real local Windows acceptance testing of Build 2 against a live Palworld
server and Playit tunnel, before any production Agent activation was performed — this is
an internal continuity fix, not a change to the visible Version 1.5 identity:

- A normal Agent restart (an application update, a Windows Service restart, or a crash
  and recovery) no longer stops the real Playit tunnel process. Previously, the Agent's
  own graceful shutdown unconditionally terminated a live, managed Playit process as a
  side effect — meaning every Agent-updating install could interrupt an already-working
  tunnel and change its process identity, even though the game servers it tunnels to were
  never restarted.
- On startup, the Agent now looks for and re-adopts an already-running Playit process
  before ever starting a new one — the same continuity guarantee Palworld and Minecraft
  server processes already had. Re-adoption validates the process's executable path and,
  when a prior identity was recorded, its exact start time, so a Windows-recycled process
  ID is never mistaken for the real one; when it cannot be safely confirmed, no process is
  started or stopped rather than guessing.
- The real, explicit "Stop Playit" / "Disable Playit" actions are unaffected — those still
  intentionally stop the managed process exactly as before.

## Safety and reliability repairs (Build 2)

An independent review of Build 1 and a follow-up independent re-audit of the resulting
fixes found and corrected the following, entirely internal to the Agent, Client, and
update tooling — no change to the visible Version 1.5 identity:

- Backup restore is now transactional across its full pre-restore safety-backup step,
  not only its file-swap loop: a failure creating that safety backup (for example, disk
  exhaustion) is caught, rolled back, and reported instead of leaving the server stuck in
  a "Restoring" state. Disk-space pre-checks now budget for the safety backup as well as
  the restored content.
- The Agent's local HTTP API requires authentication on every privileged endpoint,
  including from the same machine; a wrong LAN pairing credential is now correctly
  reported as 403 Forbidden rather than 401 Unauthorized.
- Concurrent operations against the same game server (start, stop, restart, backup,
  restore) are serialized through one required, shared coordinator, replacing a
  same-process fallback that could have silently masked a future wiring mistake.
- Diagnostics redaction now covers the "Copy Diagnostics" button on the game server page,
  which previously copied the server's raw last-error text and console output to the
  clipboard unredacted.
- Applying and rolling back an application update is atomic on both the forward and the
  recovery path: rollback now stages and swaps the previous Client and Agent directories
  the same way a forward update does, instead of copying files directly over the live
  directories.
- The fixed Stable AppUserModelID is now stamped onto every shortcut a normal install or
  in-app update actually produces (Desktop, Start Menu, and Administrator), not only a
  shortcut created through the Admin Tools window — this is what lets a pinned taskbar
  icon merge with the running application's taskbar button.
- A secret-storage scope repair (DPAPI, ACL) and a diagnostics-redaction pattern repair
  from Build 1 were independently re-verified against the actual committed code with no
  further defects found.
- Fixed two clean-checkout portability gaps (a test-only path collision and a hard-coded
  build output path) and one pre-existing, low-probability race condition in
  update-time dashboard-process verification, none of which affect any installed system.

## Fixed visible version policy (Build 1)

- Migrates the visible product version once from 1.3.2 to 1.5.
- Keeps the normal interface and About page at Version 1.5.
- Uses a separate monotonically increasing internal Build revision for future updates.
- Orders updates by product version, Build revision, and verified package hash.
- Rejects older Builds and requires explicit repair mode for an identical Build.

## Installed updates and rollback

- Continues to update through the permanent installed launcher without Setup.exe.
- Stores Version 1.5 Builds in build-specific installed directories behind the stable launcher.
- Preserves the immediately previous Build as the operational rollback target.
- Preserves the historical 1.3.2 release as the pre-1.5 migration fallback.
- Keeps game servers, Playit, user data, saves, backups, tunnels, and configurations outside the update transaction.

## Release output

- Uses the rolling `artifacts/release/1.5` release directory.
- Builds a validated candidate first and promotes it only after the installed update and rollback snapshot are verified.
- Adds `BUILD_REVISION` and `build-info.json` without presenting Build 1 as product version 1.5.1.
- From Build 8, also contains `1SalemConnect-1.5.zip`, the 1Salem Connect friend app, and the Agent
  includes the Connect host transport.
