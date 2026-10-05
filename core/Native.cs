// SPDX-License-Identifier: GPL-3.0-or-later
// SwarlexBattery native helpers: Win32 calls that WinForms / WPF do not offer.
// User-level APIs only: no drivers, no injection, no elevation.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SwarlexBattery
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // ---------------------------------------------------------------- window helpers
    public static class Win
    {
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT mon, work; public uint flags; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

        static readonly HashSet<string> Shell = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        // "Quiet while gaming": a fullscreen foreground window (not the desktop) suppresses notifications.
        public static bool ForegroundIsFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64); GetClassName(fg, sb, 64);
            if (Shell.Contains(sb.ToString())) return false;
            RECT r; if (!GetWindowRect(fg, out r)) return false;
            var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(mi);
            if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
            return r.Left <= mi.mon.Left && r.Top <= mi.mon.Top && r.Right >= mi.mon.Right && r.Bottom >= mi.mon.Bottom;
        }

        // A tray click gives this process foreground rights; the flyout uses them.
        public static void ForceForeground(IntPtr h) { if (h != IntPtr.Zero) SetForegroundWindow(h); }

        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);

        // The taskbar's own rectangle (physical pixels) and edge: 0 left, 1 top, 2 right, 3 bottom.
        // Needed for an auto-hiding taskbar: the work area then covers the whole screen, the taskbar does not.
        public static bool Taskbar(out RECT rc, out int edge)
        {
            var d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(d);
            bool ok = SHAppBarMessage(5, ref d) != IntPtr.Zero;    // ABM_GETTASKBARPOS
            rc = d.rc; edge = (int)d.edge;
            return ok;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        // After the flyout closes, WPF's drawing memory sits unused until the next click: hand it back to Windows.
        public static void TrimMemory()
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

        // The work area of one monitor, with the taskbar that sits on that monitor kept out (the primary one,
        // Shell_TrayWnd, or a secondary one). An auto-hiding taskbar leaves the work area whole; its rectangle
        // still says how thick it is and on which edge it appears.
        public static System.Drawing.Rectangle AreaOutsideTaskbar(System.Drawing.Rectangle bounds, System.Drawing.Rectangle work)
        {
            var bars = new List<IntPtr>();
            IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); if (h != IntPtr.Zero) bars.Add(h);
            for (h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero; ) bars.Add(h);
            int left = work.Left, top = work.Top, right = work.Right, bottom = work.Bottom;
            foreach (var bar in bars)
            {
                RECT r; if (!GetWindowRect(bar, out r)) continue;
                int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                if (cx < bounds.Left - 50 || cx > bounds.Right + 50 || cy < bounds.Top - 50 || cy > bounds.Bottom + 50) continue;   // another monitor's
                int wdt = r.Right - r.Left, hgt = r.Bottom - r.Top;
                if (wdt >= hgt)
                {
                    if (cy < (bounds.Top + bounds.Bottom) / 2) top = Math.Max(top, bounds.Top + hgt);
                    else bottom = Math.Min(bottom, bounds.Bottom - hgt);
                }
                else
                {
                    if (cx < (bounds.Left + bounds.Right) / 2) left = Math.Max(left, bounds.Left + wdt);
                    else right = Math.Min(right, bounds.Right - wdt);
                }
            }
            if (right - left < 100 || bottom - top < 100) return work;
            return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
        }

        // tray icon size for the primary monitor's current scale (it changes live when the user changes it)
        public static int TrayIconSize() { return Math.Max(16, (int)Math.Round(16 * DpiScale(new System.Drawing.Point(0, 0)))); }

        // Windows 11 is build 22000 and later (the app manifest makes Windows report its real version)
        public static bool IsWindows11 { get { return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000; } }

        // display scale of the monitor under a point (1.0 = 96 dpi)
        public static double DpiScale(System.Drawing.Point p)
        {
            try
            {
                uint x, y; var m = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2);
                if (GetDpiForMonitor(m, 0, out x, out y) == 0 && x > 0) return x / 96.0;
            }
            catch { }
            return 1.0;
        }

        public static bool WindowSize(IntPtr h, out int w, out int hgt)
        {
            RECT r; bool ok = GetWindowRect(h, out r);
            w = r.Right - r.Left; hgt = r.Bottom - r.Top; return ok && w > 0 && hgt > 0;
        }

        // move without resizing, reordering or activating
        public static void MoveTo(IntPtr h, int x, int y) { SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        // Windows 11 look for the flyout: rounded corners, the system border and shadow, dark frame.
        // (DWM draws them itself, so there is exactly one frame; ignored on Windows 10.)
        public static void FlyoutFrame(IntPtr h, bool dark)
        {
            if (h == IntPtr.Zero) return;
            int round = 2;            DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
            int d = dark ? 1 : 0;
            if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE (19 before Windows 10 20H1)
        }
    }

    // ---------------------------------------------------------------- device plug / unplug
    // A hidden top-level window receives WM_DEVICECHANGE broadcasts (a receiver or a cable
    // plugged in or out). The host polls Changes and refreshes batteries right away.
    public static class DeviceWatch
    {
        delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS { public uint style; public WndProcFn proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string menu, name; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);

        public static int Changes;
        public static IntPtr Handle { get { return hwnd; } }
        static WndProcFn procRef;   // keep the delegate alive
        static IntPtr hwnd;

        // Call on the UI thread (its message loop delivers the broadcasts).
        public static void Start()
        {
            if (hwnd != IntPtr.Zero) return;
            procRef = (h, msg, w, l) =>
            {
                if (msg == 0x0219) Interlocked.Increment(ref Changes);   // WM_DEVICECHANGE
                return DefWindowProc(h, msg, w, l);
            };
            var wc = new WNDCLASS { proc = procRef, inst = GetModuleHandle(null), name = "SwarlexBatteryDeviceWatch" };
            RegisterClass(ref wc);
            hwnd = CreateWindowEx(0x80, "SwarlexBatteryDeviceWatch", "SwarlexBatteryDeviceWatch", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero); // tool popup, never shown
        }
    }

    // ---------------------------------------------------------------- tray icon handles
    public static class IconUtil
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        public static bool Destroy(IntPtr h) { return h != IntPtr.Zero && DestroyIcon(h); }
    }

    // ---------------------------------------------------------------- Xbox / XInput controller batteries
    public static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)] struct BATTERY { public byte Type; public byte Level; }
        [StructLayout(LayoutKind.Sequential)] struct STATE { public uint Packet; public ushort b; public byte lt, rt; public short lx, ly, rx, ry; }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint user, out STATE s);
        [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint user, byte devType, out BATTERY b);

        // "index|type|percent" for each connected pad with a battery. XInput only reports four
        // levels (empty / low / medium / full), so the percent is approximate.
        // Battery type 0 means "disconnected", 1 "wired" and 0xFF "unknown": none has a level to show.
        public static string[] List()
        {
            var r = new List<string>();
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    STATE s; if (XInputGetState(i, out s) != 0) continue;
                    BATTERY b; if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;
                    if (b.Type == 0 || b.Type == 1 || b.Type == 0xFF || b.Level > 3) continue;
                    string type = b.Type == 2 ? "alkaline" : b.Type == 3 ? "nimh" : "unknown";
                    int pct = b.Level == 0 ? 5 : b.Level == 1 ? 30 : b.Level == 2 ? 65 : 100;
                    r.Add(i + "|" + type + "|" + pct);
                }
            }
            catch (DllNotFoundException) { }
            return r.ToArray();
        }
    }
}
