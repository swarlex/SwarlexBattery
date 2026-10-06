// SPDX-License-Identifier: GPL-3.0-or-later
// Tests that need no device: protocol parsers fed with frames captured on real hardware, what the panel and the
// tray show, and the texts of every language. build.ps1 compiles them with the app's sources and runs them;
// a failing test fails the build (and so a release). They never read or write the user's settings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using SwarlexBattery;

namespace SwarlexBatteryTests
{
    static class Program
    {
        static int passed, failed;

        static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL: " + what); }
        }

        static void Equal<T>(T expected, T actual, string what)
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), what + " (expected " + expected + ", got " + actual + ")");
        }

        static byte[] Hex(string s) { return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(); }

        // private helpers of the app, called as they are
        static object Private(Type t, string name, params object[] args)
        {
            var m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (m == null) throw new Exception("no method " + t.Name + "." + name);
            return m.Invoke(null, args);
        }

        static int Main()
        {
            // the defaults only: the user's own config.json is never read
            Program_.Init();
            foreach (var test in new Action[] { Texts, SetupTexts, Atk, RazerMtk, SteelSeries, NovaElite, CloudIIIS, Centurion, GWolves, Jbl, CorsairNxp, Logitech, Names, Versions, LowBattery, Charged, BluetoothIds, AirPodsAds, NoLevelNote, UserChoices, TrayIcon, Estimate, TimeLeft })
            {
                try { test(); }
                catch (Exception e) { failed++; Console.WriteLine("FAIL: " + test.Method.Name + " threw " + (e.InnerException ?? e).Message); }
            }
            Console.WriteLine("tests: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ texts
        static readonly Regex Placeholder = new Regex(@"\{\d+\}");
        static string Holes(string s) { return string.Join(",", Placeholder.Matches(s).Cast<Match>().Select(m => m.Value).OrderBy(x => x)); }

        static void Texts()
        {
            var js = new JavaScriptSerializer();
            Func<string, Dictionary<string, object>> load = code =>
            {
                var t = Resources.Text("lang." + code + ".json");
                Check(t != null, "lang/" + code + ".json is embedded");
                return t == null ? new Dictionary<string, object>() : js.Deserialize<Dictionary<string, object>>(t);
            };
            var en = load("en");
            Check(en.Count > 40, "en.json has the app's texts");
            foreach (var l in Strings.Languages)
            {
                var code = l[0]; var d = load(code);
                foreach (var k in en.Keys)
                {
                    Check(d.ContainsKey(k), code + ".json has \"" + k + "\"");
                    if (!d.ContainsKey(k)) continue;
                    var s = d[k] as string;
                    Check(!string.IsNullOrWhiteSpace(s), code + ".json \"" + k + "\" is not empty");
                    if (s != null) Equal(Holes((string)en[k]), Holes(s), code + ".json \"" + k + "\" placeholders");
                }
                foreach (var k in d.Keys) Check(en.ContainsKey(k), code + ".json \"" + k + "\" is also in en.json");
            }
            // the menu's language list and the embedded files are the same set
            var files = Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith("lang.")).Select(n => n.Substring(5, n.Length - 10)).OrderBy(x => x);
            Equal(string.Join(",", Strings.Languages.Select(l => l[0]).OrderBy(x => x)), string.Join(",", files), "every lang/*.json is in the language list");
            Check(Strings.Has("tr") && !Strings.Has("xx"), "Strings.Has");
        }

        static void SetupTexts()
        {
            var t = (Dictionary<string, string[]>)typeof(SwarlexBatterySetup.S).GetField("T", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Equal(SwarlexBatterySetup.S.Codes.Length, SwarlexBatterySetup.S.Names.Length, "setup: a name for each language");
            Equal(string.Join(",", Strings.Languages.Select(l => l[0])), string.Join(",", SwarlexBatterySetup.S.Codes), "setup and app have the same languages");
            foreach (var kv in t)
            {
                Equal(SwarlexBatterySetup.S.Codes.Length, kv.Value.Length, "setup text \"" + kv.Key + "\" in every language");
                foreach (var s in kv.Value)
                {
                    Check(!string.IsNullOrWhiteSpace(s), "setup text \"" + kv.Key + "\" is not empty");
                    Equal(Holes(kv.Value[0]), Holes(s ?? ""), "setup text \"" + kv.Key + "\" placeholders");
                }
            }
        }

        // ------------------------------------------------------------ protocols
        static void Atk()
        {
            int[] v; string why;
            // VXE MAD 8K receiver (373B:1040), from a diagnostics report: 95 %, on battery, 4097 / 4092 mV
            Equal(1, Hid.AtkParse(Hex("08 04 00 00 00 02 5F 00 10 01 00 00 00 00 00 00 D7"), out v, out why), "atk: real reply accepted");
            Check(v != null && v[0] == 95 && v[1] == 0 && v[2] == 4097, "atk: 95 %, on battery, 4097 mV");
            Equal(1, Hid.AtkParse(Hex("08 04 00 00 00 02 5F 00 0F FC 00 00 00 00 00 00 DD"), out v, out why), "atk: second real reply");
            Check(v != null && v[0] == 95 && v[2] == 4092, "atk: 4092 mV");
            Equal(0, Hid.AtkParse(Hex("08 04 00 00 00 02 5F 00 10 01 00 00 00 00 00 00 D8"), out v, out why), "atk: damaged frame skipped");
            Equal(0, Hid.AtkParse(Hex("0A 04 00 00 00 02 5F 00 10 01 00 00 00 00 00 00 D5"), out v, out why), "atk: pushed event skipped");
            Equal(0, Hid.AtkParse(Hex("08 04 00 00"), out v, out why), "atk: short frame skipped");
            // the mouse switched off: level 0 and no voltage - nothing is shown, never "0 %"
            var off = Hex("08 04 00 00 00 02 00 00 00 00 00 00 00 00 00 00 00"); off[16] = (byte)((0x55 - off.Take(16).Sum(b => b)) & 0xFF);
            Equal(-1, Hid.AtkParse(off, out v, out why), "atk: mouse off gives no level");
            var odd = Hex("08 04 00 00 00 02 32 00 01 00 00 00 00 00 00 00 00"); odd[16] = (byte)((0x55 - odd.Take(16).Sum(b => b)) & 0xFF);
            Equal(-1, Hid.AtkParse(odd, out v, out why), "atk: implausible voltage gives no level");
            var f = Hid.AtkFrame(17);
            Check(f.Length == 17 && f[0] == 0x08 && f[1] == 0x04 && f[16] == (byte)((0x55 - 0x0C) & 0xFF), "atk: query frame and its checksum");
        }

        static void RazerMtk()
        {
            // BlackShark V2 HyperSpeed reply to 0x21 (battery): value in byte 13, XOR of bytes 1..61 in byte 62
            var r = new byte[63];
            var head = Hex("02 02 6D 00 00 00 05 00 80 80 21 01 01 4E 00 00");
            Array.Copy(head, r, head.Length);
            byte x = 0; for (int i = 1; i <= 61; i++) x ^= r[i]; r[62] = x;
            Equal(0x4E, Hid.MtkParse(r, 0x21), "mtk: battery 78 %");
            Equal(-1, Hid.MtkParse(r, 0x2A), "mtk: reply to another command skipped");
            r[13] = 0x4F;
            Equal(-2, Hid.MtkParse(r, 0x21), "mtk: damaged reply rejected");
            Equal(-1, Hid.MtkParse(new byte[16], 0x21), "mtk: short report skipped");
        }

        static void SteelSeries()
        {
            var o = new Reading();
            Check((bool)Private(typeof(Hid), "SsNova7", Hex("B0 03 64 01"), o) && o.Level == 100 && o.Charging, "Arctis Nova 7: 100 %, charging");
            o = new Reading();
            Check(!(bool)Private(typeof(Hid), "SsNova7", Hex("B0 03 50 00"), o), "Arctis Nova 7: status 0 = headset off, no level");
            o = new Reading();
            Check((bool)Private(typeof(Hid), "Ss7Plus", Hex("B0 02 03 00"), o) && o.Level == 75 && o.Approx && !o.Charging, "Arctis 7+: step 3 = ~75 %");
            o = new Reading();
            Check((bool)Private(typeof(Hid), "SsNova5", Hex("B0 03 00 55 01"), o) && o.Level == 85 && o.Charging, "Arctis Nova 5: 85 %, charging");
        }

        static void NovaElite()
        {
            // the direct reply a real station sent while SteelSeries GG showed 31 % (HaloBattery #138); power 08 = on
            var st = new[] { -1, 0, -1 };
            var reply = new byte[64]; Array.Copy(Hex("01 B0 00 00 01 00 1F 64"), reply, 8); reply[14] = 0x08; reply[15] = 0x08;
            Check(Hid.EliteParse(reply, st), "Nova Elite: direct reply recognised");
            Equal(31, Hid.EliteLevel(st), "Nova Elite: 31 %");
            Equal(0, st[1], "Nova Elite: on battery");
            st = new[] { -1, 0, -1 };
            Hid.EliteParse(Hex("07 B7 50 64 02 00"), st); Hid.EliteParse(Hex("07 B5 00 00 08 00"), st);
            Check(Hid.EliteLevel(st) == 80 && st[1] == 1, "Nova Elite: 07 b7 frame, 80 %, charging");
            st = new[] { -1, 0, -1 };
            Hid.EliteParse(Hex("07 B7 50 64 08 00"), st); Hid.EliteParse(Hex("07 B5 00 00 01 00"), st);
            Equal(-1, Hid.EliteLevel(st), "Nova Elite: headset offline gives no level");
            st = new[] { -1, 0, -1 };
            var zero = (byte[])reply.Clone(); zero[6] = 0;
            Hid.EliteParse(zero, st);
            Equal(-1, Hid.EliteLevel(st), "Nova Elite: a switched-off headset's 0 % is not shown");
            Check(!Hid.EliteParse(Hex("07 C0 00 00 00"), new[] { -1, 0, -1 }), "Nova Elite: settings frames ignored");
        }

        static void CloudIIIS()
        {
            Equal("0C-02-03-01-00-06", BitConverter.ToString(Hid.Cloud3SRequest(0x06)), "Cloud III S: battery request");
            var v = Hid.Cloud3SParse(Hex("0C 02 03 01 00 06 59 00"));
            Check(v != null && v[0] == 0x06 && v[1] == 89, "Cloud III S: 89 % (the level NGENUITY showed)");
            v = Hid.Cloud3SParse(Hex("0C 02 03 01 00 48 01 00"));
            Check(v != null && v[0] == 0x48 && v[1] == 1, "Cloud III S: charging");
            Check(Hid.Cloud3SParse(Hex("0C 02 03 01 00 06 FF 00")) == null, "Cloud III S: ff = no value");
            v = Hid.Cloud3SParse(Hex("0D 00 00 00 01 2A 00"));
            Check(v != null && v[0] == 0x06 && v[1] == 42, "Cloud III S: pushed battery notification");
        }

        static void Centurion()
        {
            var f = Hid.CentFrame(new byte[] { 0x00, 0x01, 0x00, 0x01 });
            Check(f.Length == 64 && f[0] == 0x51 && f[1] == 5 && f[2] == 0 && f[3] == 0x00 && f[4] == 0x01 && f[6] == 0x01, "Centurion: frame 51 <len> <flags> <payload>");
            var p = Hid.CentPayload(f);
            Check(p != null && p.Length == 4 && p[1] == 0x01, "Centurion: payload back from a frame");
            Check(Hid.CentPayload(Hex("50 05 00 01 02")) == null, "Centurion: other report ids ignored");
            var b = Hid.CentBattery(new byte[] { 0x47, 0x00, 0x02 });
            Check(b != null && b[0] == 71 && b[1] == 1, "Centurion: 71 %, charging over USB");
            b = Hid.CentBattery(new byte[] { 0x47, 0x00, 0x00 });
            Check(b != null && b[1] == 0, "Centurion: discharging");
            Check(Hid.CentBattery(new byte[] { 0xC8 }) == null, "Centurion: above 100 refused");
            var legacy = Hex("51 0B 00 00 00 00 00 00 04 00 3C 00 02");
            var l = Hid.CentLegacy(legacy);
            Check(l != null && l[0] == 60 && l[1] == 1, "Centurion legacy reply: 60 %, charging");
        }

        static void CorsairNxp()
        {
            var r = new byte[65]; r[5] = 3;   // report id 0, then the reply: byte 4 = index 3
            Equal(50, Hid.CorsairNxpParse(r), "Dark Core: index 3 = 50 %");
            var s = new byte[64]; s[4] = 4;
            Equal(100, Hid.CorsairNxpParse(s), "Dark Core: without the report id, index 4 = 100 %");
            s[4] = 9;
            Equal(-1, Hid.CorsairNxpParse(s), "Dark Core: an index out of the table is no level");
        }

        static void Jbl()
        {
            Equal(64, Hid.JblParse(Hex("08 40 00")), "JBL Quantum 910: 64 %");
            Equal(-1, Hid.JblParse(Hex("08 C8 00")), "JBL: above 100 refused");
            Equal(-1, Hid.JblParse(Hex("09 40 00")), "JBL: other reports ignored");
            Equal(-1, Hid.JblParse(null), "JBL: nothing read");
        }

        static void GWolves()
        {
            var v = Hid.W83Parse(Hex("00 A1 00 02 02 00 83 01 4B 00"));
            Check(v != null && v[0] == 75 && v[1] == 1, "w83: 75 %, charging");
            Check(Hid.W83Parse(Hex("00 A0 00 02 02 00 83 01 4B 00")) == null, "w83: mouse not active");
            v = Hid.W83OldParse(Hex("00 A1 02 8F 00 00 3C"));
            Check(v != null && v[0] == 60 && v[1] == 0, "w83 old: 60 % with the report id byte");
            v = Hid.W83OldParse(Hex("A1 02 8F 00 01 50"));
            Check(v != null && v[0] == 80 && v[1] == 1, "w83 old: 80 % without it");
            Check(Hid.W83OldParse(Hex("00 A1 02 8F 00 00 C8")) == null, "w83 old: above 100 refused");
            var models = (System.Collections.IDictionary)typeof(Hid).GetField("GWolvesModels", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Equal(48, models.Count, "G-Wolves: every model pid of the web driver's list");
            Func<int, string> desc = pid => { var m = models[pid]; var t = m.GetType(); return t.GetField("Name").GetValue(m) + "|" + ((int)t.GetField("Receiver").GetValue(m)).ToString("X4") + "|" + t.GetField("Old").GetValue(m) + "|" + t.GetField("Wired").GetValue(m); };
            Equal("G-Wolves HSK Pro ACE|5803|True|False", desc(0x5803), "G-Wolves: HSK Pro ACE receiver");
            Equal("G-Wolves HSK Pro ACE|5803|True|True", desc(0x5804), "G-Wolves: HSK Pro ACE cable");
            Equal("G-Wolves HTM Plus|3817|False|True", desc(0x3808), "G-Wolves: HTM Plus cable, new exchange");
            Equal("G-Wolves HSK Pro|5817|True|False", desc(0x5807), "G-Wolves: HSK Pro second receiver");
        }

        static void Logitech()
        {
            int prev = -1;
            for (int mv = 3000; mv <= 4400; mv += 10)
            {
                int p = (int)Private(typeof(Hid), "LogiVoltPct", mv);
                Check(p >= 0 && p <= 100, "Logitech voltage " + mv + " mV -> 0..100");
                Check(p >= prev, "Logitech voltage curve never falls (" + mv + " mV)");
                prev = p;
            }
        }

        static void Names()
        {
            Equal("Razer BlackShark V2 HyperSpeed", (string)Private(typeof(Hid), "Clean", "Razer BlackShark V2 HS 2.4"), "name: HS 2.4");
            Equal("MAD 8K", (string)Private(typeof(Hid), "Clean", "MAD 8K DONGLE"), "name: dongle");
            Equal("G Pro", (string)Private(typeof(Hid), "Clean", "  G Pro   Wireless Receiver "), "name: receiver and spaces");
        }

        // ------------------------------------------------------------ updates
        static void Versions()
        {
            Check(Updater.Norm("v1.10.2") > Updater.Norm("1.9.0"), "1.10.2 is newer than 1.9.0");
            Equal(new Version(1, 2, 0), Updater.Norm("1.2"), "1.2 = 1.2.0");
            Equal(new Version(1, 10, 3), Updater.Norm("V1.10.3.0"), "four parts");
            Check(Updater.Norm("latest") == null && Updater.Norm(null) == null, "not a version");
        }

        // ------------------------------------------------------------ what is shown
        static Gadget G(string id, string kind, int pct, bool charging = false, bool approx = false)
        {
            return new Gadget { Id = id, Name = id, Kind = kind, Pct = pct, Charging = charging, Approx = approx, Online = true };
        }

        static void LowBattery()
        {
            int low = (int)Config.Num("plugins.gadgets.lowThreshold", 15);
            Equal(1, BatteryReader.Build(new List<Gadget> { G("m", "mouse", low) }).Notify.Count, "a notice at the threshold");
            Equal(0, BatteryReader.Build(new List<Gadget> { G("m", "mouse", low + 1) }).Notify.Count, "no notice above it");
            Equal(0, BatteryReader.Build(new List<Gadget> { G("m", "mouse", 5, true) }).Notify.Count, "no notice while charging");
            Equal(0, BatteryReader.Build(new List<Gadget> { new Gadget { Id = "m", Name = "m", Kind = "mouse", Pct = 5, Online = false, Asleep = true } }).Notify.Count, "no notice for a sleeping device");
        }

        static void Charged()
        {
            Func<List<Gadget>, int> full = l => BatteryReader.Build(l).Notify.Count(n => n.Key.StartsWith("full-"));
            Equal(0, full(new List<Gadget> { G("c1", "headphones", 80, true) }), "charging at 80 %: no 'charged' notice yet");
            var notice = BatteryReader.Build(new List<Gadget> { G("c1", "headphones", 100, true) }).Notify.FirstOrDefault(x => x.Key == "full-c1");
            Check(notice != null && notice.Info && notice.Pct < 0, "reaching 100 % while charging: one 'charged' notice, as information, no low battery sound");
            Equal(0, full(new List<Gadget> { G("c1", "headphones", 100, true) }), "still full on the cable: not again");
            Equal(0, full(new List<Gadget> { G("c2", "mouse", 100, true) }), "plugged in already full: no notice");
            Equal(0, full(new List<Gadget> { G("c3", "gamepad", 75, true, true) }), "coarse levels: not tracked");
            Equal(0, full(new List<Gadget> { G("c3", "gamepad", 100, true, true) }), "coarse levels: no 'charged' notice");
            Equal(0, full(new List<Gadget> { G("c4", "mouse", 60, true) }) + full(new List<Gadget> { G("c4", "mouse", 61) }) + full(new List<Gadget> { G("c4", "mouse", 100) }),
                  "unplugged before it was full: no notice later");

            // "Remind at 80 %": off by default, once per charge when it is on
            Func<List<Gadget>, int> limit = l => BatteryReader.Build(l).Notify.Count(n => n.Key.StartsWith("limit-"));
            Equal(0, limit(new List<Gadget> { G("r1", "mouse", 50, true) }) + limit(new List<Gadget> { G("r1", "mouse", 85, true) }), "80 % reminder: off by default");
            object keep;
            bool had = Config.Data.TryGetValue("limitNotify", out keep);
            try
            {
                Config.Data["limitNotify"] = true;
                Equal(0, limit(new List<Gadget> { G("r2", "mouse", 79, true) }), "80 % reminder: not below 80 %");
                var r = BatteryReader.Build(new List<Gadget> { G("r2", "mouse", 81, true) }).Notify.FirstOrDefault(x => x.Key == "limit-r2");
                Check(r != null && r.Info && r.Pct < 0 && r.Title.Contains("81"), "80 % reminder: once at 80 %, as information, with the level");
                Equal(0, limit(new List<Gadget> { G("r2", "mouse", 90, true) }), "80 % reminder: not again in the same charge");
                Equal(0, limit(new List<Gadget> { G("r3", "mouse", 90, true) }), "80 % reminder: plugged in above 80 %: no notice");
                Equal(0, limit(new List<Gadget> { G("r4", "gamepad", 50, true, true) }) + limit(new List<Gadget> { G("r4", "gamepad", 100, true, true) }), "80 % reminder: coarse levels: no notice");
                Equal(0, limit(new List<Gadget> { G("r5", "mouse", 70, true) }) + limit(new List<Gadget> { G("r5", "mouse", 70) }) + limit(new List<Gadget> { G("r5", "mouse", 85, true) }),
                      "80 % reminder: unplugged and plugged in again above 80 %: no notice");
            }
            finally { if (had) Config.Data["limitNotify"] = keep; else Config.Data.Remove("limitNotify"); }
        }

        static void BluetoothIds()
        {
            // instance ids captured from a Logitech G435 on Bluetooth: the level sits on the Hands-Free AG node
            Equal("405899571DB9", BluetoothBattery.MacOf(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&000107E3_PID&2002\7&1B605E42&0&405899571DB9_C00000000"), "service node: the address, not the GUID's end");
            Equal("405899571DB9", BluetoothBattery.MacOf(@"BTHENUM\DEV_405899571DB9\7&1B605E42&0&BLUETOOTHDEVICE_405899571DB9"), "device node: the same address");
            Equal("D32FC1E2B5C6", BluetoothBattery.MacOf(@"BTHLEDEVICE\{00001800-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023_REV&0011_d32fc1e2b5c6\8&2A1B3C4D&0&0010"), "BLE service node: the address");
            Equal("D32FC1E2B5C6", BluetoothBattery.MacOf(@"BTHLE\DEV_d32fc1e2b5c6\8&1A2B3C4D&0&D32FC1E2B5C6"), "BLE device node: the address");
            Check(BluetoothBattery.MacOf(@"BTHHFENUM\BTHHFPAUDIO\8&F03E73A&0&97") == null, "no address: null");
        }

        static void AirPodsAds()
        {
            // captured from AirPods (2nd generation, 0x200F) in the ears, the case closed: left 90 %, right 100 %
            var mine = AirPods.Parse(Hex("07 19 01 0F 20 01 A9 8F 03 00 04 9E 6A 56 B5 7A 1E 32 76 42 1F D3 C9 B1 59 07 1F"));
            Check(mine != null && mine.Model == 0x200F, "AirPods: the model from the advertisement");
            Equal(90, mine.Left, "AirPods: left earbud (status 0x01: the low nibble)");
            Equal(100, mine.Right, "AirPods: right earbud");
            Equal(-1, mine.Case, "AirPods: the closed case reports no level");
            Check(!mine.ChargingLeft && !mine.ChargingRight && !mine.ChargingCase, "AirPods: nothing charging");
            // someone else's AirPods heard at the same time, far away: only the right earbud out, 80 %
            var other = AirPods.Parse(Hex("07 19 01 0F 20 22 F8 8F 01 00 06 2B 62 79 C5 B2 1B AE 95 34 12 09 EF F3 37 8C 10"));
            Check(other != null && other.Left == -1 && other.Right == 80, "AirPods: status 0x22: the high nibble is the left earbud");
            Check(AirPods.Pick(new[] { Tuple.Create(other, -90.0), Tuple.Create(mine, -60.0) }, 0x200F) == mine, "AirPods: the strongest signal wins");
            Check(AirPods.Pick(new[] { Tuple.Create(other, -90.0) }, 0x200F) == null, "AirPods: a far-away pair is not taken");
            Check(AirPods.Pick(new[] { Tuple.Create(mine, -60.0) }, 0x2014) == null, "AirPods: another model is not taken");
            var charging = AirPods.Parse(Hex("07 19 01 0F 20 01 A9 66 03 00"));
            Check(charging.ChargingLeft && !charging.ChargingRight && charging.ChargingCase && charging.Case == 60, "AirPods: charging flags and the case level");
            Check(AirPods.Parse(Hex("10 05 01 18 2C")) == null && AirPods.Parse(Hex("07 19 01 0F 20 01 FF FF")) == null, "AirPods: other Apple messages and empty levels are ignored");

            var dev = new BluetoothBattery.Device { Mac = "AABBCCDDEEFF", Name = "My AirPods", Pid = 0x200F };
            var g = BatteryReader.AirPodsGadget(dev, mine);
            Check(g.Pct == 90 && g.Approx && g.Kind == "earbuds" && g.Id == "bt-AABBCCDDEEFF", "AirPods: the panel shows the lower earbud, approximate");
            Check(g.Detail.Contains("90") && g.Detail.Contains("100"), "AirPods: each earbud under it");
            Check(BatteryReader.AirPodsGadget(dev, AirPods.Parse(Hex("07 19 01 0F 20 01 FF 85"))) == null, "AirPods: both in the case: not shown");
        }

        static void NoLevelNote()
        {
            var note = new Gadget { Id = "nolevel-046D0ACB", Name = "Logitech G435", Kind = "headphones", Pct = -1, Hint = "no level" };
            var s = BatteryReader.Build(new List<Gadget> { G("m", "mouse", 80), note });
            var item = s.Items.FirstOrDefault(i => i.Label == "Logitech G435");
            Check(item != null && item.Value == "" && item.Pct < 0 && item.Sub == "no level", "a device without a level shows the reason, no number");
            Check(s.Notify.Count == 0, "a device without a level never notifies");
            Check(s.Icons.All(i => !(i.Tooltip ?? "").Contains("G435") && (i.Rings ?? new double[0]).All(r => r >= 0)), "a device without a level stays out of the tray icon");
            var only = BatteryReader.Build(new List<Gadget> { note });
            Check(only.Icons.Count == 1 && only.Icons[0].Dim, "only a note: the dim placeholder icon keeps the menu reachable");
        }

        static void UserChoices()
        {
            var gadgets = (Dictionary<string, object>)((Dictionary<string, object>)Config.Data["plugins"])["gadgets"];
            var keep = new Dictionary<string, object>(gadgets);
            try
            {
                gadgets["hidden"] = new Dictionary<string, object> { { "h", "Hidden headset" } };
                gadgets["names"] = new Dictionary<string, object> { { "m", "Game mouse" }, { "Old Keyboard", "Desk keyboard" } };
                gadgets["icons"] = new Dictionary<string, object> { { "m", "gamepad" }, { "k", "not-a-kind" } };
                var list = new List<Gadget> { G("m", "mouse", 60), G("h", "headphones", 10), G("k", "keyboard", 50) };
                list[2].Name = "Old Keyboard";
                var s = BatteryReader.Build(list);
                Check(s.Items.All(i => i.Id != "h") && s.Notify.Count == 0, "a hidden device: not in the panel, no low battery notice");
                Check(s.Icons.All(i => !(i.Tooltip ?? "").Contains("10%")), "a hidden device stays out of the tray");
                var m = s.Items.First(i => i.Id == "m");
                Check(m.Label == "Game mouse" && m.OwnName == "m" && m.IconChoice == "gamepad", "renamed, own name kept, icon chosen");
                Check(m.Icon != BatteryReader.IconFor("mouse"), "the chosen icon replaces the mouse pictogram");
                var k = s.Items.First(i => i.Id == "k");
                Check(k.Label == "Desk keyboard", "names set by hand in config.json by the device's own name still work");
                Check(k.Icon == BatteryReader.IconFor("keyboard"), "an unknown icon choice is ignored");
                // a device's own low battery level: the mouse at 60 % is low with its own 70 %, the keyboard at 50 % not
                gadgets["lowLevels"] = new Dictionary<string, object> { { "m", "70" } };
                var s2 = BatteryReader.Build(new List<Gadget> { G("m", "mouse", 60), G("k", "keyboard", 50) });
                Check(s2.Notify.Count == 1 && s2.Notify[0].Key == "low-m", "a device's own low battery level");
                Check(s2.Items.First(i => i.Id == "m").State == "error" && s2.Items.First(i => i.Id == "k").State == "", "the panel colours by each device's own level");
                Equal("m", list[0].Name, "the reading itself keeps its own name (Reset name goes back to it)");
            }
            finally { gadgets.Clear(); foreach (var kv in keep) gadgets[kv.Key] = kv.Value; }
        }

        static void TrayIcon()
        {
            var s = BatteryReader.Build(new List<Gadget> { G("m", "mouse", 60), G("h", "headphones", 30) });
            Equal(1, s.Icons.Count, "one combined icon");
            Equal(30, s.Icons[0].Percent, "the icon's number is the lower level");
            Check(s.Icons[0].Rings.Length == 2 && Math.Abs(s.Icons[0].Rings[0] - 0.6) < 1e-9, "mouse on the left half");
            var a = BatteryReader.Build(new List<Gadget> { G("m", "mouse", 60), G("x", "gamepad", 25, false, true) });
            Equal(60, a.Icons[0].Percent, "a coarse level is not shown as a number");
            Check(BatteryReader.Build(new List<Gadget> { G("k", "keyboard", 50), G("m", "mouse", 90) }).Icons[0].Tooltip.StartsWith(Strings.T("kindMouse")), "the mouse comes first");
        }

        static List<double[]> Pts(string s)
        {
            return s.Split(' ').Select(x => x.Split(',')).Select(a => new[] { double.Parse(a[0], CultureInfo.InvariantCulture), double.Parse(a[1], CultureInfo.InvariantCulture) }).ToList();
        }

        // histories recorded by the app on real devices (seconds of use, level)
        const string MadHistory = "0,100 330,100 655,100 898,95 1204,95 1529,95 1858,95 2178,95 2649,95 2969,95 3277,95 3592,95 3912,95 4234,95 4538,95 4954,95 5262,95 5566,95 5884,95 6177,90 6482,90 6790,90 7002,85 7379,85 7704,85 8008,85 8318,85 8618,85 8649,80 8971,80 9429,80 9554,75 9872,75 10184,75 10670,75 10857,70 11359,70 11662,70 11897,65 12300,65 12630,65 12940,60 13241,60 13551,60 14016,60 14357,60 14667,60 14878,55 15200,55 15510,55 15820,55 16130,55 16441,55 16744,55 17066,55 17375,55";
        const string HeadsetHistory = "0,89 74,88 106,87 137,85 168,82 199,79 504,79 703,78 1004,78 1316,78 1620,78 1837,77 2168,77 2469,77 3041,77 3325,76 3805,76 4291,76 4571,75 4980,75 5283,75 5611,75 5921,75 6251,75 6561,75 6862,75 7172,75 7482,75 7792,75 7885,74 8195,74 8499,74 8801,74 9122,74 9432,74 9742,74 10052,74 10363,74 10673,74 10982,73 11285,73 11607,73 11705,72";

        static void Estimate()
        {
            // BlackShark V2 HyperSpeed: about 2 % an hour after the first minutes -> some 30-40 hours, not the 25 the
            // old fit gave (it counted the 89 -> 79 % settling of the first 3 minutes)
            double h = BatteryReader.HoursLeft(Pts(HeadsetHistory), 11705, 72);
            Console.WriteLine("  estimate: headset 72 % -> " + h.ToString("0.0", CultureInfo.InvariantCulture) + " h");
            Check(h > 28 && h < 45, "headset estimate follows its steady drain");
            // VXE MAD 8K in 5 % steps: 95 -> 55 % in 2.4 h of use, then 40 minutes on 55 %
            double m = BatteryReader.HoursLeft(Pts(MadHistory), 17375, 55);
            Console.WriteLine("  estimate: mouse 55 % -> " + m.ToString("0.0", CultureInfo.InvariantCulture) + " h");
            Check(m > 3 && m < 9, "mouse estimate from its 5 % steps");
            // right after a new step the fitted rate alone counts
            double m2 = BatteryReader.HoursLeft(Pts(MadHistory.Substring(0, MadHistory.IndexOf(" 15200,55"))), 14878, 55);
            Check(m2 > 2.5 && m2 < m, "a longer stay on a step means a slower drain now");
            // the headset left on 72 % for 5 more hours of use: slower, but not without limit
            double idle = BatteryReader.HoursLeft(Pts(HeadsetHistory), 11705 + 5 * 3600, 72);
            Console.WriteLine("  estimate: headset 72 % after 5 idle hours -> " + idle.ToString("0.0", CultureInfo.InvariantCulture) + " h");
            Check(idle > h && idle < 3 * h, "a long stay on one level makes the estimate longer, at most about 3 times");
            Equal(-1.0, BatteryReader.HoursLeft(Pts("0,80 600,80 1200,80 2400,80 4000,80"), 4000, 80), "no drop: no estimate");
            Equal(-1.0, BatteryReader.HoursLeft(Pts("0,80 700,79 900,78"), 900, 78), "too short: no estimate");
            Equal(-1.0, BatteryReader.HoursLeft(Pts("0,100 900,100 3000,100"), 3000, 100), "full: no estimate");
        }

        static void TimeLeft()
        {
            Check(BatteryReader.TimeLeft(0.5).Contains("30"), "30 minutes");
            Check(BatteryReader.TimeLeft(0.01).Contains("5"), "at least 5 minutes");
            Check(BatteryReader.TimeLeft(12.4).Contains("12"), "whole hours from 10 h");
        }
    }

    static class Program_
    {
        public static void Init()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var dir = Path.Combine(Path.GetTempPath(), "swarlexbattery-tests");
            Directory.CreateDirectory(dir);
            SwarlexBattery.Program.DataDir = dir; SwarlexBattery.Program.CacheDir = dir;
            Log.Init(Path.Combine(dir, "test.log"));
            Config.Data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Resources.Text("config.default.json"));
            Strings.Load("en");
        }
    }
}
