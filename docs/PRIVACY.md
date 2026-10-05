# Privacy

SwarlexBattery runs only on your computer. It has no account, no telemetry, no analytics and no ads.

## What it sends over the network

One thing only: it asks `github.com/swarlex/SwarlexBattery` which release is the latest, at start and
every 6 hours, and downloads that release only when you click *Update*. That request carries nothing
about you or your devices - it is an ordinary web request, as when you open the release page in a browser
(GitHub sees your IP address, as for any visit). Turn it off with `"update": { "check": false }` in
`%APPDATA%\SwarlexBattery\config.json`; *Check for updates* in the menu still works on request.

Nothing else leaves your computer. Battery levels, device names and the log stay local.

## What it keeps on your computer

- `%APPDATA%\SwarlexBattery`: settings (`config.json`), and `status.json` when you turn that on.
- `%TEMP%\SwarlexBattery`: the last three *Diagnostics…* reports, when you ask for one.
- `%LOCALAPPDATA%\SwarlexBattery`: the log, the last reading and the battery history of each device (for the
  time-left estimate), and the app's icon for Windows notifications.

The uninstaller removes all of it if you choose so.

## What it changes in Windows

Only for your user (`HKCU`): the *Start with Windows* entry when you turn it on, the setting that keeps its
own tray icon visible next to the clock (`"alwaysShowInTray": false` turns that off), its entry in
*Settings > Apps* when installed, and its name and icon for Windows notifications.
