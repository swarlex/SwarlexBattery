// SPDX-License-Identifier: GPL-3.0-or-later
// AirPods and Beats: Windows shows no level for them. They tell it in their Bluetooth LE "proximity pairing"
// advertisement (Apple, company 0x004C, type 0x07) - the one an iPhone shows when the case opens - every
// second or so, to anyone listening. It is only listened to (passive scan, nothing is sent), and only while
// AirPods paired with this PC are connected. Levels come in 10 % steps.
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth.Advertisement;

namespace SwarlexBattery
{
    static class AirPods
    {
        public class Reading
        {
            public int Model, Left = -1, Right = -1, Case = -1;
            public bool ChargingLeft, ChargingRight, ChargingCase, LeftInEar, RightInEar, BothInCase, LidOpen, FromLeft;
        }

        // data after the company id (layout as in AirPodsDesktop's AppleCP.h, GPL-3.0):
        //   07 19 01 <model lo> <model hi> <status> <pods> <charging|case> <lid> ...
        // The advertisement comes from one earbud ("this" one) and tells about the other too.
        //   status: 0x02 this one in an ear, 0x04 both in the case, 0x08 the other in an ear, 0x20 this one is the left
        //   pods: low nibble this one, high nibble the other; 0-10 = 0-100 %, 15 = not reported
        //   charging|case: low nibble the case's level; 0x10 this one charging, 0x20 the other, 0x40 the case
        //   lid: 0x08 closed
        public static Reading Parse(byte[] d)
        {
            if (d == null || d.Length < 8 || d[0] != 0x07 || d[1] < 6 || d[2] != 0x01) return null;
            bool left = (d[5] & 0x20) != 0;
            var r = new Reading { Model = d[3] | d[4] << 8, FromLeft = left };
            int self = d[6] & 0xF, other = d[6] >> 4;
            bool selfCharging = (d[7] & 0x10) != 0, otherCharging = (d[7] & 0x20) != 0;
            bool selfInEar = (d[5] & 0x02) != 0, otherInEar = (d[5] & 0x08) != 0;
            r.Left = Level(left ? self : other); r.Right = Level(left ? other : self); r.Case = Level(d[7] & 0xF);
            r.ChargingLeft = r.Left >= 0 && (left ? selfCharging : otherCharging);
            r.ChargingRight = r.Right >= 0 && (left ? otherCharging : selfCharging);
            r.ChargingCase = r.Case >= 0 && (d[7] & 0x40) != 0;
            // an earbud that charges is in the case, whatever the in-ear bit says
            r.LeftInEar = r.Left >= 0 && !r.ChargingLeft && (left ? selfInEar : otherInEar);
            r.RightInEar = r.Right >= 0 && !r.ChargingRight && (left ? otherInEar : selfInEar);
            r.BothInCase = (d[5] & 0x04) != 0;
            r.LidOpen = d.Length > 8 && (d[8] & 0x08) == 0;
            return r.Left < 0 && r.Right < 0 && r.Case < 0 ? null : r;
        }
        static int Level(int n) { return n <= 10 ? n * 10 : -1; }

        // ------------------------------------------------------------ listening
        public class Heard { public Reading R; public double Rssi; public int Count; public ulong Address; public DateTime At; }
        static readonly Dictionary<ulong, Heard> heard = new Dictionary<ulong, Heard>();   // by the (rotating) LE address
        static readonly Dictionary<int, ulong> chosen = new Dictionary<int, ulong>();       // per model: the pair shown last
        static BluetoothLEAdvertisementWatcher watcher;
        static volatile bool Fresh;
        public static bool TakeFresh() { if (!Fresh) return false; Fresh = false; return true; }

