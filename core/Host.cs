// SPDX-License-Identifier: GPL-3.0-or-later
// Ties the parts together: battery polls on a background thread, tray icons, flyout, notifications,
// device-change refreshes, the updater schedule and the "Start with Windows" / language settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SwarlexBattery
{
    class Host : IDisposable
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwarlexBattery";

        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        readonly BatteryReader reader = new BatteryReader();
        TrayIcons tray; Flyout flyout; DispatcherTimer timer;
        public Snapshot Current;

        bool polling; DateTime nextPoll = DateTime.MinValue;
        readonly Dictionary<string, DateTime> notified = new Dictionary<string, DateTime>();
        int seenChanges; readonly Queue<DateTime> devicePolls = new Queue<DateTime>();
        DateTime promoteAt = DateTime.Now.AddSeconds(3); int promoteCount;
        DateTime themeAt = DateTime.MinValue;

        public ReleaseInfo Update;               // a newer release, when one was found
        public string UpdateBusy;                // null, "check" or "install"
        public DateTime? LastCheck; public string LastResult = "";
        DateTime updateStarted, nextUpdateCheck = DateTime.Now.AddSeconds(20);
        bool updateManual; string updateNotified = "";
        bool stopping;

        public void Start()
        {
            tray = new TrayIcons();
            tray.Click += b => { if (b == MouseButtons.Right) flyout.ToggleMenu(); else flyout.TogglePanel(); };
            flyout = new Flyout(this);
            // placeholder until the first poll
            tray.Sync(new List<TraySpec> { new TraySpec { Id = "all", Icon = "E83F", State = "off", Dim = true, Tooltip = Strings.T("title") } });
            MigrateWinBar();
            UpdateUninstallVersion();
            DeviceWatch.Start();
            // the display scale changed (Settings > Display): redraw the tray icon at the new size, not blurred
            SystemEvents.DisplaySettingsChanged += (s, e) => ui.BeginInvoke(new Action(() =>
            {
                int size = Win.TrayIconSize();
                if (size != TrayRenderer.Size) { TrayRenderer.Size = size; TrayRenderer.Clear(); if (Current != null) tray.Sync(Current.Icons); }
            }));
            // locked: nobody looks at the tray, so the devices are left alone; unlocked or woken up: read at once
            SystemEvents.SessionSwitch += (s, e) => ui.BeginInvoke(new Action(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; tray.Pause(true); }
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; tray.Pause(Win.ForegroundIsFullscreen()); PollSoon(); }
            }));
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) ui.BeginInvoke(new Action(() => PollSoon())); };
            // once a second is enough for everything below (the closest deadline is a re-read 1.5 s after a plug-in),
            // and every wake-up of an idle tray app costs a little battery on a laptop
            timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log.Once("tick: " + ex); } };
            timer.Start();
            Log.Write("SwarlexBattery v" + Program.AppVersion + " started (PID " + Process.GetCurrentProcess().Id + ", icon " + TrayRenderer.Size + " px, " + TrayRenderer.GlyphFont + ", language " + Strings.Lang + ")");
        }

        void Tick()
        {
            var now = DateTime.Now;
            // Explorer registers a new icon a few seconds after it appears (and again for an exe in a new folder):
            // keep the icon pinned next to the clock - every 5 s for the first 90 s, then every 10 min
            if (now >= promoteAt)
            {
                promoteCount++;
                promoteAt = promoteCount < 18 ? now.AddSeconds(5) : now.AddMinutes(10);
                try { TrayIcons.Promote(); } catch (Exception e) { Log.Write("promote: " + e.Message); }
            }
            // a device was plugged in or out (receiver, charging cable): refresh after it settles, and once more later
            if (DeviceWatch.Changes != seenChanges)
            {
                seenChanges = DeviceWatch.Changes;
                devicePolls.Clear(); devicePolls.Enqueue(now.AddSeconds(1.5)); devicePolls.Enqueue(now.AddSeconds(6));
            }
            if (devicePolls.Count > 0 && now >= devicePolls.Peek()) { devicePolls.Dequeue(); PollSoon(); }
            if (AirPods.TakeFresh() && !locked) PollSoon();   // AirPods told a new level
            // Windows marks a Bluetooth headset gone a few seconds after it is switched off, often after the plug-in
            // re-reads above: its list (well under a millisecond) is looked at every 3 s, and a change reads at once
            if (now >= btAt && !locked && !btBusy && Config.Bool("plugins.gadgets.bluetooth", true))
            {
                btAt = now.AddSeconds(3); btBusy = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string sig = "";
                    try { sig = string.Join(",", BluetoothBattery.List().Select(d => d.Mac + (d.Connected ? "+" : "-") + d.Level)) + "|" + BluetoothBattery.Apple.Count; } catch { }
                    ui.BeginInvoke(new Action(() => { btBusy = false; if (btSig != null && sig != btSig) PollSoon(); btSig = sig; }));
                });
            }
            if (!polling && !locked && now >= nextPoll) Poll();

            if (UpdateBusy == null && Config.Bool("update.check", true) && now >= nextUpdateCheck) CheckUpdates(false);

            // taskbar theme switch -> repaint
            if (now >= themeAt)
            {
                themeAt = now.AddSeconds(5);
                bool light = TrayRenderer.IsLightTaskbar();
                if (light != TrayRenderer.LightTaskbar) { TrayRenderer.LightTaskbar = light; tray.Repaint(); }
                // notifications held back while a full-screen app was in front: shown once it is gone
                bool fullscreen = Win.ForegroundIsFullscreen();
                if (held.Count > 0 && !(fullscreen && Config.Bool("quietWhileGaming", true))) ShowHeld();
                tray.Pause(locked || fullscreen);
            }
        }

        // ------------------------------------------------------------ batteries
        bool locked;
        DateTime btAt; bool btBusy; string btSig;
        // asked while a read is running: one more read right after it (it would otherwise wait a whole interval)
        bool pollAgain;
        public void PollSoon() { nextPoll = DateTime.MinValue; if (polling) pollAgain = true; else if (!locked) Poll(); }

        void Poll()
        {
            polling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Snapshot snap = null;
                try { var gadgets = reader.Read(); snap = BatteryReader.Build(gadgets); BatteryReader.WriteStatus(gadgets); }
                catch (Exception e) { Log.Once("poll: " + e); }
                ui.BeginInvoke(new Action(() =>
                {
                    polling = false;
                    // during a full-screen game the devices are asked less often (a plug-in still reads at once)
                    nextPoll = pollAgain ? DateTime.MinValue : DateTime.Now.AddSeconds(flyout.Open == "panel" ? 5 : Gaming() ? Math.Max(BatteryReader.PollSeconds, 300) : BatteryReader.PollSeconds);
                    pollAgain = false;
                    if (snap != null && !stopping) Apply(snap);
                }));
            });
        }

        void Apply(Snapshot snap)
        {
            Current = snap;
            tray.Sync(snap.Icons);
            // one notification per key; the key re-arms only after it has been gone for 30 min, so a device
            // that naps and comes back does not repeat the same "battery low" toast
            var now = DateTime.Now;
            foreach (var n in snap.Notify)
            {
                if (!notified.ContainsKey(n.Key)) Toast(n.Title, n.Body, n.Info ? ToolTipIcon.Info : ToolTipIcon.Warning);
                notified[n.Key] = now;
            }
            foreach (var k in notified.Keys.ToList()) if ((now - notified[k]).TotalMinutes > 30) notified.Remove(k);
            LowSound(snap.Notify.Where(n => n.Pct >= 0).ToList(), now);
            if (flyout.Open == "panel") flyout.Refresh(); else flyout.Warm();
        }

        // "Sound with low battery alerts" (off by default), for full-screen games where a notification is not seen:
        // Windows' own Battery Low sound (Battery Critical at 5 % or less), and again every 5 minutes while a device
        // stays low, awake and off the charger (a low notice is only made for such a device). The sound files are
        // played directly: the "low battery" sound events are often left empty on desktop PCs.
        DateTime lowSoundAt = DateTime.MinValue;

        void LowSound(List<Notice> low, DateTime now)
        {
            if (low.Count == 0) { lowSoundAt = DateTime.MinValue; return; }
            if (!Config.Bool("lowSound", false) || (now - lowSoundAt).TotalMinutes < 5) return;
            lowSoundAt = now;
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media",
                                    low.Any(n => n.Pct <= 5) ? "Windows Battery Critical.wav" : "Windows Battery Low.wav");
            try
            {
                if (File.Exists(file)) { var p = new System.Media.SoundPlayer(file); p.Play(); }
                else System.Media.SystemSounds.Exclamation.Play();
            }
            catch (Exception e) { Log.Once("low battery sound: " + e.Message); }
        }

        // ------------------------------------------------------------ Preferences and the device menu
        void Save(string path, object value)
        {
            try { Config.SetPath(path, value); } catch (Exception e) { Log.Write("save " + path + ": " + e.Message); }
        }

        // a switch in Preferences; the panel and the tray follow at once
        public void Toggle(string path, bool def)
        {
            Save(path, !Config.Bool(path, def));
            if (path == "plugins.gadgets.bluetooth") reader.RefreshSlow();
            if ((path == "iconPercent" || path == "monochrome" || path == "chargeAnimation") && Current != null) tray.Sync(Current.Icons);
            if (path == "alwaysShowInTray") try { TrayIcons.SetPromoted(Config.Bool(path, true)); } catch (Exception e) { Log.Write("promote: " + e.Message); }
            if (path == "update.check" || path == "update.beta") { Update = null; LastResult = ""; if (Config.Bool("update.check", true)) CheckUpdates(true); }
            PollSoon();
        }

        public static readonly int[] Intervals = { 10, 15, 30, 60, 120, 300 };
        public static readonly int[] LowLevels = { 5, 10, 15, 20, 25, 30, 40, 50 };

        // - / + in Preferences: the next or the previous value of the list
        public void Step(string path, int[] values, int def, int dir)
        {
            int cur = (int)Config.Num(path, def), i = Array.IndexOf(values, cur);
            // a value set by hand that is not in the list: the next one of the list in that direction
            if (i >= 0) i += dir;
            else i = dir > 0 ? values.Count(v => v <= cur) : values.Count(v => v < cur) - 1;
            i = Math.Max(0, Math.Min(values.Length - 1, i));
            Save(path, values[i]);
            if (path == "plugins.gadgets.interval") nextPoll = DateTime.Now.AddSeconds(values[i]);
            else PollSoon();
        }

        public void SetTheme(string mode) { Save("theme.mode", mode); }

        public void Rename(string id, string name)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { Config.SetMapEntry("plugins.gadgets.names", id, string.IsNullOrWhiteSpace(name) ? null : name.Trim()); } catch (Exception e) { Log.Write("rename: " + e.Message); }
            PollSoon();
        }

        public void SetIcon(string id, string kind)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { Config.SetMapEntry("plugins.gadgets.icons", id, kind); } catch (Exception e) { Log.Write("icon: " + e.Message); }
            PollSoon();
        }

        // a device's own low battery level; null = the general one from Preferences
        public void SetDeviceLow(string id, int? pct)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { Config.SetMapEntry("plugins.gadgets.lowLevels", id, pct.HasValue ? pct.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null); }
            catch (Exception e) { Log.Write("device low: " + e.Message); }
            PollSoon();
        }

        // hidden by id; the name is kept for the "Hidden devices" list
        public void Hide(string id, string name)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { Config.SetMapEntry("plugins.gadgets.hidden", id, name ?? id); } catch (Exception e) { Log.Write("hide: " + e.Message); }
            Log.Write("hidden by the user: " + name + " [" + id + "]");
            PollSoon();
        }

        public void Unhide(string id)
        {
            try { Config.SetMapEntry("plugins.gadgets.hidden", id, null); } catch (Exception e) { Log.Write("unhide: " + e.Message); }
            PollSoon();
        }

        bool Gaming() { return Config.Bool("quietWhileGaming", true) && Win.ForegroundIsFullscreen(); }

        // While a full-screen app is in front, a notification is held (one per title) instead of dropped,
        // and shown when the app is gone.
        readonly List<Tuple<string, string, ToolTipIcon>> held = new List<Tuple<string, string, ToolTipIcon>>();

        void Toast(string title, string body, ToolTipIcon kind = ToolTipIcon.Info)
        {
            if (string.IsNullOrEmpty(title)) return;
            if (Gaming())
            {
                held.RemoveAll(x => x.Item1 == title);
                held.Add(Tuple.Create(title, body, kind));
                return;
            }
            tray.Balloon(title, body, kind);
        }

        void ShowHeld()
        {
            var list = held.ToList(); held.Clear();
            if (list.Count == 1) tray.Balloon(list[0].Item1, list[0].Item2, list[0].Item3);
            else tray.Balloon("SwarlexBattery", string.Join("\n", list.Select(x => x.Item1 + (string.IsNullOrEmpty(x.Item2) ? "" : " - " + x.Item2))),
                              list.Any(x => x.Item3 == ToolTipIcon.Warning) ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        // ------------------------------------------------------------ settings from the menu
        public bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && (k.GetValue("SwarlexBattery") as string) == "\"" + Program.ExePath + "\"";
            }
        }

        public void ToggleAutostart()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (Autostart) k.DeleteValue("SwarlexBattery", false);
                else k.SetValue("SwarlexBattery", "\"" + Program.ExePath + "\"");
            }
        }

        // "Language" > a language: saves it in config.json and re-polls in the new language
        public void SetLanguage(string next)
        {
            try { Config.SetUser("language", next); } catch (Exception e) { Log.Write("language save: " + e.Message); }
            Strings.Load(next);
            PollSoon();
        }

        // menu > Diagnostics: the report for a device report, written in the background and opened in Notepad
        public void Diagnostics()
        {
            var snap = Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // In the temp folder: Windows 11 Notepad (a packaged app) opens files under %APPDATA% and
                    // %LOCALAPPDATA% as an empty tab, the temp folder it reads. A new file for each report: an editor
                    // that still has an older report open (Notepad keeps its tabs) would otherwise show that old copy.
                    var dir = Path.Combine(Path.GetTempPath(), "SwarlexBattery");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    foreach (var old in new DirectoryInfo(dir).GetFiles("diagnostics*.txt").OrderByDescending(x => x.LastWriteTimeUtc).Skip(2))
                        try { old.Delete(); } catch { }   // keep the two before this one
                    foreach (var old in new DirectoryInfo(Program.DataDir).GetFiles("diagnostics*.txt"))
                        try { old.Delete(); } catch { }   // reports of 1.10.0 and older were written there
                    // written beside it and swapped in at once: an editor that has the old report open would otherwise
                    // reload it while it is still empty (the write truncates first) and keep showing nothing
                    var tmp = file + ".tmp";
                    File.WriteAllText(tmp, BatteryReader.Diagnostics(snap), new System.Text.UTF8Encoding(true));
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });   // the user's own text editor
                }
                catch (Exception e) { Log.Write("diagnostics: " + e.Message); }
            });
        }

        // "Start with Windows" chosen in the old WinBar version moves over to this exe
        void MigrateWinBar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null || k.GetValue("WinBar") == null) return;
                    k.DeleteValue("WinBar", false);
                    k.SetValue("SwarlexBattery", "\"" + Program.ExePath + "\"");
                }
            }
            catch { }
        }

        // installed with SwarlexBattery-Setup: keep the version shown in Settings > Apps current after self-updates
        void UpdateUninstallVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                {
                    var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(loc) && Program.ExePath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                        k.SetValue("DisplayVersion", Program.AppVersion.ToString());
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ updates
        public void CheckUpdates(bool manual)
        {
            var repo = Config.Str("update.repo", "");
            if (UpdateBusy != null || repo == "") return;
            UpdateBusy = "check"; updateManual = manual; updateStarted = DateTime.Now;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ReleaseInfo info = null; string err = null;
                try { info = Config.Bool("update.beta", false) ? Updater.CheckWithBeta(repo) : Updater.Check(repo); } catch (Exception e) { err = e.Message; }
                // a check from the menu shows "Checking..." for at least 0.8 s, so the click visibly did something
                int wait = 800 - (int)(DateTime.Now - updateStarted).TotalMilliseconds;
                if (manual && wait > 0) Thread.Sleep(wait);
                ui.BeginInvoke(new Action(() => CheckDone(info, err)));
            });
        }

        void CheckDone(ReleaseInfo info, string err)
        {
            UpdateBusy = null;
            nextUpdateCheck = DateTime.Now.AddHours(Math.Max(1, Config.Num("update.intervalHours", 6)));
            LastCheck = DateTime.Now;
            // a manual check started from the menu shows its result in the menu; a toast only when it is closed
            bool menuOpen = flyout.Open == "menu";
            if (err != null || info == null)
            {
                Log.Write("update check: " + err);
                LastResult = "error";
                if (updateManual && !menuOpen) Toast("SwarlexBattery", Strings.T("updCheckFailed", err));
            }
            else if (info.Version != null && info.Version > Program.AppVersion)
            {
                Update = info; LastResult = "available";
                if (updateNotified != info.Version.ToString() && !menuOpen) { updateNotified = info.Version.ToString(); Toast("SwarlexBattery", Strings.T("updAvailable", info.Version)); }
            }
            else
            {
                Update = null; LastResult = "uptodate";
                if (updateManual && !menuOpen) Toast("SwarlexBattery", Strings.T("updUpToDate", Program.AppVersion));
            }
            if (menuOpen) flyout.Refresh();
        }

        public void InstallUpdate()
        {
            var info = Update;
            if (UpdateBusy != null || info == null) return;
            if (string.IsNullOrEmpty(info.Url) || string.IsNullOrEmpty(info.Sha))
            {
                Toast("SwarlexBattery", Strings.T("updNoAssets"));
                if (!string.IsNullOrEmpty(info.Page) && info.Page.StartsWith("https://github.com/")) Process.Start(info.Page);
                return;
            }
            UpdateBusy = "install";
            Toast("SwarlexBattery", Strings.T("updDownloading", info.Version));
            var repo = Config.Str("update.repo", ""); var fresh = Program.ExePath + ".new";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { Updater.Download(info, fresh, repo); } catch (Exception e) { err = e.Message; }
                ui.BeginInvoke(new Action(() => InstallDone(info, err)));
            });
        }

        void InstallDone(ReleaseInfo info, string err)
        {
            UpdateBusy = null;
            if (err != null) { Log.Write("update: " + err); Toast("SwarlexBattery", Strings.T("updFailed", err)); return; }
            string exe = Program.ExePath, old = exe + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);                     // the running exe can be renamed
                File.Move(exe + ".new", exe);
                Log.Write("updated: v" + Program.AppVersion + " -> v" + info.Version);
                Process.Start(exe);                      // waits for this instance to exit
                Exit();
            }
            catch (Exception e)
            {
                Log.Write("update swap: " + e);
                if (!File.Exists(exe) && File.Exists(old)) try { File.Move(old, exe); } catch { }
                Toast("SwarlexBattery", Strings.T("updApplyFailed", e.Message));
            }
        }

        // ------------------------------------------------------------ lifecycle
        public void Exit()
        {
            if (stopping) return; stopping = true;
            timer.Stop();
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            stopping = true;
            if (timer != null) timer.Stop();
            if (flyout != null) flyout.Dispose();
            if (tray != null) tray.Dispose();
        }
    }
}
