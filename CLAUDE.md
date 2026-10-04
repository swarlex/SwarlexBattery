# SwarlexBattery

Windows tray app (pure C#: WinForms tray icon + WPF flyout, HID readers; no scripts) that shows wireless mouse / keyboard /
headset batteries. Public repo: https://github.com/swarlex/SwarlexBattery (GPL-3.0-or-later).

## Layout
- `core/App.cs` - entry point, config, texts, log; `core/Host.cs` - timers, polls, notifications, settings
- `core/Batteries.cs` - battery sources (HID, Bluetooth, XInput, laptop, external.json) and what is shown
- `core/Tray.cs` - tray icon renderer + pinning, `core/Flyout.cs` - panel and menu, `core/Updater.cs` - GitHub releases
- `core/Devices.cs`, `core/Hid.cs` - vendor battery protocols (read-only queries only)
- `core/Native.cs` - Win32 helpers, `core/Setup.cs` - install wizard / uninstaller (its texts are inside the file; keep en + tr)
- `lang/en.json`, `lang/tr.json` - all app texts; every user-visible string goes through these
- `config.default.json` - defaults (embedded), merged with `%APPDATA%\SwarlexBattery\config.json`
- `build.ps1` -> `dist/SwarlexBattery.exe` (+ `.sha256`); version comes from `VERSION`

## Conventions
- Repo content (code, comments, docs, commit messages, release notes) is English.
- The UI is English and Turkish: add every new string to both language files (Turkish with proper
  characters: ş ı ğ ü ö ç). The `.ps1` files stay ASCII (PowerShell 5.1 reads BOM-less files as
  ANSI); C# sources are compiled with `/codepage:65001`.
- The compiler is the .NET Framework csc (C# 5): no string interpolation, `?.` or `=>` members.
- Never invent battery values: a reader returns -1 when the device did not answer.
- The repo is public: never commit secrets, tokens, personal paths or e-mail addresses.

## Publishing rule (the owner asked for this)
Every change made here must also reach GitHub, so installed copies get it through the updater.
After a change is finished and verified (parse check, plugin run, exe builds and starts):

```powershell
.\tools\release.ps1 -ByClaude -Notes "<short English description>"
```

It bumps the patch version (use `-Version X.Y.Z` for bigger changes), builds, commits, pushes and
creates the GitHub release. Do not publish a change that fails to build or was not tested; say so instead.

Changes that do not touch what ships in the exe or the setup (README, docs/, CHANGELOG, .github/,
CLAUDE.md) are committed and pushed without a release, so users are not offered an empty update.
Add each release to `CHANGELOG.md`. Screenshots in `docs/images` must not show personal data
(user names in paths, other apps' windows).
