# Connect UI acceptance — complete

Date: 2026-09-29. Branch: `claude/connect-phase-2`. Tool: `tools/ServerManager.Connect.UiReview`
(real windows and pages, fake Agent / friend fakes; see its README).

**UI ACCEPTANCE COMPLETE** in English (`en-US`, left to right) and Arabic (`ar-SA`, right to left).
Final renders: owner 10 screens and friend 5 screens per language, 0 clipped controls, 0 input
without an accessible name, 0 failed checks.

## Screens reviewed

| Owner (Server Manager) | Friend (1Salem Connect) |
|---|---|
| Connect setup: not set up (OAuth client form), ready, policy needs attention | Invite accepted, waiting for the owner |
| Minecraft Connect card: off; on with a pending friend | Connecting… |
| Invite: before and after creating a one-time invitation | Connected, with the loopback address and Copy |
| Friends: pending, approved (ready), approved (waiting for the friend) | Access revoked |
| Revoke: confirmation dialog and completed result | Servers list with a failed setup |

## Checks

| Check | Result |
|---|---|
| English and Arabic text, layout direction | PASS after the fixes below |
| Keyboard: real Tab traversal on every screen | PASS: logical order, every action reachable; the revoke confirmation starts on Cancel |
| Screen-reader names on every Tab stop | PASS after the fixes below |
| Clipping or broken layout | PASS: none detected or seen |
| Invite copy on the real Windows clipboard | PASS: exact link; `CanIncludeInClipboardHistory` and `CanUploadToCloudClipboard` both 0, in both languages |
| Friend address copy on the real Windows clipboard | PASS |

## Defects found and fixed

- **Inputs without an accessible name** (owner): OAuth client id and secret, invitation validity,
  link and code. Screen readers announced them as unlabeled edit fields. They are now labeled by
  their visible labels (`AutomationProperties.LabeledBy`).
- **Unlabeled nickname box** (owner, Friends): an approved friend's nickname box had only a
  tooltip, so it showed as an empty unlabeled box. It now has a visible label that also names it.
- **Duplicate status** (owner, Friends): a pending friend showed "Waiting for approval" twice.
- **Friend app in Arabic** (friend): there was no Arabic string table, yet the window turned right
  to left on Arabic Windows, which mirrored English sentences (a leading period, the "1" of 1Salem
  displaced). Added `Strings.ar.resx` (all 76 strings, using the owner app's Connect terms); the
  window is right to left only when the culture actually has a table; the product name keeps its
  own direction at the window's start edge. Tests check key and placeholder parity and the
  direction rule.
- **Contradictory failed setup** (friend): a card read "Enrollment pending" right above "Setting up
  this server failed". It now reads "Setup failed".

Focused tests after the fixes: friend app 161/161, Server Manager Connect UI 23/23.

## Not covered

Pixel-level review at other DPI settings, high-contrast themes, the Windows clipboard history
panel itself, and Minecraft's own UI.
