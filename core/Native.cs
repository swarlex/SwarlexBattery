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

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        // Windows 11 look for the flyout: rounded corners, the system border and shadow, dark frame.
        // (DWM draws them itself, so there is exactly one frame; ignored on Windows 10.)
        public static void FlyoutFrame(IntPtr h, bool dark)
        {
            if (h == IntPtr.Zero) return;
            int round = 2;            DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
            int d = dark ? 1 : 0;     DwmSetWindowAttribute(h, 20, ref d, 4);       // DWMWA_USE_IMMERSIVE_DARK_MODE
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
        // Battery type 0 means "disconnected" and 1 "wired": neither has a level to show.
        public static string[] List()
        {
            var r = new List<string>();
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    STATE s; if (XInputGetState(i, out s) != 0) continue;
                    BATTERY b; if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;
                    if (b.Type == 0 || b.Type == 1) continue;
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
