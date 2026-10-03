# Security

## What SwarlexBattery does on your computer

- Runs as your normal user. It never asks for administrator rights, installs no driver and injects
  into no other process.
- Talks to your devices over HID with **read-only battery and status queries** only. It never writes
  device settings.
- Writes only to `%APPDATA%\SwarlexBattery` (settings), `%LOCALAPPDATA%\SwarlexBattery` (log, cache,
  extracted scripts), and, when you enable them, the per-user *Start with Windows* entry
  (`HKCU\...\Run`) and the Windows setting that keeps its own tray icon visible.
- Its only network access is the update check described below. It sends no telemetry.

## Updates

- The app reads `https://api.github.com/repos/yukicanclaude/SwarlexBattery/releases/latest`.
- A download whose address is not on `github.com` is refused.
- The downloaded exe must start with an `MZ` header and match the SHA-256 in
  `SwarlexBattery.exe.sha256` from the same release; otherwise it is deleted and not installed.
- An update is applied only when you click *Update* in the menu.
- Note: the checksum comes from the same GitHub release. It protects against damaged or partial
  downloads, not against a compromised GitHub account.

## Reporting a vulnerability

Please report security problems privately through
[GitHub security advisories](https://github.com/yukicanclaude/SwarlexBattery/security/advisories/new)
instead of a public issue.
