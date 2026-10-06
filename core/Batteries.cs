// SPDX-License-Identifier: GPL-3.0-or-later
// Battery sources and the rules that turn readings into what the tray and the flyout show.
// Honesty rules: a value is shown only when the device itself answered. One missed answer keeps the
// last value for 45 s (no flicker); after that the device is shown dimmed as asleep with the age of
// its last reading; after 24 h it disappears.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SwarlexBattery
{
    class Gadget
    {
        public string Id, Name, Kind, Detail, Glyph;   // Glyph: tray / panel icon when it is not the kind's default
        public string Hint;                              // a device that gives no level at all: why (shown instead of a level)
        public string OwnName;                           // the device's own name when the user renamed it
        public Gadget Copy() { return (Gadget)MemberwiseClone(); }
        public int Pct;
        public bool Charging, Approx, Online, Asleep;
        public double HoursLeft = -1;                     // estimated hours of use left, -1 = no estimate
    }

    class TraySpec
    {
        public string Id, Icon, State, Tooltip; public double? Ring; public double[] Rings; public bool Charging, Dim; public int Percent = -1;
        public bool[] RingCharging;              // which ring belongs to a charging device (the charging animation)
        public TraySpec Copy() { var c = (TraySpec)MemberwiseClone(); if (Rings != null) c.Rings = (double[])Rings.Clone(); return c; }
    }
    class PanelItem { public string Icon, Label, Value, State, Sub; public double Pct; public string Id, OwnName, IconChoice; }
    class Notice { public string Key, Title, Body; public int Pct = -1; public bool Info; }   // Info: good news (charged), not a warning
    class Snapshot { public List<TraySpec> Icons = new List<TraySpec>(); public List<PanelItem> Items = new List<PanelItem>(); public string Empty; public List<Notice> Notify = new List<Notice>(); public string Title; }

    class BatteryReader
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        readonly string lastFile = Path.Combine(Program.CacheDir, "state", "gadgets", "hid-last.json");
        Dictionary<string, Dictionary<string, object>> last = new Dictionary<string, Dictionary<string, object>>();
        List<Gadget> slow = new List<Gadget>(); DateTime slowAt = DateTime.MinValue;
        string lastSeen; readonly HashSet<string> loggedStates = new HashSet<string>();

        // ------------------------------------------------------------ estimated time left
        // Per device, the level against the time it was awake and on battery since its last charge. Gaps
        // longer than a few polls (asleep, switched off, PC suspended) are not counted as use. An estimate
        // needs 30 minutes of use and a 3-point drop; it comes from a least-squares line through the points.
        class Track { public long Last; public double Use; public List<double[]> Pts = new List<double[]>(); }
        readonly string histFile = Path.Combine(Program.CacheDir, "state", "gadgets", "history.json");
        Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        long histSavedAt; bool histDirty;

        void LoadHistory()
        {
            try
            {
                if (!File.Exists(histFile)) return;
                foreach (var kv in Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(histFile)))
                {
                    var d = kv.Value as Dictionary<string, object>; if (d == null) continue;
                    var t = new Track { Last = Convert.ToInt64(d["last"]), Use = Convert.ToDouble(d["use"], CultureInfo.InvariantCulture) };
                    foreach (var o in (System.Collections.ArrayList)d["pts"]) { var a = (System.Collections.ArrayList)o; t.Pts.Add(new[] { Convert.ToDouble(a[0], CultureInfo.InvariantCulture), Convert.ToDouble(a[1], CultureInfo.InvariantCulture) }); }
                    tracks[kv.Key] = t;
                }
            }
            catch { tracks.Clear(); }
        }

        void Estimate(List<Gadget> list)
        {
            if (!Config.Bool(S("timeLeft"), true)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double gap = Math.Max(330, 3 * PollSeconds + 30);
            foreach (var g in list)
            {
                if (!g.Online || g.Asleep) continue;
                if (g.Charging) { if (tracks.Remove(g.Id)) histDirty = true; continue; }    // a new charge starts a new history
                if (g.Approx) continue;                                                     // coarse steps make no slope
                Track t;
                if (!tracks.TryGetValue(g.Id, out t)) { t = new Track(); tracks[g.Id] = t; }
                if (t.Pts.Count > 0 && g.Pct > t.Pts[t.Pts.Count - 1][1] + 5) { t = new Track(); tracks[g.Id] = t; }  // charged somewhere else
                double dt = now - t.Last;
                if (t.Last > 0 && dt > 0 && dt <= gap) t.Use += dt;
                t.Last = now;
                var lastPt = t.Pts.Count > 0 ? t.Pts[t.Pts.Count - 1] : null;
                if (lastPt == null || lastPt[1] != g.Pct || t.Use - lastPt[0] >= 300)
                {
                    t.Pts.Add(new[] { t.Use, (double)g.Pct }); if (t.Pts.Count > 400) t.Pts.RemoveAt(0); histDirty = true;
                }
                g.HoursLeft = HoursLeft(t.Pts, t.Use, g.Pct);
            }
            if (histDirty && now - histSavedAt >= 300)
            {
                try
                {
                    var o = tracks.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> { { "last", kv.Value.Last }, { "use", kv.Value.Use }, { "pts", kv.Value.Pts } });
                    File.WriteAllText(histFile, Json.Serialize(o), new UTF8Encoding(false)); histSavedAt = now; histDirty = false;
                }
                catch { }
            }
        }

        // Hours of use left, or -1. pts = { use seconds, level } since the last charge, use = the use time now.
        //  * Only the moments a new, lower level first appeared count ("edges"): many devices report in steps
        //    (5 % for the VXE / ATK mice), and the time spent on one step says nothing until the next one comes.
        //  * The first 10 minutes of a history are left out: right after a charge or a start the reading settles
        //    (a BlackShark V2 HyperSpeed showed 89 -> 79 % in 3 minutes, then 1-2 % an hour).
        //  * A weighted least-squares line: an edge counts half as much for every 3 hours of use it is older,
        //    so the estimate follows how the device is used now.
        //  * A device that has stayed on its level for longer than that rate allows is draining slower now: the
        //    rate is capped at one step over that time, and the time already spent on the level is taken off.
        // Needs 3 edges over at least 30 minutes of use and a 3-point drop.
        public static double HoursLeft(List<double[]> pts, double use, int pct)
        {
            if (pts == null || pts.Count < 3 || pct <= 0) return -1;
            double start = pts[0][0] + 600;
            var edges = new List<double[]>();
            double prev = double.MaxValue;
            foreach (var p in pts)
            {
                if (p[1] < prev && p[1] < 100 && p[0] >= start) edges.Add(p);
                prev = Math.Min(prev, p[1]);
            }
            if (edges.Count < 3) return -1;
            var last = edges[edges.Count - 1];
            if (last[0] - edges[0][0] < 1800 || edges[0][1] - last[1] < 3) return -1;
            double sw = 0, mx = 0, my = 0;
            var w = edges.Select(e => Math.Pow(0.5, (last[0] - e[0]) / 10800.0)).ToList();
            for (int i = 0; i < edges.Count; i++) { sw += w[i]; mx += w[i] * edges[i][0]; my += w[i] * edges[i][1]; }
            mx /= sw; my /= sw;
            double sxy = 0, sxx = 0;
            for (int i = 0; i < edges.Count; i++) { sxy += w[i] * (edges[i][0] - mx) * (edges[i][1] - my); sxx += w[i] * (edges[i][0] - mx) * (edges[i][0] - mx); }
            if (sxx <= 0 || sxy >= 0) return -1;
            double rate = -sxy / sxx;                                                   // points per second
            var steps = new List<double>();
            for (int i = 1; i < edges.Count; i++) steps.Add(edges[i - 1][1] - edges[i][1]);
            steps.Sort();
            double step = Math.Max(1, steps[steps.Count / 2]);
            double since = Math.Max(0, use - last[0]);
            // slower now, but at most 3 times slower than the fitted rate: a device left idle for hours would
            // otherwise get an ever longer estimate
            if (since > 0) rate = Math.Max(rate / 3, Math.Min(rate, step / since));
            double hours = (Math.Min(pct, last[1]) / rate - since) / 3600;
            return hours > 0 && hours < 500 ? hours : -1;
        }

        public static string TimeLeft(double hours)
        {
            if (hours < 1) return Strings.T("timeLeftM", Math.Max(5, (int)Math.Round(hours * 12) * 5));
            return Strings.T("timeLeftH", hours < 10 ? Math.Round(hours * 2) / 2 : Math.Round(hours));
        }

        // ------------------------------------------------------------ menu > status file for other apps (Rainmeter, Stream Deck, scripts)
        public static void WriteStatus(List<Gadget> list)
        {
            if (!Config.Bool("statusFile", false)) return;
            try
            {
                var o = new Dictionary<string, object> {
                    { "updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, { "version", Program.AppVersion.ToString() },
                    { "devices", View(list).Where(g => g.Hint == null).Select(g => new Dictionary<string, object> {
                        { "id", g.Id }, { "name", g.Name }, { "kind", g.Kind }, { "level", g.Pct }, { "charging", g.Charging }, { "online", g.Online },
                        { "asleep", g.Asleep }, { "approximate", g.Approx }, { "hoursLeft", g.HoursLeft > 0 ? (object)Math.Round(g.HoursLeft, 1) : null } }).ToList() } };
                string file = Path.Combine(Program.DataDir, "status.json"), tmp = file + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(o), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);   // readers never see half a file
            }
            catch (Exception e) { Log.Once("status.json: " + e.Message); }
        }

        static string S(string key) { return "plugins.gadgets." + key; }

        public BatteryReader()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lastFile));
            try
            {
                if (File.Exists(lastFile))
                    foreach (var kv in Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(lastFile)))
                    {
                        // only complete entries: a damaged file must not break every later poll
                        var d = kv.Value as Dictionary<string, object>;
                        long ts; int pct;
                        if (d != null && d.ContainsKey("ts") && d.ContainsKey("pct") && long.TryParse(Convert.ToString(d["ts"], CultureInfo.InvariantCulture), out ts)
                            && int.TryParse(Convert.ToString(d["pct"], CultureInfo.InvariantCulture), out pct) && pct >= 0 && pct <= 100) last[kv.Key] = d;
                    }
            }
            catch { }
            LoadHistory();
        }

        static readonly Dictionary<string, string> KindIcons = new Dictionary<string, string> {
            { "earbuds", "E7F6" }, { "headphones", "E7F6" }, { "mouse", "E962" }, { "keyboard", "E765" }, { "gamepad", "E7FC" },
            { "pen", "EDC6" }, { "watch", "E916" }, { "speaker", "E7F5" }, { "phone", "E8EA" }, { "laptop", "E7F8" }, { "other", "E702" } };
        public static string IconFor(string kind) { string i; return KindIcons.TryGetValue(kind ?? "other", out i) ? i : KindIcons["other"]; }

        static string GuessKind(string name, string cls)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, "buds|airpods|earbud|wf-|freebuds|pods")) return "earbuds";
            if (Regex.IsMatch(n, @"headset|headphone|wh-|bose|qc\d|jbl|sony|arctis|hyperx|corsair")) return "headphones";
            if (Regex.IsMatch(n, @"mouse|mx master|mx anywhere|g\d{3}|viper|deathadder|razer")) return "mouse";
            if (Regex.IsMatch(n, @"keyboard|keys|mx keys|k\d{3}")) return "keyboard";
            if (Regex.IsMatch(n, "controller|xbox|dualsense|dualshock|gamepad|pro controller")) return "gamepad";
            if (Regex.IsMatch(n, "pen|stylus")) return "pen";
            if (n.Contains("watch")) return "watch";
            if (Regex.IsMatch(n, "speaker|soundcore|boom|flip|charge")) return "speaker";
            if (Regex.IsMatch(n, "phone|iphone|galaxy|pixel")) return "phone";
            if (cls == "Mouse") return "mouse"; if (cls == "Keyboard") return "keyboard";
            return "other";
        }

        // ------------------------------------------------------------ menu > Diagnostics: one file for a device report
        // Everything a report needs, with Bluetooth MAC addresses and Logitech unit ids (serials) masked.
        public static string Diagnostics(Snapshot snap)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SwarlexBattery v" + Program.AppVersion + "  |  " + Environment.OSVersion.VersionString + "  |  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("Windows notifications for this app: " + Toasts.Setting());
            sb.AppendLine("Settings and log: " + (Program.Portable ? "portable (the data folder next to the exe)" : "%APPDATA% / %LOCALAPPDATA%"));
            sb.AppendLine();
            sb.AppendLine("=== Shown now ===");
            if (snap == null || snap.Items.Count == 0) sb.AppendLine("(nothing)");
            else foreach (var it in snap.Items) sb.AppendLine(it.Label + ": " + it.Value + (string.IsNullOrEmpty(it.Sub) ? "" : "  (" + it.Sub + ")"));
            sb.AppendLine();
            sb.AppendLine("=== Fresh read of supported devices ===");
            try
            {
                var rs = Hid.ReadAll();
                if (rs.Length == 0) sb.AppendLine("(no supported device found)");
                foreach (var r in rs) sb.AppendLine(r + (r.Receiver ? "  [receiver]" : "") + (r.Level < 0 ? "  <- no answer" : ""));
            }
            catch (Exception e) { sb.AppendLine("error: " + e.Message); }
            sb.AppendLine();
            sb.AppendLine("=== Protocol details (last steps) ===");
            lock (Hid.Trace) { if (Hid.Trace.Count == 0) sb.AppendLine("(none)"); foreach (var t in Hid.Trace.Skip(Math.Max(0, Hid.Trace.Count - 40))) sb.AppendLine(t); }
            sb.AppendLine();
            sb.AppendLine("=== Bluetooth (the level Windows reports) ===");
            try
            {
                var bt = BluetoothBattery.List();
                if (bt.Count == 0) sb.AppendLine("(none)");
                foreach (var d in bt) sb.AppendLine(d.Name + ": " + d.Level + "%" + (d.Connected ? "" : "  (not connected)"));
            }
            catch (Exception e) { sb.AppendLine("error: " + e.Message); }
            sb.AppendLine();
            sb.AppendLine("=== All HID devices ===");
            try
            {
                foreach (var d in Hid.List(null).OrderBy(d => d.Vid).ThenBy(d => d.Pid).ThenBy(d => d.Interface).ThenBy(d => d.UsagePage))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "VID={0:x4} PID={1:x4} if={2} usage={3:x4}:{4:x4} in={5} out={6} feat={7} '{8}'",
                        d.Vid, d.Pid, d.Interface, d.UsagePage, d.Usage, d.InLen, d.OutLen, d.FeatLen, d.Product));
            }
            catch (Exception e) { sb.AppendLine("error: " + e.Message); }
            sb.AppendLine();
            sb.AppendLine("=== Recent log ===");
            foreach (var l in Log.Tail(30)) sb.AppendLine(l);
            // a report is meant to be posted publicly: no MAC addresses, no serial numbers
            var text = Regex.Replace(sb.ToString(), @"\b(bt-|logi-)[0-9A-Fa-f]{8,12}\b", m => m.Groups[1].Value + "xxxxxxxx");
            return Regex.Replace(text, @"\b[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}\b", "xx:xx:xx:xx:xx:xx");
        }

        // how often batteries are read while the flyout is closed (it reads every 5 s while open)
        public static int PollSeconds { get { return (int)Math.Max(10, Math.Min(300, Config.Num(S("interval"), 30))); } }

        static string SourceOf(int vid)
        {
            switch (vid)
            {
                case 0x1532: return "razer"; case 0x046D: return "logitech"; case 0x1038: return "steelseries";
                case 0x03F0: return "hyperx"; case 0x1B1C: return "corsair"; case 0x248A: case 0x1915: return "darmoshark"; case 0x054C: return "playstation"; case 0x057E: return "nintendo"; case 0x2DC8: return "8bitdo"; case 0x36A7: case 0x373E: case 0x33E4: return "w83"; case 0x3434: return "keychron"; case 0x0B05: return "asus"; case 0x5253: case 0x3837: case 0xA8A5: return "mchose"; case 0x3151: return "aminfinity"; case 0x388D: return "lofree"; case 0x0ECB: return "jbl"; case 0x3329: return "audeze"; default: return "atk";
            }
        }

        // which controller outline a pad gets in the tray (see TrayRenderer.DrawGamepad)
        static string PadGlyph(string id, string name)
        {
            var n = (name ?? "").ToLowerInvariant(); id = id ?? "";
            if (id.StartsWith("ps-0CE6") || id.StartsWith("ps-0DF2") || n.Contains("dualsense")) return "PAD:DS5";
            if (id.StartsWith("ps-") || n.Contains("dualshock") || n == "wireless controller") return "PAD:DS4";
            if (id.StartsWith("xinput-") || n.Contains("xbox")) return "PAD:XBOX";
            return "E7FC";
        }

        void Add(List<Gadget> list, Gadget g)
        {
            if (g.Glyph == null && g.Kind == "gamepad") g.Glyph = PadGlyph(g.Id, g.Name);
            list.Add(g);
        }

        public void RefreshSlow() { slowAt = DateTime.MinValue; }   // e.g. Bluetooth turned on or off in Preferences

        public List<Gadget> Read()
        {
            var list = new List<Gadget>();
            // slow sources (power status, Bluetooth device properties) once a minute
            if ((DateTime.UtcNow - slowAt).TotalSeconds > 55) { slow = ReadSlow(); slowAt = DateTime.UtcNow; }
            list.AddRange(slow);
            if (Config.Bool(S("hid"), true)) ReadHid(list);
            if (Config.Bool(S("xinput"), true))
            {
                // An Xbox pad on Bluetooth is also an XInput pad: XInput only knows four coarse levels,
                // Windows' Bluetooth level is the real one. While as many Bluetooth controllers are
                // connected as XInput reports, the XInput entries are those same pads and are left out.
                var pads = Gamepad.List();
                int btPads = list.Count(g => g.Id.StartsWith("bt-") && g.Kind == "gamepad" && g.Online);
                if (pads.Length > btPads)
                    foreach (var s in pads)
                    {
                        var p = s.Split('|');
                        Add(list, new Gadget { Id = "xinput-" + p[0], Name = Strings.T("controller", int.Parse(p[0]) + 1), Kind = "gamepad", Pct = int.Parse(p[2]), Online = true, Approx = true });
                    }
            }
            ReadExternal(list);
            Estimate(list);
            return list;
        }

        List<Gadget> ReadSlow()
        {
            var list = new List<Gadget>();
            if (Config.Bool(S("systemBattery"), true))
            {
                try
                {
                    // GetSystemPowerStatus: a full battery on AC is not "charging"; only the Charging flag says so
                    var ps = SystemInformation.PowerStatus;
                    if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1)
                        Add(list, new Gadget { Id = "system", Name = Strings.T("thisPc"), Kind = "laptop", Pct = (int)Math.Round(ps.BatteryLifePercent * 100), Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0, Online = true });
                }
                catch (Exception e) { Log.Once("system battery: " + e.Message); }
            }
            if (Config.Bool(S("bluetooth"), true))
            {
                try
                {
                    var seen = new HashSet<string>();
                    foreach (var d in BluetoothBattery.List())
                    {
                        if (!d.Connected && !Config.Bool(S("showDisconnected"), false)) continue;
                        if (!seen.Add(d.Mac)) continue;   // one device shows up as several service nodes
                        Add(list, new Gadget { Id = "bt-" + d.Mac, Name = d.Name, Kind = GuessKind(d.Name, d.Class), Pct = d.Level, Online = d.Connected });
                    }
                }
                catch (Exception e) { Log.Once("bluetooth: " + e.Message); }
            }
            return list;
        }

        void ReadHid(List<Gadget> list)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var byId = new Dictionary<string, Reading>(); var order = new List<string>(); var realNames = new Dictionary<string, string>();
            try
            {
                foreach (var r in Hid.ReadAll())
                {
                    Reading cur;
                    if (!byId.TryGetValue(r.Id, out cur)) { order.Add(r.Id); byId[r.Id] = r; }
                    // one device seen twice (receiver + cable): a real reading beats none, a charging one wins
                    else if ((cur.Level < 0 && r.Level >= 0) || (r.Level >= 0 && r.Charging)) byId[r.Id] = r;
                    // the device on its cable reports its real model name; the receiver only its own
                    if (!r.Receiver && !string.IsNullOrEmpty(r.Name)) realNames[r.Id] = r.Name;
                }
            }
            catch (Exception e) { Log.Once("HID: " + e.Message); return; }
            // which devices answered, written when that changes: the first thing to look at in a device report
            var pids = Hid.LastList.Select(d => d.Vid.ToString("X4") + ":" + d.Pid.ToString("X4")).Distinct().OrderBy(x => x).ToList();
            var seen = string.Join(",", order.Select(i => i + (byId[i].Level >= 0 ? "+" : "-"))) + "|" + string.Join(",", pids) + "|" + Hid.Others.Length;
            if (seen != lastSeen)
            {
                lastSeen = seen;
                Log.Write("HID devices: " + (order.Count == 0 ? "none" : string.Join("; ", order.Select(i => byId[i].ToString()))));
                // the long lines below once per state and run: a mouse that naps and wakes flips between two states all day
                if (loggedStates.Count < 50 && loggedStates.Add(seen))
                {
                    // devices of supported vendors that gave no reading at all: their collections tell which protocol fits
                    var quiet = Hid.LastList.Where(d => !byId.Values.Any(r => r.Level >= 0 && r.Source == SourceOf(d.Vid))).Select(d => d.ToString()).Distinct().ToList();
                    if (quiet.Count > 0) Log.Write("HID unread: " + string.Join("; ", quiet.Take(40)));
                    if (Hid.Others.Length > 0) Log.Write("HID other vendors: " + string.Join("; ", Hid.Others.Take(40)));
                    if (order.Any(i => byId[i].Level < 0) || quiet.Count > 0) lock (Hid.Trace) Log.Write("HID trace: " + string.Join(" | ", Hid.Trace.Skip(Math.Max(0, Hid.Trace.Count - 12))));
                }
            }

            foreach (var id in order)
            {
                var r = byId[id];
                Dictionary<string, object> prev; last.TryGetValue(id, out prev);
                string prevReal = prev != null ? prev.ContainsKey("realName") ? prev["realName"] as string : null : null;
                string real = realNames.ContainsKey(id) ? realNames[id] : prevReal;
                string name = real ?? r.Name;
                if (r.Level >= 0)
                {
                    // A battery that is not charging cannot gain charge: a small rise (up to 10 points) is the
                    // reading wobbling at a step boundary (e.g. 15 -> 20 -> 15), so the lower value the device
                    // gave stays. A big rise, a charging device or an old previous reading is taken as it is.
                    int pct = r.Level;
                    if (prev != null && !r.Charging && !(prev["charging"] is bool && (bool)prev["charging"]))
                    {
                        int was = Convert.ToInt32(prev["pct"]);
                        if (pct > was && pct - was <= 10 && now - Convert.ToInt64(prev["ts"]) < 6 * 3600) pct = was;
                    }
                    last[id] = new Dictionary<string, object> { { "pct", pct }, { "charging", r.Charging }, { "approx", r.Approx }, { "ts", now }, { "name", name }, { "kind", r.Kind }, { "realName", real } };
                    Add(list, new Gadget { Id = id, Name = name, Kind = r.Kind, Pct = pct, Charging = r.Charging, Approx = r.Approx, Online = true });
                }
                else if (r.Charging && prev != null)
                {
                    // charging but the device gives no level while on its cable (AULA F75): the last real
                    // reading, marked approximate - the charge is at least that
                    Add(list, new Gadget { Id = id, Name = name, Kind = r.Kind, Pct = Convert.ToInt32(prev["pct"]), Charging = true, Approx = true, Online = true });
                }
                else if (prev != null)
                {
                    long age = now - Convert.ToInt64(prev["ts"]);
                    int pct = Convert.ToInt32(prev["pct"]); bool approx = prev.ContainsKey("approx") && prev["approx"] is bool && (bool)prev["approx"];
                    // one or two missed answers keep the last value (no flicker): three poll intervals
                    if (age < Math.Max(45, 3 * PollSeconds + 5))
                        Add(list, new Gadget { Id = id, Name = name, Kind = r.Kind, Pct = pct, Charging = prev["charging"] is bool && (bool)prev["charging"], Approx = approx, Online = true });
                    else if (age < 86400)
                    {
                        int mins = (int)(age / 60);
                        Add(list, new Gadget { Id = id, Name = name, Kind = r.Kind, Pct = pct, Approx = approx, Online = false, Asleep = true,
                            Detail = Strings.T("asleep", mins < 60 ? Strings.T("minutes", mins) : Strings.T("hours", mins / 60)) });
                    }
                }
            }
            // receivers known to give no level at all are listed with the reason, instead of being left out
            // silently - unless the same device already shows a level (e.g. the headset on Bluetooth)
            foreach (var k in NoLevel)
            {
                if (!Hid.LastList.Any(d => d.Vid == k.Vid && d.Pid == k.Pid)) continue;
                if (list.Any(g => g.Hint == null && g.Online && (g.Name ?? "").IndexOf(k.Match, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                list.Add(new Gadget { Id = "nolevel-" + k.Vid.ToString("X4") + k.Pid.ToString("X4"), Name = k.Name, Kind = k.Kind, Pct = -1, Hint = Strings.T(k.Text) });
            }
            foreach (var k in last.Keys.ToList()) { object ts; if (!last[k].TryGetValue("ts", out ts) || now - Convert.ToInt64(ts) > 604800) last.Remove(k); }   // forget after a week
            // saved when a value changes, otherwise every 5 minutes (only for "last reading N min ago" after a restart)
            var sig = string.Join(";", last.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value["pct"] + "/" + kv.Value["charging"] + "/" + kv.Value["name"]));
            if (sig != lastSaved || now - lastSavedAt >= 300)
            {
                try { File.WriteAllText(lastFile, Json.Serialize(last), new UTF8Encoding(false)); lastSaved = sig; lastSavedAt = now; } catch { }
            }
        }
        string lastSaved; long lastSavedAt;

        class NoLevelDevice { public int Vid, Pid; public string Name, Kind, Match, Text; }
        static readonly NoLevelDevice[] NoLevel = {
            // Logitech G435 on its LIGHTSPEED receiver: the receiver answers no battery query unless it is put into
            // its firmware-update mode, which cuts the sound; over Bluetooth Windows reports the level
            new NoLevelDevice { Vid = 0x046D, Pid = 0x0ACB, Name = "Logitech G435", Kind = "headphones", Match = "G435", Text = "noLevelReceiver" } };

        // %APPDATA%\SwarlexBattery\gadgets\external.json: [{"id","name","kind","pct","charging","ts","ttl","left","right","case"}]
        void ReadExternal(List<Gadget> list)
        {
            var path = Config.Str(S("externalPath"), "");
            path = path != "" ? Environment.ExpandEnvironmentVariables(path) : Path.Combine(Program.DataDir, "gadgets", "external.json");
            if (!File.Exists(path)) return;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var arr = Json.DeserializeObject(File.ReadAllText(path)) as IEnumerable;
                if (arr == null) return;
                foreach (var o in arr)
                {
                    // entries without a name or a level are skipped (a missing level is never shown as 0 %);
                    // one bad entry does not hide the others
                    var e = o as Dictionary<string, object>; if (e == null || !e.ContainsKey("name") || !e.ContainsKey("pct") || e["pct"] == null) continue;
                    try
                    {
                        Func<string, object> f = k => e.ContainsKey(k) ? e[k] : null;
                        bool online = !(f("ts") != null && f("ttl") != null && now - Convert.ToInt64(f("ts")) > Convert.ToInt64(f("ttl")));
                        if (!online && !Config.Bool(S("showDisconnected"), false)) continue;
                        var parts = new List<string>();
                        foreach (var k in new[] { "left", "right", "case" }) if (f(k) != null && Convert.ToInt32(f(k)) >= 0) parts.Add(k + " " + f(k) + "%");
                        Add(list, new Gadget { Id = "ext-" + (f("id") ?? f("name")), Name = f("name").ToString(), Kind = (f("kind") ?? "other").ToString(),
                            Pct = Math.Max(0, Math.Min(100, Convert.ToInt32(f("pct")))), Charging = f("charging") is bool && (bool)f("charging"), Online = online, Detail = string.Join(", ", parts) });
                    }
                    catch (Exception ex) { Log.Once("external.json entry '" + e["name"] + "': " + ex.Message); }
                }
            }
            catch (Exception e) { Log.Once("external.json: " + e.Message); }
        }

        // ------------------------------------------------------------ what to show
        // The user's choices from the panel (plugins.gadgets.hidden / names / icons, by device id): a hidden device is
        // left out everywhere (panel, tray, notices, status file), a renamed one gets its new name, a chosen icon
        // replaces the kind's own. Applied to copies, so the readings keep the device's own name. "names" may also
        // be keyed by the device's own name (how it was set by hand in config.json before).
        public static readonly string[] IconChoices = { "mouse", "headphones", "earbuds", "keyboard", "gamepad", "speaker", "other" };

        public static List<Gadget> View(List<Gadget> gadgets)
        {
            var hidden = Config.Map(S("hidden")); var names = Config.Map(S("names")); var icons = Config.Map(S("icons"));
            var o = new List<Gadget>();
            foreach (var g in gadgets)
            {
                if (g.Id != null && hidden.ContainsKey(g.Id)) continue;
                var c = g.Copy(); c.OwnName = g.Name;
                string n;
                if ((g.Id != null && names.TryGetValue(g.Id, out n)) || (g.Name != null && names.TryGetValue(g.Name, out n))) c.Name = n;
                string k;
                if (g.Id != null && icons.TryGetValue(g.Id, out k) && Array.IndexOf(IconChoices, k) >= 0) { c.Kind = k; c.Glyph = k == "gamepad" ? PadGlyph(g.Id, g.Name) : null; }
                o.Add(c);
            }
            return o;
        }

        static readonly HashSet<string> chargingBelowFull = new HashSet<string>();   // devices seen charging and not yet full
        static readonly HashSet<string> chargingBelow80 = new HashSet<string>();     // the same, for "Remind at 80 %"

        public static Snapshot Build(List<Gadget> gadgets)
        {
            gadgets = View(gadgets);
            var iconChoice = Config.Map(S("icons"));
            var snap = new Snapshot { Title = Strings.T("title") };
            int low = (int)Config.Num(S("lowThreshold"), 15);
            // a device's own low battery level (the device menu), else the general one
            var lowLevels = Config.Map(S("lowLevels"));
            Func<Gadget, int> lowFor = x => { string v; int n; return x.Id != null && lowLevels.TryGetValue(x.Id, out v) && int.TryParse(v, out n) ? n : low; };
            var order = new Dictionary<string, int> { { "mouse", 0 }, { "headphones", 1 }, { "earbuds", 1 }, { "keyboard", 2 }, { "gamepad", 3 } };
            bool showOff = Config.Bool(S("showDisconnected"), false);
            // mouse first, then headset, then the rest; sleeping devices last
            var shown = gadgets.Where(g => g.Hint == null && (g.Online || g.Asleep || showOff))
                .OrderBy(g => !(g.Online || g.Asleep)).ThenBy(g => order.ContainsKey(g.Kind ?? "") ? order[g.Kind] : 9).ThenBy(g => g.Name).ToList();

            foreach (var g in shown)
            {
                string state = !g.Online ? "off" : (g.Pct <= lowFor(g) && !g.Charging) ? "error" : "";
                // "Coloured icon": the panel follows the tray icon - green while fine, orange near the low level
                if (state == "" && !Config.Bool("monochrome", true)) state = !g.Charging && g.Pct <= lowFor(g) + 10 ? "warn" : "ok";
                // a full device on its cable is "full", not "charging"; coarse levels say so
                string sub = g.Charging && g.Pct >= 100 ? Strings.T("full") : g.Charging ? Strings.T("charging") : !string.IsNullOrEmpty(g.Detail) ? g.Detail : !g.Online ? Strings.T("notConnected") : "";
                if (g.Approx) sub = string.Join(" - ", new[] { sub, Strings.T("approx") }.Where(x => x != ""));
                if (g.HoursLeft > 0 && g.Online && !g.Charging) sub = string.Join(" - ", new[] { sub, TimeLeft(g.HoursLeft) }.Where(x => x != ""));
                snap.Items.Add(new PanelItem { Icon = g.Glyph ?? IconFor(g.Kind), Label = g.Name, Value = g.Pct + "%", Pct = g.Pct / 100.0, State = state, Sub = sub,
                                               Id = g.Id, OwnName = g.OwnName, IconChoice = g.Id != null && iconChoice.ContainsKey(g.Id) ? iconChoice[g.Id] : null });
                if (lowFor(g) > 0 && g.Online && !g.Charging && g.Pct <= lowFor(g))
                    snap.Notify.Add(new Notice { Key = "low-" + g.Id, Title = Strings.T("lowTitle", g.Name), Body = Strings.T("lowBody", g.Pct), Pct = g.Pct });
                // "Notify when charged" (Preferences, on by default): once, when a device seen charging below 100 % reaches
                // 100 % - not when one already full is plugged in, and not for coarse levels (their top step is not "full")
                if (g.Online && g.Charging && g.Pct < 100 && !g.Approx) chargingBelowFull.Add(g.Id);
                else if (g.Online && g.Pct >= 100 && chargingBelowFull.Remove(g.Id) && Config.Bool("fullNotify", true))
                    snap.Notify.Add(new Notice { Key = "full-" + g.Id, Title = Strings.T("fullTitle", g.Name), Body = Strings.T("fullBody"), Info = true });
                else if (!g.Charging) chargingBelowFull.Remove(g.Id);   // unplugged before it was full
                // "Remind at 80 %" (Preferences, off by default): lithium cells last longer when they are not kept full;
                // once per charge, when a device seen charging below 80 % gets there
                if (g.Online && g.Charging && g.Pct < 80 && !g.Approx) chargingBelow80.Add(g.Id);
                else if (g.Online && g.Charging && g.Pct >= 80 && chargingBelow80.Remove(g.Id) && Config.Bool("limitNotify", false))
                    snap.Notify.Add(new Notice { Key = "limit-" + g.Id, Title = Strings.T("limitTitle", g.Name, g.Pct), Body = Strings.T("limitBody"), Info = true });
                else if (!g.Charging) chargingBelow80.Remove(g.Id);
            }
            // devices that give no level: name and reason only (no number, no bar, never in the tray icon)
            foreach (var g in gadgets.Where(x => x.Hint != null))
                snap.Items.Add(new PanelItem { Icon = g.Glyph ?? IconFor(g.Kind), Label = g.Name, Value = "", Pct = -1, State = "off", Sub = g.Hint,
                                               Id = g.Id, OwnName = g.OwnName, IconChoice = g.Id != null && iconChoice.ContainsKey(g.Id) ? iconChoice[g.Id] : null });
            if (snap.Items.Count == 0) snap.Empty = Strings.T("noDevices");

            if (Config.Bool(S("combine"), true) && shown.Count >= 1)
            {
                // one tray icon: two devices -> the ring is split, left half the first (mouse), right half the second
                var label = new Dictionary<string, string> { { "mouse", Strings.T("kindMouse") }, { "headphones", Strings.T("kindHeadset") }, { "earbuds", Strings.T("kindHeadset") }, { "keyboard", Strings.T("kindKeyboard") }, { "gamepad", Strings.T("kindController") } };
                var pair = shown.Take(2).ToList();
                var parts = shown.Select(g => (label.ContainsKey(g.Kind ?? "") ? label[g.Kind] : g.Name) + " " + (g.Approx ? "~" : "") + g.Pct + "%" +
                    (g.Charging && g.Pct >= 100 ? Strings.T("shortFull") : g.Charging ? Strings.T("shortCharging") : !g.Online ? Strings.T("shortAsleep") : ""));
                snap.Icons.Add(new TraySpec {
                    Id = "all", Icon = pair.Count == 1 ? (pair[0].Glyph ?? IconFor(pair[0].Kind)) : "E83F",
                    Rings = pair.Select(g => g.Pct / 100.0).ToArray(), RingCharging = pair.Select(g => g.Charging && g.Pct < 100).ToArray(),
                    State = pair.Any(g => g.Online && !g.Charging && g.Pct <= lowFor(g)) ? "error" : pair.Any(g => g.Online && !g.Charging && g.Pct <= lowFor(g) + 10) ? "warn" : "ok",
                    Charging = pair.Any(g => g.Charging), Dim = !pair.Any(g => g.Online),
                    // the number for "percentage in the icon": the lowest exact level of the two (the one that needs a charge first)
                    Percent = pair.Where(g => g.Online && !g.Approx).Select(g => g.Pct).DefaultIfEmpty(-1).Min(), Tooltip = string.Join("  |  ", parts) });
            }
            else
            {
                foreach (var g in shown)
                    snap.Icons.Add(new TraySpec {
                        Id = g.Id, Icon = g.Glyph ?? IconFor(g.Kind), Ring = g.Pct / 100.0, RingCharging = new[] { g.Charging && g.Pct < 100 },
                        State = g.Pct <= lowFor(g) && !g.Charging ? "error" : g.Pct <= lowFor(g) + 10 && !g.Charging ? "warn" : "ok",
                        Charging = g.Charging, Dim = !g.Online, Percent = g.Online && !g.Approx ? g.Pct : -1,
                        Tooltip = g.Name + ": " + (g.Approx ? "~" : "") + g.Pct + "%" + (g.Charging && g.Pct >= 100 ? Strings.T("tipFull") : g.Charging ? Strings.T("tipCharging") : g.Asleep ? Strings.T("tipAsleep") : !g.Online ? Strings.T("tipNotConnected") : "") });
            }
            // nothing found yet: one dim battery icon keeps the menu (and Exit) reachable
            // (same id as the combined icon: the tray keeps its place instead of a new icon appearing)
            if (snap.Icons.Count == 0) snap.Icons.Add(new TraySpec { Id = "all", Icon = "E83F", State = "off", Dim = true, Tooltip = Strings.T("noDevicesTip") });
            return snap;
        }
    }

    // ---------------------------------------------------------------- Bluetooth battery (the value Windows Settings shows)
    static class BluetoothBattery
    {
        public class Device { public string Name, Class, Mac; public int Level; public bool Connected; }

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int req);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out int type, byte[] buf, int size, out int req, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static readonly DEVPROPKEY Battery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);       // DEVPKEY_Bluetooth_Battery (byte)
        static readonly DEVPROPKEY Connected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);    // DEVPKEY_Device_IsConnected (bool)
        static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly DEVPROPKEY DeviceDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static readonly DEVPROPKEY ClassName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key, out int type)
        {
            var buf = new byte[512]; int req;
            return SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, buf.Length, out req, 0) ? buf.Take(req).ToArray() : null;
        }
        static string Str(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key) { int t; var b = Prop(set, ref d, key, out t); return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0'); }

        // the address in an instance id: 12 hex digits after "&", "_" or "\" - not the end of a service GUID
        // ({0000111E-0000-1000-8000-00805F9B34FB}), which every classic service node has
        public static string MacOf(string id)
        {
            var ms = Regex.Matches(id ?? "", @"(?<=[&_\\])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase);
            return ms.Count == 0 ? null : ms[ms.Count - 1].Groups[1].Value.ToUpperInvariant();
        }

        public static List<Device> List()
        {
            var r = new List<Device>();
            // a device is several nodes, and the level can sit on one that says "not connected" (the G435's Hands-Free AG
            // node has it, while the headset node itself is connected): any connected node of the address counts
            var connected = new HashSet<string>(); var names = new Dictionary<string, string>();
            foreach (var en in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
            {
                IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, en, IntPtr.Zero, 0x2 | 0x4);   // DIGCF_PRESENT | DIGCF_ALLCLASSES
                if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;
                try
                {
                    var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                    {
                        int t; var c = Prop(set, ref d, Connected, out t);
                        bool on = c == null || c.Length == 0 || c[0] != 0;
                        var id = new StringBuilder(512); int req; SetupDiGetDeviceInstanceId(set, ref d, id, id.Capacity, out req);
                        string mac = MacOf(id.ToString());
                        if (on && c != null && c.Length > 0 && mac != null) connected.Add(mac);
                        // the device node (BTHENUM\DEV_..., BTHLE\DEV_...) carries the plain name: "...Headset", not "...Headset Hands-Free AG"
                        if (mac != null && id.ToString().IndexOf("\\DEV_", StringComparison.OrdinalIgnoreCase) >= 0) { var dn = Str(set, ref d, FriendlyName); if (!string.IsNullOrEmpty(dn)) names[mac] = dn; }
                        var b = Prop(set, ref d, Battery, out t);
                        if (b == null || b.Length < 1) continue;
                        string name = Str(set, ref d, FriendlyName) ?? Str(set, ref d, DeviceDesc) ?? "Bluetooth";
                        r.Add(new Device { Name = name, Class = Str(set, ref d, ClassName), Mac = mac ?? name, Level = Math.Min(100, (int)b[0]), Connected = on });
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            foreach (var x in r)
            {
                if (connected.Contains(x.Mac)) x.Connected = true;
                string n; if (names.TryGetValue(x.Mac, out n)) x.Name = n;
            }
            return r;
        }
    }
}
