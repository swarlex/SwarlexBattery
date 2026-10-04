# Changelog

All notable changes. Downloads: [Releases](https://github.com/swarlex/SwarlexBattery/releases).

## 1.4.3
- The log also lists devices of supported brands that gave no reading at all (with their HID
  collections), so a device report shows why a device is missing.

## 1.4.2
- "Check for updates" sees a new release right after it is published (GitHub cached the answer for
  a few minutes).

## 1.4.1
- Razer mice: the battery query now reaches the mouse's own HID collection (Windows refuses a read/write
  open there; feature reports work on a no-access handle), and every candidate collection is tried.
  This should bring in many Razer mice, e.g. the Viper V3 HyperSpeed.
- Logitech headsets that are not on the list (e.g. G435): HID++ is tried on their vendor collections.
- With "automatically hide the taskbar" the flyout no longer opens under the taskbar.
- The first click after start opens the flyout right away.
- The log lists the battery devices found and, when one does not answer, the last protocol steps.

## 1.4.0
- Rewritten in C#: the app no longer runs PowerShell or unpacks scripts, so it starts faster, uses less
  memory and looks less suspicious to antivirus heuristics.
- "Check for updates" shows "Checking...", then the result right in the menu.
- Fixed two menu rows staying highlighted at the same time.
- Removed: scripts in `collectors.d`. Other programs can still report batteries through `external.json`.

## 1.3.2
- The update check no longer uses the GitHub API, so it is not affected by its rate limit
  (60 requests per hour per IP address); the API is only a fallback.

## 1.3.1
- The flyout and the menu use the Windows 11 window frame, so the occasional double box is gone.
- Wider menu for the Turkish texts.
- The language picked in the setup wizard becomes the app's language (silent install: `/lang:en|tr`).

## 1.3.0
- Install wizard `SwarlexBattery-Setup.exe` (English / Turkish): per-user install without administrator
  rights, Start menu / desktop shortcuts, Start with Windows, upgrades in place, uninstaller in
  Settings > Apps. Silent install: `/silent [/dir:<folder>] [/noautostart]`.

## 1.2.1
- Updates come from the renamed repository `swarlex/SwarlexBattery`; the updater only accepts files
  from that repository's releases.

## 1.2.0
- English and Turkish interface (follows the Windows language; switch from the right-click menu).
- Licensed under the GNU GPL v3 or later.
- Focused on batteries: the unrelated Sync, Pocket, Mousekit and Glass plugins were removed.
- An Xbox controller no longer reports a level while its battery is disconnected.

## 1.1.2
- The tray icon is pinned next to the clock within a few seconds instead of landing in the overflow menu.

## 1.1.1
- One-command release flow.

## 1.1.0
- First release: mouse, keyboard and headset batteries in one tray icon; Razer, Logitech,
  SteelSeries, HyperX, Corsair, ATK / VXE / Pulsar, Bluetooth and Xbox controller support;
  self-update from GitHub Releases.
