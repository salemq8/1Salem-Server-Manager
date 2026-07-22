# 1Salem Server Manager 1.2.1

Version 1.2.1 is a focused Palworld dashboard, metrics, and usability hotfix.

## Palworld process metrics

The Agent now queries the Windows Job Object assigned to each managed server,
samples only its member processes, identifies known Palworld game executables,
and aggregates CPU, working set, private memory, and observed peak working set.
Root PID, game PID, executable names, and child count are refreshed from live
Job membership after every restart.

## Local Palworld management

The Agent polls Palworld's official REST API through
`http://127.0.0.1:8212/v1/api` only. The protected admin password is decrypted
inside the Agent solely for HTTP Basic authentication and is never returned to
the dashboard or written to logs. Info, metrics, and player data are polled at
five-second intervals with bounded exponential backoff.

If management is disabled, the dashboard offers a confirmed guided flow. It
updates managed metadata with a safety copy, preserves unrelated INI values,
restarts only after confirmation, and verifies the localhost endpoint.

## Dashboard and usability

- Separate local and Internet/Playit addresses, copy actions, tunnel target,
  honest online/verified status, and local-vs-public latency guidance.
- Palworld player list, ping, player ID, current/max count, FPS, official uptime,
  server name, and description with explicit unavailable states.
- Global active, inactive, hover, disabled, and keyboard-focus tab styling.
- Readable enabled, hover, pressed, disabled, and focused button styling.
- Separate wrapping backup and update lines with full tooltips.
- PerMonitorV2 awareness and inherited Arabic RTL flow.

## Update path

The `1SalemServerManager-Update-1.2.1.zip` package is compatible with version
1.2.0 and preserves the configured ProgramData and game roots. Setup remains
available for first install and repair.
