// SPDX-License-Identifier: GPL-3.0-or-later
// SwarlexBattery-Setup.exe: per-user install wizard (no administrator rights) and uninstaller.
// The app exe and LICENSE are embedded as resources ("app.exe", "LICENSE").
//   SwarlexBattery-Setup.exe                     wizard
//   SwarlexBattery-Setup.exe /silent [/dir:<folder>] [/noautostart] [/lang:en|tr]   install without UI
//   Uninstall.exe /uninstall [/silent]           (copied into the install folder; listed in Settings > Apps)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwarlexBatterySetup
{
    static class S
    {
        public static bool Tr = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "tr";
        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]> {
            // key                en                                                      tr
            { "title",        new[] { "SwarlexBattery Setup", "SwarlexBattery Kurulumu" } },
            { "welcomeH",     new[] { "Welcome", "Hoş geldin" } },
            { "welcomeS",     new[] { "This wizard installs SwarlexBattery {0}.", "Bu sihirbaz SwarlexBattery {0} sürümünü kurar." } },
            { "welcome",      new[] { "SwarlexBattery shows the battery of your wireless mouse, keyboard and headset in the system tray, next to the clock.\r\n\r\nIt is installed for your user only and does not need administrator rights.",
                                      "SwarlexBattery, kablosuz mouse, klavye ve kulaklığının pilini sistem tepsisinde, saatin yanında gösterir.\r\n\r\nSadece senin kullanıcın için kurulur ve yönetici izni gerektirmez." } },
            { "upgrade",      new[] { "SwarlexBattery {0} is already installed. It will be upgraded; your settings are kept.", "SwarlexBattery {0} zaten kurulu. Yükseltilecek; ayarların korunur." } },
            { "language",     new[] { "Language:", "Dil:" } },
            { "licenseH",     new[] { "License", "Lisans" } },
            { "licenseS",     new[] { "SwarlexBattery is free software under the GNU GPL v3.", "GNU GPL v3 lisanslı özgür yazılım. Lisansın geçerli resmî metni İngilizcedir." } },
            { "accept",       new[] { "I accept the license", "Lisansı kabul ediyorum" } },
            { "optionsH",     new[] { "Options", "Seçenekler" } },
            { "optionsS",     new[] { "Choose where to install and what to set up.", "Nereye kurulacağını ve nelerin ayarlanacağını seç." } },
            { "folder",       new[] { "Install folder:", "Kurulum klasörü:" } },
            { "browse",       new[] { "Browse...", "Gözat..." } },
            { "startMenu",    new[] { "Start menu shortcut", "Başlat menüsü kısayolu" } },
            { "desktop",      new[] { "Desktop shortcut", "Masaüstü kısayolu" } },
            { "autostart",    new[] { "Start with Windows", "Windows ile başlat" } },
            { "launch",       new[] { "Start SwarlexBattery when setup finishes", "Kurulum bitince SwarlexBattery'yi başlat" } },
            { "installingH",  new[] { "Installing", "Kuruluyor" } },
            { "installingS",  new[] { "Please wait...", "Lütfen bekle..." } },
            { "doneH",        new[] { "Done", "Tamamlandı" } },
            { "doneS",        new[] { "SwarlexBattery is installed.", "SwarlexBattery kuruldu." } },
            { "done",         new[] { "The battery icon appears next to the clock. Left click shows the details, right click opens the menu.\r\n\r\nUpdates are offered in the right-click menu. You can uninstall it from Settings > Apps.",
                                      "Pil ikonu saatin yanında görünür. Sol tık detayları, sağ tık menüyü açar.\r\n\r\nGüncellemeler sağ tık menüsünde çıkar. Ayarlar > Uygulamalar'dan kaldırabilirsin." } },
            { "back",         new[] { "< Back", "< Geri" } },
            { "next",         new[] { "Next >", "İleri >" } },
            { "install",      new[] { "Install", "Kur" } },
            { "finish",       new[] { "Finish", "Bitir" } },
            { "cancel",       new[] { "Cancel", "İptal" } },
            { "cancelQ",      new[] { "Cancel the setup?", "Kurulum iptal edilsin mi?" } },
            { "stStop",       new[] { "Closing the running SwarlexBattery...", "Çalışan SwarlexBattery kapatılıyor..." } },
            { "stCopy",       new[] { "Copying files...", "Dosyalar kopyalanıyor..." } },
            { "stShortcuts",  new[] { "Creating shortcuts...", "Kısayollar oluşturuluyor..." } },
            { "stRegister",   new[] { "Registering in Settings > Apps...", "Ayarlar > Uygulamalar'a kaydediliyor..." } },
            { "failed",       new[] { "Setup failed:\r\n{0}", "Kurulum başarısız:\r\n{0}" } },
            { "unQ",          new[] { "Uninstall SwarlexBattery?", "SwarlexBattery kaldırılsın mı?" } },
            { "unSettings",   new[] { "Also delete your SwarlexBattery settings and logs?", "SwarlexBattery ayarların ve günlüklerin de silinsin mi?" } },
            { "unDone",       new[] { "SwarlexBattery was uninstalled.", "SwarlexBattery kaldırıldı." } },
            { "unTitle",      new[] { "Uninstall SwarlexBattery", "SwarlexBattery'yi kaldır" } },
        };
        public static string Get(string k, params object[] a) { string[] v; if (!T.TryGetValue(k, out v)) return k; var f = Tr ? v[1] : v[0]; return a.Length > 0 ? string.Format(f, a) : f; }
    }

    static class Installer
    {
        public const string AppName = "SwarlexBattery";
        public const string ExeName = "SwarlexBattery.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwarlexBattery";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RepoUrl = "https://github.com/swarlex/SwarlexBattery";

        public static string Version { get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } }
        public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null ? null : k.GetValue("InstallLocation") as string;
        }
        public static string InstalledVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null ? null : k.GetValue("DisplayVersion") as string;
        }

        static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        public static void StopRunning(string exePath)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    string path = null; try { path = p.MainModule.FileName; } catch { }
                    if (path == null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(5000); }
                }
                catch { }
            }
        }

        static void Shortcut(string lnk, string target, string dir)
        {
            // WScript.Shell through reflection: no extra assembly needed
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            var st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "SwarlexBattery" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static void Install(string dir, bool startMenu, bool desktop, bool autostart, Action<string, int> progress)
        {
            dir = Path.GetFullPath(dir);
            string exe = Path.Combine(dir, ExeName);
            progress(S.Get("stStop"), 10);
            StopRunning(exe);
            Thread.Sleep(300);

            progress(S.Get("stCopy"), 35);
            Directory.CreateDirectory(dir);
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe"))
            using (var dst = File.Create(exe + ".new")) src.CopyTo(dst);
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(exe + ".new", exe);
            foreach (var old in new[] { exe + ".old" }) { try { if (File.Exists(old)) File.Delete(old); } catch { } }
            string me = Assembly.GetExecutingAssembly().Location;
            string uninst = Path.Combine(dir, "Uninstall.exe");
            if (!string.Equals(Path.GetFullPath(me), uninst, StringComparison.OrdinalIgnoreCase)) File.Copy(me, uninst, true);
            using (var lic = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE"))
            using (var dst = File.Create(Path.Combine(dir, "LICENSE.txt"))) lic.CopyTo(dst);

            progress(S.Get("stShortcuts"), 65);
            if (startMenu) Shortcut(StartMenuLnk, exe, dir); else TryDelete(StartMenuLnk);
            if (desktop) Shortcut(DesktopLnk, exe, dir); else TryDelete(DesktopLnk);
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autostart) run.SetValue(AppName, "\"" + exe + "\"");
                else if (run.GetValue(AppName) != null) run.DeleteValue(AppName, false);
            }

            // the language picked in the wizard is the app's language too; the wizard itself is English or Turkish,
            // so English on a German, Spanish or Italian Windows means the app's own language for it
            string sys = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
            SetAppLanguage(S.Tr ? "tr" : (sys == "de" || sys == "es" || sys == "it") ? sys : "en");

            progress(S.Get("stRegister"), 85);
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "swarlex");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /silent");
                k.SetValue("URLInfoAbout", RepoUrl);
                k.SetValue("HelpLink", RepoUrl + "/issues");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = 0; foreach (var f in Directory.GetFiles(dir)) size += new FileInfo(f).Length;
                k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            progress(S.Get("doneS"), 100);
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        // Writes "language" into %APPDATA%\SwarlexBattery\config.json and keeps every other setting.
        // A config file that cannot be read is left alone rather than overwritten.
        static void SetAppLanguage(string lang)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            string file = Path.Combine(dir, "config.json");
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            Dictionary<string, object> cfg = null;
            if (File.Exists(file))
            {
                try { cfg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); }
                catch { return; }
            }
            if (cfg == null) cfg = new Dictionary<string, object>();
            cfg["language"] = lang;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, js.Serialize(cfg), new System.Text.UTF8Encoding(false));
        }

        public static void Uninstall(string dir, bool deleteSettings)
        {
            string exe = Path.Combine(dir, ExeName);
            StopRunning(exe);
            Thread.Sleep(300);
            TryDelete(StartMenuLnk); TryDelete(DesktopLnk);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run != null && run.GetValue(AppName) != null) run.DeleteValue(AppName, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\Swarlex.SwarlexBattery", false);   // Windows notification registration
            foreach (var f in new[] { exe, exe + ".old", exe + ".new", Path.Combine(dir, "LICENSE.txt"), Path.Combine(dir, "Uninstall.exe") }) TryDelete(f);
            try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
            if (deleteSettings)
            {
                foreach (var d in new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName) })
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
        }
    }

    class Wizard : Form
    {
        readonly Panel header = new Panel(), body = new Panel(), footer = new Panel();
        readonly Label hTitle = new Label(), hSub = new Label();
        readonly Button back = new Button(), next = new Button(), cancel = new Button();
        readonly List<Panel> pages = new List<Panel>();
        int page;
        readonly ComboBox lang = new ComboBox();
        readonly Label welcomeText = new Label(), upgradeText = new Label(), langLabel = new Label(), doneText = new Label(), status = new Label(), folderLabel = new Label();
        readonly TextBox license = new TextBox(), folder = new TextBox();
        readonly CheckBox accept = new CheckBox(), cbStart = new CheckBox(), cbDesk = new CheckBox(), cbAuto = new CheckBox(), cbLaunch = new CheckBox();
        readonly Button browse = new Button();
        readonly ProgressBar bar = new ProgressBar();
        readonly string existingVersion;
        bool installed;

        public Wizard()
        {
            existingVersion = Installer.InstalledVersion();
            Text = S.Get("title");
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            ClientSize = new Size(600, 430); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9.5f);
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;

            header.Dock = DockStyle.Top; header.Height = 72; header.BackColor = Color.White;
            var pic = new PictureBox { Image = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(40, 40), Location = new Point(538, 16) };
            hTitle.Font = new Font("Segoe UI Semibold", 13f); hTitle.Location = new Point(20, 12); hTitle.AutoSize = true;
            hSub.Location = new Point(22, 42); hSub.AutoSize = true; hSub.ForeColor = Color.DimGray;
            header.Controls.AddRange(new Control[] { pic, hTitle, hSub });
            var line = new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.Gainsboro };

            footer.Dock = DockStyle.Bottom; footer.Height = 52;
            var fline = new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.Gainsboro };
            foreach (var b in new[] { back, next, cancel }) { b.Size = new Size(92, 30); b.FlatStyle = FlatStyle.System; footer.Controls.Add(b); }
            back.Location = new Point(300, 12); next.Location = new Point(396, 12); cancel.Location = new Point(498, 12);
            footer.Controls.Add(fline);
            back.Click += (s, e) => Show(page - 1);
            next.Click += (s, e) => Next();
            cancel.Click += (s, e) => Close();

            body.Dock = DockStyle.Fill; body.Padding = new Padding(24, 16, 24, 8);
            Controls.Add(body); Controls.Add(line); Controls.Add(header); Controls.Add(footer);

            // 0 welcome
            var p0 = Page();
            welcomeText.SetBounds(0, 0, 550, 110);
            upgradeText.SetBounds(0, 116, 550, 44); upgradeText.ForeColor = Color.FromArgb(0, 95, 184);
            langLabel.SetBounds(0, 186, 80, 24); langLabel.TextAlign = ContentAlignment.MiddleLeft;
            lang.SetBounds(84, 186, 160, 24); lang.DropDownStyle = ComboBoxStyle.DropDownList;
            lang.Items.AddRange(new object[] { "English", "Türkçe" }); lang.SelectedIndex = S.Tr ? 1 : 0;
            lang.SelectedIndexChanged += (s, e) => { S.Tr = lang.SelectedIndex == 1; ApplyTexts(); };
            p0.Controls.AddRange(new Control[] { welcomeText, upgradeText, langLabel, lang });

            // 1 license
            var p1 = Page();
            license.Multiline = true; license.ReadOnly = true; license.ScrollBars = ScrollBars.Vertical; license.BackColor = Color.White;
            license.Font = new Font("Consolas", 8.5f); license.SetBounds(0, 0, 550, 226);
            using (var r = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE"))) license.Text = r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
            accept.SetBounds(0, 234, 400, 24);
            accept.CheckedChanged += (s, e) => UpdateButtons();
            p1.Controls.AddRange(new Control[] { license, accept });

            // 2 options
            var p2 = Page();
            folderLabel.SetBounds(0, 0, 300, 22);
            folder.SetBounds(0, 24, 450, 26); folder.Text = Installer.InstalledDir() ?? Installer.DefaultDir;
            browse.SetBounds(458, 23, 92, 28); browse.FlatStyle = FlatStyle.System;
            browse.Click += (s, e) => { using (var d = new FolderBrowserDialog { SelectedPath = folder.Text }) if (d.ShowDialog(this) == DialogResult.OK) folder.Text = Path.Combine(d.SelectedPath, Installer.AppName); };
            cbStart.SetBounds(0, 72, 400, 24); cbStart.Checked = true;
            cbDesk.SetBounds(0, 100, 400, 24);
            cbAuto.SetBounds(0, 128, 400, 24); cbAuto.Checked = true;
            cbLaunch.SetBounds(0, 156, 400, 24); cbLaunch.Checked = true;
            p2.Controls.AddRange(new Control[] { folderLabel, folder, browse, cbStart, cbDesk, cbAuto, cbLaunch });

            // 3 installing
            var p3 = Page();
            status.SetBounds(0, 40, 550, 24); bar.SetBounds(0, 70, 550, 22);
            p3.Controls.AddRange(new Control[] { status, bar });

            // 4 done
            var p4 = Page();
            doneText.SetBounds(0, 0, 550, 120);
            p4.Controls.Add(doneText);

            FormClosing += (s, e) =>
            {
                if (!installed && page != 3 && e.CloseReason == CloseReason.UserClosing &&
                    MessageBox.Show(this, S.Get("cancelQ"), S.Get("title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
                if (page == 3 && !installed) e.Cancel = true;
            };
            ApplyTexts();
            Show(0);
        }

        Panel Page() { var p = new Panel { Dock = DockStyle.Fill, Visible = false }; body.Controls.Add(p); pages.Add(p); return p; }

        void ApplyTexts()
        {
            Text = S.Get("title");
            welcomeText.Text = S.Get("welcome");
            upgradeText.Text = existingVersion != null ? S.Get("upgrade", existingVersion) : "";
            langLabel.Text = S.Get("language"); accept.Text = S.Get("accept");
            folderLabel.Text = S.Get("folder"); browse.Text = S.Get("browse");
            cbStart.Text = S.Get("startMenu"); cbDesk.Text = S.Get("desktop"); cbAuto.Text = S.Get("autostart"); cbLaunch.Text = S.Get("launch");
            doneText.Text = S.Get("done");
            back.Text = S.Get("back"); cancel.Text = S.Get("cancel");
            Show(page);
        }

        void Show(int i)
        {
            if (i < 0 || i >= pages.Count) return;
            page = i;
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            string[] h = { "welcomeH", "licenseH", "optionsH", "installingH", "doneH" };
            string[] sub = { "welcomeS", "licenseS", "optionsS", "installingS", "doneS" };
            hTitle.Text = S.Get(h[i]); hSub.Text = S.Get(sub[i], Installer.Version);
            UpdateButtons();
        }

        void UpdateButtons()
        {
            back.Enabled = page > 0 && page < 3;
            next.Text = page == 2 ? S.Get("install") : page == 4 ? S.Get("finish") : S.Get("next");
            next.Enabled = page != 3 && (page != 1 || accept.Checked);
            cancel.Enabled = page < 3;
            if (page == 4) cancel.Visible = false;
        }

        void Next()
        {
            if (page == 2) { Show(3); DoInstall(); return; }
            if (page == 4)
            {
                if (cbLaunch.Checked) try { Process.Start(Path.Combine(folder.Text, Installer.ExeName)); } catch { }
                Close(); return;
            }
            Show(page + 1);
        }

        void DoInstall()
        {
            string dir = folder.Text; bool sm = cbStart.Checked, dk = cbDesk.Checked, au = cbAuto.Checked;
            var t = new Thread(() =>
            {
                try
                {
                    Installer.Install(dir, sm, dk, au, (msg, pct) => BeginInvoke((Action)(() => { status.Text = msg; bar.Value = pct; })));
                    BeginInvoke((Action)(() => { installed = true; Show(4); }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() => { MessageBox.Show(this, S.Get("failed", ex.Message), S.Get("title"), MessageBoxButtons.OK, MessageBoxIcon.Error); Show(2); }));
                }
            });
            t.IsBackground = true; t.Start();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var a = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string dirArg = null;
            foreach (var x in args)
            {
                if (x.StartsWith("/dir:", StringComparison.OrdinalIgnoreCase)) dirArg = x.Substring(5);
                else if (x.StartsWith("/lang:", StringComparison.OrdinalIgnoreCase)) S.Tr = x.Substring(6).Equals("tr", StringComparison.OrdinalIgnoreCase);
                else a.Add(x);
            }
            bool silent = a.Contains("/silent") || a.Contains("/S");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (a.Contains("/uninstall")) return Uninstall(dirArg, silent);
                if (silent) { Installer.Install(dirArg ?? Installer.InstalledDir() ?? Installer.DefaultDir, true, false, !a.Contains("/noautostart"), (m, p) => { }); return 0; }
                Application.Run(new Wizard());
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show(S.Get("failed", e.Message), S.Get("title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int Uninstall(string dir, bool silent)
        {
            string me = Assembly.GetExecutingAssembly().Location;
            if (dir == null)
            {
                // running from the install folder: continue from a temp copy so the folder can be deleted
                dir = Installer.InstalledDir() ?? Path.GetDirectoryName(me);
                if (!silent && MessageBox.Show(S.Get("unQ"), S.Get("unTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                bool del = !silent && MessageBox.Show(S.Get("unSettings"), S.Get("unTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                string tmp = Path.Combine(Path.GetTempPath(), "SwarlexBattery-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(me, tmp, true);
                var psi = new ProcessStartInfo(tmp, "/uninstall /continue" + (silent ? " /silent" : "") + (del ? " /settings" : "") + " \"/dir:" + dir + "\"") { UseShellExecute = false };
                Process.Start(psi);
                return 0;
            }
            var args = new HashSet<string>(Environment.GetCommandLineArgs(), StringComparer.OrdinalIgnoreCase);
            Thread.Sleep(500);   // let the original uninstaller exit
            Installer.Uninstall(dir, args.Contains("/settings"));
            if (!silent) MessageBox.Show(S.Get("unDone"), S.Get("unTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            // remove this temp copy shortly after exiting
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + me + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            return 0;
        }
    }
}
