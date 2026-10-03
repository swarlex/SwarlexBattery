# SwarlexBattery

Windows tray app (PowerShell + WPF host, C# HID readers) that shows wireless mouse / keyboard /
headset batteries. Public repo: https://github.com/swarlex/SwarlexBattery (GPL-3.0-or-later).

## Layout
- `SwarlexBattery.ps1` - host: tray icon, flyout, menu, languages, updater
- `core/Devices.cs`, `core/Hid.cs` - vendor battery protocols (read-only queries only)
- `core/Native.cs` - Win32 helpers, `core/Launcher.cs` - exe entry point
- `plugins/gadgets` - the battery plugin (its texts: `plugins/gadgets/lang.json`)
- `lang/en.json`, `lang/tr.json` - host texts; every user-visible string goes through these
- `build.ps1` -> `dist/SwarlexBattery.exe` (+ `.sha256`); version comes from `VERSION`

## Conventions
- Repo content (code, comments, docs, commit messages, release notes) is English.
- The UI is English and Turkish: add every new string to both language files (Turkish with proper
  characters: ş ı ğ ü ö ç). The `.ps1` files themselves stay ASCII (PowerShell 5.1 reads BOM-less
  files as ANSI), so non-ASCII text lives only in the UTF-8 JSON files.
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
