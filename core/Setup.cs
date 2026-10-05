// SPDX-License-Identifier: GPL-3.0-or-later
// SwarlexBattery-Setup.exe: per-user install wizard (no administrator rights) and uninstaller.
// The app exe and LICENSE are embedded as resources ("app.exe", "LICENSE").
//   SwarlexBattery-Setup.exe                     wizard
//   SwarlexBattery-Setup.exe /silent [/dir:<folder>] [/noautostart] [/lang:en|tr|de|es|it]   install without UI
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
        // the wizard's languages, in the order of each text below (the same languages as the app)
        public static readonly string[] Codes = { "en", "tr", "de", "es", "it" };
        public static readonly string[] Names = { "English", "Türkçe", "Deutsch", "Español", "Italiano" };
        public static int Lang = Math.Max(0, Array.IndexOf(Codes, Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName));
        public static string Code { get { return Codes[Lang]; } }
        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]> {
            // key                en / tr / de / es / it
            { "title",        new[] { "SwarlexBattery Setup", "SwarlexBattery Kurulumu", "SwarlexBattery-Setup", "Instalación de SwarlexBattery", "Installazione di SwarlexBattery" } },
            { "welcomeH",     new[] { "Welcome", "Hoş geldin", "Willkommen", "Bienvenido", "Benvenuto" } },
            { "welcomeS",     new[] { "This wizard installs SwarlexBattery {0}.", "Bu sihirbaz SwarlexBattery {0} sürümünü kurar.", "Dieser Assistent installiert SwarlexBattery {0}.",
                                      "Este asistente instala SwarlexBattery {0}.", "Questa procedura installa SwarlexBattery {0}." } },
            { "welcome",      new[] { "SwarlexBattery shows the battery of your wireless mouse, keyboard and headset in the system tray, next to the clock.\r\n\r\nIt is installed for your user only and does not need administrator rights.",
                                      "SwarlexBattery, kablosuz mouse, klavye ve kulaklığının pilini sistem tepsisinde, saatin yanında gösterir.\r\n\r\nSadece senin kullanıcın için kurulur ve yönetici izni gerektirmez.",
                                      "SwarlexBattery zeigt den Akkustand deiner kabellosen Maus, Tastatur und deines Headsets im Infobereich neben der Uhr.\r\n\r\nEs wird nur für deinen Benutzer installiert und braucht keine Administratorrechte.",
                                      "SwarlexBattery muestra la batería de tu ratón, teclado y auriculares inalámbricos en la bandeja del sistema, junto al reloj.\r\n\r\nSe instala solo para tu usuario y no necesita permisos de administrador.",
                                      "SwarlexBattery mostra la batteria di mouse, tastiera e cuffie wireless nell'area di notifica, accanto all'orologio.\r\n\r\nViene installato solo per il tuo utente e non richiede diritti di amministratore." } },
            { "upgrade",      new[] { "SwarlexBattery {0} is already installed. It will be upgraded; your settings are kept.", "SwarlexBattery {0} zaten kurulu. Yükseltilecek; ayarların korunur.",
                                      "SwarlexBattery {0} ist bereits installiert. Es wird aktualisiert; deine Einstellungen bleiben erhalten.",
                                      "SwarlexBattery {0} ya está instalado. Se actualizará; tu configuración se conserva.",
                                      "SwarlexBattery {0} è già installato. Verrà aggiornato; le tue impostazioni restano." } },
            { "language",     new[] { "Language:", "Dil:", "Sprache:", "Idioma:", "Lingua:" } },
            { "licenseH",     new[] { "License", "Lisans", "Lizenz", "Licencia", "Licenza" } },
            { "licenseS",     new[] { "SwarlexBattery is free software under the GNU GPL v3.", "GNU GPL v3 lisanslı özgür yazılım. Lisansın geçerli resmî metni İngilizcedir.",
                                      "Freie Software unter der GNU GPL v3. Rechtsgültig ist der englische Lizenztext.",
                                      "Software libre bajo la GNU GPL v3. El texto oficial de la licencia es el inglés.",
                                      "Software libero con licenza GNU GPL v3. Il testo ufficiale della licenza è quello inglese." } },
            { "accept",       new[] { "I accept the license", "Lisansı kabul ediyorum", "Ich akzeptiere die Lizenz", "Acepto la licencia", "Accetto la licenza" } },
            { "optionsH",     new[] { "Options", "Seçenekler", "Optionen", "Opciones", "Opzioni" } },
            { "optionsS",     new[] { "Choose where to install and what to set up.", "Nereye kurulacağını ve nelerin ayarlanacağını seç.", "Wähle den Installationsort und was eingerichtet wird.",
                                      "Elige dónde instalar y qué configurar.", "Scegli dove installare e cosa configurare." } },
            { "folder",       new[] { "Install folder:", "Kurulum klasörü:", "Installationsordner:", "Carpeta de instalación:", "Cartella di installazione:" } },
            { "browse",       new[] { "Browse...", "Gözat...", "Durchsuchen...", "Examinar...", "Sfoglia..." } },
            { "startMenu",    new[] { "Start menu shortcut", "Başlat menüsü kısayolu", "Verknüpfung im Startmenü", "Acceso directo en el menú Inicio", "Collegamento nel menu Start" } },
            { "desktop",      new[] { "Desktop shortcut", "Masaüstü kısayolu", "Verknüpfung auf dem Desktop", "Acceso directo en el escritorio", "Collegamento sul desktop" } },
            { "autostart",    new[] { "Start with Windows", "Windows ile başlat", "Mit Windows starten", "Iniciar con Windows", "Avvia con Windows" } },
            { "launch",       new[] { "Start SwarlexBattery when setup finishes", "Kurulum bitince SwarlexBattery'yi başlat", "SwarlexBattery nach der Installation starten",
                                      "Iniciar SwarlexBattery al terminar", "Avvia SwarlexBattery al termine" } },
            { "installingH",  new[] { "Installing", "Kuruluyor", "Installation", "Instalando", "Installazione" } },
            { "installingS",  new[] { "Please wait...", "Lütfen bekle...", "Bitte warten...", "Espera, por favor...", "Attendere..." } },
            { "doneH",        new[] { "Done", "Tamamlandı", "Fertig", "Listo", "Fatto" } },
            { "doneS",        new[] { "SwarlexBattery is installed.", "SwarlexBattery kuruldu.", "SwarlexBattery ist installiert.", "SwarlexBattery está instalado.", "SwarlexBattery è installato." } },
            { "done",         new[] { "The battery icon appears next to the clock. Left click shows the details, right click opens the menu.\r\n\r\nUpdates are offered in the right-click menu. You can uninstall it from Settings > Apps.",
                                      "Pil ikonu saatin yanında görünür. Sol tık detayları, sağ tık menüyü açar.\r\n\r\nGüncellemeler sağ tık menüsünde çıkar. Ayarlar > Uygulamalar'dan kaldırabilirsin.",
                                      "Das Akkusymbol erscheint neben der Uhr. Linksklick zeigt die Details, Rechtsklick öffnet das Menü.\r\n\r\nUpdates werden im Rechtsklick-Menü angeboten. Deinstallieren kannst du es unter Einstellungen > Apps.",
                                      "El icono de la batería aparece junto al reloj. Clic izquierdo muestra los detalles, clic derecho abre el menú.\r\n\r\nLas actualizaciones se ofrecen en el menú del clic derecho. Puedes desinstalarlo en Configuración > Aplicaciones.",
                                      "L'icona della batteria compare accanto all'orologio. Clic sinistro mostra i dettagli, clic destro apre il menu.\r\n\r\nGli aggiornamenti vengono offerti nel menu del clic destro. Puoi disinstallarlo da Impostazioni > App." } },
            { "back",         new[] { "< Back", "< Geri", "< Zurück", "< Atrás", "< Indietro" } },
            { "next",         new[] { "Next >", "İleri >", "Weiter >", "Siguiente >", "Avanti >" } },
            { "install",      new[] { "Install", "Kur", "Installieren", "Instalar", "Installa" } },
            { "finish",       new[] { "Finish", "Bitir", "Fertigstellen", "Finalizar", "Fine" } },
            { "cancel",       new[] { "Cancel", "İptal", "Abbrechen", "Cancelar", "Annulla" } },
            { "cancelQ",      new[] { "Cancel the setup?", "Kurulum iptal edilsin mi?", "Installation abbrechen?", "¿Cancelar la instalación?", "Annullare l'installazione?" } },
            { "stStop",       new[] { "Closing the running SwarlexBattery...", "Çalışan SwarlexBattery kapatılıyor...", "Laufendes SwarlexBattery wird beendet...",
                                      "Cerrando SwarlexBattery en ejecución...", "Chiusura di SwarlexBattery in esecuzione..." } },
            { "stCopy",       new[] { "Copying files...", "Dosyalar kopyalanıyor...", "Dateien werden kopiert...", "Copiando archivos...", "Copia dei file..." } },
            { "stShortcuts",  new[] { "Creating shortcuts...", "Kısayollar oluşturuluyor...", "Verknüpfungen werden erstellt...", "Creando accesos directos...", "Creazione dei collegamenti..." } },
            { "stRegister",   new[] { "Registering in Settings > Apps...", "Ayarlar > Uygulamalar'a kaydediliyor...", "Eintrag unter Einstellungen > Apps...",
                                      "Registrando en Configuración > Aplicaciones...", "Registrazione in Impostazioni > App..." } },
            { "failed",       new[] { "Setup failed:\r\n{0}", "Kurulum başarısız:\r\n{0}", "Installation fehlgeschlagen:\r\n{0}", "La instalación falló:\r\n{0}", "Installazione non riuscita:\r\n{0}" } },
            { "unQ",          new[] { "Uninstall SwarlexBattery?", "SwarlexBattery kaldırılsın mı?", "SwarlexBattery deinstallieren?", "¿Desinstalar SwarlexBattery?", "Disinstallare SwarlexBattery?" } },
            { "unSettings",   new[] { "Also delete your SwarlexBattery settings and logs?", "SwarlexBattery ayarların ve günlüklerin de silinsin mi?",
                                      "Auch die Einstellungen und Protokolle von SwarlexBattery löschen?", "¿Eliminar también la configuración y los registros de SwarlexBattery?",
                                      "Eliminare anche le impostazioni e i log di SwarlexBattery?" } },
            { "unDone",       new[] { "SwarlexBattery was uninstalled.", "SwarlexBattery kaldırıldı.", "SwarlexBattery wurde deinstalliert.", "SwarlexBattery se ha desinstalado.", "SwarlexBattery è stato disinstallato." } },
            { "unTitle",      new[] { "Uninstall SwarlexBattery", "SwarlexBattery'yi kaldır", "SwarlexBattery deinstallieren", "Desinstalar SwarlexBattery", "Disinstalla SwarlexBattery" } },
        };
        public static string Get(string k, params object[] a)
        {
            string[] v; if (!T.TryGetValue(k, out v)) return k;
            var f = Lang < v.Length ? v[Lang] : v[0];
            return a.Length > 0 ? string.Format(f, a) : f;
        }
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

            SetAppLanguage(S.Code);   // the language picked in the wizard is the app's language too

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
            lang.Items.AddRange(S.Names); lang.SelectedIndex = S.Lang;
            lang.SelectedIndexChanged += (s, e) => { S.Lang = Math.Max(0, lang.SelectedIndex); ApplyTexts(); };
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
                else if (x.StartsWith("/lang:", StringComparison.OrdinalIgnoreCase)) S.Lang = Math.Max(0, Array.IndexOf(S.Codes, x.Substring(6).ToLowerInvariant()));
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
