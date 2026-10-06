// SPDX-License-Identifier: GPL-3.0-or-later
// SwarlexBattery entry point, settings, texts and log. Pure C#: no scripts are extracted or run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("SwarlexBattery")]
[assembly: AssemblyProduct("SwarlexBattery")]
[assembly: AssemblyDescription("Battery levels of wireless mice, keyboards, headsets and controllers in the Windows tray")]
[assembly: AssemblyCompany("swarlex")]
[assembly: AssemblyCopyright("Copyright (C) 2026 swarlex. GPL-3.0-or-later")]

namespace SwarlexBattery
{
    static class Program
    {
        public static string ExePath;
        public static Version AppVersion;
        public static string DataDir, CacheDir;
        public static bool Portable;              // settings and log next to the exe (portable.txt)

        [STAThread]
        static int Main()
        {
            ExePath = Assembly.GetExecutingAssembly().Location;
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            AppVersion = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwarlexBattery");
            CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarlexBattery");
            // portable: an empty portable.txt next to the exe keeps settings, log and history in a "data" folder beside
            // it; a folder that cannot be written (e.g. a read-only stick) falls back to %APPDATA%, and the log says so
            string portableNote = null;
            var exeDir = Path.GetDirectoryName(ExePath);
            if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            {
                var dir = Path.Combine(exeDir, "data");
                try
                {
                    Directory.CreateDirectory(dir);
                    var probe = Path.Combine(dir, ".write-test"); File.WriteAllText(probe, ""); File.Delete(probe);
                    DataDir = CacheDir = dir; Portable = true;
                }
                catch (Exception e) { portableNote = "portable.txt found, but its data folder cannot be written (" + e.Message + "): using %APPDATA%"; }
            }
            Directory.CreateDirectory(DataDir); Directory.CreateDirectory(Path.Combine(CacheDir, "state"));
            Log.Init(Path.Combine(CacheDir, "swarlexbattery.log"));
            if (portableNote != null) Log.Write(portableNote);

            // right after a self-update the previous version is still closing: wait for it instead of quitting
            bool justUpdated = File.Exists(ExePath + ".old");
            var mutex = new Mutex(false, @"Local\SwarlexBattery.SingleInstance");
            bool got;
            try { got = mutex.WaitOne(justUpdated ? 20000 : 0); } catch (AbandonedMutexException) { got = true; }
            if (!got) return 0;
            if (justUpdated) { Thread.Sleep(500); try { File.Delete(ExePath + ".old"); } catch { } }
            CleanOldVersions();

            try
            {
                Config.Load();
                Strings.Load(Config.Language());
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Write("UI: " + e.Exception); e.Handled = true; };
                var host = new Host();
                host.Start();
                app.Run();
                host.Dispose();
            }
            catch (Exception e) { Log.Write("fatal: " + e); return 1; }
            finally { try { mutex.ReleaseMutex(); } catch { } }
            return 0;
        }

