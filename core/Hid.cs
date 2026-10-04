// SPDX-License-Identifier: GPL-3.0-or-later
// Minimal HID access for reading wireless peripheral batteries (Razer, ATK/VXE ...),
// the same vendor queries HaloBattery / OpenRazer send. Read-only power queries only.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SwarlexBattery
{
    public class HidInfo
    {
        public string Path; public int Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen; public string Product;
        // USB interface number from the path ("...&mi_03..."), -1 when the device has one interface
        public int Interface { get { var m = System.Text.RegularExpressions.Regex.Match(Path ?? "", "&mi_([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase); return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1; } }
        // the device instance without the collection number: two collections of one interface share it
        public string Instance { get { var p = (Path ?? "").Split('#'); if (p.Length < 3) return ""; var i = p[2].ToLowerInvariant(); int k = i.LastIndexOf('&'); return k > 0 ? i.Substring(0, k) : i; } }
        public override string ToString() { return string.Format("{0:X4}:{1:X4} {2:X4}:{3:X4} in={4} out={5} feat={6} '{7}'", Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen, Product); }
    }

    public static partial class Hid
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort Vid, Pid, Ver; }
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS {
            public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] r;
            public ushort a, b, c, d, e, f, g, h, i, j, k; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
        [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA o);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr buf, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] b, int n, out int w, ref NativeOverlapped o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] b, int n, IntPtr r, ref NativeOverlapped o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, ref NativeOverlapped o, out int n, bool wait);
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, ref NativeOverlapped o);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

        public static List<string> Trace = new List<string>();
        static string Hex(byte[] b, int n) { var sb = new StringBuilder(); for (int i = 0; i < Math.Min(n, b.Length); i++) sb.Append(b[i].ToString("X2")).Append(' '); return sb.ToString(); }

        const uint GENERIC_RW = 0xC0000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        // All HID collections of the given vendor ids (0 = all). Opened with no access, so nothing reaches the device.
        public static HidInfo[] List(int[] vids)
        {
            var list = new List<HidInfo>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    // the vendor id is part of the path (USB "vid_1532", Bluetooth "_vid&00021532"): other
                    // vendors' devices (keyboards, cameras, ...) are skipped without being opened at all
                    if (vids != null && vids.Length > 0)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(path, @"vid[_&](?:0002|0001)?([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success && Array.IndexOf(vids, Convert.ToInt32(m.Groups[1].Value, 16)) < 0) continue;
                    }
                    using (var h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
                        if (!HidD_GetAttributes(h, ref a)) continue;
                        if (vids != null && vids.Length > 0 && Array.IndexOf(vids, (int)a.Vid) < 0) continue;
                        var info = new HidInfo { Path = path, Vid = a.Vid, Pid = a.Pid };
                        IntPtr pp;
                        if (HidD_GetPreparsedData(h, out pp))
                        {
                            var c = new HIDP_CAPS(); HidP_GetCaps(pp, ref c); HidD_FreePreparsedData(pp);
                            info.Usage = c.Usage; info.UsagePage = c.UsagePage; info.InLen = c.InLen; info.OutLen = c.OutLen; info.FeatLen = c.FeatLen;
                        }
                        var sb = new byte[256];
                        if (HidD_GetProductString(h, sb, sb.Length)) info.Product = Encoding.Unicode.GetString(sb).TrimEnd('\0');
                        list.Add(info);
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list.ToArray();
        }

        static SafeFileHandle Open(string path) { return CreateFile(path, GENERIC_RW, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero); }

        // Feature report round trip (Razer 90-byte protocol). buf[0] is the report id.
        // Opened with no access rights: Windows keeps mouse / keyboard collections (where Razer mice answer)
        // to itself and refuses a read/write open, but feature reports work on a zero-access handle.
        public static byte[] Feature(string path, byte[] request, int replyLen, int delayMs)
        {
            using (var h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) { Trace.Add("feature open failed " + Marshal.GetLastWin32Error()); return null; }
                if (!HidD_SetFeature(h, request, request.Length)) return null;
                Thread.Sleep(delayMs);
                var r = new byte[replyLen]; r[0] = request[0];
                return HidD_GetFeature(h, r, r.Length) ? r : null;
            }
        }

        // Output report, then read input reports until match() accepts one or the timeout passes.
        public static byte[] Exchange(string path, byte[] frame, int inLen, int timeoutMs, Func<byte[], bool> match, byte[] pre)
        {
            using (var h = Open(path))
            {
                if (h.IsInvalid) { Trace.Add("open failed " + Marshal.GetLastWin32Error()); return null; }
                if (pre != null) { Write(h, pre); Thread.Sleep(35); }
                if (!Write(h, frame)) { Trace.Add("write failed " + Marshal.GetLastWin32Error()); return null; }
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var r = Read(h, inLen, (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
                    if (r != null) Trace.Add("in: " + Hex(r, 20));
                    if (r != null && match(r)) return r;
                }
                return null;
            }
        }

        static bool Write(SafeFileHandle h, byte[] b)
        {
            var ev = new ManualResetEvent(false);
            try
            {
                var o = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
                int w;
                if (!WriteFile(h, b, b.Length, out w, ref o))
                {
                    if (Marshal.GetLastWin32Error() != 997) return false; // ERROR_IO_PENDING
                    if (WaitForSingleObject(o.EventHandle, 1000) != 0) { CancelIoEx(h, ref o); int x; GetOverlappedResult(h, ref o, out x, true); return false; }
                    return GetOverlappedResult(h, ref o, out w, false);
                }
                return true;
            }
            finally { ev.Dispose(); }
        }

        static byte[] Read(SafeFileHandle h, int len, int ms)
        {
            var ev = new ManualResetEvent(false);
            try
            {
                var o = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
                var b = new byte[len];
                if (!ReadFile(h, b, len, IntPtr.Zero, ref o) && Marshal.GetLastWin32Error() != 997) return null;
                if (WaitForSingleObject(o.EventHandle, (uint)ms) != 0) { CancelIoEx(h, ref o); int x; GetOverlappedResult(h, ref o, out x, true); return null; }
                int n; return GetOverlappedResult(h, ref o, out n, false) && n > 0 ? b : null;
            }
            finally { ev.Dispose(); }
        }

        // ------------------------------------------------------------ protocols

        // Razer 90-byte feature report: class 0x07 power, 0x80 battery (0..255), 0x84 charging.
        static byte[] RazerRequest(byte tid, byte cls, byte cmd)
        {
            var m = new byte[91]; // [0] report id 0
            m[2] = tid; m[6] = 0x02; m[7] = cls; m[8] = cmd;
            byte crc = 0; for (int i = 3; i < 89; i++) crc ^= m[i];
            m[89] = crc;
            return m;
        }
        // returns arg1 or -1; status 0x02 = ok
        public static int RazerQuery(string path, byte tid, byte cls, byte cmd)
        {
            var req = RazerRequest(tid, cls, cmd);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var r = Feature(path, req, 91, attempt == 0 ? 60 : 90);
                if (r == null) return -1;
                // r[1] status, r[7] class, r[8] cmd, r[10] arg1
                if (r[1] == 0x02 && r[7] == cls && r[8] == cmd) return r[10];
                if (r[1] == 0x01 || (r[1] == 0x02)) { Thread.Sleep(60); req = attempt % 2 == 1 ? RazerRequest(tid, cls, cmd) : req; continue; }
                return -1 - r[1];
            }
            return -1;
        }

        // Razer BlackShark "PA" protocol (64-byte output/input reports, report id 2), as in
        // HaloBattery providers/blackshark.py and OpenRazer PR #2862. Remote mode on, query, remote mode off.
        static byte[] PaFrame(byte type, byte cmd, int len, bool remoteOn)
        {
            var b = new byte[len];
            b[0] = 0x02; b[1] = 0x80; b[5] = 0x50; b[6] = 0x41;
            if (type == 0x02) { b[2] = 0x07; b[7] = 0x0E; b[9] = 0x02; b[10] = 0xE1; b[11] = (byte)(remoteOn ? 1 : 0); }
            else { b[2] = 8; b[7] = 0x08; b[9] = type; b[10] = cmd; }
            return b;
        }

        static void Drain(SafeFileHandle h, int inLen) { for (int i = 0; i < 32; i++) if (Read(h, inLen, 5) == null) break; }

        static int PaQuery(SafeFileHandle h, byte cmd, int outLen, int inLen)
        {
            Drain(h, inLen);
            Write(h, PaFrame(0x02, 0, outLen, true)); Thread.Sleep(35);   // sacrificial frame: wakes the radio link
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Write(h, PaFrame(0x02, 0, outLen, true)); Thread.Sleep(35);
                if (!Write(h, PaFrame(0x03, cmd, outLen, false))) return -1;
                var deadline = DateTime.UtcNow.AddMilliseconds(150);
                while (DateTime.UtcNow < deadline)
                {
                    var r = Read(h, inLen, 50);
                    if (r == null) continue;
                    Trace.Add("pa in: " + Hex(r, 20));
                    if (r.Length > 15 && r[0] == 0x02 && r[12] == cmd && r[13] == 0x01) return r[15];
                }
            }
            return -1;
        }

        // -> { level 0..100, charging 0/1 } or null (headset off / no link)
        public static int[] RazerPa(string path, int outLen, int inLen)
        {
            using (var h = Open(path))
            {
                if (h.IsInvalid) { Trace.Add("open failed " + Marshal.GetLastWin32Error()); return null; }
                bool woke = false;
                for (int i = 0; i < 4 && !woke; i++) { woke = Write(h, PaFrame(0x02, 0, outLen, true)); if (!woke) Thread.Sleep(150 * (i + 1)); }
                if (!woke) { Trace.Add("receiver does not accept commands"); return null; }
                Thread.Sleep(35);
                try
                {
                    int bat = PaQuery(h, 0x21, outLen, inLen);
                    if (bat < 0) { Trace.Add("no battery reply (headset off?)"); return null; }
                    int chg = PaQuery(h, 0x2A, outLen, inLen);
                    return new[] { Math.Min(bat, 100), chg > 0 ? 1 : 0 };
                }
                finally { Write(h, PaFrame(0x02, 0, outLen, false)); }
            }
        }

        // Razer BlackShark V2 HyperSpeed (1532:0565 dongle, 056E wired, 0566): MediaTek frames,
        // from justik13/razer-blackshark-v2-hyperspeed-webhid. Report id 2 + 63 bytes:
        // [1]=0x60|seq [5]=len(4) [8]=domain(0x80 RF) [9]=cmd [11]=count, [61]=XOR(0x02, p[0..60]).
        // Reply: [9]=cmd [10]=ack(1) [12..]=data, [61]=XOR(p[0..60]). Only GET 0x21/0x2A are sent.
        static int mtkSeq;
        static byte[] MtkFrame(byte cmd, int outLen, byte domain)
        {
            var b = new byte[Math.Max(64, outLen)];
            b[0] = 0x02;
            b[2] = (byte)(0x60 | (Interlocked.Increment(ref mtkSeq) & 0x1F));
            b[6] = 0x04; b[9] = domain; b[10] = cmd;   // domain 0x80 = through the 2.4 GHz dongle, 0x00 = USB cable
            byte x = 0x02; for (int i = 1; i <= 61; i++) x ^= b[i];
            b[62] = x;
            return b;
        }

        static int MtkQuery(SafeFileHandle h, byte cmd, int outLen, int inLen, byte domain)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Drain(h, inLen);
                if (!Write(h, MtkFrame(cmd, outLen, domain))) { Trace.Add("mtk write failed " + Marshal.GetLastWin32Error()); return -1; }
                var deadline = DateTime.UtcNow.AddMilliseconds(400);
                while (DateTime.UtcNow < deadline)
                {
                    var r = Read(h, inLen, 100);
                    if (r == null) continue;
                    Trace.Add("mtk in: " + Hex(r, 16));
                    if (r.Length < 63 || r[0] != 0x02 || r[10] != cmd || r[11] != 0x01) continue;
                    byte x = 0; for (int i = 1; i <= 61; i++) x ^= r[i];
                    if (x != r[62]) { Trace.Add("mtk checksum mismatch"); continue; }
                    return r[13];
                }
            }
            return -1;
        }

        // -> { level 0..100, charging 0/1 } or null (headset off / not linked)
        public static int[] RazerMtk(string path, int outLen, int inLen, byte domain)
        {
            using (var h = Open(path))
            {
                if (h.IsInvalid) { Trace.Add("open failed " + Marshal.GetLastWin32Error()); return null; }
                if (domain == 0x80)
                {
                    int link = MtkQuery(h, 0x20, outLen, inLen, domain);
                    if (link == 0) { Trace.Add("mtk: headset not linked"); return null; }   // off / out of range: no stale value
                }
                int bat = MtkQuery(h, 0x21, outLen, inLen, domain);
                if (bat < 0 || bat > 100) return null;
                int chg = MtkQuery(h, 0x2A, outLen, inLen, domain);
                return new[] { bat, chg > 0 ? 1 : 0 };
            }
        }

        // ATK / VXE / Pulsar / Hitscan 17-byte frame (HaloBattery providers/pulsar.py):
        // [0]=0x08 [1]=0x04 power ... [16]=0x55-sum. Reply: level [6], on-cable flag [7].
        public static int[] Atk(string path, int outLen, int inLen)
        {
            using (var h = Open(path))
            {
                if (h.IsInvalid) { Trace.Add("open failed " + Marshal.GetLastWin32Error()); return null; }
                var f = new byte[Math.Max(17, outLen)];
                f[0] = 0x08; f[1] = 0x04;
                int sum = 0; for (int i = 0; i < 16; i++) sum += f[i];
                f[16] = (byte)((0x55 - sum) & 0xFF);
                Drain(h, Math.Max(17, inLen));
                if (!Write(h, f)) { Trace.Add("write failed " + Marshal.GetLastWin32Error()); return null; }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var x = Read(h, Math.Max(17, inLen), 250);
                    if (x == null) break;
                    Trace.Add("atk in: " + Hex(x, 20));
                    if (x.Length < 17 || x[0] != 0x08 || x[1] != 0x04) continue;   // pushed events (0x0a) are skipped
                    int s = 0; for (int i = 0; i < 16; i++) s += x[i];
                    if (x[16] != (byte)((0x55 - s) & 0xFF) || x[6] > 100) continue;
                    int mv = (x[8] << 8) | x[9];
                    if (x[6] == 0 && mv < 3000) { Trace.Add("atk: level 0 without a battery voltage (mouse off)"); return null; }
                    if (mv != 0 && (mv < 2800 || mv > 4600)) { Trace.Add("atk: implausible voltage " + mv); return null; }
                    return new[] { (int)x[6], (int)x[7], mv };
                }
                return null;
            }
        }
    }
}
