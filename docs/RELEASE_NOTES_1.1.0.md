# 1Salem Server Manager 1.1.0

Version 1.1.0 turns the native shell into a connected server-management panel.

- Completed functional Minecraft server creation using official version
  metadata, verified downloads, compatible Java detection or verified automatic
  Temurin installation, and real startup/port readiness checks.
- Added real Xms/Xmx RAM controls and safe recommendations based on current
  total and available system memory.
- Added the local server IP and port to Home, server pages, the creation wizard,
  the network page, success results, and the tray.
- Reworked the dashboard with live Agent, process, console, backup, update,
  resource, player, network, and disk data.
- Added actionable server startup diagnostics with the failed stage, executable,
  working directory, Java/memory results, port state, and recent output.
- Connected visible Start, Stop, Force Stop, Restart, Console, Backup, Restore,
  Update, File, Folder, Network, Resource, and configuration actions to the
  Agent.
- Added persistence migration version 2 and recovery of registered servers and
  auto-start settings after the Agent or Windows restarts.
- Preserved the official application icon and all existing Agent data, worlds,
  saves, configurations, backups, and SteamCMD data.

The offline setup remains self-contained for Windows 10/11 x64. Its size is
primarily the result of independent self-contained .NET runtimes for the WPF
dashboard and Windows Service Agent; those copies preserve reliable independent
startup and servicing.
