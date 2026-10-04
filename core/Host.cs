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
            timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
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
            if (!polling && now >= nextPoll) Poll();

            if (UpdateBusy == null && Config.Bool("update.check", true) && now >= nextUpdateCheck) CheckUpdates(false);

            // taskbar theme switch -> repaint
            if (now >= themeAt)
            {
                themeAt = now.AddSeconds(5);
                bool light = TrayRenderer.IsLightTaskbar();
                if (light != TrayRenderer.LightTaskbar) { TrayRenderer.LightTaskbar = light; tray.Repaint(); }
            }
        }

        // ------------------------------------------------------------ batteries
        public void PollSoon() { nextPoll = DateTime.MinValue; if (!polling) Poll(); }

        void Poll()
        {
            polling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Snapshot snap = null;
                try { snap = BatteryReader.Build(reader.Read()); }
                catch (Exception e) { Log.Once("poll: " + e); }
                ui.BeginInvoke(new Action(() =>
                {
                    polling = false;
                    nextPoll = DateTime.Now.AddSeconds(flyout.Open == "panel" ? 5 : BatteryReader.PollSeconds);
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
                if (!notified.ContainsKey(n.Key)) Toast(n.Title, n.Body, ToolTipIcon.Warning);
                notified[n.Key] = now;
            }
            foreach (var k in notified.Keys.ToList()) if ((now - notified[k]).TotalMinutes > 30) notified.Remove(k);
            if (flyout.Open == "panel") flyout.Refresh(); else flyout.Warm();
        }

        void Toast(string title, string body, ToolTipIcon kind = ToolTipIcon.Info)
        {
            if (string.IsNullOrEmpty(title)) return;
            if (Config.Bool("quietWhileGaming", true) && Win.ForegroundIsFullscreen()) return;
            tray.Balloon(title, body, kind);
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

        // "Language": toggles en <-> tr, saves it in config.json and re-polls in the new language
        public void SwitchLanguage()
        {
            var next = Strings.Lang == "tr" ? "en" : "tr";
            try { Config.SetUser("language", next); } catch (Exception e) { Log.Write("language save: " + e.Message); }
            Strings.Load(next);
            PollSoon();
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
                try { info = Updater.Check(repo); } catch (Exception e) { err = e.Message; }
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
