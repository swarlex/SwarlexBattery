# Changelog

All notable changes. Downloads: [Releases](https://github.com/swarlex/SwarlexBattery/releases).

## 1.9.5
### Fixed
- The theme in Preferences read "like the taskbar", which said little: it is now *Theme: Automatic*, with
  "follows Windows' light or dark mode" below it, and *Light* / *Dark*.

## 1.9.4
### Added
- **Keep the icon next to the clock** (Preferences, on by default): the app keeps its icon in the visible part
  of the tray, as before. Turned off, the icon goes behind the ^ arrow once, and from then on stays wherever
  you put it (drag it, or Settings > Personalization > Taskbar > Other system tray icons).

## 1.9.3
Preferences in a window of their own, and a menu for each device: rename it, choose its icon or hide it.

### Added
- **Preferences** (right-click > *Preferences*): a window beside the menu with every setting, each applied at
  once: the poll interval and the low battery level with - / +, *Estimated time left*, *Quiet while gaming*,
  *Sound with low battery alerts*, *Windows Bluetooth devices*, *Percentage in the icon*, the theme (like the
  taskbar, light or dark), the language, the status file, *Start with Windows*, *Check for updates
  automatically* and *Get beta versions*. The menu itself is shorter: Refresh, Preferences, the update row,
  Diagnostics and Exit.
- **A menu for each device**: right-click a device in the panel to **rename** it (type the name, Enter keeps
  it, Esc cancels; *Reset name* goes back to the device's own), choose its **icon** (mouse, headset, earbuds,
  keyboard, controller, speaker, other) or **hide** it. A hidden device is left out of the panel, the tray,
  the notifications and the status file; right-click > *Hidden devices* shows it again.

### Changed
- New screenshots on GitHub, in light and dark.

## 1.9.2
A more accurate *time left*.

### Changed
- **The time left follows how a device is used now.** It is worked out from the moments a new, lower level
  first appeared, so devices that report in steps (5 % for the VXE / ATK mice) are no longer misjudged by the
  time spent on one step. Recent use counts more than older use (half as much per 3 hours of use), the
  first 10 minutes after a charge or a start are left out while the reading settles, and a device that stays
  on its level longer than its rate allows is taken to be draining slower now. On recorded histories: a
  BlackShark V2 HyperSpeed at 72 % went from 25 to about 42 hours (it drains some 1.7 % an hour), a VXE MAD
  8K mouse at 55 % from 5 to about 7 hours.

## 1.9.1
Light theme, German, Spanish and Italian, new devices from HaloBattery 1.14.0, a portable mode and automatic
tests on every build.

### Added
- **Light theme**: the flyout and the menu are light when the taskbar is light, and follow it when it
  changes. `"theme": { "mode": "dark" }` or `"light"` in config.json keeps one look.
- **German, Spanish and Italian**, in the app and in the installer. *Language* in the menu opens a list of all
  languages; with `"language": "auto"` the app follows the Windows display language. `/lang:de|es|it` for a
  silent install.
- **New devices**, all with read-only requests:
  - SteelSeries Arctis Nova Elite (base station 1038:2244): level and charging, without SteelSeries GG. A
    switched-off headset, which the station reports as 0 %, shows no level.
  - HyperX Cloud III S Wireless (03F0:02CC and 03F0:06BE): level and charging, without NGENUITY.
  - Logitech G PRO X 2 LIGHTSPEED (046D:0AF7) on its receiver, which speaks Logitech's Centurion protocol
    instead of HID++: level and charging, without G HUB.
  - G-Wolves models with a receiver of their own (HSK Pro / Plus / Lite and their ACE versions, HTS, HTX, HTR,
    HT-S2, Fenrir, VUK, HTM Plus), on the receiver or the cable.
  - Razer DeathStalker V2 Pro / Pro TKL, BlackWidow V3 Mini, V4 Mini and V4 Tenkeyless HyperSpeed and
    BlackWidow V3 Pro keyboards: asked on their own USB interface and transaction id first (from OpenRazer),
    and a sleeping keyboard on its receiver stays in the panel as asleep.

  The Arctis Nova Elite, Cloud III S and G PRO X 2 protocols are confirmed on real hardware in HaloBattery;
  the G-Wolves models and the keyboards are not confirmed yet. None of them is tested with SwarlexBattery yet:
  a diagnostics report from an owner is welcome.
- **Logitech G435 on its LIGHTSPEED receiver** is listed with a note instead of being missing: the receiver
  gives no battery level (only in its firmware-update mode, which cuts the sound); the level shows when the
  headset is connected by Bluetooth. No number is shown for it, and it never enters the tray icon,
  notifications or the status file.
- **Sound with low battery alerts** (right-click menu, off by default), for full-screen games where the
  notification is not seen: Windows' own *Battery Low* sound (*Battery Critical* at 5 % or less), and again
  every 5 minutes while the device stays low, awake and off the charger.
- **Portable mode**: an empty `portable.txt` next to `SwarlexBattery.exe` keeps the settings, log and battery
  history in a `data` folder beside it. A folder that cannot be written falls back to `%APPDATA%`.
- **Automatic tests** on every build and on GitHub: battery replies captured on real devices, what the panel
  and the tray icon show, version comparison, and every text in every language. A failing test stops the
  build, so a broken release cannot be published.

### Fixed
- *Diagnostics…* opened an empty Notepad tab: the new Windows 11 Notepad shows files under `%APPDATA%` as
  empty. Reports are now written to `%TEMP%\SwarlexBattery`, where Notepad shows them.
- Opening the language list made the menu grow below the taskbar, and closing it left the menu floating in
  the middle of the screen. It now stays on the taskbar.

### Changed
- The app wakes up half as often while idle (once a second), a little kinder to laptop batteries.
- New screenshots on GitHub, in light and dark.

## 1.9.0
A beta channel for those who want fixes first, and a round of Windows 10 / 11 compatibility fixes.
From now on small fixes are published as pre-releases and collected into fewer, larger stable releases.

### Added
- **Get beta versions** (right-click menu, off by default): also offers pre-releases. Everyone else only
  gets stable releases.

### Fixed
- *Diagnostics…* could open an empty or outdated report in Notepad (Windows 11 Notepad keeps a closed
  file's old tab). Every report is now a new dated file (`diagnostics-YYYYMMDD-HHMMSS.txt`, the last
  three are kept), written as UTF-8 with a BOM so the Windows 10 Notepad shows Turkish letters correctly.
- The tray icon is redrawn at the right size when the display scale changes, without a restart.
- Reading pauses while the PC is locked and runs right away after sleep, so levels are fresh on wake.
- The dark window frame also works on Windows 10 versions before 20H1.
- On Windows 10 the low-battery alert uses the tray notification Windows 10 already shows in the
  notification centre; the Windows 11 notifier is used only on Windows 11.
- The update check no longer receives a cached, older answer right after a release.

## 1.8.0
Windows 10 / 11 notifications instead of tray balloons, the level as a number in the icon if you want
it, and the flyout now opens in the right place with a second monitor.

### Added
- **Percentage in the icon** (right-click > *Percentage in the icon*, off by default): the ring shows the
  level as a number instead of the pictogram, sized so that "100" fits too and coloured like the ring when
  the battery is low. With two devices on one icon it shows the lower level - the one to charge first.
  Charging still shows the bolt, and devices that only report rough steps keep their pictogram.
- **Windows notifications**: low-battery alerts are real Windows 10 / 11 notifications with the app's name
  and icon, and they stay in the notification centre. When Windows has notifications turned off for the
  app, or on an older Windows, the tray balloon is used as before.
- *Diagnostics…* now says whether Windows allows notifications for the app - the first thing to check
  when an alert never shows up.

### Fixed
- **The flyout and the menu could open in the wrong place with more than one monitor**: they were placed
  on the primary monitor's coordinates. They are now placed in physical pixels on the monitor of the
  clicked icon, next to that monitor's own taskbar (an auto-hiding one included), at that monitor's scale.

## 1.7.0
Battery levels now come with an estimate of the time left, notifications wait until your game is over,
and other apps can read every level from a status file. Plus four fixes found by comparing notes with
HaloBattery 1.13.0.

### Added
- **Estimated time left** in the flyout ("about 5 h left"). It comes from a least-squares line through
  the level against the time the device was awake and on battery since its last charge; time asleep,
  switched off or with the PC suspended does not count. No estimate until 30 minutes of use and a 3-point
  drop, none for devices that only report rough steps, and a new charge starts a new history. Kept in
  `%LOCALAPPDATA%\SwarlexBattery\state\gadgets\history.json`, so it survives a restart. Setting:
  `plugins.gadgets.timeLeft`.
- **Quiet while gaming, the better way**: while a full-screen app is in front, a low-battery notification
  is now held (one per device) and shown when the game closes - it used to be dropped. The devices are
  asked only every 5 minutes during the game, so the app talks to them less; plugging something in still
  reads it at once.
- **Status file for other apps** (off by default, `"statusFile": true`): `%APPDATA%\SwarlexBattery\status.json`,
  rewritten after every read and replaced in one step, so a reader never sees half a file. Each device has
  its name, kind, level, charging, online / asleep, approximate and hours left - for Rainmeter, Stream Deck
  or a script.

### Fixed
- **A Razer mouse on its cable and on its receiver showed as two devices**: the two use different product
  ids, which were the device's key. The key is now the model, so they are one device and the cable's
  (charging) reading wins.
- **Razer Barracuda Pro switched off delayed every other device** by about 4 seconds per read: it is now
  asked twice for half a second each, then left alone until the next read.
- **HyperX Cloud III Wireless**: a dongle that refuses the battery request as a normal write ("Incorrect
  function") now gets it as a feature report, as the vendor intends.
- **Audeze Maxwell**: a dongle that is stuck - it answers every packet with an empty echo, so no battery
  ever arrives - is now recognised; *Diagnostics…* says to unplug it and plug it back in.

### Changed
- Release notes like this one: a summary, then what was added and fixed, taken straight from this
  changelog.

## 1.6.0
- Many more devices, ported from HaloBattery's providers (MIT) and the projects they name. All of them
  are read with battery / status requests only, and every answer is checked before it is shown:
  - mice: ASUS ROG / TUF (G-Helper), WLmouse Beast X, LAMZU Maya X, G-Wolves (11 models, one firmware
    family), MCHOSE M7 / L7 / A7 and G7, AM Infinity 8K;
  - headsets: Razer Barracuda Pro (2.4 GHz), Astro A50 Gen 5, Audeze Maxwell / Maxwell 2 (battery packet
    only), JBL Quantum 910 (listen only), SteelSeries Arctis 1 / 7 / 7P / 7X / 9 / Pro Wireless 2019;
  - keyboards: Keychron (Ultra-Link 8K, and the M5 mouse), Lofree Hyzen.
- Razer keyboards are shown as keyboards (they were labelled as mice).
- Not added on purpose: Corsair Dark Core (its answer cannot be checked), Arctis Pro Wireless 2017 (its
  answer does not echo the request).

## 1.5.6
- Controller tray icons redrawn as real silhouettes (HaloBattery's outlines, MIT): Xbox pads and other
  controllers get the Xbox outline with offset sticks, PS4 the DualShock 4 outline with touchpad and two
  sticks, PS5 the same outline with the DualSense's larger touchpad.

## 1.5.5
- Each controller family gets its own tray icon outline (no brand logos): PS5 DualSense - two-tone wings
  with a dark middle, PS4 DualShock 4 - touchpad bar and sticks side by side, Xbox (and pads in Xbox mode) -
  oval body with offset sticks; other controllers keep the plain pad.

## 1.5.4
- New controller tray icon: drawn as a shape (body, grips, D-pad and buttons) instead of the font glyph,
  which was too wide for the ring and turned into a blob at tray size.

## 1.5.3
- Game controllers: PS5 DualSense / DualSense Edge and PS4 DualShock 4 (USB, and Bluetooth while Steam or
  a game uses them), Nintendo Switch Pro Controller and Joy-Con (Bluetooth), 8BitDo in D-input mode.
  Protocols from HaloBattery (MIT), after the Linux hid-playstation driver and SDL. Listen only, except one
  read-only "device info" request to Switch controllers.
- An Xbox pad on Bluetooth is no longer shown twice (as a Bluetooth device and as an XInput pad).
- XInput pads that report an unknown battery type no longer show a made-up level.
- README: new screenshots of the flyout, the menu and the tray icons.

## 1.5.2
- New menu item **Diagnostics…**: writes `%APPDATA%\SwarlexBattery\diagnostics.txt` and opens it - what
  is shown, a fresh read of the supported devices, the last protocol steps, Bluetooth levels, all HID
  devices and the recent log. Bluetooth MAC addresses and device serials are masked, so the file can be
  attached to a public issue as it is.
- Contributing guide (`.github/CONTRIBUTING.md`) and simpler issue forms built around the diagnostics file.

## 1.5.1
- The update check no longer requests the release's checksum file: GitHub counted every check as a
  download, which inflated the download numbers. The file is still fetched and verified when you update.

## 1.5.0
- Steadier levels: a device that is not charging no longer jumps up by a few points when its reading
  wobbles at a step boundary (e.g. 15 -> 20 -> 15); the lower value it gave stays. A real charge, a
  big rise or the charging state is shown at once.
- Batteries are read every 30 seconds while the flyout is closed (5 while it is open) instead of every
  10: fewer radio wake-ups for the mouse and headset. Plugging a device in still reads at once.
  Setting: `plugins.gadgets.interval`.
- Fixed the dotted focus frame drawn around the flyout and the menu content.

## 1.4.9
- Darmoshark 4K receiver (M3 4K): rewritten from the vendor's own web driver. 1.4.8 sent the Telink
  models' request, which these Nordic mice do not answer; the reader now sends the 4K power read
  (command 0x41 / 0x01, read item 1, checksum 161). While charging the driver shows no level, so the
  last reading is shown as charging, marked approximate.

## 1.4.8
- Darmoshark 4K receiver ("4K NRF Dongle", 1915:0725, e.g. M3 4K): battery read with the vendor's read-
  configuration request (output report 0xB3). Experimental: a reply is used only when its DPI fields are
  plausible; otherwise it is written to the log for a fix.

## 1.4.7
- The log also lists other vendors' vendor-defined HID collections (once per plug / unplug), so a
  device report for an unsupported mouse or keyboard shows its ids.
- README: Darmoshark support covers the Telink models (M3, M3S, N3); the M3 4K uses a Nordic chip with
  an unpublished protocol.

## 1.4.6
- New: AULA F75 keyboard on its 2.4 GHz receiver (protocol from Device-Battery-Info, MIT). On the cable
  the keyboard reports no level, so the last reading is shown as charging, marked approximate.
- New: Darmoshark M3 family mice on the receiver and on the cable (protocol from
  darmoshark-m3-configurator, MIT). A sleeping mouse shows its last reading as asleep.

## 1.4.5
- Much lighter: about 10 MB of memory instead of about 100 MB (the flyout's drawing memory is given back
  when it closes), and other vendors' HID devices are no longer opened on every poll; the device list
  is scanned again only when something is plugged in or out.
- The last readings are saved to disk only when they change (was every 10 seconds).
- The tray icon keeps its place when no device is found (it used to be replaced by a new icon).
- Screen readers announce "Batteries" plus the levels (the name used to start with "loading").
- `external.json`: an entry without a level is skipped instead of shown as 0 %, and one broken entry
  no longer hides the others.
- A damaged `hid-last.json` no longer stops all readings; repeating errors are logged once.

## 1.4.4
- Unlisted Logitech headsets are only probed on collections shaped like HID++ (20-byte reports); other
  vendor interfaces, such as the audio chip of the G435 receiver, are never written to.
- README: the G435 on its USB receiver cannot be read (no known battery query).

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
