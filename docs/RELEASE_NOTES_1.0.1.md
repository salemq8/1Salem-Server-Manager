# 1Salem Server Manager 1.0.1

Maintenance release for the native Windows x64 installer and product identity.

## Fixed

- Fixed the Windows Service installation failure caused by invalid `sc.exe`
  argument formatting.
- Added safe repair for partially installed, stopped, running, outdated, and
  deletion-pending Agent services.
- Added bounded service state waits and verification of the local Agent health
  endpoint before Setup reports success.
- Improved installer failure reporting, retry behavior, rollback, and
  per-operation logs.

## Branding

- Applied the supplied official 1Salem Server Manager falcon/server icon to the
  Client, Agent, Updater, Setup, WPF windows, taskbar, system tray, shortcuts,
  About view, installer branding, and Apps & Features registration.

## Data safety

Setup, rollback, repair, and uninstall do not delete Agent data, Minecraft
worlds, Palworld saves, server configurations, backups, SteamCMD data, or
user-selected server roots.

## Known limitations

- Windows 10/11 x64 only.
- Locally produced binaries are not publisher code-signed.
- Windows taskbar pinning still requires the normal user-initiated Windows
  action.
