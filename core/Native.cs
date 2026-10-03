// SwarlexBattery native helpers. Compiled once by SwarlexBattery.ps1 and cached as a DLL.
// Only user-level Win32 APIs: no drivers, no injection, no elevation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SwarlexBattery
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // ---------------------------------------------------------------- bar backdrop
    public static class Backdrop
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int State; public int Flags; public uint Gradient; public int Anim; }
        [StructLayout(LayoutKind.Sequential)]
        struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref CompositionData d);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        // mode: "acrylic" | "blur" | "none". tint is 0xRRGGBB, alpha 0..1.
        public static void Apply(IntPtr hwnd, string mode, int tint, double alpha)
        {
            int state = mode == "acrylic" ? 4 : mode == "blur" ? 3 : 0;
            byte a = (byte)Math.Max(1, Math.Min(255, (int)(alpha * 255)));
            // AccentPolicy wants ABGR.
            uint abgr = ((uint)a << 24) | ((uint)(tint & 0xFF) << 16) | (uint)(tint & 0xFF00) | (uint)((tint >> 16) & 0xFF);
            var policy = new AccentPolicy { State = state, Flags = 2, Gradient = abgr };
            int size = Marshal.SizeOf(policy);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new CompositionData { Attribute = 19, Data = p, Size = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            int pref = 2; // DWMWCP_ROUND (Windows 11; ignored on 10)
            DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
        }
    }

    // ---------------------------------------------------------------- appbar (reserve screen space)
    public static class AppBar
    {
        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);

        static APPBARDATA Data(IntPtr h) { var d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(d); d.hWnd = h; return d; }

        // Reserves a strip on the top (edge 1) or bottom (edge 3) of the given monitor rect, in physical pixels.
        public static RECT Register(IntPtr h, uint edge, RECT monitor, int thickness)
        {
            var d = Data(h);
            SHAppBarMessage(0, ref d); // ABM_NEW
            d.edge = edge;
            d.rc = monitor;
            if (edge == 1) d.rc.Bottom = monitor.Top + thickness; else d.rc.Top = monitor.Bottom - thickness;
            SHAppBarMessage(2, ref d); // ABM_QUERYPOS
            if (edge == 1) d.rc.Bottom = d.rc.Top + thickness; else d.rc.Top = d.rc.Bottom - thickness;
            SHAppBarMessage(3, ref d); // ABM_SETPOS
            return d.rc;
        }

        public static void Remove(IntPtr h) { var d = Data(h); SHAppBarMessage(1, ref d); }
    }

    // ---------------------------------------------------------------- glass: per-window opacity
    // Runs its own thread with a WinEvent hook so new/activated windows get the right alpha.
    public static class Glass
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT mon, work; public uint flags; }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_TRANSPARENT = 0x20;
        const uint LWA_ALPHA = 2, WM_APP = 0x8000, WM_QUIT = 0x12;

        static readonly object Sync = new object();
        static readonly Dictionary<IntPtr, byte> Applied = new Dictionary<IntPtr, byte>();
        static readonly HashSet<IntPtr> AddedLayered = new HashSet<IntPtr>();
        static readonly Dictionary<uint, string> ProcNames = new Dictionary<uint, string>();
        static readonly HashSet<string> SkipClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "ApplicationFrameWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
            "NotifyIconOverflowWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd" };

        static HashSet<string> exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool enabled; static byte activeA = 235, inactiveA = 210;
        static Thread thread; static uint threadId;
        static WinEventProc procRef; // keep the delegate alive
        static readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        public static bool Enabled { get { return enabled; } }

        // "Quiet while gaming": a fullscreen foreground window (not the desktop) suppresses toasts.
        public static bool ForegroundIsFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64); GetClassName(fg, sb, 64);
            if (SkipClasses.Contains(sb.ToString())) return false;
            return IsFullscreen(fg);
        }

        // The tray click gives this process foreground rights; use them for the flyout.
        public static void ForceForeground(IntPtr h) { if (h != IntPtr.Zero) SetForegroundWindow(h); }
        public static int WindowCount { get { lock (Sync) { return Applied.Count; } } }

        public static void Configure(bool on, double active, double inactive, string[] excludeProcesses)
        {
            lock (Sync)
            {
                enabled = on;
                activeA = ToByte(active);
                inactiveA = ToByte(inactive);
                exclude = new HashSet<string>(excludeProcesses ?? new string[0], StringComparer.OrdinalIgnoreCase);
            }
            EnsureThread();
            PostThreadMessage(threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        // Restores every window we touched. Safe to call more than once.
        public static void Shutdown()
        {
            lock (Sync) { enabled = false; }
            if (thread != null)
            {
                PostThreadMessage(threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
                PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(2000);
                thread = null;
            }
            RestoreAll();
        }

        static byte ToByte(double v) { return (byte)Math.Max(26, Math.Min(255, (int)Math.Round(v * 255))); }

        static void EnsureThread()
        {
            if (thread != null) return;
            var ready = new ManualResetEvent(false);
            thread = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                procRef = OnEvent;
                // 0x0003 foreground, 0x8002 object show.
                IntPtr h1 = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, procRef, 0, 0, 0x0002);
                IntPtr h2 = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, procRef, 0, 0, 0x0002);
                MSG m;
                PeekQueue(); ready.Set();
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.msg == WM_APP && m.h == IntPtr.Zero) { ApplyAll(); continue; }
                    TranslateMessage(ref m); DispatchMessage(ref m);
                }
                UnhookWinEvent(h1); UnhookWinEvent(h2);
            });
            thread.IsBackground = true;
            thread.Name = "SwarlexBattery.Glass";
            thread.Start();
            ready.WaitOne(2000);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreAll();
        }

        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
        static void PeekQueue() { MSG m; PeekMessage(out m, IntPtr.Zero, 0, 0, 0); } // creates the queue

        static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0) return; // OBJID_WINDOW, CHILDID_SELF only
            if (ev == 0x8002 && GetWindow(hwnd, 4) != IntPtr.Zero) return; // owned popups
            ApplyAll();
        }

        static bool Eligible(IntPtr h)
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return false; // GW_OWNER
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT)) != 0) return false;
            int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
            var sb = new System.Text.StringBuilder(128); GetClassName(h, sb, 128);
            if (SkipClasses.Contains(sb.ToString())) return false;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == selfPid) return false;
            string name;
            if (!ProcNames.TryGetValue(pid, out name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { name = ""; }
                if (ProcNames.Count > 512) ProcNames.Clear();
                ProcNames[pid] = name;
            }
            if (exclude.Contains(name)) return false;
            return !IsFullscreen(h); // leave games / video players alone
        }

        static bool IsFullscreen(IntPtr h)
        {
            RECT r; if (!GetWindowRect(h, out r)) return false;
            var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(mi);
            if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            return r.Left <= mi.mon.Left && r.Top <= mi.mon.Top && r.Right >= mi.mon.Right && r.Bottom >= mi.mon.Bottom;
        }

        static void ApplyAll()
        {
            bool on; byte act, inact;
            lock (Sync) { on = enabled; act = activeA; inact = inactiveA; }
            if (!on) { RestoreAll(); return; }
            IntPtr fg = GetForegroundWindow();
            var seen = new HashSet<IntPtr>();
            EnumWindows((h, lp) =>
            {
                if (!Eligible(h)) return true;
                seen.Add(h);
                SetAlpha(h, h == fg ? act : inact);
                return true;
            }, IntPtr.Zero);
            // forget windows that closed or stopped being eligible
            var stale = new List<IntPtr>();
            lock (Sync) { foreach (var h in Applied.Keys) if (!seen.Contains(h)) stale.Add(h); }
            foreach (var h in stale) Restore(h);
        }

        static void SetAlpha(IntPtr h, byte a)
        {
            lock (Sync)
            {
                byte cur;
                if (Applied.TryGetValue(h, out cur) && cur == a) return; // nothing to do: no redraw
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0)
                {
                    if (a == 255) return;
                    SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                    AddedLayered.Add(h);
                }
                else if (!Applied.ContainsKey(h) && !AddedLayered.Contains(h))
                {
                    return; // app manages its own layering (e.g. custom-shaped windows): don't fight it
                }
                if (SetLayeredWindowAttributes(h, 0, a, LWA_ALPHA)) Applied[h] = a;
            }
        }

        static void Restore(IntPtr h)
        {
            lock (Sync)
            {
                if (IsWindow(h))
                {
                    SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                    if (AddedLayered.Contains(h))
                        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_LAYERED);
                }
                Applied.Remove(h); AddedLayered.Remove(h);
            }
        }

        static void RestoreAll()
        {
            List<IntPtr> all;
            lock (Sync) { all = new List<IntPtr>(Applied.Keys); }
            foreach (var h in all) Restore(h);
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

    // ---------------------------------------------------------------- mouse settings
    public static class Mouse
    {
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, ref int v, uint f);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, int[] v, uint f);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, IntPtr v, uint f);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
        const uint SAVE = 3; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

        public static int GetSpeed() { int v = 0; SystemParametersInfo(0x70, 0, ref v, 0); return v; }
        public static void SetSpeed(int v) { SystemParametersInfo(0x71, 0, (IntPtr)Math.Max(1, Math.Min(20, v)), SAVE); }

        public static bool GetPrecision() { var a = new int[3]; SystemParametersInfo(0x03, 0, a, 0); return a[2] != 0; }
        public static void SetPrecision(bool on)
        {
            var a = on ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
            SystemParametersInfo(0x04, 0, a, SAVE);
        }

        public static int GetWheelLines() { int v = 0; SystemParametersInfo(0x68, 0, ref v, 0); return v; }
        public static void SetWheelLines(int v) { SystemParametersInfo(0x69, (uint)Math.Max(1, Math.Min(100, v)), IntPtr.Zero, SAVE); }

        public static int GetDoubleClick() { return (int)GetDoubleClickTime(); }
        public static void SetDoubleClick(int ms) { SystemParametersInfo(0x20, (uint)Math.Max(100, Math.Min(1500, ms)), IntPtr.Zero, SAVE); }

        public static bool GetSwapped() { return GetSystemMetrics(23) != 0; }
        public static void SetSwapped(bool on) { SystemParametersInfo(0x21, on ? 1u : 0u, IntPtr.Zero, SAVE); }

        public static bool GetSonar() { int v = 0; SystemParametersInfo(0x101C, 0, ref v, 0); return v != 0; }
        public static void SetSonar(bool on) { SystemParametersInfo(0x101D, 0, (IntPtr)(on ? 1 : 0), SAVE); }
    }

    // ---------------------------------------------------------------- Xbox / XInput controller batteries
    public static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)] struct BATTERY { public byte Type; public byte Level; }
        [StructLayout(LayoutKind.Sequential)] struct STATE { public uint Packet; public ushort b; public byte lt, rt; public short lx, ly, rx, ry; }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint user, out STATE s);
        [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint user, byte devType, out BATTERY b);

        // Returns "index|type|percent" for each connected pad. type: wired, alkaline, nimh, unknown.
        public static string[] List()
        {
            var r = new List<string>();
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    STATE s; if (XInputGetState(i, out s) != 0) continue;
                    BATTERY b; if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;
                    string type = b.Type == 1 ? "wired" : b.Type == 2 ? "alkaline" : b.Type == 3 ? "nimh" : "unknown";
                    int pct = b.Level == 0 ? 5 : b.Level == 1 ? 30 : b.Level == 2 ? 65 : 100;
                    r.Add(i + "|" + type + "|" + pct);
                }
            }
            catch (DllNotFoundException) { }
            return r.ToArray();
        }
    }
}
