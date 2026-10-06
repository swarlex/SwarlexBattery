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
        public class Reading { public int Model, Left = -1, Right = -1, Case = -1; public bool ChargingLeft, ChargingRight, ChargingCase; }

        // data after the company id: 07 19 01 <model lo> <model hi> <status> <pods> <flags|case> ...
        // pods: one nibble per earbud, 0-10 = 0-100 %, 15 = not reported; which nibble is left depends on the
        // status byte (0x20 clear: the left one is the low nibble). flags: 1 / 2 an earbud charging, 4 the case.
        public static Reading Parse(byte[] d)
        {
            if (d == null || d.Length < 8 || d[0] != 0x07 || d[1] < 6 || d[2] != 0x01) return null;
            var r = new Reading { Model = d[3] | d[4] << 8 };
            bool flip = (d[5] & 0x20) == 0;
            int hi = d[6] >> 4, lo = d[6] & 0xF, flags = d[7] >> 4;
            r.Left = Level(flip ? lo : hi); r.Right = Level(flip ? hi : lo); r.Case = Level(d[7] & 0xF);
            r.ChargingLeft = r.Left >= 0 && (flags & (flip ? 2 : 1)) != 0;
            r.ChargingRight = r.Right >= 0 && (flags & (flip ? 1 : 2)) != 0;
            r.ChargingCase = r.Case >= 0 && (flags & 4) != 0;
            return r.Left < 0 && r.Right < 0 && r.Case < 0 ? null : r;
        }
        static int Level(int n) { return n <= 10 ? n * 10 : -1; }

        // ------------------------------------------------------------ listening
        class Heard { public Reading R; public double Rssi; public DateTime At; }
        static readonly Dictionary<ulong, Heard> heard = new Dictionary<ulong, Heard>();   // by the (rotating) LE address
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
                        watcher.Received += OnReceived;
                    }
                    if (on && watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) watcher.Start();
                    else if (!on && watcher != null) { watcher.Stop(); watcher = null; heard.Clear(); }
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
                    if (r == null) continue;
                    lock (heard)
                    {
                        Heard h;
                        // a new level of the paired model: the panel reads it at once (Host checks this every second)
                        if (!heard.TryGetValue(e.BluetoothAddress, out h) || h.R.Left != r.Left || h.R.Right != r.Right || h.R.Case != r.Case ||
                            h.R.ChargingLeft != r.ChargingLeft || h.R.ChargingRight != r.ChargingRight)
                            if (BluetoothBattery.Apple.Any(a => a.Pid == r.Model)) Fresh = true;
                        // the signal is smoothed: one strong packet from someone else's AirPods passing by does not win
                        if (heard.TryGetValue(e.BluetoothAddress, out h)) h.Rssi = h.Rssi * 0.7 + e.RawSignalStrengthInDBm * 0.3;
                        else heard[e.BluetoothAddress] = h = new Heard { Rssi = e.RawSignalStrengthInDBm };
                        h.R = r; h.At = DateTime.UtcNow;
                    }
                }
            }
            catch { }
        }

        // the paired model's reading: other people's AirPods can be near, so the model must match and the
        // strongest signal heard in the last minute wins (and it must be close: about a room)
        public static Reading Best(int model)
        {
            lock (heard)
            {
                var now = DateTime.UtcNow;
                foreach (var k in heard.Where(x => (now - x.Value.At).TotalSeconds > 60).Select(x => x.Key).ToList()) heard.Remove(k);
                return Pick(heard.Values.Select(h => Tuple.Create(h.R, h.Rssi)), model);
            }
        }

        public static Reading Pick(IEnumerable<Tuple<Reading, double>> seen, int model)
        {
            return seen.Where(x => x.Item1.Model == model && x.Item2 >= -80).OrderByDescending(x => x.Item2).Select(x => x.Item1).FirstOrDefault();
        }
    }
}