        // versions up to 1.3.x extracted PowerShell scripts here; nothing reads them any more
        static void CleanOldVersions()
        {
            foreach (var d in new[] { Path.Combine(CacheDir, "app") })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            try { foreach (var f in Directory.GetFiles(CacheDir, "SwarlexBattery.Native.*.dll")) File.Delete(f); } catch { }
        }
    }

    static class Log
    {
        static string file; static readonly object Sync = new object();
        public static void Init(string path)
        {
            file = path;
            try { if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Delete(file); } catch { }
        }
        // the last lines of the log (for the diagnostics report); the log may be open for writing meanwhile
        public static string[] Tail(int n)
        {
            try
            {
                lock (Sync)
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8))
                    {
                        var lines = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        var o = new string[Math.Min(n, lines.Length)];
                        Array.Copy(lines, lines.Length - o.Length, o, 0, o.Length);
                        return o;
                    }
            }
            catch { return new string[0]; }
        }

        // for errors that would repeat on every poll: each distinct message is written once per run
        static readonly HashSet<string> Seen = new HashSet<string>();
        public static void Once(string msg)
        {
            lock (Sync) { if (Seen.Count > 200 || !Seen.Add(msg)) return; }
            Write(msg);
        }

        public static void Write(string msg)
        {
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(file, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z' ") + msg + Environment.NewLine, new UTF8Encoding(false));
                    // the app runs for weeks: past 1 MB the newest quarter is kept (checked now and then, not on every line)
                    if (++writes % 50 == 0 && new FileInfo(file).Length > 1024 * 1024)
                    {
                        var all = File.ReadAllText(file, Encoding.UTF8);
                        int cut = all.IndexOf('\n', all.Length * 3 / 4);
                        File.WriteAllText(file, cut < 0 ? "" : all.Substring(cut + 1), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }
        static int writes;
    }

    // config.default.json (embedded) merged with %APPDATA%\SwarlexBattery\config.json
    static class Config
    {
        public static Dictionary<string, object> Data = new Dictionary<string, object>();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static string UserFile { get { return Path.Combine(Program.DataDir, "config.json"); } }

        public static void Load()
        {
            var def = Json.Deserialize<Dictionary<string, object>>(Resources.Text("config.default.json"));
            Dictionary<string, object> user = null;
            if (File.Exists(UserFile))
            {
                try { user = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(UserFile)); }
                catch (Exception e) { Log.Write("could not read config.json: " + e.Message); }
            }
            else
            {
                // the app used to be called WinBar: keep a config made with that version
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinBar", "config.json");
                try { if (File.Exists(old)) { File.Copy(old, UserFile); user = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(UserFile)); } else File.WriteAllText(UserFile, Resources.Text("config.default.json"), new UTF8Encoding(false)); }
                catch { }
            }
            Data = Merge(def, user);
        }

        static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var r = new Dictionary<string, object>(a);
            if (b == null) return r;
            foreach (var kv in b)
            {
                var x = r.ContainsKey(kv.Key) ? r[kv.Key] as Dictionary<string, object> : null;
                var y = kv.Value as Dictionary<string, object>;
                r[kv.Key] = (x != null && y != null) ? Merge(x, y) : kv.Value;
            }
            return r;
        }

        public static object Get(string path)
        {
            object cur = Data;
            foreach (var part in path.Split('.'))
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(part, out cur)) return null;
            }
            return cur;
        }
        public static bool Bool(string path, bool def) { var o = Get(path); return o is bool ? (bool)o : def; }
        public static double Num(string path, double def) { var o = Get(path); try { return o == null ? def : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return def; } }
        public static string Str(string path, string def) { var o = Get(path) as string; return o ?? def; }

        // "auto" follows the Windows display language when the app has it, English otherwise
        public static string Language()
        {
            var l = Str("language", "auto").ToLowerInvariant();
            if (Strings.Has(l)) return l;
            var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Strings.Has(sys) ? sys : "en";
        }

        // changes one value at a dotted path ("plugins.gadgets.interval") in the user's config.json and in the
        // settings in use, and keeps everything else; null removes it
        public static void SetPath(string path, object value) { SetParts(path.Split('.'), value); }

        // one entry of a map: the key is taken as it is (a device id may contain dots)
        public static void SetMapEntry(string mapPath, string key, string value)
        {
            var parts = mapPath.Split('.').ToList(); parts.Add(key);
            SetParts(parts.ToArray(), value);
        }

        static void SetParts(string[] parts, object value)
        {
            Dictionary<string, object> user = null;
            try { if (File.Exists(UserFile)) user = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(UserFile)); } catch { }
            if (user == null) user = new Dictionary<string, object>();
            Put(user, parts, value);
            // the settings in use are changed on a copy and swapped in at once: a battery read on another thread
            // keeps reading the old ones whole, never a dictionary that is being changed
            var copy = new Dictionary<string, object>(Data); Put(copy, parts, value); Data = copy;
            WriteUser(user);
        }

        // written beside it and swapped in: a crash or power loss in the middle leaves the old file, not half of one
        static void WriteUser(Dictionary<string, object> user)
        {
            var tmp = UserFile + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(user), new UTF8Encoding(false));
            if (File.Exists(UserFile)) File.Replace(tmp, UserFile, null); else File.Move(tmp, UserFile);
        }

        static void Put(Dictionary<string, object> d, string[] parts, object value)
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                object o; var next = d.TryGetValue(parts[i], out o) ? o as Dictionary<string, object> : null;
                // a copy: the settings in use may share a dictionary with the embedded defaults
                next = next == null ? new Dictionary<string, object>() : new Dictionary<string, object>(next);
                d[parts[i]] = next; d = next;
            }
            if (value == null) d.Remove(parts[parts.Length - 1]); else d[parts[parts.Length - 1]] = value;
        }

        // a string -> string map at a path (e.g. plugins.gadgets.names); empty when there is none
        public static Dictionary<string, string> Map(string path)
        {
            var d = Get(path) as Dictionary<string, object>;
            var o = new Dictionary<string, string>();
            if (d != null) foreach (var kv in d) if (kv.Value is string) o[kv.Key] = (string)kv.Value;
            return o;
        }

        // changes one top-level value in the user's config.json and keeps everything else
        public static void SetUser(string key, object value)
        {
            Dictionary<string, object> user = null;
            try { if (File.Exists(UserFile)) user = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(UserFile)); } catch { }
            if (user == null) user = new Dictionary<string, object>();
            user[key] = value;
            WriteUser(user);
            var copy = new Dictionary<string, object>(Data); copy[key] = value; Data = copy;
        }
    }

    // texts: lang/en.json + lang/<lang>.json (embedded); missing keys fall back to English
    static class Strings
    {
        public static string Lang = "en";
        static Dictionary<string, object> en, cur;
        // code and name (in that language) of every lang/<code>.json, in menu order
        public static readonly string[][] Languages = {
            new[] { "en", "English" }, new[] { "tr", "Türkçe" }, new[] { "de", "Deutsch" }, new[] { "es", "Español" }, new[] { "it", "Italiano" } };
        public static bool Has(string code) { return Languages.Any(x => x[0] == code); }
        public static void Load(string lang)
        {
            var js = new JavaScriptSerializer();
            en = js.Deserialize<Dictionary<string, object>>(Resources.Text("lang.en.json"));
            cur = en; Lang = "en";
            if (lang != "en") { var t = Resources.Text("lang." + lang + ".json"); if (t != null) { cur = js.Deserialize<Dictionary<string, object>>(t); Lang = lang; } }
        }
        public static string T(string key, params object[] args)
        {
            object v;
            string f = (cur.TryGetValue(key, out v) || en.TryGetValue(key, out v)) ? v as string : null;
            if (f == null) f = key;
            return args.Length > 0 ? string.Format(f, args) : f;
        }
    }

    static class Resources
    {
        public static string Text(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
}
