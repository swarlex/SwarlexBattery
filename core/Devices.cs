// SPDX-License-Identifier: GPL-3.0-or-later
// Battery readers for wireless mice, keyboards and headsets, spoken to directly over HID.
// Protocols as documented by HaloBattery (MIT), HeadsetControl, Solaar, OpenRazer and the
// projects named at each reader. Only read-only battery / status queries are ever sent.
// A reader never invents a value: no answer, an implausible answer or a reply that does not
// echo our request gives Level = -1 ("present, no reading"), never a made-up percentage.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SwarlexBattery
{
    public class Reading
    {
        public string Id, Name, Kind, Source;
        public int Level = -1;            // 0..100, -1 = device present but no reading (asleep / off)
        public bool Charging, Approx, Receiver;
        public override string ToString() { return string.Format("{0} '{1}' {2} {3}% chg={4}{5}", Id, Name, Kind, Level, Charging, Approx ? " approx" : ""); }
    }

    public static partial class Hid
    {
        static readonly object ScanLock = new object();
        public static HidInfo[] LastList = new HidInfo[0];   // collections seen by the last scan (for the log)
        static int listChanges = -1; static DateTime listAt;

        // One pass over every supported device. Safe to call from several threads (serialised).
        public static Reading[] ReadAll()
        {
            lock (ScanLock)
            {
                if (Trace.Count > 500) Trace.Clear();   // debug trail only; keep it from growing forever
                // the device list changes only when something is plugged in or out: scan again then (or once a minute)
                if (listChanges != DeviceWatch.Changes || DateTime.UtcNow > listAt.AddSeconds(60))
                {
                    listChanges = DeviceWatch.Changes; listAt = DateTime.UtcNow;
                    LastList = List(new[] { 0x1532, 0x373B, 0x3554, 0x3770, 0x046D, 0x1038, 0x03F0, 0x1B1C });
                }
                var all = LastList;
                var outp = new List<Reading>();
                foreach (var grp in all.GroupBy(d => d.Vid))
                {
                    try
                    {
                        switch (grp.Key)
                        {
                            case 0x1532: ReadRazer(grp.ToList(), outp); break;
                            case 0x373B: case 0x3554: case 0x3770: ReadAtk(grp.ToList(), outp); break;
                            case 0x046D: ReadLogitech(grp.ToList(), outp); break;
                            case 0x1038: ReadSteelSeries(grp.ToList(), outp); break;
                            case 0x03F0: ReadHyperX(grp.ToList(), outp); break;
                            case 0x1B1C: ReadCorsair(grp.ToList(), outp); break;
                        }
                    }
                    catch (Exception e) { Trace.Add(string.Format("{0:X4}: {1}", grp.Key, e.Message)); }
                }
                return outp.ToArray();
            }
        }

        static string Clean(string n)
        {
            n = (n ?? "").Trim();
            n = System.Text.RegularExpressions.Regex.Replace(n, @"(?i)\s*(wireless receiver|receiver|dongle)\s*$", "");
            n = System.Text.RegularExpressions.Regex.Replace(n, @"(?i)\bHS 2\.4\b", "HyperSpeed");
            return System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
        }

        static HidInfo Pick(IEnumerable<HidInfo> g, int page, int usage) { return g.FirstOrDefault(d => d.UsagePage == page && d.Usage == usage); }

        // Output report, then input reports until accept() takes one. pre-padded to the collection's length.
        static byte[] Ask(SafeFileHandle h, byte[] frame, int outLen, int inLen, int timeoutMs, Func<byte[], bool> accept)
        {
            var f = new byte[Math.Max(frame.Length, outLen)];
            Array.Copy(frame, f, frame.Length);
            Drain(h, inLen);
            if (!Write(h, f)) { Trace.Add("write failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return null; }
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                var r = Read(h, inLen, Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds));
                if (r == null) continue;
                if (accept(r)) return r;
            }
            return null;
        }

        static byte[] Strip0(byte[] r) { if (r != null && r.Length > 1 && r[0] == 0) { var o = new byte[r.Length - 1]; Array.Copy(r, 1, o, 0, o.Length); return o; } return r; }

        // ================================================================ Razer
        static readonly Dictionary<int, byte> RazerTid = new Dictionary<int, byte>();        // pid -> transaction id that answered
        static readonly Dictionary<int, string> RazerPath = new Dictionary<int, string>();  // pid -> collection that answered
        static readonly Dictionary<string, DateTime> NoBattery = new Dictionary<string, DateTime>(); // path -> retry after

        static void ReadRazer(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                var first = g.First();
                int pid = first.Pid;
                string name = Clean(first.Product);
                bool headset = System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)blackshark|kraken|barracuda|nari|headset");
                var r = new Reading { Id = "razer-" + pid.ToString("X4"), Name = name, Kind = headset ? "headphones" : "mouse", Source = "razer" };
                var vend = g.FirstOrDefault(d => (d.UsagePage == 0xFF14 || d.UsagePage == 0xFF00) && d.OutLen >= 64);
                int[] v = null;
                if (pid == 0x0565 || pid == 0x0566 || pid == 0x056E)
                {
                    // BlackShark V2 HyperSpeed: dongle (domain 0x80) and cable (0x00) are one headset
                    r.Id = "razer-bs2hs"; r.Name = "Razer BlackShark V2 HyperSpeed"; r.Kind = "headphones"; r.Receiver = pid != 0x056E;
                    if (vend != null) v = RazerMtk(vend.Path, vend.OutLen, vend.InLen, (byte)(pid == 0x056E ? 0x00 : 0x80));
                }
                else if (pid == 0x0555 || pid == 0x0556 || pid == 0x0557)
                {
                    r.Kind = "headphones"; r.Receiver = true;
                    if (vend != null) v = RazerPa(vend.Path, vend.OutLen, vend.InLen);
                }
                else
                {
                    // every collection with the 90-byte feature report: the one that answered last time first
                    var feats = g.Where(d => d.FeatLen >= 91).OrderBy(d => RazerPath.ContainsValue(d.Path) ? 0 : 1).ThenBy(d => d.Interface).ToList();
                    if (feats.Count == 0) continue;                               // no battery interface
                    DateTime retry;
                    if (feats.All(f => NoBattery.TryGetValue(f.Path, out retry) && DateTime.UtcNow < retry)) continue;
                    var tids = new List<byte>();
                    byte known; if (RazerTid.TryGetValue(pid, out known)) tids.Add(known);
                    foreach (byte t in new byte[] { 0x1F, 0x3F, 0xFF, 0x9F, 0x08 }) if (!tids.Contains(t)) tids.Add(t);
                    bool sawNotSupported = false, asleep = false;
                    foreach (var feat in feats)
                    {
                        if (NoBattery.TryGetValue(feat.Path, out retry) && DateTime.UtcNow < retry) continue;
                        bool notSupported = false, answered = false;
                        foreach (byte tid in tids)
                        {
                            int lvl = RazerQuery(feat.Path, tid, 0x07, 0x80);
                            if (lvl >= 0)
                            {
                                RazerTid[pid] = tid; RazerPath[pid] = feat.Path;
                                int chg = RazerQuery(feat.Path, tid, 0x07, 0x84);
                                v = new[] { (int)Math.Round(lvl / 255.0 * 100), chg > 0 ? 1 : 0 };
                                break;
                            }
                            if (lvl < -1) answered = true;
                            if (lvl == -1 - 0x05) notSupported = true;                 // "not supported": wired-only device
                            if (lvl == -1 - 0x04) { asleep = true; break; }            // "timeout": receiver present, device asleep
                        }
                        if (v != null || asleep) break;
                        if (notSupported) { sawNotSupported = true; NoBattery[feat.Path] = DateTime.UtcNow.AddMinutes(10); }
                        else if (!answered) NoBattery[feat.Path] = DateTime.UtcNow.AddMinutes(2);   // this collection does not speak the protocol
                    }
                    if (v == null && sawNotSupported && !asleep) continue;
                    r.Receiver = System.Text.RegularExpressions.Regex.IsMatch(first.Product ?? "", "(?i)hyperspeed|dongle|receiver|wireless");
                    if (v == null && !r.Receiver) continue;                           // never answered and not a receiver: not a battery device
                }
                if (v != null) { r.Level = Math.Min(100, v[0]); r.Charging = v[1] != 0; }
                outp.Add(r);
            }
        }

        // ================================================================ ATK / VXE / Pulsar / Hitscan
        static void ReadAtk(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                var c = g.FirstOrDefault(d => d.UsagePage == 0xFF02 && d.Usage == 0x0002 && d.OutLen == 17);
                if (c == null) continue;
                var first = g.First();
                bool dongle = System.Text.RegularExpressions.Regex.IsMatch(first.Product ?? "", "(?i)dongle|receiver");
                // the receiver and the mouse on its cable are one mouse: one id per vendor
                var r = new Reading { Id = "atk-" + first.Vid.ToString("X4") + "-mouse", Name = Clean(first.Product), Kind = "mouse", Source = "atk", Receiver = dongle };
                var v = Atk(c.Path, c.OutLen, c.InLen);
                if (v != null) { r.Level = v[0]; r.Charging = v[1] != 0; }   // byte 7 = on the cable (powered)
                outp.Add(r);
            }
        }

        // ================================================================ Logitech HID++ 2.0
        // Solaar / HaloBattery logitech.py: receivers (slots 1..6), cabled devices and headsets (slot 0xFF).
        static readonly HashSet<int> LogiReceivers = new HashSet<int> {
            0xC548, 0xC52B, 0xC532, 0xC52F, 0xC518, 0xC51A, 0xC51B, 0xC521, 0xC525, 0xC526, 0xC52E, 0xC531, 0xC534, 0xC535, 0xC537,
            0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D };
        static readonly Dictionary<int, string> LogiHeadsets = new Dictionary<int, string> {
            { 0x0A66, "Logitech G533" }, { 0x0AC4, "Logitech G535" }, { 0x0A5C, "Logitech G633" }, { 0x0A89, "Logitech G635" },
            { 0x0A5B, "Logitech G933" }, { 0x0A87, "Logitech G935" }, { 0x0AB5, "Logitech G733" }, { 0x0AFE, "Logitech G733" },
            { 0x0B1F, "Logitech G733" }, { 0x0AA7, "Logitech G PRO" }, { 0x0AAA, "Logitech G PRO X" }, { 0x0ABA, "Logitech G PRO X" },
            { 0x0AFB, "Logitech G PRO X 2" }, { 0x0AFC, "Logitech G PRO X 2" } };
        static readonly int[] LogiVoltCurve = { 4186, 100, 4067, 90, 3989, 80, 3922, 70, 3859, 60, 3811, 50, 3778, 40, 3751, 30, 3717, 20, 3671, 10, 3646, 5, 3579, 2, 3500, 0 };

        class LogiSlot { public string Name = "", Kind = "", Unit = ""; public int BatFeature, BatIndex; public bool Identified; }
        static readonly Dictionary<string, LogiSlot> LogiCache = new Dictionary<string, LogiSlot>();
        static readonly HashSet<string> LogiAsleep = new HashSet<string>();
        static readonly Dictionary<string, DateTime> LogiSilent = new Dictionary<string, DateTime>();

        class LogiChannel : IDisposable
        {
            public SafeFileHandle Long, Short; public int LongIn = 20, ShortIn = 7, LongOut = 20;
            public bool Error; public int ErrorCode; int swid;
            public void Dispose() { if (Long != null) Long.Dispose(); if (Short != null) Short.Dispose(); }

            public byte[] Request(int idx, int feat, int func, byte[] prm, int timeoutMs)
            {
                Error = false; ErrorCode = 0;
                swid = (swid + 1) % 6;
                int fn = (func << 4) | (0x0A + swid);
                var req = new byte[Math.Max(20, LongOut)];
                req[0] = 0x11; req[1] = (byte)idx; req[2] = (byte)feat; req[3] = (byte)fn;
                if (prm != null) Array.Copy(prm, 0, req, 4, Math.Min(prm.Length, 16));
                if (!Write(Long, req)) return null;
                var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < end)
                {
                    foreach (var pair in new[] { Tuple.Create(Long, LongIn), Tuple.Create(Short, ShortIn) })
                    {
                        if (pair.Item1 == null) continue;
                        var r = Read(pair.Item1, pair.Item2, 8);
                        if (r == null || r.Length < 4 || r[1] != idx) continue;
                        if ((r[2] == 0x8F || r[2] == 0xFF) && r.Length >= 6 && r[3] == feat && r[4] == fn) { Error = true; ErrorCode = r[5]; return null; }
                        if (r[2] == feat && r[3] == fn) { var p = new byte[20]; Array.Copy(r, 4, p, 0, Math.Min(16, r.Length - 4)); return p; }
                    }
                }
                return null;
            }

            public int FeatureIndex(int idx, int feature)
            {
                var r = Request(idx, 0, 0, new[] { (byte)(feature >> 8), (byte)feature }, 600);
                return r != null ? r[0] : 0;
            }
        }

        static int LogiVoltPct(int mv)
        {
            if (mv >= LogiVoltCurve[0]) return 100;
            for (int i = 0; i + 3 < LogiVoltCurve.Length; i += 2)
            {
                int hiMv = LogiVoltCurve[i], hiP = LogiVoltCurve[i + 1], loMv = LogiVoltCurve[i + 2], loP = LogiVoltCurve[i + 3];
                if (mv >= loMv) return (int)Math.Round(loP + (mv - loMv) * (double)(hiP - loP) / (hiMv - loMv));
            }
            return 0;
        }

        static void ReadLogitech(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid.ToString("X4") + "|" + d.Instance))
            {
                var first = g.First();
                int pid = first.Pid;
                var lng = Pick(g, 0xFF00, 0x0002);
                var sht = Pick(g, 0xFF00, 0x0001);
                var cands = new List<HidInfo>();
                if (lng != null) cands.Add(lng);
                else if (LogiHeadsets.ContainsKey(pid) || (pid >= 0x0A00 && pid <= 0x0BFF))
                {
                    // headsets (product ids 0Axx): HID++ sits on a vendor or consumer collection that carries
                    // the 20-byte long report. Known places first, then vendor collections of exactly that shape;
                    // other vendor collections (e.g. the G435's audio-chip interface) are never written to.
                    if (LogiHeadsets.ContainsKey(pid))
                        foreach (var c in new[] { Pick(g, 0xFF43, 0x0202), Pick(g, 0x000C, 0x0001) }) if (c != null && c.OutLen >= 20) cands.Add(c);
                    foreach (var c in g.Where(d => d.UsagePage >= 0xFF00 && d.OutLen == 20)) if (!cands.Contains(c)) cands.Add(c);
                }
                bool receiver = LogiReceivers.Contains(pid) || (first.Product ?? "").ToLowerInvariant().Contains("receiver");
                foreach (var col in cands)
                {
                    DateTime retry;
                    if (LogiSilent.TryGetValue(col.Path, out retry) && DateTime.UtcNow < retry) continue;
                    bool got = false;
                    using (var ch = new LogiChannel())
                    {
                        ch.Long = Open(col.Path); if (ch.Long.IsInvalid) continue;
                        ch.LongIn = Math.Max(20, col.InLen); ch.LongOut = Math.Max(20, col.OutLen);
                        if (sht != null) { ch.Short = Open(sht.Path); ch.ShortIn = Math.Max(7, sht.InLen); if (ch.Short.IsInvalid) ch.Short = null; }
                        var slots = receiver ? new[] { 1, 2, 3, 4, 5, 6 } : new[] { 0xFF };
                        foreach (int idx in slots)
                        {
                            string key = col.Instance + "|" + pid.ToString("X4") + "|" + idx;
                            var rd = LogiRead(ch, key, pid, idx, first.Product);
                            if (rd != null) { outp.Add(rd); got = true; }
                        }
                    }
                    if (got) break;
                    // a headset collection that never answered HID++: try it again later, not on every poll
                    if (col != lng) LogiSilent[col.Path] = DateTime.UtcNow.AddMinutes(2);
                }
            }
        }

        static Reading LogiRead(LogiChannel ch, string key, int pid, int idx, string product)
        {
            LogiSlot s; LogiCache.TryGetValue(key, out s);
            // ping: a paired device that is asleep does not answer at all; empty slots answer error 0x08 at once
            bool answered = ch.Request(idx, 0, 1, null, LogiAsleep.Contains(key) ? 300 : 900) != null;
            if (!answered)
            {
                if (!ch.Error) { LogiAsleep.Add(key); }
                else { LogiAsleep.Remove(key); if (ch.ErrorCode == 0x08) { LogiCache.Remove(key); return null; } }
                if (s == null || !s.Identified) return null;              // never read: nothing to show
                return new Reading { Id = LogiId(s, pid, idx), Name = s.Name, Kind = s.Kind, Source = "logitech", Receiver = true };
            }
            LogiAsleep.Remove(key);
            if (s == null) { s = new LogiSlot(); LogiCache[key] = s; }
            if (!s.Identified)
            {
                int fi = ch.FeatureIndex(idx, 0x0005);
                if (fi != 0)
                {
                    var r = ch.Request(idx, fi, 0, null, 600); int len = r != null ? r[0] : 0;
                    var raw = new List<byte>();
                    while (raw.Count < len) { r = ch.Request(idx, fi, 1, new[] { (byte)raw.Count }, 600); if (r == null) break; raw.AddRange(r.Take(16)); }
                    s.Name = Encoding.UTF8.GetString(raw.Take(len).ToArray()).Trim('\0', ' ');
                    r = ch.Request(idx, fi, 2, null, 600);
                    if (r != null) s.Kind = (r[0] == 0 || r[0] == 2) ? "keyboard" : (r[0] >= 3 && r[0] <= 5) ? "mouse" : "";
                }
                fi = ch.FeatureIndex(idx, 0x0003);
                if (fi != 0) { var r = ch.Request(idx, fi, 0, null, 600); if (r != null && (r[1] | r[2] | r[3] | r[4]) != 0) s.Unit = BitConverter.ToString(r, 1, 4).Replace("-", ""); }
                if (LogiHeadsets.ContainsKey(pid)) { if (s.Name == "") s.Name = LogiHeadsets[pid]; s.Kind = "headphones"; }
                else if (idx == 0xFF && pid >= 0x0A00 && pid <= 0x0BFF) s.Kind = "headphones";   // 0Axx: Logitech headsets
                if (s.Name != "" && !s.Name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) && s.Kind == "headphones") s.Name = "Logitech " + s.Name;
                if (s.Name == "") s.Name = Clean(product) == "" ? "Logitech" : Clean(product);
                if (s.Kind == "") s.Kind = "mouse";
                s.Identified = s.Name != "" || s.Unit != "";
            }
            var rd = new Reading { Id = LogiId(s, pid, idx), Name = s.Name, Kind = s.Kind, Source = "logitech", Receiver = idx != 0xFF };
            foreach (int feature in new[] { 0x1004, 0x1000, 0x1001, 0x1F20 })
            {
                if (s.BatFeature != 0 && s.BatFeature != feature) continue;
                int fi = s.BatFeature == feature && s.BatIndex != 0 ? s.BatIndex : ch.FeatureIndex(idx, feature);
                if (fi == 0) continue;
                var p = ch.Request(idx, fi, feature == 0x1004 ? 1 : 0, null, 600);
                if (p == null) continue;
                s.BatFeature = feature; s.BatIndex = fi;
                if (feature == 0x1004)
                {
                    bool chg = p[2] >= 1 && p[2] <= 4;
                    if (p[0] > 0 && p[0] <= 100) { rd.Level = p[0]; rd.Charging = chg; }
                    else if (p[0] == 0) { int a = p[1] == 8 ? 90 : p[1] == 4 ? 50 : p[1] == 2 ? 20 : p[1] == 1 ? 5 : -1; if (a >= 0) { rd.Level = a; rd.Approx = true; rd.Charging = chg; } }
                }
                else if (feature == 0x1000) { if (p[0] > 0 && p[0] <= 100) { rd.Level = p[0]; rd.Charging = p[2] >= 1 && p[2] <= 4; } }
                else
                {
                    int mv = (p[0] << 8) | p[1];
                    if (feature == 0x1F20 && (p[2] & 0x01) == 0) break;            // headset not connected
                    if (mv < 2500) break;                                           // not a real battery voltage
                    rd.Level = LogiVoltPct(mv); rd.Approx = true;
                    rd.Charging = feature == 0x1001 ? (p[2] & 0x80) != 0 : (p[2] & 0x02) != 0;
                }
                break;
            }
            return rd;
        }

        static string LogiId(LogiSlot s, int pid, int idx) { return "logi-" + (s.Unit != "" ? s.Unit : pid.ToString("X4") + "-" + idx); }

        // ================================================================ SteelSeries
        // HeadsetControl / HaloBattery steelseries.py. b0 exchange on the FFC0 collection.
        delegate bool SsParse(byte[] r, Reading o);
        static bool SsNova7(byte[] r, Reading o) { if (r.Length < 4 || r[0] != 0xB0 || r[1] != 0x03 || r[3] == 0) return false; o.Level = Math.Min((int)r[2], 100); o.Charging = r[3] == 1 || r[3] == 2; return true; }
        static bool SsNova7D(byte[] r, Reading o) { if (!SsNova7(r, o)) return false; o.Level = Math.Min(o.Level, 4) * 25; o.Approx = true; return true; }
        static bool SsNova5(byte[] r, Reading o) { if (r.Length < 5 || r[0] != 0xB0 || r[1] == 0x02) return false; o.Level = Math.Min((int)r[3], 100); o.Charging = r[4] == 1; return true; }
        static bool Ss7Plus(byte[] r, Reading o) { if (r.Length < 4 || r[0] != 0xB0 || r[1] == 0x01) return false; o.Level = Math.Min((int)r[2], 4) * 25; o.Approx = true; o.Charging = r[3] == 1; return true; }
        static bool SsBuds(byte[] r, Reading o)
        {
            if (r.Length < 7 || r[0] != 0xB0) return false;
            var lv = new List<int>(); for (int i = 0; i < 2; i++) if (r[3 + i] == 0x03) lv.Add(r[5 + i]);
            if (lv.Count == 0) return false; o.Level = Math.Min(lv.Min(), 100); return true;
        }
        static readonly Dictionary<int, Tuple<string, SsParse>> SsHeadsets = new Dictionary<int, Tuple<string, SsParse>> {
            { 0x22A1, Tuple.Create("Arctis Nova 7", (SsParse)SsNova7) }, { 0x2202, Tuple.Create("Arctis Nova 7", (SsParse)SsNova7D) },
            { 0x227E, Tuple.Create("Arctis Nova 7 Gen 2", (SsParse)SsNova7) }, { 0x2206, Tuple.Create("Arctis Nova 7x", (SsParse)SsNova7D) },
            { 0x2258, Tuple.Create("Arctis Nova 7x", (SsParse)SsNova7) }, { 0x229E, Tuple.Create("Arctis Nova 7x", (SsParse)SsNova7) },
            { 0x22AD, Tuple.Create("Arctis Nova 7x", (SsParse)SsNova7) }, { 0x22A4, Tuple.Create("Arctis Nova 7X", (SsParse)SsNova7D) },
            { 0x22A5, Tuple.Create("Arctis Nova 7X", (SsParse)SsNova7) }, { 0x223A, Tuple.Create("Arctis Nova 7 Diablo IV", (SsParse)SsNova7D) },
            { 0x22A9, Tuple.Create("Arctis Nova 7 Diablo IV", (SsParse)SsNova7) }, { 0x227A, Tuple.Create("Arctis Nova 7 WoW Edition", (SsParse)SsNova7D) },
            { 0x2232, Tuple.Create("Arctis Nova 5", (SsParse)SsNova5) }, { 0x2253, Tuple.Create("Arctis Nova 5X", (SsParse)SsNova5) },
            { 0x220A, Tuple.Create("Arctis Nova 7P", (SsParse)SsNova7D) }, { 0x22A7, Tuple.Create("Arctis Nova 7P", (SsParse)SsNova7) },
            { 0x2298, Tuple.Create("Arctis Nova 7P", (SsParse)SsNova7) }, { 0x2269, Tuple.Create("Arctis Nova 3P Wireless", (SsParse)SsNova5) },
            { 0x226D, Tuple.Create("Arctis Nova 3X Wireless", (SsParse)SsNova5) }, { 0x220E, Tuple.Create("Arctis 7+", (SsParse)Ss7Plus) },
            { 0x2212, Tuple.Create("Arctis 7+ PS5", (SsParse)Ss7Plus) }, { 0x2216, Tuple.Create("Arctis 7+ Xbox", (SsParse)Ss7Plus) },
            { 0x2236, Tuple.Create("Arctis 7+ Destiny", (SsParse)Ss7Plus) }, { 0x230A, Tuple.Create("Arctis GameBuds", (SsParse)SsBuds) } };
        static readonly Dictionary<int, string> SsAerox = new Dictionary<int, string> {
            { 0x1838, "SteelSeries Aerox 3 Wireless" }, { 0x1878, "SteelSeries Aerox 3 Wireless CS2" }, { 0x1852, "SteelSeries Aerox 5 Wireless" },
            { 0x185C, "SteelSeries Aerox 5 Wireless Destiny 2" }, { 0x1860, "SteelSeries Aerox 5 Wireless Diablo IV" },
            { 0x1858, "SteelSeries Aerox 9 Wireless" }, { 0x1874, "SteelSeries Aerox 9 Wireless WOW" } };
        static readonly Dictionary<int, string> SsRival = new Dictionary<int, string> {
            { 0x1830, "SteelSeries Rival 3 Wireless" }, { 0x1872, "SteelSeries Rival 3 Wireless Gen 2" } };

        static void ReadSteelSeries(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                int pid = g.Key;
                var r = new Reading { Id = "ss-" + pid.ToString("X4"), Source = "steelseries", Receiver = true };
                if (pid == 0x12E0 || pid == 0x12E5)
                {
                    // Nova Pro Wireless base station: 06 b0; level code 0..8 at [6], state at [15] (01 off, 02 charging, 08 battery)
                    var c = g.FirstOrDefault(d => d.UsagePage == 0xFFC0 && (d.Interface == 3 || d.Interface == 4)) ?? g.FirstOrDefault(d => d.UsagePage == 0xFFC0);
                    if (c == null) continue;
                    r.Name = "Arctis Nova Pro Wireless"; r.Kind = "headphones";
                    using (var h = Open(c.Path))
                    {
                        if (h.IsInvalid) continue;
                        var x = Strip0(Ask(h, new byte[] { 0x06, 0xB0 }, c.OutLen, c.InLen, 1000, q => { var s = Strip0(q); return s.Length >= 16 && (s[15] == 1 || s[15] == 2 || s[15] == 8); }));
                        if (x != null && x[15] != 1 && x[6] <= 8) { r.Level = (int)Math.Round(x[6] * 12.5); r.Approx = true; r.Charging = x[15] == 2; }
                    }
                    outp.Add(r); continue;
                }
                var col = g.FirstOrDefault(d => d.UsagePage == 0xFFC0 && d.Interface == 3) ?? g.FirstOrDefault(d => d.UsagePage == 0xFFC0);
                if (col == null) continue;
                Tuple<string, SsParse> hs;
                if (SsHeadsets.TryGetValue(pid, out hs))
                {
                    r.Name = hs.Item1; r.Kind = pid == 0x230A ? "earbuds" : "headphones";
                    using (var h = Open(col.Path))
                    {
                        if (h.IsInvalid) continue;
                        var tmp = new Reading();
                        Ask(h, new byte[] { 0x00, 0xB0 }, col.OutLen, col.InLen, 1000, q => { var s = Strip0(q); if (s.Length < 2 || s[0] != 0xB0) return false; return hs.Item2(s, tmp) || true; });
                        if (tmp.Level >= 0) { r.Level = tmp.Level; r.Charging = tmp.Charging; r.Approx = tmp.Approx; }
                    }
                    outp.Add(r); continue;
                }
                string mname;
                bool aerox = SsAerox.TryGetValue(pid, out mname);
                if (!aerox && !SsRival.TryGetValue(pid, out mname)) continue;
                r.Name = mname; r.Kind = "mouse";
                using (var h = Open(col.Path))
                {
                    if (h.IsInvalid) continue;
                    if (aerox)
                    {
                        // d2 echo; level byte = bit 7 charging + 1..21 steps, or a percentage above 21; 0 = asleep
                        var x = Strip0(Ask(h, new byte[] { 0x00, 0xD2 }, col.OutLen, col.InLen, 600, q => { var s = Strip0(q); return s.Length >= 2 && s[0] == 0xD2; }));
                        if (x != null) { int b = x[1], v = b & 0x7F; if (v != 0) { r.Level = v > 21 ? Math.Min(v, 100) : (v - 1) * 5; r.Charging = (b & 0x80) != 0; r.Approx = v <= 21; } }
                    }
                    else
                    {
                        var x = Strip0(Ask(h, new byte[] { 0x00, 0xAA, 0x01 }, col.OutLen, col.InLen, 600, q => { var s = Strip0(q); return s.Length >= 3; }));
                        if (x != null)
                        {
                            if (x.Length >= 4 && x[0] == 0xAA && x[1] <= 100) { r.Level = x[1]; r.Charging = x[3] != 0; }
                            else if (x[0] <= 100 && (x[2] == 0 || x[2] == 1)) { r.Level = x[0]; r.Charging = x[2] == 1; }
                        }
                    }
                }
                outp.Add(r);
            }
        }

        // ================================================================ HyperX (HP)
        static void ReadHyperX(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                int pid = g.Key;
                if (pid == 0x0696 || pid == 0x018B)
                {
                    // Cloud II Wireless: FF90:0303, 06 ff bb <cmd>; level [7], charging (cmd 03) [4]
                    var c = Pick(g, 0xFF90, 0x0303); if (c == null) continue;
                    var r = new Reading { Id = "hyperx-cloud2", Name = "HyperX Cloud II Wireless", Kind = "headphones", Source = "hyperx", Receiver = true };
                    using (var h = Open(c.Path))
                    {
                        if (h.IsInvalid) continue;
                        var x = Ask(h, new byte[] { 0x06, 0xFF, 0xBB, 0x02 }, c.OutLen, c.InLen, 1000, q => q.Length > 7 && q[0] == 0x06 && q[1] == 0xFF && q[2] == 0xBB && q[3] == 0x02);
                        if (x != null && x[7] <= 100)
                        {
                            r.Level = x[7];
                            var y = Ask(h, new byte[] { 0x06, 0xFF, 0xBB, 0x03 }, c.OutLen, c.InLen, 1000, q => q.Length > 4 && q[0] == 0x06 && q[1] == 0xFF && q[2] == 0xBB && q[3] == 0x03);
                            r.Charging = y != null && y[4] == 1;
                        }
                    }
                    outp.Add(r);
                }
                else if (pid == 0x05B7 || pid == 0x0C9D)
                {
                    // Cloud III Wireless: FF13:0001, report 0x66; 89 battery ([4] when [2]|[3] != 0), 8A charging ([2] 1/2)
                    var c = Pick(g, 0xFF13, 0x0001); if (c == null) continue;
                    var r = new Reading { Id = "hyperx-cloud3", Name = "HyperX Cloud III Wireless", Kind = "headphones", Source = "hyperx", Receiver = true };
                    using (var h = Open(c.Path))
                    {
                        if (h.IsInvalid) continue;
                        var x = Ask(h, new byte[] { 0x66, 0x89 }, c.OutLen, c.InLen, 1000, q => q.Length > 4 && q[0] == 0x66 && (q[1] == 0x89 || q[1] == 0x0D));
                        if (x != null && (x[2] | x[3]) != 0 && x[4] <= 100)
                        {
                            r.Level = x[4];
                            var y = Ask(h, new byte[] { 0x66, 0x8A }, c.OutLen, c.InLen, 1000, q => q.Length > 2 && q[0] == 0x66 && (q[1] == 0x8A || q[1] == 0x0C));
                            r.Charging = y != null && (y[2] == 1 || y[2] == 2);
                        }
                    }
                    outp.Add(r);
                }
                else if (pid == 0x08BE)
                {
                    // Cloud Alpha 2: FF13:FF00, 50 02 -> 51 02 <level> .. [6] bit 7 charging
                    var c = Pick(g, 0xFF13, 0xFF00); if (c == null) continue;
                    var r = new Reading { Id = "hyperx-alpha2", Name = "HyperX Cloud Alpha 2", Kind = "headphones", Source = "hyperx", Receiver = true };
                    using (var h = Open(c.Path))
                    {
                        if (h.IsInvalid) continue;
                        var x = Ask(h, new byte[] { 0x50, 0x02 }, c.OutLen, c.InLen, 1000, q => q.Length > 6 && q[0] == 0x51 && q[1] == 0x02);
                        if (x != null && x[2] <= 100) { r.Level = x[2]; r.Charging = (x[6] & 0x80) != 0; }
                    }
                    outp.Add(r);
                }
            }
        }

        // ================================================================ Corsair (Void v2 Wireless family)
        // HeadsetControl corsair_void_v2w: interface 4; 02 <endpoint> 02 <cmd>; endpoint 08 receiver, 09 headset.
        static readonly Dictionary<int, string> CorsairHeadsets = new Dictionary<int, string> {
            { 0x2A08, "Corsair Void v2 Wireless" }, { 0x2A02, "Corsair Virtuoso Max Wireless" }, { 0x0A97, "Corsair HS80 Max Wireless" } };

        static void ReadCorsair(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                string name; if (!CorsairHeadsets.TryGetValue(g.Key, out name)) continue;
                var c = g.FirstOrDefault(d => d.Interface == 4); if (c == null) continue;
                var r = new Reading { Id = "corsair-" + g.Key.ToString("X4"), Name = name, Kind = "headphones", Source = "corsair", Receiver = true };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) continue;
                    Func<byte, byte, byte[]> frame = (ep, cmd) => new byte[] { 0x00, 0x02, ep, 0x02, cmd };
                    // minimal handshake (no switch to software mode): receiver firmware, receiver + headset heartbeat
                    Ask(h, frame(0x08, 0x13), c.OutLen, c.InLen, 300, q => true);
                    Ask(h, frame(0x08, 0x12), c.OutLen, c.InLen, 300, q => true);
                    Ask(h, frame(0x09, 0x12), c.OutLen, c.InLen, 300, q => true);
                    for (int i = 0; i < 3 && r.Level < 0; i++)
                    {
                        var x = Strip0(Ask(h, frame(0x09, 0x0F), c.OutLen, c.InLen, 500, q => Strip0(q).Length > 5));
                        if (x == null) continue;
                        int v = x[4] | (x[5] << 8);                                     // tenths of a percent
                        if (v > 0 && v <= 1000) r.Level = v / 10;                       // 0 / >1000: the receiver answered with something else
                    }
                }
                outp.Add(r);
            }
        }
    }
}