        public static void Watch(bool on)
        {
            lock (heard)
            {
                try
                {
                    if (on && (watcher == null || watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted))
                    {
                        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                        // Windows passes on only Apple's proximity pairing packets (company 0x004C, data starting 07),
                        // not every advertisement around (hundreds a second in a busy place)
                        var w = new Windows.Storage.Streams.DataWriter(); w.WriteByte(0x07);
                        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = 0x004C, Data = w.DetachBuffer() });
                        watcher.Received += OnReceived;
                    }
                    if (on && watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) watcher.Start();
                    else if (!on && watcher != null) { watcher.Stop(); watcher = null; heard.Clear(); chosen.Clear(); }
                }
                catch (Exception e) { Log.Once("AirPods: " + e.Message); }
            }
        }

        static void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            try
            {
                foreach (var m in e.Advertisement.ManufacturerData)
                {
                    if (m.CompanyId != 0x004C || m.Data.Length < 8) continue;
                    var dr = Windows.Storage.Streams.DataReader.FromBuffer(m.Data);
                    var b = new byte[m.Data.Length]; dr.ReadBytes(b);
                    var r = Parse(b);
                    // only the paired models are kept (in a busy place many people's AirPods are around)
                    if (r == null || !BluetoothBattery.Apple.Any(a => a.Pid == r.Model)) continue;
                    lock (heard)
                    {
                        Heard h;
                        bool known = heard.TryGetValue(e.BluetoothAddress, out h);
                        // something shown changed (a level, charging, an earbud in or out): the panel reads it at once
                        // (Host looks every second) - for the paired model only
                        if (!known || Shown(h.R) != Shown(r)) Fresh = true;
                        // the signal is smoothed: one strong packet from someone else's AirPods passing by does not win
                        if (known) h.Rssi = h.Rssi * 0.7 + e.RawSignalStrengthInDBm * 0.3;
                        else heard[e.BluetoothAddress] = h = new Heard { Rssi = e.RawSignalStrengthInDBm, Address = e.BluetoothAddress };
                        h.R = r; h.At = DateTime.UtcNow; h.Count++;
                    }
                }
            }
            catch { }
        }

        static string Shown(Reading r)
        {
            return r.Left + "/" + r.Right + "/" + r.Case + (r.ChargingLeft ? "L" : "") + (r.ChargingRight ? "R" : "") + (r.LeftInEar ? "l" : "") + (r.RightInEar ? "r" : "");
        }

        // the paired model's reading
        public static Reading Best(int model)
        {
            lock (heard)
            {
                var now = DateTime.UtcNow;
                foreach (var k in heard.Where(x => (now - x.Value.At).TotalSeconds > 30).Select(x => x.Key).ToList()) heard.Remove(k);   // quiet for 30 s: gone
                ulong last; chosen.TryGetValue(model, out last);
                var h = Pick(heard.Values, model, last);
                if (h == null) { chosen.Remove(model); return null; }
                chosen[model] = h.Address;
                return Merge(h.R, Partner(heard.Values, h));
            }
        }

        // Out of the case both earbuds send, each from its own address, and each knows its own state best (one in
        // the case does not tell whether the other is worn). The other earbud of the same pair: the same model, the
        // other side, heard in the same 10 s, with the same two levels.
        public static Heard Partner(IEnumerable<Heard> seen, Heard h)
        {
            return seen.FirstOrDefault(x => x != h && x.R.Model == h.R.Model && x.R.FromLeft != h.R.FromLeft &&
                                            Math.Abs((x.At - h.At).TotalSeconds) <= 10 && x.R.Left == h.R.Left && x.R.Right == h.R.Right);
        }

        // each earbud's state from its own packet; the case from whichever tells it
        public static Reading Merge(Reading a, Heard partner)
        {
            if (partner == null) return a;
            var b = partner.R;
            var l = a.FromLeft ? a : b; var r = a.FromLeft ? b : a;
            return new Reading { Model = a.Model, FromLeft = a.FromLeft, Left = a.Left, Right = a.Right,
                Case = a.Case >= 0 ? a.Case : b.Case, ChargingCase = a.Case >= 0 ? a.ChargingCase : b.ChargingCase,
                ChargingLeft = l.ChargingLeft, LeftInEar = l.LeftInEar, ChargingRight = r.ChargingRight, RightInEar = r.RightInEar,
                BothInCase = a.BothInCase && b.BothInCase, LidOpen = a.LidOpen || b.LidOpen };
        }

        // Other people's AirPods can be near. The model must match the paired one; a pair heard fewer than 3 times
        // (someone passing by) or from farther than about a room (-80 dBm) is left out; of the rest the strongest
        // signal wins - but the pair shown last stays while it is within 10 dB of it, so two pairs side by side
        // do not take turns. Only what was heard in the 10 s before the newest packet counts: the earbuds take turns
        // sending, each from its own address, and one that went quiet (taken out of the case) must not keep
        // showing its last state
        public static Heard Pick(IEnumerable<Heard> seen, int model, ulong last)
        {
            var all = seen.Where(x => x.R.Model == model).ToList();
            if (all.Count == 0) return null;
            var newest = all.Max(x => x.At);
            var ok = all.Where(x => (newest - x.At).TotalSeconds <= 10 && x.Count >= 3 && x.Rssi >= -80).OrderByDescending(x => x.Rssi).ToList();
            if (ok.Count == 0) return null;
            var keep = ok.FirstOrDefault(x => x.Address == last && last != 0);
            return keep != null && keep.Rssi >= ok[0].Rssi - 10 ? keep : ok[0];
        }
    }
}
