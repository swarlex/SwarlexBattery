# Changelog

All notable changes. Downloads: [Releases](https://github.com/swarlex/SwarlexBattery/releases).

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
