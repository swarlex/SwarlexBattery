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
        static readonly int[] Vendors = { 0x1532, 0x373B, 0x3554, 0x3770, 0x046D, 0x1038, 0x03F0, 0x1B1C, 0x248A, 0x1915, 0x054C, 0x057E, 0x2DC8,
            0x36A7, 0x373E, 0x33E4, 0x3434, 0x0B05, 0x5253, 0x3837, 0xA8A5, 0x3151, 0x388D, 0x0ECB, 0x3329 };
        public static string[] Others = new string[0];       // unsupported vendors' vendor collections (for the log)

        // One pass over every supported device. Safe to call from several threads (serialised).
        public static Reading[] ReadAll()
        {
            lock (ScanLock)
            {
                if (Trace.Count > 500) Trace.Clear();   // debug trail only; keep it from growing forever
                // the device list changes only when something is plugged in or out: scan again then (or once a minute)
                if (listChanges != DeviceWatch.Changes || DateTime.UtcNow > listAt.AddSeconds(60))
                {
                    bool plugged = listChanges != DeviceWatch.Changes;
                    listChanges = DeviceWatch.Changes; listAt = DateTime.UtcNow;
                    LastList = List(Vendors);
                    if (plugged)
                    // for device reports: other vendors' vendor-defined collections (where battery protocols live),
                    // listed once per plug / unplug - this is how an unsupported mouse shows its ids in the log
                    try
                    {
                        Others = List(null).Where(d => Array.IndexOf(Vendors, d.Vid) < 0 && d.UsagePage >= 0xFF00)
                                           .Select(d => d.ToString() + " if=" + d.Interface).Distinct().ToArray();
                    }
                    catch { Others = new string[0]; }
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
                            case 0x248A: ReadDarmoshark(grp.ToList(), outp); break;
                            case 0x1915: ReadDarmoshark4K(grp.ToList(), outp); break;
                            case 0x054C: ReadPlayStation(grp.ToList(), outp); break;
                            case 0x057E: ReadNintendo(grp.ToList(), outp); break;
                            case 0x2DC8: ReadEightBitDo(grp.ToList(), outp); break;
                            case 0x36A7: case 0x373E: case 0x33E4: ReadW83Family(grp.Key, grp.ToList(), outp); break;
                            case 0x3434: ReadKeychron(grp.ToList(), outp); break;
                            case 0x0B05: ReadAsus(grp.ToList(), outp); break;
                            case 0x5253: case 0x3837: case 0xA8A5: ReadMchose(grp.Key, grp.ToList(), outp); break;
                            case 0x3151: ReadAmInfinity(grp.ToList(), outp); break;
                            case 0x388D: ReadLofree(grp.ToList(), outp); break;
                            case 0x0ECB: ReadJbl(grp.ToList(), outp); break;
                            case 0x3329: ReadAudeze(grp.ToList(), outp); break;
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

        // Devices whose interface Windows splits into several collections, each with its own handle: the request goes
        // as an output report to the first collection that takes it (Windows refuses a report id a collection does not
        // declare), vendor pages first and the one that took it last time before all; the reply is looked for on every
        // collection, because Windows delivers an input report to the collection that declares it. accept() may keep
        // state across reports (a level and a power state that come in two frames); null frame = only listen.
        static readonly Dictionary<string, string> TookRequest = new Dictionary<string, string>();   // key -> path

        static byte[] AskAll(string key, IEnumerable<HidInfo> cols, byte[] frame, int timeoutMs, Func<byte[], bool> accept)
        {
            var hs = new List<KeyValuePair<HidInfo, SafeFileHandle>>();
            try
            {
                string known; TookRequest.TryGetValue(key, out known);
                foreach (var c in cols.OrderBy(c => c.Path == known ? 0 : c.UsagePage >= 0xFF00 ? 1 : 2))
                {
                    var h = Open(c.Path);
                    if (h.IsInvalid) { h.Dispose(); continue; }   // e.g. keyboard and mouse collections, kept by Windows
                    hs.Add(new KeyValuePair<HidInfo, SafeFileHandle>(c, h));
                }
                foreach (var p in hs) if (p.Key.InLen > 0) Drain(p.Value, p.Key.InLen);
                if (frame != null)
                {
                    bool sent = false;
                    foreach (var p in hs)
                    {
                        if (p.Key.OutLen < frame.Length) continue;
                        var f = new byte[p.Key.OutLen]; Array.Copy(frame, f, frame.Length);
                        if (Write(p.Value, f)) { TookRequest[key] = p.Key.Path; sent = true; break; }
                    }
                    if (!sent) Trace.Add(key + ": no collection took the request, listening only");
                }
                var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < end)
                    foreach (var p in hs)
                    {
                        if (p.Key.InLen <= 0) continue;
                        var r = Read(p.Value, p.Key.InLen, 15);
                        if (r == null) continue;
                        Trace.Add(key + " in: " + Hex(r, 16));
                        if (accept(r)) return r;
                    }
                return null;
            }
            finally { foreach (var p in hs) p.Value.Dispose(); }
        }

        static byte[] Strip0(byte[] r) { if (r != null && r.Length > 1 && r[0] == 0) { var o = new byte[r.Length - 1]; Array.Copy(r, 1, o, 0, o.Length); return o; } return r; }

        // ================================================================ Razer
        static readonly Dictionary<int, byte> RazerTid = new Dictionary<int, byte>();        // pid -> transaction id that answered
        static readonly Dictionary<int, string> RazerPath = new Dictionary<int, string>();  // pid -> collection that answered
        static readonly Dictionary<string, DateTime> NoBattery = new Dictionary<string, DateTime>(); // path -> retry after

        // Razer keyboards with a battery, from OpenRazer's keyboard driver (via HaloBattery providers/razer.py): pid ->
        // name, the transaction id it answers on, the USB interface that takes the commands (-1 = any), on the receiver
        class RazerKb { public string Name; public byte Tid; public int Iface; public bool Wireless; }
        static readonly Dictionary<int, RazerKb> RazerKeyboards = new Dictionary<int, RazerKb> {
            { 0x0290, new RazerKb { Name = "Razer DeathStalker V2 Pro", Tid = 0x9F, Iface = 2, Wireless = true } },
            { 0x0292, new RazerKb { Name = "Razer DeathStalker V2 Pro", Tid = 0x1F, Iface = 3 } },
            { 0x0296, new RazerKb { Name = "Razer DeathStalker V2 Pro TKL", Tid = 0x9F, Iface = 2, Wireless = true } },
            { 0x0298, new RazerKb { Name = "Razer DeathStalker V2 Pro TKL", Tid = 0x1F, Iface = 3 } },
            { 0x0271, new RazerKb { Name = "Razer BlackWidow V3 Mini HyperSpeed", Tid = 0x9F, Iface = 3, Wireless = true } },
            { 0x0258, new RazerKb { Name = "Razer BlackWidow V3 Mini HyperSpeed", Tid = 0x1F, Iface = 3 } },
            { 0x02BA, new RazerKb { Name = "Razer BlackWidow V4 Mini HyperSpeed", Tid = 0x9F, Iface = 3, Wireless = true } },
            { 0x02B9, new RazerKb { Name = "Razer BlackWidow V4 Mini HyperSpeed", Tid = 0x1F, Iface = 3 } },
            { 0x02D5, new RazerKb { Name = "Razer BlackWidow V4 Tenkeyless HyperSpeed", Tid = 0x9F, Iface = 2, Wireless = true } },
            { 0x02D7, new RazerKb { Name = "Razer BlackWidow V4 Tenkeyless HyperSpeed", Tid = 0x1F, Iface = 3 } },
            { 0x025C, new RazerKb { Name = "Razer BlackWidow V3 Pro", Tid = 0x9F, Iface = -1, Wireless = true } },
            { 0x025A, new RazerKb { Name = "Razer BlackWidow V3 Pro", Tid = 0x3F, Iface = -1 } } };

        static void ReadRazer(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                var first = g.First();
                int pid = first.Pid;
                string name = Clean(first.Product);
                bool headset = System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)blackshark|kraken|barracuda|nari|headset");
                bool keyboard = System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)blackwidow|huntsman|ornata|cynosa|deathstalker|keyboard|pro type");
                // one id per model (not per product id): the mouse on its cable and its receiver are one device
                string model = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                var r = new Reading { Id = "razer-" + (model != "" ? model : pid.ToString("X4")), Name = name, Kind = headset ? "headphones" : keyboard ? "keyboard" : "mouse", Source = "razer" };
                var vend = g.FirstOrDefault(d => (d.UsagePage == 0xFF14 || d.UsagePage == 0xFF00) && d.OutLen >= 64);
                int[] v = null;
                if (pid == 0x053A)
                {
                    // Barracuda Pro on its 2.4 GHz receiver: the PA protocol, shifted (BarracudaPro)
                    r.Name = "Razer Barracuda Pro"; r.Kind = "headphones"; r.Receiver = true;
                    var bc = g.FirstOrDefault(d => d.UsagePage == 0xFF00 && d.OutLen >= 64);
                    if (bc != null) v = BarracudaPro(bc.Path, bc.OutLen, bc.InLen);
                }
                else if (pid == 0x0565 || pid == 0x0566 || pid == 0x056E)
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
                    RazerKb kb; RazerKeyboards.TryGetValue(pid, out kb);
                    if (kb != null) { r.Name = kb.Name; r.Kind = "keyboard"; r.Id = "razer-" + System.Text.RegularExpressions.Regex.Replace(kb.Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-'); }
                    // every collection with the 90-byte feature report: the one that answered last time first, then a
                    // known keyboard's own interface
                    var feats = g.Where(d => d.FeatLen >= 91).OrderBy(d => RazerPath.ContainsValue(d.Path) ? 0 : 1)
                                 .ThenBy(d => kb != null && d.Interface == kb.Iface ? 0 : 1).ThenBy(d => d.Interface).ToList();
                    if (feats.Count == 0) continue;                               // no battery interface
                    DateTime retry;
                    if (feats.All(f => NoBattery.TryGetValue(f.Path, out retry) && DateTime.UtcNow < retry)) continue;
                    var tids = new List<byte>();
                    byte known; if (RazerTid.TryGetValue(pid, out known)) tids.Add(known);
                    if (kb != null && !tids.Contains(kb.Tid)) tids.Add(kb.Tid);
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
                    r.Receiver = (kb != null && kb.Wireless) || System.Text.RegularExpressions.Regex.IsMatch(first.Product ?? "", "(?i)hyperspeed|dongle|receiver|wireless");
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
                if (g.Key == 0xFA09) { ReadAula(g.ToList(), outp); continue; }
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

        // ================================================================ AULA F75 (Compx 2.4G receiver 3554:FA09)
        // Device-Battery-Info AulaProtocol (MIT), built from frames captured on real hardware: output report
        // 0x13, 20 bytes, command 0x4A, byte-sum checksum last. Reply: level [5], state [6] (01 on battery,
        // 10 on the cable; on the cable the level always reads 100, so it says nothing about the charge).
        static byte AulaSum(byte[] f) { byte s = 0; for (int i = 0; i < 19; i++) s += f[i]; return s; }

        static void ReadAula(List<HidInfo> devs, List<Reading> outp)
        {
            var c = devs.FirstOrDefault(d => d.UsagePage == 0xFF02 && d.Usage == 0x0002 && d.OutLen >= 20 && d.InLen >= 20);
            if (c == null) return;
            var r = new Reading { Id = "aula-f75", Name = "AULA F75", Kind = "keyboard", Source = "atk", Receiver = true };
            var req = new byte[20]; req[0] = 0x13; req[1] = 0x4A; req[19] = AulaSum(req);
            using (var h = Open(c.Path))
            {
                if (h.IsInvalid) return;
                // the receiver also pushes other 0x13 frames (a 0x0A status among them): match command and checksum
                var x = Ask(h, req, c.OutLen, c.InLen, 800, q => q.Length >= 20 && q[0] == 0x13 && (q[1] & 0x7F) == 0x4A && q[19] == AulaSum(q));
                if (x != null && x[5] >= 1 && x[5] <= 100)
                {
                    if (x[6] == 0x10) r.Charging = true;     // on the cable: charging, level unknown (Level stays -1)
                    else r.Level = x[5];
                }
            }
            outp.Add(r);
        }

        // ================================================================ Darmoshark (Telink 248A, "dms" contract)
        // darmoshark-m3-configurator (MIT) PROTOCOL.md, confirmed there on an M3: identify (opcode 0x06) through
        // feature report 0x51 on the usage page 0x8C interface. Reply: [0] report id, [1] echo 06, [2] status
        // 1 = ok, [11] battery %. Over the 2.4 GHz receiver the answer lands in the feature buffer, which keeps
        // the previous answer: when it already echoes 06, the bond read (03, answered by the receiver itself)
        // goes first, so an 06 echo can only be the new answer. A sleeping mouse never answers 06.
        static readonly HashSet<int> DmsPids = new HashSet<int> { 0xFF10, 0xFF12, 0xFF18, 0xFF30, 0xFF31 };

        static byte[] DmsGet(SafeFileHandle h, int len) { var b = new byte[len]; b[0] = 0x51; return HidD_GetFeature(h, b, len) ? b : null; }
        static bool DmsSend(SafeFileHandle h, int len, byte op) { var b = new byte[len]; b[0] = 0x51; b[1] = op; return HidD_SetFeature(h, b, len); }
        static byte[] DmsAwait(SafeFileHandle h, int len, byte op, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                var r = DmsGet(h, len);
                if (r != null && r[1] == op) return r;
                Thread.Sleep(10);
            }
            return null;
        }

        static void ReadDarmoshark(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                if (!DmsPids.Contains(g.Key)) continue;
                var c = g.FirstOrDefault(d => d.UsagePage == 0x008C && d.FeatLen >= 21);
                if (c == null) continue;
                bool dongle = g.Key == 0xFF30;
                string name = dongle ? "" : Clean(g.First().Product);
                // the receiver and the mouse on its cable are one mouse
                var r = new Reading { Id = "dms-mouse", Name = name == "" ? "Darmoshark" : name, Kind = "mouse", Source = "darmoshark", Receiver = dongle };
                using (var h = CreateFile(c.Path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    byte[] x = null;
                    if (dongle)
                    {
                        var cur = DmsGet(h, c.FeatLen);
                        bool primed = cur == null || cur[1] != 0x06 || (DmsSend(h, c.FeatLen, 0x03) && DmsAwait(h, c.FeatLen, 0x03, 800) != null);
                        if (primed && DmsSend(h, c.FeatLen, 0x06)) x = DmsAwait(h, c.FeatLen, 0x06, 1500);
                    }
                    else
                    {
                        for (int i = 0; i < 3 && x == null; i++)
                        {
                            if (!DmsSend(h, c.FeatLen, 0x06)) break;
                            Thread.Sleep(80);
                            var y = DmsGet(h, c.FeatLen);
                            if (y != null && y[1] == 0x06) x = y; else Thread.Sleep(100);
                        }
                    }
                    if (x != null && x.Length > 11 && x[2] == 1 && x[11] >= 1 && x[11] <= 100) r.Level = x[11];   // 0 = no reading, not an empty battery
                    else if (x != null) Trace.Add("dms in: " + Hex(x, 16));
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- Darmoshark 4K (Nordic, "4K NRF Dongle" 1915:0725)
        // From the vendor's own web driver (the Keychron launcher bundle behind darmoshark.cc: usage page FF0A =
        // "mouse_4k", protocol "4k", getPower). Unnumbered 64-byte reports: [0] command, [2] 0x81 = read, [3] item,
        // [63] checksum = 161 - (sum of [0..62]). Command 0x01 item 1 = power; through the receiver the command gets
        // bit 6 (0x41). Reply echoes [0] and [3]; [5] state (non-zero = charging: the driver then shows no level),
        // [6] level %. Read-only; a sleeping mouse does not answer.
        static byte[] Dms4kFrame(byte cmd)
        {
            var f = new byte[65];                       // [0] report id 0, then the 64-byte report
            f[1] = cmd; f[3] = 0x81; f[4] = 0x01;
            int sum = 0; for (int i = 1; i < 64; i++) sum += f[i];
            f[64] = (byte)(161 - (sum & 0xFF));
            return f;
        }

        static void ReadDarmoshark4K(List<HidInfo> devs, List<Reading> outp)
        {
            var c = devs.FirstOrDefault(d => d.UsagePage == 0xFF0A && d.Usage == 0x0001 && d.OutLen >= 65 && d.InLen >= 65
                                         && (d.Pid == 0x0725 || (d.Product ?? "").IndexOf("NRF Dongle", StringComparison.OrdinalIgnoreCase) >= 0));
            if (c == null) return;
            var r = new Reading { Id = "dms4k-mouse", Name = "Darmoshark 4K", Kind = "mouse", Source = "darmoshark", Receiver = true };
            using (var h = Open(c.Path))
            {
                if (h.IsInvalid) { Trace.Add("dms4k open failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return; }
                byte[] x = null;
                // through the receiver first (0x41), then the plain form (0x01)
                foreach (byte cmd in new byte[] { 0x41, 0x01 })
                {
                    byte want = cmd;
                    x = Ask(h, Dms4kFrame(cmd), c.OutLen, c.InLen, 700, q => q.Length >= 8 && q[1] == want && q[4] == 0x01);
                    if (x != null) break;
                }
                if (x == null) Trace.Add("dms4k: no reply (asleep?)");
                else if (x[6] != 0) r.Charging = true;                          // charging: the driver shows no level either
                else if (x[7] >= 1 && x[7] <= 100) r.Level = x[7];
                else Trace.Add("dms4k in: " + Hex(x, 16));
            }
            outp.Add(r);
        }

        // ================================================================ more mice, headsets and keyboards
        // From HaloBattery's providers (MIT) and the projects each one names. Every exchange below is a
        // read: a battery / status request and its answer, checked before it counts.

        // a feature report on a no-access handle (feature reports need no read/write rights)
        static SafeFileHandle OpenQuery(string path) { return CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero); }
        static bool SetFeature(SafeFileHandle h, byte[] f) { return HidD_SetFeature(h, f, f.Length); }
        static byte[] GetFeature(SafeFileHandle h, byte id, int len) { var b = new byte[len]; b[0] = id; return HidD_GetFeature(h, b, len) ? b : null; }

        // An output report, or - when the collection refuses that write ("Incorrect function") - the same bytes as a
        // feature report; then input reports until accept() takes one.
        static byte[] AskOrFeature(SafeFileHandle h, byte[] frame, HidInfo c, int timeoutMs, Func<byte[], bool> accept)
        {
            Drain(h, c.InLen);
            var f = new byte[Math.Max(frame.Length, c.OutLen)]; Array.Copy(frame, f, frame.Length);
            if (c.OutLen == 0 || !Write(h, f))
            {
                if (c.FeatLen == 0) return null;
                var ff = new byte[Math.Max(frame.Length, c.FeatLen)]; Array.Copy(frame, ff, frame.Length);
                if (!HidD_SetFeature(h, ff, ff.Length)) { Trace.Add("write and feature both refused"); return null; }
                Trace.Add("request sent as a feature report");
            }
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                var r = Read(h, c.InLen, Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds));
                if (r != null && accept(r)) return r;
            }
            return null;
        }

        // ---------------------------------------------------------------- WLmouse / LAMZU / G-Wolves (one firmware family)
        // incconutwo/mouse-battery-tray, Sheroune/lamzu-battery-monitory, G-Wolves' web driver: feature report 0,
        // 64 bytes 00 00 02 02 00 83 ...; the answer (feature report 0) a1|a2 00 02 02 00 83 <charging> <level %>.
        static int[] W83Read(string path, int featLen, int tries)
        {
            using (var h = OpenQuery(path))
            {
                if (h.IsInvalid) return null;
                var q = new byte[Math.Max(65, featLen)]; q[3] = 0x02; q[4] = 0x02; q[6] = 0x83;   // [0] report id 0, then 00 00 02 02 00 83
                if (!SetFeature(h, q)) { Trace.Add("w83: request refused"); return null; }
                for (int t = 0; t < tries; t++)
                {
                    Thread.Sleep(50);
                    var v = W83Parse(GetFeature(h, 0, q.Length));
                    if (v != null) return v;
                }
                Trace.Add("w83: no a1 answer (mouse asleep or off)");
                return null;
            }
        }

        // the 0x83 answer -> { level %, charging 0/1 } or null
        public static int[] W83Parse(byte[] r)
        {
            if (r == null) return null;
            for (int i = 5; i < r.Length - 2; i++)
                if (r[i] == 0x83 && r[i - 1] == 0x00 && r[i - 2] == 0x02 && (r[i - 5] == 0xA1 || r[i - 5] == 0xA2) && r[i + 2] <= 100)
                    return new[] { (int)r[i + 2], r[i + 1] != 0 ? 1 : 0 };
            return null;
        }

        // G-Wolves' older exchange (the web driver's getOldBattery): feature report 0, 64 bytes 00 02 8f <01 over a
        // receiver, 00 on the cable>; the answer a1 02 8f .. <charging> <level %>, with or without the report id byte
        static int[] W83OldRead(string path, int featLen, bool wired)
        {
            using (var h = OpenQuery(path))
            {
                if (h.IsInvalid) return null;
                var q = new byte[Math.Max(65, featLen)]; q[2] = 0x02; q[3] = 0x8F; q[4] = (byte)(wired ? 0x00 : 0x01);   // [0] report id 0
                if (!SetFeature(h, q)) { Trace.Add("w83 old: request refused"); return null; }
                for (int t = 0; t < 15; t++)
                {
                    Thread.Sleep(50);
                    var v = W83OldParse(GetFeature(h, 0, q.Length));
                    if (v != null) return v;
                }
                Trace.Add("w83 old: no a1 answer (mouse asleep or off)");
                return null;
            }
        }

        public static int[] W83OldParse(byte[] r)
        {
            if (r == null) return null;
            foreach (int off in new[] { 1, 0 })
                if (r.Length > off + 5 && r[off] == 0xA1 && r[off + 1] == 0x02 && r[off + 2] == 0x8F && r[off + 5] <= 100)
                    return new[] { (int)r[off + 5], r[off + 4] != 0 ? 1 : 0 };
            return null;
        }

        // G-Wolves models with a receiver of their own, from the model list of G-Wolves' web driver (via HaloBattery
        // providers/gwolves.py; HSK Pro ACE confirmed there): pid -> name, the model's receiver (one icon per model),
        // the older exchange, on the cable
        class GwModel { public string Name; public int Receiver; public bool Old, Wired; }
        static readonly Dictionary<int, GwModel> GWolvesModels = GwList(
            "HTM Plus:3817:N:3808,3817", "HSK Pro 2.0:6817:N:6808,6817", "HTXU:5617:N:5608,5617", "Fenrir Pro:3617:N:3608,3617",
            "VUK:3917:O:3908,3917", "HT-S2:7913:O:7904,7913", "Fenrir Max:3717:O:3708,3717", "HTS Ultra:5317:O:5308,5317",
            "HTR:7713:O:7704,7713", "Fenrir:3517:O:3508,3517", "HT-S2 Pro:7917:O:7908,7917", "HSK Pro:5817:O:5808,5817,5807",
            "HTX Mini:2717:O:2708,2717", "HTS Plus:5417:O:5408,5417,5407", "HTX:5717:O:5708,5717,5707", "HSK Plus:5917:O:5908,5917,5907",
            "HSK Lite:7203:O:7204,7203", "HTR Pro:7717:O:7708,7717", "HSK Pro ACE:5803:O:5804,5803", "HTS Plus ACE:5403:O:5404,5403",
            "HTX ACE:5703:O:5704,5703", "HSK Plus ACE:5903:O:5904,5903");

        // "name:receiver:N|O:the cable pid,the receiver pid[,another receiver pid]"
        static Dictionary<int, GwModel> GwList(params string[] rows)
        {
            var d = new Dictionary<int, GwModel>();
            foreach (var row in rows)
            {
                var p = row.Split(':'); int rcv = Convert.ToInt32(p[1], 16); var pids = p[3].Split(',');
                for (int i = 0; i < pids.Length; i++)
                    d[Convert.ToInt32(pids[i], 16)] = new GwModel { Name = "G-Wolves " + p[0], Receiver = rcv, Old = p[2] == "O", Wired = i == 0 };
            }
            return d;
        }

        static readonly Dictionary<int, string> WlMice = new Dictionary<int, string> { { 0xA887, "WLmouse Beast X" }, { 0xA868, "WLmouse Beast X Mini Pro" }, { 0xA880, "WLmouse Beast X Max" } };
        static readonly Dictionary<int, string> GWolvesWired = new Dictionary<int, string> {
            { 0x5418, "G-Wolves HTS Plus" }, { 0x5419, "G-Wolves HTS Plus" }, { 0x5219, "G-Wolves HTS Plus Pro" }, { 0x4718, "G-Wolves Lycan" }, { 0x4719, "G-Wolves Lycan" },
            { 0x5618, "G-Wolves HTXU" }, { 0x5619, "G-Wolves HTXU" }, { 0x3619, "G-Wolves Fenrir Pro" }, { 0x3519, "G-Wolves Fenrir Asym" }, { 0x2719, "G-Wolves HTX Mini" }, { 0x4219, "G-Wolves WARG" } };

        static void ReadW83Family(int vid, List<HidInfo> devs, List<Reading> outp)
        {
            // the mouse on its cable first: it charges and names the model; one icon per family
            var groups = devs.GroupBy(d => d.Pid).ToList();
            Reading r = null;
            foreach (var g in groups.OrderBy(x => vid == 0x33E4 ? (x.Key == 0x3854 ? 1 : 0) : vid == 0x373E ? (x.Key == 0x001C ? 0 : 1) : (WlMice.ContainsKey(x.Key) ? 1 : 0)))
            {
                HidInfo c; string name; bool receiver;
                if (vid == 0x373E)
                {
                    if (g.Key != 0x001E && g.Key != 0x001C) continue;
                    c = g.FirstOrDefault(d => d.Interface == 2 && d.UsagePage == 0xFFFF); name = "LAMZU Maya X"; receiver = g.Key == 0x001E;
                }
                else if (vid == 0x33E4)
                {
                    if (g.Key != 0x3854 && !GWolvesWired.ContainsKey(g.Key)) continue;
                    c = g.FirstOrDefault(d => d.FeatLen == 65); receiver = g.Key == 0x3854;
                    name = receiver ? "G-Wolves mouse" : GWolvesWired[g.Key];
                }
                else
                {
                    c = g.Where(d => d.UsagePage == 0xFFFF).OrderBy(d => d.Usage == 0 ? 0 : 1).FirstOrDefault();
                    receiver = WlMice.ContainsKey(g.Key) || (g.First().Product ?? "").ToUpperInvariant().Contains("RECEIVER");
                    name = WlMice.ContainsKey(g.Key) ? WlMice[g.Key] : "WLmouse " + Clean(g.First().Product);
                }
                if (c == null) continue;
                if (r == null) r = new Reading { Id = vid == 0x373E ? "lamzu-mouse" : vid == 0x33E4 ? "gwolves-mouse" : "wlmouse-mouse", Name = name, Kind = "mouse", Source = "w83", Receiver = receiver };
                var v = W83Read(c.Path, c.FeatLen, vid == 0x373E ? 10 : 15);
                if (v != null) { r.Level = v[0]; r.Charging = v[1] != 0; r.Receiver = receiver; if (!receiver) r.Name = name; break; }
            }
            if (r != null) outp.Add(r);
            if (vid != 0x33E4) return;
            // G-Wolves models on a receiver of their own: one icon per model, the cable first (it charges)
            foreach (var model in devs.Where(d => GWolvesModels.ContainsKey(d.Pid)).GroupBy(d => GWolvesModels[d.Pid].Receiver))
            {
                var mr = new Reading { Id = "gwolves-" + model.Key.ToString("X4"), Name = GWolvesModels[model.First().Pid].Name, Kind = "mouse", Source = "w83", Receiver = true };
                foreach (var pg in model.GroupBy(d => d.Pid).OrderBy(x => GWolvesModels[x.Key].Wired ? 0 : 1))
                {
                    var m = GWolvesModels[pg.Key];
                    var c = pg.FirstOrDefault(d => d.FeatLen == 65);
                    if (c == null) continue;
                    var v = m.Old ? W83OldRead(c.Path, c.FeatLen, m.Wired) : W83Read(c.Path, c.FeatLen, 15);
                    if (v != null) { mr.Level = v[0]; mr.Charging = v[1] != 0; mr.Receiver = !m.Wired; break; }
                }
                outp.Add(mr);
            }
        }

        // ---------------------------------------------------------------- Keychron (3434): M5, Ultra-Link 8K receiver
        // csutcliff/keychron-battery-dkms: on interface 4, feature report b3 06 00 ...; the answer is an input report
        // b4 06 ... with the level in byte 20. No charging flag.
        static readonly Dictionary<int, string> KeychronPids = new Dictionary<int, string> { { 0xD028, "Keychron (Ultra-Link 8K)" }, { 0xD048, "Keychron M5" } };

        static void ReadKeychron(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.Where(d => KeychronPids.ContainsKey(d.Pid)).GroupBy(d => d.Pid))
            {
                var c = g.FirstOrDefault(d => d.Interface == 4 && d.InLen >= 21 && d.FeatLen >= 2); if (c == null) continue;
                var r = new Reading { Id = "keychron-" + g.Key.ToString("X4"), Name = KeychronPids[g.Key], Kind = g.Key == 0xD048 ? "mouse" : "keyboard", Source = "keychron", Receiver = g.Key == 0xD028 };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) { outp.Add(r); continue; }
                    for (int a = 0; a < 3 && r.Level < 0; a++)
                    {
                        Drain(h, c.InLen);
                        var f = new byte[Math.Max(c.FeatLen, 64)]; f[0] = 0xB3; f[1] = 0x06;
                        if (!SetFeature(h, f)) { Trace.Add("keychron: request refused"); break; }
                        var end = DateTime.UtcNow.AddMilliseconds(500);
                        while (DateTime.UtcNow < end)
                        {
                            var x = Read(h, c.InLen, 100);
                            if (x != null && x.Length > 20 && x[0] == 0xB4 && x[1] == 0x06 && x[20] <= 100) { r.Level = x[20]; break; }
                        }
                    }
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- ASUS ROG / TUF mice (0B05)
        // G-Helper (AsusMouse.cs): on the vendor collection of interface 0, output 00 12 07; the answer echoes 12 07,
        // byte 4 after the echo = level (a percentage, or 0..4 on older models), byte 9 = charging. 0 and not
        // charging is standby, not an empty battery; ff aa is "unknown command".
        static readonly Dictionary<int, string> AsusMice = new Dictionary<int, string> {
            { 0x1A72, "ROG Gladius III Aimpoint" }, { 0x1A70, "ROG Gladius III Aimpoint" }, { 0x1B0C, "ROG Gladius III Eva 2" }, { 0x1B0A, "ROG Gladius III Eva 2" },
            { 0x197F, "ROG Gladius III Wireless" }, { 0x197D, "ROG Gladius III Wireless" }, { 0x1A1A, "ROG Chakram X" }, { 0x1A18, "ROG Chakram X" },
            { 0x1A94, "ROG Harpe Ace Aim Lab Edition" }, { 0x1A92, "ROG Harpe Ace Aim Lab Edition" }, { 0x1A68, "ROG Keris Wireless Aimpoint" }, { 0x1A66, "ROG Keris Wireless Aimpoint" },
            { 0x1979, "ROG Spatha X" }, { 0x1977, "ROG Spatha X" }, { 0x19F4, "TUF Gaming M4 Wireless" }, { 0x1A8D, "TX Gaming Mouse" }, { 0x1AF5, "TX Gaming Mouse Mini" },
            { 0x1AF3, "TX Gaming Mouse Mini" }, { 0x1C57, "TUF Gaming Mini Miku Edition" }, { 0x1C56, "TUF Gaming Mini Miku Edition" },
            { 0x18E5, "ROG Chakram" }, { 0x18E3, "ROG Chakram" }, { 0x1960, "ROG Keris Wireless" }, { 0x195E, "ROG Keris Wireless" }, { 0x1A59, "ROG Keris EVA Edition" },
            { 0x1A57, "ROG Keris EVA Edition" }, { 0x1908, "ROG Pugio II" }, { 0x1906, "ROG Pugio II" }, { 0x1949, "ROG Strix Impact II Wireless" }, { 0x1947, "ROG Strix Impact II Wireless" } };
        static readonly HashSet<int> AsusSteps = new HashSet<int> { 0x18E5, 0x18E3, 0x1960, 0x195E, 0x1A59, 0x1A57, 0x1908, 0x1906, 0x1949, 0x1947 };

        static void ReadAsus(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.Where(d => AsusMice.ContainsKey(d.Pid)).GroupBy(d => d.Pid))
            {
                var c = g.FirstOrDefault(d => d.Interface == 0 && d.UsagePage >= 0xFF00 && d.OutLen >= 3); if (c == null) continue;
                var r = new Reading { Id = "asus-" + AsusMice[g.Key].ToLowerInvariant().Replace(' ', '-'), Name = AsusMice[g.Key], Kind = "mouse", Source = "asus", Approx = AsusSteps.Contains(g.Key) };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) { outp.Add(r); continue; }
                    Func<byte[], int> echo = q => q.Length > 2 && q[0] == 0x12 && q[1] == 0x07 ? 0 : q.Length > 3 && q[0] == 0x00 && q[1] == 0x12 && q[2] == 0x07 ? 1 : -1;
                    var x = Ask(h, new byte[] { 0x00, 0x12, 0x07 }, c.OutLen, c.InLen, 900, q => echo(q) >= 0);
                    int m = x == null ? -1 : echo(x);
                    if (m >= 0 && x.Length > m + 9)
                    {
                        int raw = x[m + 4]; bool chg = x[m + 9] != 0;
                        if (!(raw == 0 && !chg))
                        {
                            if (AsusSteps.Contains(g.Key)) { if (raw <= 4) r.Level = raw * 25; }
                            else if (raw <= 100) r.Level = raw;
                            r.Charging = chg && r.Level >= 0;
                        }
                    }
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- MCHOSE (5253 / 3837) and the G7 (A8A5:2255)
        // alexfrih/mchose-linux (from MCHOSE's M HUB bundle): on the FF01 collection, feature report 0x11 (20 bytes)
        // or 0x12 (64) with every payload byte inverted; command 06 answers vid, model, firmware, flags, level,
        // charging (inverted from byte 2, echo cmd ^ ff in byte 1). Asked again for every attempt. G7: output
        // 00 55 30 a5 0b 2e 01 01 01, answer AA 30 ... level in byte 8, charging in byte 9.
        static void ReadMchose(int vid, List<HidInfo> devs, List<Reading> outp)
        {
            if (vid == 0xA8A5)
            {
                var c = devs.FirstOrDefault(d => d.Pid == 0x2255 && d.UsagePage == 0xFF01 && d.Usage == 0x0010 && d.OutLen >= 9); if (c == null) return;
                var r = new Reading { Id = "mchose-g7", Name = "MCHOSE G7", Kind = "mouse", Source = "mchose", Receiver = true };
                using (var h = Open(c.Path))
                {
                    if (!h.IsInvalid)
                    {
                        Func<byte[], int> at = q => q.Length > 10 && q[0] == 0xAA && q[1] == 0x30 ? 0 : q.Length > 11 && q[0] == 0x00 && q[1] == 0xAA && q[2] == 0x30 ? 1 : -1;
                        var x = Ask(h, new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 }, c.OutLen, c.InLen, 600, q => at(q) >= 0);
                        if (x != null) { int m = at(x); if (x[m + 8] <= 100) { r.Level = x[m + 8]; r.Charging = x[m + 9] != 0; } }
                    }
                }
                outp.Add(r); return;
            }
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                var c = g.FirstOrDefault(d => d.UsagePage == 0xFF01 && d.FeatLen >= 21); if (c == null) continue;
                var r = new Reading { Id = "mchose-" + vid.ToString("X4"), Name = "MCHOSE mouse", Kind = "mouse", Source = "mchose", Receiver = true };
                using (var h = OpenQuery(c.Path))
                {
                    if (h.IsInvalid) { outp.Add(r); continue; }
                    foreach (var ch in new[] { new[] { 0x11, 20 }, new[] { 0x12, 64 } })
                    {
                        if (ch[1] + 1 > c.FeatLen || r.Level >= 0) continue;
                        for (int a = 0; a < 4 && r.Level < 0; a++)
                        {
                            var f = new byte[c.FeatLen]; f[0] = (byte)ch[0];
                            for (int i = 1; i <= ch[1]; i++) f[i] = (byte)((i == 1 ? 0x06 : 0x00) ^ 0xFF);
                            if (!SetFeature(h, f)) break;
                            Thread.Sleep(120);
                            var x = GetFeature(h, (byte)ch[0], c.FeatLen);
                            if (x == null || x.Length < 13 || x[1] != (0x06 ^ 0xFF)) continue;
                            var p = x.Skip(2).Select(b => (byte)(b ^ 0xFF)).ToArray();
                            int pvid = p[0] | (p[1] << 8), model = p[2] | (p[3] << 8);
                            if ((pvid == 0x5253 || pvid == 0x3837) && p[9] <= 100)
                            {
                                r.Level = p[9]; r.Charging = p[10] != 0;
                                if (model == 0x0031) r.Name = "MCHOSE M7 Ultra";
                            }
                        }
                    }
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- AM Infinity 8K mouse (3151:5007)
        // Aiacos/ajazz-control-center: on FFFF:0002, feature report 0 "00 f7" (the vendor's status poll), then feature
        // report 05: 05 00 00 <charge>. A non-zero byte before the charge is reconnect junk; 0 = link not up yet.
        static void ReadAmInfinity(List<HidInfo> devs, List<Reading> outp)
        {
            var c = devs.FirstOrDefault(d => d.Pid == 0x5007 && d.UsagePage == 0xFFFF && d.Usage == 0x0002 && d.FeatLen >= 4); if (c == null) return;
            var r = new Reading { Id = "aminfinity-mouse", Name = "AM Infinity 8K Mouse", Kind = "mouse", Source = "aminfinity", Receiver = true };
            using (var h = OpenQuery(c.Path))
            {
                if (!h.IsInvalid)
                {
                    var f = new byte[c.FeatLen]; f[1] = 0xF7;
                    if (SetFeature(h, f))
                    {
                        Thread.Sleep(30);
                        var x = GetFeature(h, 0x05, c.FeatLen);
                        if (x != null && x[0] == 0x05 && x[1] == 0 && x[2] == 0 && x[3] > 0) r.Level = Math.Min((int)x[3], 100);
                    }
                }
            }
            outp.Add(r);
        }

        // ---------------------------------------------------------------- Lofree Hyzen keyboard dongle (388D:0025)
        // Lofree's web driver (hyzen.lofree.tech): transactions on report 0x04 - start (00 00 01), command, end
        // (00 00 02); command AA = online (0 = off), 1A = battery %, the answer starts at byte 7 after the id.
        static void ReadLofree(List<HidInfo> devs, List<Reading> outp)
        {
            if (devs.Any(d => d.Pid == 0x0024)) return;                       // on its cable: the web driver does not read it then
            var c = devs.FirstOrDefault(d => d.Pid == 0x0025 && d.UsagePage == 0xFF1C && d.Usage == 0x0092 && d.OutLen >= 8); if (c == null) return;
            var r = new Reading { Id = "lofree-keyboard", Name = "Lofree " + Clean(((c.Product ?? "Hyzen").Split('@'))[0]), Kind = "keyboard", Source = "lofree", Receiver = true };
            using (var h = Open(c.Path))
            {
                if (!h.IsInvalid)
                {
                    Func<byte, int> cmd = op =>
                    {
                        Func<byte[], bool> isAck = q => q.Length > 3 && q[0] == 0x04;
                        if (Ask(h, new byte[] { 0x04, 0x00, 0x00, 0x01 }, c.OutLen, c.InLen, 1500, q => isAck(q) && q[3] == 0x01) == null) return -1;
                        var a = Ask(h, new byte[] { 0x04, 0x00, 0x00, op }, c.OutLen, c.InLen, 1500, q => isAck(q) && q.Length > 8 && (q[3] == op || q[3] == 0) && q[5] == 0 && q[6] == 0);
                        Ask(h, new byte[] { 0x04, 0x00, 0x00, 0x02 }, c.OutLen, c.InLen, 500, q => isAck(q) && q[3] == 0x02);
                        return a == null ? -1 : a[8];
                    };
                    int online = cmd(0xAA);
                    if (online > 0) { int lvl = cmd(0x1A); if (lvl >= 0 && lvl <= 100) r.Level = lvl; }
                    else if (online == 0) Trace.Add("lofree: keyboard offline");
                }
            }
            outp.Add(r);
        }

        // ---------------------------------------------------------------- JBL Quantum 910 Wireless (0ECB:2088)
        // plugato/JBL_Baterry_Monitor: listen only - the headset pushes report 08 <level %> on FF13:0001, on events.
        static void ReadJbl(List<HidInfo> devs, List<Reading> outp)
        {
            var c = devs.FirstOrDefault(d => d.Pid == 0x2088 && d.UsagePage == 0xFF13 && d.Usage == 0x0001 && d.InLen >= 2); if (c == null) return;
            var r = new Reading { Id = "jbl-q910", Name = "JBL Quantum 910 Wireless", Kind = "headphones", Source = "jbl", Receiver = true };
            // The headset sends its level now and then on its own: a listener reads the receiver all the time in the
            // background (as HaloBattery 1.14 does), so a level sent between polls is not missed and a poll never
            // waits. A level older than 3 minutes is not used.
            lock (JblLock)
            {
                JblSeen = DateTime.UtcNow;
                if (JblThread == null || !JblThread.IsAlive || JblPath != c.Path)
                {
                    JblPath = c.Path; JblLevel = -1;
                    var path = c.Path; int len = c.InLen;
                    JblThread = new Thread(() => JblListen(path, len)) { IsBackground = true, Name = "jbl listener" };
                    JblThread.Start();
                }
                if (JblLevel >= 0 && (DateTime.UtcNow - JblAt).TotalSeconds < 180) r.Level = JblLevel;
            }
            outp.Add(r);
        }

        static readonly object JblLock = new object();
        static Thread JblThread; static string JblPath; static int JblLevel = -1; static DateTime JblAt, JblSeen;

        // runs while the receiver is plugged in: stops 5 minutes after the last poll that saw it
        static void JblListen(string path, int len)
        {
            try
            {
                using (var h = Open(path))
                {
                    if (h.IsInvalid) return;
                    while (true)
                    {
                        lock (JblLock) { if (JblPath != path || (DateTime.UtcNow - JblSeen).TotalMinutes > 5) return; }
                        var x = Read(h, len, 1000);
                        int lvl = JblParse(x);
                        if (lvl >= 0) lock (JblLock) { JblLevel = lvl; JblAt = DateTime.UtcNow; }
                    }
                }
            }
            catch (Exception e) { lock (Trace) Trace.Add("jbl listener: " + e.Message); }
        }

        // the level report 08 <level %>
        public static int JblParse(byte[] x) { return x != null && x.Length >= 2 && x[0] == 0x08 && x[1] <= 100 ? x[1] : -1; }

        // ---------------------------------------------------------------- Audeze Maxwell (3329)
        // HeadsetControl (audeze_maxwell.hpp), as HaloBattery reads it: only the battery packet
        // 06 07 80 05 5a 03 00 d6 0c (output report 06, 62 bytes); input report 07 holds a rolling buffer with the
        // marker d6 0c 00 00 <level %>. "Audeze Maxwell Dongle" = no headset linked. The cable endpoint means charging.
        static readonly Dictionary<int, string> AudezePids = new Dictionary<int, string> {
            { 0x4B19, "Audeze Maxwell" }, { 0x4B18, "Audeze Maxwell (Xbox)" }, { 0x4B1A, "Audeze Maxwell" }, { 0x4B1E, "Audeze Maxwell (Xbox)" },
            { 0x4B29, "Audeze Maxwell 2" }, { 0x4B28, "Audeze Maxwell 2 (Xbox)" } };

        static void ReadAudeze(List<HidInfo> devs, List<Reading> outp)
        {
            Reading r = null;
            foreach (var g in devs.Where(d => AudezePids.ContainsKey(d.Pid)).GroupBy(d => d.Pid).OrderBy(x => x.Key == 0x4B1A || x.Key == 0x4B1E ? 0 : 1))
            {
                if (g.Any(d => (d.Product ?? "").Trim().Equals("Audeze Maxwell Dongle", StringComparison.OrdinalIgnoreCase))) { Trace.Add("audeze: no headset linked"); continue; }
                var c = g.FirstOrDefault(d => d.UsagePage == 0xFF13 && d.Usage == 0x0001 && d.OutLen >= 10 && d.InLen >= 10); if (c == null) continue;
                bool cable = g.Key == 0x4B1A || g.Key == 0x4B1E;
                if (r == null) r = new Reading { Id = "audeze-maxwell", Name = AudezePids[g.Key], Kind = "headphones", Source = "audeze", Receiver = !cable };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) continue;
                    var f = new byte[c.OutLen]; var pkt = new byte[] { 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C }; Array.Copy(pkt, f, pkt.Length);
                    if (!Write(h, f)) continue;
                    int level = -1; bool stuck = true;
                    for (int i = 0; i < 3 && level < 0; i++)
                    {
                        Thread.Sleep(60);
                        var x = new byte[c.InLen]; x[0] = 0x07;
                        if (!HidD_GetInputReport(h, x, x.Length)) { stuck = false; continue; }
                        if (x.Skip(3).Any(v => v != 0)) stuck = false;
                        for (int k = 0; k + 4 < x.Length; k++) if (x[k] == 0xD6 && x[k + 1] == 0x0C && x[k + 2] == 0 && x[k + 3] == 0 && x[k + 4] <= 100) { level = x[k + 4]; break; }
                    }
                    if (level > 0) { r.Level = level; r.Charging = cable; r.Receiver = !cable; break; }   // 0 right after power-on is "not measured yet"
                    if (level < 0 && stuck) Trace.Add("audeze: the dongle answers with empty echoes only - unplug it and plug it back in");
                }
            }
            if (r != null) outp.Add(r);
        }

        // ---------------------------------------------------------------- Astro A50 Gen 5 base station (046D:0B1C)
        // HeadsetControl logitech_astro_a50: on FF32:0074, report 02: 02 0c 03 00 06 <handle>; the answer
        // 02 0c .. 00 06 .. <level> <level2> <docked>.
        static Reading ReadAstro(IEnumerable<HidInfo> g)
        {
            var c = g.FirstOrDefault(d => d.UsagePage == 0xFF32 && d.Usage == 0x0074 && d.OutLen >= 6); if (c == null) return null;
            var r = new Reading { Id = "astro-a50", Name = "Astro A50 Gen 5", Kind = "headphones", Source = "astro", Receiver = true };
            using (var h = Open(c.Path))
            {
                if (h.IsInvalid) return r;
                var x = Ask(h, new byte[] { 0x02, 0x0C, 0x03, 0x00, 0x06, 0x0C }, c.OutLen, c.InLen, 1500, q => q.Length > 8 && q[0] == 0x02 && q[1] == 0x0C && q[4] == 0x06);
                if (x != null && x[6] <= 100) { r.Level = x[6]; r.Charging = x[8] != 0; }
            }
            return r;
        }

        // ---------------------------------------------------------------- Razer Barracuda Pro, 2.4 GHz (1532:053A)
        // HaloBattery barracuda.py, from a capture of Synapse: report 01, 'P','A' request two bytes earlier than the
        // BlackShark's; remote mode on, query 21 (battery) and 2A (charging), remote mode off. Reply 'P','I', echo at 13.
        static int[] BarracudaPro(string path, int outLen, int inLen)
        {
            using (var h = Open(path))
            {
                if (h.IsInvalid) return null;
                Func<byte, byte, byte, byte[]> frame = (len, type, cmd) => { var b = new byte[Math.Max(64, outLen)]; b[0] = 0x01; b[1] = 0x80; b[2] = len; b[3] = 0x50; b[4] = 0x41; b[5] = 0x08; b[6] = 0x08; b[7] = type; b[8] = cmd; return b; };
                var on = frame(0x07, 0x02, 0xE1); on[5] = 0x0E; on[9] = 1;
                var off = frame(0x07, 0x02, 0xE1); off[5] = 0x0E;
                Func<byte, int> query = cmd =>
                {
                    for (int a = 0; a < 2; a++)
                    {
                        var x = Ask(h, frame(8, 0x03, cmd), outLen, inLen, 500, q => q.Length > 16 && q[3] == 0x50 && q[4] == 0x49 && q[13] == cmd && (q[14] == 1 || q[14] == 2) && q[15] > 0);
                        if (x != null) return x[16];
                    }
                    return -1;
                };
                Drain(h, inLen); if (!Write(h, on)) return null;
                Thread.Sleep(50);
                int lvl = query(0x21); int chg = lvl >= 0 ? query(0x2A) : -1;
                Write(h, off);
                return lvl >= 0 && lvl <= 100 ? new[] { lvl, chg > 0 ? 1 : 0 } : null;
            }
        }

        // ---------------------------------------------------------------- older SteelSeries Arctis (HeadsetControl)
        // Arctis 1 / 7X / 7P (iface 3, FF43): 06 12 -> 06 12 <status> <level>, status 01 = off.
        // Arctis 7 2018 (iface 5): 06 14 -> link 03, then 06 18 -> level. Arctis 7 2019 / Pro Wireless 2019 (iface 5):
        // 06 18 -> level, 0 = off. Arctis 9 (iface 0): 00 20 -> aa 01 .. <raw 0x64..0x9a> <charging>.
        static Reading ReadArctisClassic(int pid, IEnumerable<HidInfo> g)
        {
            string name; int iface; int kind;   // kind: 1 = Arctis 1 family, 2 = Arctis 7 2018, 3 = Arctis 7 / Pro 2019, 4 = Arctis 9
            switch (pid)
            {
                case 0x12B3: name = "Arctis 1 Wireless"; iface = 3; kind = 1; break;
                case 0x12B6: name = "Arctis 1 Wireless Xbox"; iface = 3; kind = 1; break;
                case 0x12D7: name = "Arctis 7X"; iface = 3; kind = 1; break;
                case 0x12D5: name = "Arctis 7P"; iface = 3; kind = 1; break;
                case 0x12AD: name = "Arctis 7"; iface = 5; kind = 2; break;
                case 0x1260: name = "Arctis 7"; iface = 5; kind = 3; break;
                case 0x1252: name = "Arctis Pro Wireless 2019"; iface = 5; kind = 3; break;
                case 0x12C2: name = "Arctis 9"; iface = 0; kind = 4; break;
                default: return null;
            }
            var c = g.FirstOrDefault(d => d.Interface == iface && (kind == 1 ? d.UsagePage == 0xFF43 : d.UsagePage >= 0xFF00) && d.OutLen >= 2); if (c == null) return null;
            var r = new Reading { Id = "ss-" + pid.ToString("X4"), Name = name, Kind = "headphones", Source = "steelseries", Receiver = true };
            using (var h = Open(c.Path))
            {
                if (h.IsInvalid) return r;
                Func<byte, byte, Func<byte[], bool>, byte[]> ask = (a, b, ok) => Strip0(Ask(h, new byte[] { a, b }, c.OutLen, c.InLen, 800, q => ok(Strip0(q))));
                Func<byte, byte, Func<byte[], bool>> echo = (a, b) => q => q.Length >= 4 && q[0] == a && q[1] == b;
                if (kind == 1) { var x = ask(0x06, 0x12, echo(0x06, 0x12)); if (x != null && x[2] != 0x01) r.Level = Math.Min((int)x[3], 100); }
                else if (kind == 2) { var x = ask(0x06, 0x14, echo(0x06, 0x14)); if (x != null && x[2] == 0x03) { x = ask(0x06, 0x18, echo(0x06, 0x18)); if (x != null) r.Level = Math.Min((int)x[2], 100); } }
                else if (kind == 3) { var x = ask(0x06, 0x18, echo(0x06, 0x18)); if (x != null && x[2] != 0) r.Level = Math.Min((int)x[2], 100); }
                else
                {
                    var x = ask(0x00, 0x20, q => q.Length >= 5 && (q[0] == 0xAA || q[0] == 0x55));
                    if (x != null && x[0] == 0xAA && x[1] == 0x01) { int raw = Math.Min(Math.Max((int)x[3], 0x64), 0x9A); r.Level = (raw - 0x64) * 100 / (0x9A - 0x64); r.Charging = x[4] == 0x01; }
                }
            }
            return r;
        }

        // ================================================================ game controllers
        // HaloBattery providers/playstation.py, nintendo.py, eightbitdo.py (MIT), after the Linux
        // hid-playstation driver, DS4Windows and SDL's HIDAPI drivers. Xbox pads come through XInput
        // (Native.cs) and, on Bluetooth, through the level Windows itself reports (Batteries.cs).
        static bool IsBluetoothPath(string p)
        {
            p = (p ?? "").ToLowerInvariant();
            return p.Contains("{00001124-0000-1000-8000-00805f9b34fb}") || p.Contains("vid&");
        }

        static HidInfo PadCollection(IEnumerable<HidInfo> g)
        {
            return g.FirstOrDefault(d => d.UsagePage == 0x0001 && (d.Usage == 0x0005 || d.Usage == 0x0004)) ?? g.FirstOrDefault(d => d.InLen > 0);
        }

        // ---------------------------------------------------------------- PlayStation (Sony 054C)
        // Listen only. Over USB the controller streams input report 0x01 with the battery inside.
        // Over Bluetooth the battery is only in the full report (0x11 DualShock 4, 0x31 DualSense); this
        // app never switches a controller to it (that breaks DirectInput games until the controller is
        // turned off), so the level is there only while Steam or a game has switched it on.
        // DualShock 4 byte: low nibble 0..10 (11 = full on the cable), bit 4 = cable.
        // DualSense byte: low nibble 0..10, high nibble 1 = charging, 2 = full on the cable.
        // 0..10 are 10 % steps: shown as the middle of the step, like hid-playstation, marked approximate.
        static readonly Dictionary<int, string> SonyPads = new Dictionary<int, string> {
            { 0x05C4, "Sony DualShock 4" }, { 0x09CC, "Sony DualShock 4" }, { 0x05C5, "Sony DualShock 4" },
            { 0x0BA0, "Sony DualShock 4" }, { 0x0CE6, "Sony DualSense" }, { 0x0DF2, "Sony DualSense Edge" } };

        static void ReadPlayStation(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.Where(d => SonyPads.ContainsKey(d.Pid)).GroupBy(d => d.Pid + "|" + d.Instance))
            {
                var c = PadCollection(g); if (c == null) continue;
                int pid = c.Pid; bool ds = pid == 0x0CE6 || pid == 0x0DF2, bt = IsBluetoothPath(c.Path);
                byte rid = (byte)(bt ? (ds ? 0x31 : 0x11) : 0x01);
                int off = ds ? (bt ? 54 : 53) : (bt ? 32 : 30);
                var r = new Reading { Id = "ps-" + pid.ToString("X4") + "-" + c.Instance, Name = SonyPads[pid], Kind = "gamepad", Source = "playstation", Approx = true };
                if (c.InLen <= off) { Trace.Add("ps: report too short for the battery byte (" + c.InLen + ")"); outp.Add(r); continue; }
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) { Trace.Add("ps open failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + " (another app may hold the controller)"); outp.Add(r); continue; }
                    var end = DateTime.UtcNow.AddMilliseconds(600);
                    while (DateTime.UtcNow < end)
                    {
                        var x = Read(h, c.InLen, 100);
                        if (x == null) continue;
                        if (x[0] != rid)
                        {
                            if (bt && x[0] == 0x01) { Trace.Add("ps: Bluetooth basic report, no battery in it (the level shows while Steam or a game uses the controller)"); break; }
                            continue;
                        }
                        // the USB wireless adapter streams reports with no controller paired: bit 2 of byte 31 (hid-playstation)
                        if (pid == 0x0BA0 && (x[31] & 0x04) != 0) { Trace.Add("ps: adapter without a controller"); break; }
                        int b = x[off], raw = b & 0x0F;
                        bool cable = ds ? ((b >> 4) == 1 || (b >> 4) == 2) : (b & 0x10) != 0;
                        if (!ds && raw == 11) r.Level = 100;
                        else if (raw <= 10) r.Level = Math.Min(100, raw * 10 + 5);
                        else { Trace.Add("ps battery byte " + b.ToString("X2") + ": not a level"); break; }
                        r.Charging = cable;
                        break;
                    }
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- Nintendo Switch (057E), Bluetooth
        // Reports 0x21 / 0x30 / 0x31: byte 2 bits 7..5 = level 0..8 in steps of 2, bit 4 = charging.
        // First it only listens (Steam may have switched the controller to its full report). Otherwise it
        // sends one read-only subcommand, 0x02 "request device info", in output report 0x01 with the
        // neutral rumble data SDL uses (00 01 40 40), so the controller does not vibrate. Not on USB: the
        // USB protocol needs a handshake that changes the controller's state (and it charges there).
        static readonly Dictionary<int, string> SwitchPads = new Dictionary<int, string> {
            { 0x2006, "Joy-Con (L)" }, { 0x2007, "Joy-Con (R)" }, { 0x2009, "Switch Pro Controller" } };
        static int switchCounter;

        static void ReadNintendo(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.Where(d => SwitchPads.ContainsKey(d.Pid) && IsBluetoothPath(d.Path)).GroupBy(d => d.Pid + "|" + d.Instance))
            {
                var c = PadCollection(g); if (c == null || c.OutLen < 11) continue;
                var r = new Reading { Id = "switch-" + c.Pid.ToString("X4") + "-" + c.Instance, Name = SwitchPads[c.Pid], Kind = "gamepad", Source = "nintendo", Approx = true };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) { Trace.Add("switch open failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); outp.Add(r); continue; }
                    Func<byte[], bool> hasBattery = q => q.Length > 2 && (q[0] == 0x21 || q[0] == 0x30 || q[0] == 0x31);
                    byte[] x = null;
                    var listen = DateTime.UtcNow.AddMilliseconds(120);
                    while (x == null && DateTime.UtcNow < listen) { var q = Read(h, c.InLen, 30); if (q != null && hasBattery(q)) x = q; }
                    if (x == null)
                    {
                        var f = new byte[] { 0x01, (byte)(switchCounter++ & 0x0F), 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x02 };
                        x = Ask(h, f, c.OutLen, c.InLen, 600, hasBattery);
                    }
                    if (x == null) Trace.Add("switch: no report with the battery");
                    else
                    {
                        int level = (x[2] & 0xE0) >> 4;
                        if (level <= 8) { r.Level = level * 100 / 8; r.Charging = (x[2] & 0x10) != 0; }
                        else Trace.Add("switch battery byte " + x[2].ToString("X2") + ": not a level");
                    }
                }
                outp.Add(r);
            }
        }

        // ---------------------------------------------------------------- 8BitDo in D-input mode (2DC8)
        // Listen only: the level is in the "enhanced" report (0x01 over Bluetooth, 0x04 over USB) that
        // Steam / SDL switch on; byte 14 = level % (bits 0-6) and charging (bit 7). Switching it on
        // ourselves breaks DirectInput games, so without Steam the controller shows no level. In XInput
        // mode these controllers are read as Xbox pads.
        static readonly Dictionary<int, string> EightBitDoPads = new Dictionary<int, string> {
            { 0x6000, "8BitDo SF30 Pro" }, { 0x6100, "8BitDo SF30 Pro" }, { 0x6001, "8BitDo SN30 Pro" }, { 0x6101, "8BitDo SN30 Pro" },
            { 0x6003, "8BitDo Pro 2" }, { 0x6006, "8BitDo Pro 2" }, { 0x6009, "8BitDo Pro 3" } };

        static void ReadEightBitDo(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.Where(d => EightBitDoPads.ContainsKey(d.Pid)).GroupBy(d => d.Pid + "|" + d.Instance))
            {
                var c = PadCollection(g); if (c == null || c.InLen <= 14) continue;
                bool bt = IsBluetoothPath(c.Path); byte rid = (byte)(bt ? 0x01 : 0x04);
                var r = new Reading { Id = "8bitdo-" + c.Pid.ToString("X4") + "-" + c.Instance, Name = EightBitDoPads[c.Pid], Kind = "gamepad", Source = "8bitdo" };
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) { outp.Add(r); continue; }
                    var end = DateTime.UtcNow.AddMilliseconds(400);
                    for (int n = 0; n < 16 && DateTime.UtcNow < end; )
                    {
                        var x = Read(h, c.InLen, 50); if (x == null) continue; n++;
                        // a zero is the padding of the ordinary report, not an empty battery
                        int lvl = x[14] & 0x7F;
                        if (x[0] == rid && lvl >= 1 && lvl <= 100) { r.Level = lvl; r.Charging = (x[14] & 0x80) != 0; break; }
                    }
                    if (r.Level < 0) Trace.Add("8bitdo: no enhanced report (the level shows while Steam or a game uses the controller)");
                }
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

        // ---------------------------------------------------------------- Logitech Centurion (G PRO X 2 LIGHTSPEED, 046D:0AF7)
        // HaloBattery providers/logitech_centurion.py (confirmed there on a real headset), from Solaar and HeadsetControl.
        // Collection FFA0:0001, 64-byte reports with id 51: 51 <len> <flags> <payload>, len = payload length + 1.
        // A request to the receiver: <feature index> <function | sw id 1> <params>; the reply echoes the first two
        // bytes, an error is ff <index> <function>. The headset is reached through the receiver's CenturionBridge
        // (feature 0003): <bridge> 11 <size hi> <size lo> 00 <index> <function | 1> <params>; the receiver acknowledges
        // (<bridge> 11), the headset answers (<bridge> 10 <size> <size> 00 <index> <function> <data>). Battery: the
        // headset's feature 0104, function 0: [0] percent, [2] 1/2/3 = on the cable. Discovery and battery only read.
        public static byte[] CentFrame(byte[] payload)
        {
            var f = new byte[64]; f[0] = 0x51; f[1] = (byte)(payload.Length + 1);
            Array.Copy(payload, 0, f, 3, Math.Min(payload.Length, 61));
            return f;
        }

        public static byte[] CentPayload(byte[] r)
        {
            if (r == null || r.Length < 4 || r[0] != 0x51) return null;
            int n = r[1]; if (n <= 1 || n + 2 > r.Length) return null;
            var p = new byte[n - 1]; Array.Copy(r, 3, p, 0, n - 1); return p;
        }

        static byte[] Tail(byte[] p, int from) { var o = new byte[Math.Max(0, p.Length - from)]; Array.Copy(p, from, o, 0, o.Length); return o; }

        // a request to the receiver (bridge < 0) or, through the bridge, to the headset; -> the data, or null.
        // offline = the receiver took a bridge request and the headset did not answer
        static byte[] CentAsk(SafeFileHandle h, HidInfo c, int bridge, byte index, byte function, byte[] prm, out bool offline)
        {
            offline = false;
            byte fs = (byte)((function & 0xF0) | 0x01);
            byte[] pl;
            if (bridge < 0) { pl = new byte[2 + prm.Length]; pl[0] = index; pl[1] = fs; Array.Copy(prm, 0, pl, 2, prm.Length); }
            else
            {
                int sub = 3 + prm.Length;
                pl = new byte[4 + sub]; pl[0] = (byte)bridge; pl[1] = 0x11; pl[2] = (byte)((sub >> 8) & 0x0F); pl[3] = (byte)(sub & 0xFF);
                pl[4] = 0x00; pl[5] = index; pl[6] = fs; Array.Copy(prm, 0, pl, 7, prm.Length);
            }
            var f = CentFrame(pl);
            var w = new byte[Math.Max(64, c.OutLen)]; Array.Copy(f, w, 64);
            Drain(h, c.InLen);
            if (!Write(h, w)) { Trace.Add("centurion: write failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return null; }
            bool acked = false;
            var end = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < end)
            {
                var p = CentPayload(Read(h, c.InLen, 100));
                if (p == null || p.Length < 2) continue;
                if (bridge < 0)
                {
                    if (p[0] == 0xFF && p.Length >= 3 && p[1] == index && p[2] == fs) { Trace.Add("centurion: receiver feature " + index + " error"); return null; }
                    if (p[0] == index && p[1] == fs) return Tail(p, 2);
                    continue;
                }
                if (p[0] != bridge || (p[1] >> 4) != 0x1) continue;
                if ((p[1] & 0x0F) == 0x01) { acked = true; continue; }
                if ((p[1] & 0x0F) != 0 || p.Length < 7 || p[4] != 0x00) continue;
                if (p[5] == 0xFF && p[6] == index) { Trace.Add("centurion: headset rejected feature " + index); return null; }
                if (p[5] == index && p[6] == fs) return Tail(p, 7);
            }
            offline = acked;
            return null;
        }

        static readonly Dictionary<string, int[]> CentFeatures = new Dictionary<string, int[]>();   // path -> { bridge, battery index }

        static Reading ReadCenturion(IEnumerable<HidInfo> g)
        {
            var c = Pick(g, 0xFFA0, 0x0001);
            if (c == null) return null;
            var r = new Reading { Id = "logi-centurion-0af7", Name = "Logitech G PRO X 2 LIGHTSPEED", Kind = "headphones", Source = "logitech", Receiver = true };
            using (var h = Open(c.Path))
            {
                if (h.IsInvalid) return r;
                bool off; var none = new byte[0];
                int[] known;
                if (!CentFeatures.TryGetValue(c.Path, out known))
                {
                    // the receiver's FeatureSet, its CenturionBridge, then the headset's FeatureSet and battery feature
                    var fs = CentAsk(h, c, -1, 0x00, 0x00, new byte[] { 0x00, 0x01 }, out off);
                    if (fs == null || fs.Length < 1 || fs[0] == 0) return r;
                    var cnt = CentAsk(h, c, -1, fs[0], 0x00, none, out off);
                    if (cnt == null || cnt.Length < 1) return r;
                    int bridge = -1;
                    for (int n = 0; n < cnt[0] && bridge < 0; n++)
                    {
                        var d = CentAsk(h, c, -1, fs[0], 0x10, new[] { (byte)n }, out off);
                        if (d != null && d.Length >= 3 && ((d[1] << 8) | d[2]) == 0x0003) bridge = n;
                    }
                    if (bridge < 0) { Trace.Add("centurion: no bridge on the receiver"); return r; }
                    var sfs = CentAsk(h, c, bridge, 0x00, 0x00, new byte[] { 0x00, 0x01 }, out off);
                    if (sfs == null || sfs.Length < 1 || sfs[0] == 0) { if (off) Trace.Add("centurion: headset off"); return r; }
                    var scnt = CentAsk(h, c, bridge, sfs[0], 0x00, none, out off);
                    if (scnt == null || scnt.Length < 1) return r;
                    int battery = -1;
                    for (int n = 0; n < scnt[0] && battery < 0; n++)
                    {
                        var d = CentAsk(h, c, bridge, sfs[0], 0x10, new[] { (byte)n }, out off);
                        if (d == null) return r;
                        if (d.Length >= 3 && ((d[1] << 8) | d[2]) == 0x0104) battery = n;
                    }
                    known = new[] { bridge, battery };
                    CentFeatures[c.Path] = known;
                }
                if (known[1] >= 0)
                {
                    var data = CentAsk(h, c, known[0], (byte)known[1], 0x00, none, out off);
                    if (data == null) { if (off) Trace.Add("centurion: headset off"); else CentFeatures.Remove(c.Path); return r; }
                    var v = CentBattery(data);
                    if (v != null) { r.Level = v[0]; r.Charging = v[1] != 0; }
                    return r;
                }
                // firmware without 0104: HeadsetControl's fixed request
                var legacy = new byte[Math.Max(64, c.OutLen)];
                Array.Copy(new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }, legacy, 10);
                Drain(h, c.InLen);
                if (!Write(h, legacy)) return r;
                for (int i = 0; i < 4; i++)
                {
                    var x = Read(h, c.InLen, 1500);
                    if (x == null) break;
                    if (x.Length >= 7 && x[0] == 0x51 && x[1] == 0x05 && x[6] == 0x00) { Trace.Add("centurion: headset off"); break; }
                    var v = CentLegacy(x);
                    if (v != null) { r.Level = v[0]; r.Charging = v[1] != 0; break; }
                }
            }
            return r;
        }

        // feature 0104 data -> { percent, on the cable 0/1 } or null
        public static int[] CentBattery(byte[] d)
        {
            if (d == null || d.Length < 1 || d[0] > 100) return null;
            return new[] { (int)d[0], d.Length >= 3 && d[2] >= 1 && d[2] <= 3 ? 1 : 0 };
        }

        // the fixed request's battery reply 51 0b .. with [8] = 04: [10] percent, [12] = 02 charging
        public static int[] CentLegacy(byte[] x)
        {
            if (x == null || x.Length < 13 || x[0] != 0x51 || x[1] != 0x0B || x[8] != 0x04 || x[10] > 100) return null;
            return new[] { (int)x[10], x[12] == 0x02 ? 1 : 0 };
        }

        static void ReadLogitech(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid.ToString("X4") + "|" + d.Instance))
            {
                var first = g.First();
                int pid = first.Pid;
                // the Astro A50 Gen 5 base station speaks its own protocol, not HID++
                if (pid == 0x0B1C) { var astro = ReadAstro(g); if (astro != null) outp.Add(astro); continue; }
                // the G PRO X 2 LIGHTSPEED receiver speaks Centurion, not HID++
                if (pid == 0x0AF7) { var cent = ReadCenturion(g); if (cent != null) outp.Add(cent); continue; }
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

        // Arctis Nova Elite: st = { level, charging 0/1, power state }, filled from one report; true when it was one of
        // the station's. The direct reply 01 b0: level [6], power [14], charging [15] (02); 07 b7: level [2], charging
        // [4] (02); 07 b5: power [4] (01 headset off, 02 charging on the cable, 04 standby, 08 on).
        public static bool EliteParse(byte[] r, int[] st)
        {
            if (r == null || r.Length < 5) return false;
            if (r[0] == 0x01 && r[1] == 0xB0 && r.Length > 15) { if (r[6] <= 100) st[0] = r[6]; st[1] = r[15] == 0x02 ? 1 : 0; st[2] = r[14]; return true; }
            if (r[0] == 0x07 && r[1] == 0xB7) { if (r[2] <= 100) { st[0] = r[2]; st[1] = r[4] == 0x02 ? 1 : 0; } return true; }
            if (r[0] == 0x07 && r[1] == 0xB5) { st[2] = r[4]; return true; }
            return false;
        }

        // the level to show, or -1: an offline headset gives none, and the station reports a switched-off headset as
        // 0 % (SteelSeries GG shows that 0), which is not a battery level
        public static int EliteLevel(int[] st)
        {
            if (st[0] < 0 || st[2] == 0x01) return -1;
            if (st[0] == 0 && st[1] == 0 && st[2] != 0x02) return -1;
            return st[0];
        }

        static void ReadSteelSeries(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                int pid = g.Key;
                var classic = ReadArctisClassic(pid, g);
                if (classic != null) { outp.Add(classic); continue; }
                var r = new Reading { Id = "ss-" + pid.ToString("X4"), Source = "steelseries", Receiver = true };
                if (pid == 0x2244)
                {
                    // Arctis Nova Elite base station (HaloBattery providers/steelseries_elite.py, verified there on a real
                    // station): 64-byte output report 01 b0 to interface 3; the direct reply 01 b0 or 07 b7 / 07 b5 frames
                    r.Name = "Arctis Nova Elite"; r.Kind = "headphones";
                    var st = new[] { -1, 0, -1 };   // level, charging, power state
                    AskAll("ss-elite", g.Where(d => d.Interface == 3), new byte[] { 0x01, 0xB0 }, 1000,
                        q => EliteParse(q, st) && (st[2] == 0x01 || (st[0] >= 0 && st[2] >= 0)));
                    int lvl = EliteLevel(st);
                    if (lvl >= 0) { r.Level = lvl; r.Charging = st[1] != 0 || st[2] == 0x02; }
                    outp.Add(r); continue;
                }
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
        // Cloud III S Wireless: request 0c 02 03 01 00 <cmd> (01 = read a value); reply 0c .. .. .. .. <cmd> <value>
        // (ff = no value); pushed notification 0d .. .. .. <1 battery | 10 charging> <value>.
        // -> { 0x06 battery | 0x48 charging, value } or null
        public static byte[] Cloud3SRequest(byte cmd) { return new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, cmd }; }

        public static int[] Cloud3SParse(byte[] r)
        {
            if (r == null || r.Length < 7) return null;
            if (r[0] == 0x0C && r[5] == 0x06) return r[6] <= 100 ? new[] { 0x06, (int)r[6] } : null;
            if (r[0] == 0x0C && r[5] == 0x48) return r[6] <= 2 ? new[] { 0x48, (int)r[6] } : null;
            if (r[0] == 0x0D && r[4] == 1) return r[5] <= 100 ? new[] { 0x06, (int)r[5] } : null;
            if (r[0] == 0x0D && r[4] == 10) return r[5] <= 2 ? new[] { 0x48, (int)r[5] } : null;
            return null;
        }

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
                else if (pid == 0x02CC || pid == 0x06BE)
                {
                    // Cloud III S Wireless (HaloBattery providers/hyperx_cloud3s.py, verified there on both ids; from
                    // HyperHeadset and NGENUITY captures): output report 0c 02 03 01 00 <cmd>, 06 battery, 48 charging
                    var r = new Reading { Id = "hyperx-cloud3s", Name = "HyperX Cloud III S Wireless", Kind = "headphones", Source = "hyperx", Receiver = true };
                    int[] v = null;
                    AskAll("hyperx-cloud3s", g, Cloud3SRequest(0x06), 1000, q => { var x = Cloud3SParse(q); if (x != null && x[0] == 0x06) v = x; return v != null; });
                    if (v != null)
                    {
                        r.Level = v[1];
                        int[] c = null;
                        AskAll("hyperx-cloud3s", g, Cloud3SRequest(0x48), 1000, q => { var x = Cloud3SParse(q); if (x != null && x[0] == 0x48) c = x; return c != null; });
                        r.Charging = c != null && (c[1] == 1 || c[1] == 2);   // 1 charging, 2 full (on the cable)
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
                        var x = AskOrFeature(h, new byte[] { 0x66, 0x89 }, c, 1000, q => q.Length > 4 && q[0] == 0x66 && (q[1] == 0x89 || q[1] == 0x0D));
                        if (x != null && (x[2] | x[3]) != 0 && x[4] <= 100)
                        {
                            r.Level = x[4];
                            var y = AskOrFeature(h, new byte[] { 0x66, 0x8A }, c, 1000, q => q.Length > 2 && q[0] == 0x66 && (q[1] == 0x8A || q[1] == 0x0C));
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

        // Corsair Dark Core RGB Pro SE on its dongle (1B7F): ckb-next's "nxp" protocol, via HaloBattery providers/corsair.py.
        // Output report 0, 64 bytes 0e 50 (CMD_GET, FIELD_BATTERY); the reply's byte 4 is an index into 0/15/30/50/100 %,
        // so the level is coarse. The dongle's vendor collections are on usage page FF42.
        static readonly int[] NxpLevels = { 0, 15, 30, 50, 100 };

        static Reading ReadCorsairNxp(IEnumerable<HidInfo> g)
        {
            var r = new Reading { Id = "corsair-1B7F", Name = "Corsair Dark Core RGB Pro SE", Kind = "mouse", Source = "corsair", Receiver = true, Approx = true };
            var cands = g.Where(d => d.UsagePage == 0xFF42).OrderBy(d => d.Usage == 0x0001 ? 0 : 1).ThenBy(d => d.Interface).ToList();
            foreach (var c in cands)
            {
                using (var h = Open(c.Path))
                {
                    if (h.IsInvalid) continue;
                    var x = Ask(h, new byte[] { 0x00, 0x0E, 0x50 }, Math.Max(65, c.OutLen), c.InLen, 500, q => true);   // the first reply, as ckb-next reads it
                    int lvl = CorsairNxpParse(x);
                    if (lvl >= 0) { r.Level = lvl; break; }
                }
            }
            return cands.Count > 0 ? r : null;
        }

        // the reply (with or without the report id byte in front) -> level %, or -1
        public static int CorsairNxpParse(byte[] x)
        {
            if (x == null) return -1;
            int off = x.Length >= 65 ? 1 : 0;
            if (x.Length < off + 6) return -1;
            int idx = x[off + 4];
            return idx < NxpLevels.Length ? NxpLevels[idx] : -1;
        }

        static void ReadCorsair(List<HidInfo> devs, List<Reading> outp)
        {
            foreach (var g in devs.GroupBy(d => d.Pid))
            {
                if (g.Key == 0x1B7F) { var nxp = ReadCorsairNxp(g); if (nxp != null) outp.Add(nxp); continue; }
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
