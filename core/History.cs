// SPDX-License-Identifier: GPL-3.0-or-later
// Battery history for the graph in the panel (click a device): each device's level against the clock, kept for
// 7 days. A point is written when the level or charging changes, and every 15 minutes while it stays the same,
// so a gap of more than 40 minutes means the device was off, asleep or the PC was. Only real readings are kept
// (never a guessed value). The file is small (a few hundred points a day per device) and written every 10 minutes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SwarlexBattery
{
    static class History
    {
        public const long Keep = 7 * 86400, Beat = 15 * 60, Gap = 40 * 60;
        static readonly Dictionary<string, List<long[]>> points = new Dictionary<string, List<long[]>>();   // id -> { time, level, charging }
        static readonly object Sync = new object();
        static bool loaded, dirty; static long savedAt;
        static readonly Dictionary<string, long> behind = new Dictionary<string, long>();   // id -> when its readings first came in behind its last point
        public static double BehindWaitMs = 30 * 60 * 1000;                                  // tests set it to 0
        static string File_ { get { return Path.Combine(Program.CacheDir, "state", "gadgets", "levels.json"); } }

        // The saved days must survive a read that goes wrong: a file held open for a moment (antivirus, backup) is read
        // again on the next call, with nothing recorded or saved meanwhile; a damaged file is kept aside as
        // levels.json.bad and a new history starts; a damaged entry is skipped, not the whole file. (Errors are logged
        // by type only: the message holds the path, with the user's name, and the log goes into the diagnostics report.)
        static void Load()
        {
            string text;
            try
            {
                // far bigger than 7 days of points can be (a few hundred KB): damaged, not read into memory at every poll
                if (File.Exists(File_) && new FileInfo(File_).Length > 16 * 1024 * 1024)
                {
                    Log.Once("history: oversized file kept as levels.json.bad");
                    try { File.Copy(File_, File_ + ".bad", true); }
                    catch (Exception c) { Log.Once("history: could not keep the oversized file (" + c.GetType().Name + "); a new history starts"); }
                    loaded = true; return;
                }
                text = File.Exists(File_) ? File.ReadAllText(File_) : null;
            }
            catch (Exception e) { Log.Once("history: read later (" + e.GetType().Name + ")"); return; }   // loaded stays false
            loaded = true;
            if (text == null) return;
            Dictionary<string, object> o;
            try { o = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text); }
            catch (Exception e)
            {
                Log.Once("history: damaged file kept as levels.json.bad (" + e.GetType().Name + ")");
                try { File.Copy(File_, File_ + ".bad", true); }
                catch (Exception c) { Log.Once("history: could not keep the damaged file (" + c.GetType().Name + "); a new history starts"); }
                return;
            }
            foreach (var kv in o ?? new Dictionary<string, object>())
            {
                var list = new List<long[]>();
                var arr = kv.Value as System.Collections.ArrayList;
                if (arr == null) continue;
                foreach (var p in arr)
                {
                    try
                    {
                        var a = (System.Collections.ArrayList)p;
                        list.Add(new[] { Convert.ToInt64(a[0], CultureInfo.InvariantCulture), Convert.ToInt64(a[1], CultureInfo.InvariantCulture), Convert.ToInt64(a[2], CultureInfo.InvariantCulture) });
                    }
                    catch { }   // one damaged point
                }
                list.Sort((x, y) => x[0].CompareTo(y[0]));
                points[kv.Key] = list;
            }
        }

        // after each read: the devices that answered
        public static void Record(IEnumerable<Gadget> list, long now)
        {
            lock (Sync)
            {
                if (!loaded) Load();
                if (!loaded) return;   // the file could not be read just now: nothing is recorded over it
                // a device that sent nothing this time starts its 30-minute wait afresh when it is back
                if (behind.Count > 0)
                {
                    var here = new HashSet<string>(list.Where(g => g.Id != null).Select(g => g.Id));
                    foreach (var k in behind.Keys.Where(k => !here.Contains(k)).ToList()) behind.Remove(k);
                }
                foreach (var g in list)
                {
                    if (g.Id == null || !g.Online || g.Asleep || g.Hint != null || g.Pct < 0) continue;
                    Add(g.Id, now, g.Pct, g.Charging);
                }
                if (savedAt > now) savedAt = now;   // the clock was set back
                if (dirty && now - savedAt >= 600) Save(now);
            }
        }

        // one reading; a point only when something changed or the last one is 15 minutes old
        public static void Add(string id, long now, int pct, bool charging)
        {
            List<long[]> l;
            if (!points.TryGetValue(id, out l)) { l = new List<long[]>(); points[id] = l; }
            var last = l.Count > 0 ? l[l.Count - 1] : null;
            if (last != null && now < last[0])
            {
                // The clock is behind the last point. Which clock was wrong cannot be told from the times alone: a clock
                // behind at start (a flat CMOS battery, before time sync) must not cost the saved days, and a clock that
                // was ahead and has been corrected (a year off, a dual boot hours off) must not hold this device's history
                // still until the real time catches up. So readings wait, and only when the clock has stayed behind for
                // 30 minutes of real time (a monotonic counter, not the clock) do the points from "the future" go.
                long ts = System.Diagnostics.Stopwatch.GetTimestamp(), first;
                if (!behind.TryGetValue(id, out first)) { behind[id] = first = ts; }
                if (last[0] - now <= 600 || (ts - first) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < BehindWaitMs) return;
                l.RemoveAll(p => p[0] > now); dirty = true; behind.Remove(id);
                last = l.Count > 0 ? l[l.Count - 1] : null;
            }
            else behind.Remove(id);
            if (last != null && last[1] == pct && (last[2] != 0) == charging && now - last[0] < Beat) return;
            l.Add(new[] { now, (long)pct, charging ? 1L : 0L });
            int old = 0; while (old < l.Count && now - l[old][0] > Keep) old++;
            if (old > 0) l.RemoveRange(0, old);
            dirty = true;
        }

        public static void Save(long now)
        {
            lock (Sync)
            {
                // nothing new, or the file was never read: writing now would replace the saved days with an empty list
                if (!loaded || !dirty) return;
                try
                {
                    var file = File_; var tmp = file + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (var kv in points.Where(x => x.Value.Count > 0 && now - x.Value[x.Value.Count - 1][0] <= Keep))
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append(new JavaScriptSerializer().Serialize(kv.Key)).Append(":[");
                        sb.Append(string.Join(",", kv.Value.Select(p => "[" + p[0] + "," + p[1] + "," + p[2] + "]")));
                        sb.Append(']');
                    }
                    sb.Append('}');
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                    savedAt = now; dirty = false;
                }
                catch (Exception e) { Log.Once("history save: " + e.GetType().Name); }
            }
        }

        // a copy of the points since a time (for the graph)
        public static List<long[]> Since(string id, long from)
        {
            lock (Sync)
            {
                if (!loaded) Load();
                List<long[]> l;
                return points.TryGetValue(id ?? "", out l) ? l.Where(p => p[0] >= from).Select(p => (long[])p.Clone()).ToList() : new List<long[]>();
            }
        }

        // ------------------------------------------------------------ what the history says about the battery
        // The device's usual drain over the last 7 days: the drop in level over the time it was on and off the
        // charger (pairs of points less than 40 minutes apart; a jump up without charging is a new reading, not use).
        // Points per second, or -1 with too little to go on: an hour of use and a 5-point drop, and for devices that
        // report in coarse steps (AirPods: 10 %) two steps down.
        public static double DrainRate(string id, bool coarse)
        {
            lock (Sync)
            {
                if (!loaded) Load();
                List<long[]> l;
                if (!points.TryGetValue(id ?? "", out l)) return -1;
                // a coarse device's step is its smallest drop; a bigger drop at once is not use: AirPods show the lower
                // earbud in use, so taking a lower one out of the case looks like 70 -> 50 % in a few seconds
                long step = long.MaxValue;
                if (coarse) for (int i = 1; i < l.Count; i++) if (l[i][1] < l[i - 1][1] && l[i][2] == 0 && l[i - 1][2] == 0) step = Math.Min(step, l[i - 1][1] - l[i][1]);
                double time = 0, drop = 0; int steps = 0;
                for (int i = 1; i < l.Count; i++)
                {
                    long[] a = l[i - 1], b = l[i];
                    if (b[0] - a[0] > Gap || a[2] != 0 || b[2] != 0 || b[1] > a[1]) continue;
                    if (coarse && a[1] - b[1] > step) continue;
                    time += b[0] - a[0];
                    if (b[1] < a[1]) { drop += a[1] - b[1]; steps++; }
                }
                return time < 3600 || drop < 5 || (coarse && steps < 2) ? -1 : drop / time;
            }
        }

        // Hours until full while it charges. Lithium cells charge fast up to about 80 %, then slower, slowest near
        // full, so the level is taken in three parts (below 80, 80-95, 95-100), each with its own speed: how fast
        // this device charged in that part before (the last 7 days), and for the part it is in now, how fast this
        // charge goes (its last hour). A part never seen takes the speed now times the usual slowdown
        // (1, 0.6, 0.3). -1 when nothing is known yet.
        static readonly double[] Slow = { 1, 0.6, 0.3 };
        public const double AlmostFull = -2;   // HoursToFull: on the last step of a device in coarse steps, not timeable
        static int Part(double p) { return p < 80 ? 0 : p < 95 ? 1 : 2; }

        public static double HoursToFull(string id, int pct, long now)
        {
            if (pct >= 100) return 0;
            lock (Sync)
            {
                if (!loaded) Load();
                List<long[]> l;
                if (!points.TryGetValue(id ?? "", out l) || l.Count < 2) return -1;
                // how this device charged before, per part
                double[] rise = new double[3], time = new double[3], rate = { -1, -1, -1 };
                for (int i = 1; i < l.Count; i++)
                {
                    long[] a = l[i - 1], b = l[i];
                    if (b[0] - a[0] > Gap || a[2] == 0 || b[2] == 0 || b[1] < a[1]) continue;
                    int k = Part(a[1]); rise[k] += b[1] - a[1]; time[k] += b[0] - a[0];
                }
                for (int k = 0; k < 3; k++) if (time[k] >= 600 && rise[k] >= 3) rate[k] = rise[k] / time[k];
                // this charge (its run of points up to now, the last hour of it), per part as well: a mouse that went
                // from 65 to 90 % fast below 80 says little about how fast it goes on above 80
                int s = l.Count - 1;
                if (l[s][2] == 0 || now - l[s][0] > Gap) s = -1;
                while (s > 0 && l[s - 1][2] != 0 && l[s][0] - l[s - 1][0] <= Gap && l[s - 1][0] >= now - 3600) s--;
                if (s >= 0)
                {
                    double[] sr = new double[3], st = new double[3];
                    for (int i = s + 1; i <= l.Count; i++)
                    {
                        // the last stretch runs from the last point to now, at the level shown now
                        long ta = l[i - 1][0], tb = i < l.Count ? l[i][0] : Math.Max(now, ta);
                        double la = l[i - 1][1], lb = i < l.Count ? l[i][1] : pct;
                        if (lb < la) continue;
                        int k = Part(la); sr[k] += lb - la; st[k] += tb - ta;
                    }
                    for (int k = 0; k < 3; k++) if (st[k] >= 120 && sr[k] >= 2) rate[k] = sr[k] / st[k];   // fast chargers pass a part in minutes
                }
                // the steps it rises in, over the whole charge (its last hour alone may hold no rise at all: a mouse on
                // 95 % for over an hour would lose the "5 % steps" it showed earlier)
                int run = l.Count - 1;
                if (l[run][2] == 0 || now - l[run][0] > Gap) run = -1;
                while (run > 0 && l[run - 1][2] != 0 && l[run][0] - l[run - 1][0] <= Gap) run--;
                var ups = new List<double>();
                if (run >= 0) for (int i = run + 1; i < l.Count; i++) if (l[i][1] > l[i - 1][1]) ups.Add(l[i][1] - l[i - 1][1]);
                ups.Sort();
                // a device that reports in 5 % steps and is on 95 %: on its cable it never says more (an ATK mouse shows
                // 95 % "charging" until it is unplugged, then 100 %), so the last step cannot be timed - whether or not
                // a speed is known
                if (run >= 0 && pct >= 95 && ups.Count > 0 && ups[0] >= 5) return AlmostFull;
                // the speed below 80 % that the parts never seen are worked out from
                double basis = -1;
                for (int k = 0; k < 3 && basis < 0; k++) if (rate[k] > 0) basis = rate[k] / Slow[k];
                if (basis < 0) return -1;
                int here = Part(pct);
                if (rate[here] <= 0) rate[here] = basis * Slow[here];
                // still on the same level for a while (a mouse in 5 % steps sat on 95 % for 16 minutes): the next step
                // takes at least as long again, so the speed here is at most one step over that time
                if (s >= 0 && l[l.Count - 1][1] == pct)
                {
                    int f = l.Count - 1;
                    while (f > s && l[f - 1][1] == pct) f--;
                    double since = now - l[f][0];
                    double step = ups.Count > 0 ? Math.Min(ups[ups.Count / 2], 100 - pct) : 1;
                    if (since >= 120) rate[here] = Math.Min(rate[here], step / since);
                }
                double secs = 0, lo = pct;
                foreach (var edge in new double[] { 80, 95, 100 })
                {
                    if (lo >= edge) continue;
                    int k = Part(lo); double r = rate[k] > 0 ? rate[k] : basis * Slow[k];
                    secs += (edge - lo) / r; lo = edge;
                }
                double hours = secs / 3600;
                return hours > 0 && hours < 48 ? hours : -1;
            }
        }

        // tests only
        public static void Clear() { lock (Sync) { points.Clear(); behind.Clear(); loaded = true; dirty = false; } }
    }
}
