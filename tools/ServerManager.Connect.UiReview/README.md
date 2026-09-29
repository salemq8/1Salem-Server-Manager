# 1Salem Connect UI review renderer

Developer-only. Not in the solution and never shipped.

It renders the real 1Salem Connect screens to PNG in any UI culture, records each screen's actual
Tab traversal with the accessible name of every stop, flags text the layout clipped, and checks
the real Windows clipboard for the two copy actions. One process per application, because WPF
allows one `Application` per process:

- **owner**: the Server Manager Connect windows (setup, the Minecraft Connect card in Server
  Settings > Network, invite, friends, revoke). They talk only to an in-process fake Agent on a
  free loopback port (`ONE_SALEM_AGENT_URL`, `ONE_SALEM_AGENT_DATA_ROOT` and
  `ONE_SALEM_AGENT_PIPE` are pointed at it before any client code runs).
- **friend**: the 1Salem Connect pages, driven through the real view models against the friend-app
  test fakes (`tests/ServerManager.Connect.App.Tests/Fakes`).

Neither application's real startup runs: both `App` classes are subclassed with an empty
`OnStartup`, and their `App.xaml` resources are loaded from source. Nothing reads or writes the
user's preferences, identity, transport or any installed Agent. The clipboard checks save and
restore the text that was on the clipboard.

```powershell
$env:DOTNET_ROOT = (Resolve-Path .tools\dotnet).Path
.\.tools\dotnet\dotnet.exe build tools\ServerManager.Connect.UiReview\ServerManager.Connect.UiReview.csproj -c Debug
$exe = 'tools\ServerManager.Connect.UiReview\bin\Debug\net8.0-windows\ServerManager.Connect.UiReview.exe'
foreach ($mode in 'owner', 'friend') { foreach ($culture in 'en-US', 'ar-SA') {
    Start-Process $exe -ArgumentList $mode, 'C:\path\to\output', $culture -Wait } }
```

Each run writes `<output>\<mode>-<culture>\*.png` and `report.json` (screens, tab order, unnamed
and clipped controls, check results). Screens are rendered at their window size, so a strip the
size of the window frame is blank at the bottom.
