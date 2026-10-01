# Release

## Version

Read from `VERSION`. Never duplicate the current product version in this guide.

## Required gate

```powershell
dotnet restore .\1SalemServerManager.sln
dotnet build .\1SalemServerManager.sln -c Release --no-restore
dotnet test .\1SalemServerManager.sln -c Release --no-build --no-restore
powershell -ExecutionPolicy Bypass -File .\tools\build-release.ps1
```

The release script repeats restore/build/test, publishes win-x64 Client, Agent,
Updater,
Stable launcher, and setup host, creates the setup payload, builds `Setup.exe`, creates
`Portable.zip`, `Source.zip`, and
`1SalemServerManager-Update-<VERSION>.zip`, writes the HTTPS update manifest, and
writes SHA-256 hashes.

It also builds 1Salem Connect (`1SalemConnect-Setup.exe`, `1SalemConnect-Portable.zip`) and,
through `tools\New-ConnectUpdateManifest.ps1`, `1SalemConnect-update.json`: the version, build,
release tag, and the URLs, sizes and SHA-256 of both Connect files in this release. Installed
copies of 1Salem Connect read it from the latest GitHub release to update themselves, so no
release needs a change in the Connect app.

## Publishing

After the installed update succeeded and `tools\promote-release.ps1` promoted the candidate,
push `main` and the tag `v<VERSION>-build-<N>`, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-github-release.ps1
```

It uploads exactly the files listed in `SHA256SUMS.txt` plus `SHA256SUMS.txt` itself to a draft
release, verifies every upload against `SHA256SUMS.txt`, and only then publishes it as
"1Salem Server Manager <VERSION> — Build <N>" and marks it latest. It never changes an existing
release.

## Artifact verification

From `artifacts\release\<VERSION>`:

```powershell
Get-FileHash .\Setup.exe -Algorithm SHA256
Get-FileHash .\Portable.zip -Algorithm SHA256
Get-FileHash .\Source.zip -Algorithm SHA256
Get-FileHash .\1SalemServerManager-Update-<VERSION>.zip -Algorithm SHA256
```

Compare the output with `SHA256SUMS.txt`.

## Manual validation checklist

- Fresh Windows 11 x64.
- Setup All-in-One, Client-only, and Agent-only modes.
- Normal and Administrator shortcuts/UAC.
- Agent service startup after reboot.
- Minecraft install with Java absent/present, start/stop/restart/console.
- Palworld SteamCMD install, protected passwords, start/stop/restart.
- Copy/Move/Manage-in-place imports against disposable fixtures.
- Backup, verify, corrupt rejection, restore, and rollback.
- Application update from the previous release, Agent restart/health check,
  ProgramData preservation, and simulated rollback in an isolated install.
- Existing official Playit detection, hidden launch, linked/verified state,
  duplicate prevention, restart recovery, and both game tunnel targets.
- Resource profile application while one and both games run.
- LAN pairing, revoke, certificate mismatch, IP change, and Private/Public
  network profile warning.
- English, Arabic RTL, dark/light, high contrast, keyboard navigation.
- Tray minimize/close behavior.
- Upgrade and uninstall preservation of ProgramData and game roots.

Do not publish if automated validation fails. Release binaries should be code
signed before broad distribution; this source build does not claim a publisher
signature.
