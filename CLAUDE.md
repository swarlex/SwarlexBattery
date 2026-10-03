# SwarlexBattery

Windows tray app (PowerShell + WPF host, C# HID readers) that shows wireless mouse / keyboard /
headset batteries. Repo: https://github.com/yukicanclaude/SwarlexBattery. UI text is Turkish (ASCII).

## Layout
- `SwarlexBattery.ps1` - host: tray icons, flyout, plugin runner, updater
- `core/Devices.cs`, `core/Hid.cs` - vendor battery protocols (read-only queries only)
- `core/Native.cs` - Win32 helpers, `core/Launcher.cs` - exe entry point
- `plugins/gadgets` - the battery plugin (other plugins are disabled by default)
- `build.ps1` -> `dist/SwarlexBattery.exe` (+ `.sha256`); version comes from `VERSION`

## Publishing rule (the owner asked for this)
Every change made here must also reach GitHub, so installed copies get it through
"Guncelle". After a change is finished and verified (parse check, plugin run, exe builds and starts):

```powershell
.\tools\release.ps1 -ByClaude -Notes "<kisa Turkce aciklama>"
```

It bumps the patch version (use `-Version X.Y.Z` for bigger changes), builds, commits, pushes and
creates the GitHub release. Do not publish a change that fails to build or was not tested; say so instead.
Never commit secrets; the repo is public. Never invent battery values: a reader returns -1 when the
device did not answer.
