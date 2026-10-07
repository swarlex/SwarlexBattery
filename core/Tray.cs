// SPDX-License-Identifier: GPL-3.0-or-later
// Tray icons: a ring for the battery level around a glyph, white like the Windows network / volume
// icons (black on a light taskbar). Two devices share one icon: left half of the ring = first, right = second.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwarlexBattery
{
    static class TrayRenderer
    {
        public static int Size = Math.Max(16, SystemInformation.SmallIconSize.Width);   // not readonly: the README image is rendered larger
        public static readonly string GlyphFont = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        public static bool LightTaskbar = IsLightTaskbar();
        static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();

        public static bool IsLightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                { var v = k == null ? null : k.GetValue("SystemUsesLightTheme"); return v is int && (int)v == 1; }
            }
            catch { return false; }
        }

        static Color ColorFor(string state, bool charging)
        {
            var plain = LightTaskbar ? Color.FromArgb(28, 28, 28) : Color.FromArgb(255, 255, 255);
            if (Config.Bool("monochrome", true)) return plain;
            if (charging) return Color.FromArgb(63, 209, 106);
            if (state == "warn") return Color.FromArgb(245, 165, 36);
            if (state == "error") return Color.FromArgb(240, 74, 74);
            if (state == "ok") return LightTaskbar ? Color.FromArgb(30, 150, 70) : Color.FromArgb(63, 209, 106);   // "Coloured icon": green while the level is fine
            return LightTaskbar ? Color.FromArgb(28, 28, 28) : Color.FromArgb(245, 245, 245);
        }

        public static Icon Get(TraySpec spec)
        {
            var rings = spec.Rings ?? (spec.Ring.HasValue ? new[] { spec.Ring.Value } : new double[0]);
            bool hasRing = rings.Length > 0;
            var inv = CultureInfo.InvariantCulture;
            bool number = hasRing && !spec.Charging && spec.Percent >= 0 && Config.Bool("iconPercent", false);
            string key = Size + "|" + Config.Bool("monochrome", true) + "|" + spec.Icon + "|" + string.Join(";", rings.Select(r => r.ToString("0.00", inv))) + "|" + spec.State + "|" + spec.Charging + "|" + spec.Dim + "|" + LightTaskbar + "|" + (number ? spec.Percent : -1);
            Icon cached; if (Cache.TryGetValue(key, out cached)) return cached;

            // drawn 4x larger, then scaled down: smooth ring and a glyph that is bold enough at 16-24 px
            int s = Size, k = 4, B = s * k;
            using (var big = new Bitmap(B, B))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var main = ColorFor(spec.State, spec.Charging);
                    var baseColor = ColorFor("ok", false);
                    var glyphColor = main;
                    float gs;
                    if (hasRing)
                    {
                        float w = B * 0.13f, pad = w / 2 + k * 0.4f;
                        var rect = new RectangleF(pad, pad, B - 2 * pad, B - 2 * pad);
                        using (var track = new Pen(Color.FromArgb(77, baseColor), w))
                        using (var pen = new Pen(main, w))
                        {
                            if (rings.Length >= 2)
                            {
                                // left half = first (mouse), right half = second (headset), both filling from the bottom up
                                float gap = 16f, span = 180f - 2 * gap;
                                for (int i = 0; i < 2; i++)
                                {
                                    float dir = i == 0 ? 1f : -1f, start = 90 + dir * gap;
                                    g.DrawArc(track, rect, start, dir * span);
                                    double lvl = Math.Max(0, Math.Min(1, rings[i]));
                                    if (lvl > 0) g.DrawArc(pen, rect, start, (float)(dir * span * lvl));
                                }
                            }
                            else
                            {
                                g.DrawEllipse(track, rect);
                                float sweep = (float)(360 * Math.Max(0, Math.Min(1, rings[0])));
                                if (sweep > 0) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawArc(pen, rect, -90, sweep); }
                            }
                        }
                        if (!(spec.Charging || spec.State == "warn" || spec.State == "error")) glyphColor = baseColor;
                        gs = B * 0.5f;
                    }
                    else gs = B * 0.9f;

                    // glyph as a path, filled and outlined: line-style Fluent glyphs read as solid in the tray
                    if (number) DrawNumber(g, B, spec.Percent, glyphColor);
                    else if (IsPad(spec.Icon) && !(spec.Charging && hasRing)) DrawGamepad(g, B, hasRing, glyphColor, spec.Icon);
                    else if (spec.Icon == Pods && !(spec.Charging && hasRing)) DrawPods(g, B, hasRing, glyphColor);
                    else using (var path = new GraphicsPath())
                    {
                        if (spec.Charging && hasRing)
                        {
                            // charging: a solid lightning bolt drawn as a shape (font glyphs blur at 16 px)
                            float[,] pts = { { 0.60f, 0.04f }, { 0.18f, 0.56f }, { 0.46f, 0.56f }, { 0.38f, 0.96f }, { 0.82f, 0.42f }, { 0.54f, 0.42f }, { 0.64f, 0.04f } };
                            float o = (B - gs) / 2; var poly = new PointF[pts.GetLength(0)];
                            for (int i = 0; i < poly.Length; i++) poly[i] = new PointF(o + pts[i, 0] * gs, o + pts[i, 1] * gs);
                            path.AddPolygon(poly);
                        }
                        else
                        {
                            using (var fam = new FontFamily(GlyphFont))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                path.AddString(((char)Convert.ToInt32(spec.Icon, 16)).ToString(), fam, 0, gs, new RectangleF(0, 0, B, B), sf);
                        }
                        using (var brush = new SolidBrush(glyphColor))
                        using (var outline = new Pen(glyphColor, B * 0.035f) { LineJoin = LineJoin.Round })
                        { g.FillPath(brush, path); g.DrawPath(outline, path); }
                    }
                    if (!hasRing && (spec.State == "warn" || spec.State == "error"))
                    {
                        float d = B * 0.36f;
                        using (var dot = new SolidBrush(main)) g.FillEllipse(dot, B - d, B - d, d - k, d - k);
                    }
                }
                using (var bmp = new Bitmap(s, s))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var ia = new ImageAttributes())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
                        // dimming is applied to the finished image, so overlapping strokes do not double up
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = (spec.Dim || spec.State == "off") ? 150 / 255f : 1f });
                        g.DrawImage(big, new Rectangle(0, 0, s, s), 0, 0, B, B, GraphicsUnit.Pixel, ia);
                    }
                    IntPtr h = bmp.GetHicon();
                    // a managed copy owns its handle; the GDI one is released right away
                    var icon = (Icon)Icon.FromHandle(h).Clone(); IconUtil.Destroy(h);
                    if (Cache.Count > 300) Clear();
                    Cache[key] = icon;
                    return icon;
                }
            }
        }

        // Controllers are drawn as silhouettes: the Fluent gamepad glyph is too wide for the ring, and the
        // outline that makes glyphs solid at 16 px fills its buttons in. The outlines (right half, mirrored,
        // smoothed with a Catmull-Rom spline) and the cut-outs are HaloBattery's icons.py (MIT): "PAD:XBOX"
        // and the plain "E7FC" pad get the Xbox outline with its offset sticks, "PAD:DS4" the DualShock 4
        // outline with the touchpad and two sticks side by side; "PAD:DS5" the same outline with the DualSense's
        // larger touchpad. No brand logos. Cut-outs are transparent, so they read on any taskbar colour.
        public static bool IsPad(string icon) { return icon == "E7FC" || (icon ?? "").StartsWith("PAD:"); }

        // design grid about 40 units wide, x right, y down
        static readonly float[] XboxHalf = { 0f, -13.9f, 5.5f, -13.9f, 9.3f, -12.9f, 12.8f, -10.3f, 15.2f, -7.9f, 16.3f, -5.9f,
            18.1f, -0.3f, 19.7f, 4.8f, 20.0f, 7.6f, 19.5f, 10.7f, 17.9f, 12.8f, 15.3f, 14.0f, 14.5f, 13.6f, 9.0f, 8.1f, 6.0f, 6.8f, 0f, 6.8f };
        static readonly float[] Ds4Half = { 0f, -11.44f, 9.67f, -11.44f, 9.73f, -12.18f, 10.44f, -12.31f, 14.67f, -12.22f,
            15.22f, -11.6f, 16.22f, -10.44f, 17.33f, -8.89f, 18.22f, -7.11f, 18.89f, -4.44f, 19.44f, -1.11f, 19.82f, 2.22f,
            20.0f, 5.56f, 19.89f, 8.44f, 19.44f, 10.44f, 18.44f, 12.0f, 17.11f, 12.62f, 15.78f, 12.71f, 14.22f, 12.33f,
            12.89f, 11.56f, 12.0f, 10.22f, 11.33f, 8.67f, 10.67f, 6.67f, 10.11f, 5.11f, 9.67f, 3.89f, 8.22f, 4.22f,
            6.67f, 4.56f, 4.89f, 4.22f, 3.78f, 3.38f, 0f, 3.33f };

        static PointF[] Silhouette(float[] half, float cx, float cy, float k)
        {
            // the right half, then the mirrored left half without the two points on the centre line
            var loop = new List<PointF>(); int n = half.Length / 2;
            for (int i = 0; i < n; i++) loop.Add(new PointF(half[2 * i], half[2 * i + 1]));
            for (int i = n - 2; i >= 1; i--) loop.Add(new PointF(-half[2 * i], half[2 * i + 1]));
            var o = new List<PointF>(); int m = loop.Count; const int steps = 12;
            for (int i = 0; i < m; i++)
            {
                PointF p0 = loop[(i - 1 + m) % m], p1 = loop[i], p2 = loop[(i + 1) % m], p3 = loop[(i + 2) % m];
                for (int j = 0; j < steps; j++)
                {
                    float t = j / (float)steps, t2 = t * t, t3 = t2 * t;
                    Func<float, float, float, float, float> cr = (a0, a1, a2, a3) =>
                        0.5f * (2 * a1 + (-a0 + a2) * t + (2 * a0 - 5 * a1 + 4 * a2 - a3) * t2 + (-a0 + 3 * a1 - 3 * a2 + a3) * t3);
                    o.Add(new PointF(cx + cr(p0.X, p1.X, p2.X, p3.X) * k, cy + cr(p0.Y, p1.Y, p2.Y, p3.Y) * k));
                }
            }
            return o.ToArray();
        }

        // AirPods: two earbuds side by side, each a round head with its stem going down (no brand logo). Drawn as
        // solid shapes, thick enough to read at 16 px. PodShape is the same outline for the panel (0..1 square).
        public const string Pods = "PODS";

        public static RectangleF[] PodShape()
        {
            // per earbud: the head (an ellipse) and the stem (a rounded bar), the right one mirrored
            return new[] { new RectangleF(0.06f, 0.08f, 0.38f, 0.36f), new RectangleF(0.25f, 0.26f, 0.17f, 0.66f),
                           new RectangleF(0.56f, 0.08f, 0.38f, 0.36f), new RectangleF(0.58f, 0.26f, 0.17f, 0.66f) };
        }

        static void DrawPods(Graphics g, float B, bool ring, Color c)
        {
            float size = ring ? B * 0.56f : B * 0.94f, o = (B - size) / 2;
            using (var path = new GraphicsPath { FillMode = FillMode.Winding })
            {
                var s = PodShape();
                for (int i = 0; i < s.Length; i++)
                {
                    var r = new RectangleF(o + s[i].X * size, o + s[i].Y * size, s[i].Width * size, s[i].Height * size);
                    if (i % 2 == 0) path.AddEllipse(r);
                    else { float d = r.Width; path.AddArc(r.X, r.Y, d, d, 180, 180); path.AddArc(r.X, r.Bottom - d, d, d, 0, 180); path.CloseFigure(); }
                }
                using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
            }
        }

        static void DrawGamepad(Graphics g, float B, bool ring, Color c, string style)
        {
            bool ps = style == "PAD:DS4" || style == "PAD:DS5";
            float k = (ring ? B * 0.64f : B * 0.96f) / 40f, cx = B / 2, cy = B / 2 + (ps ? -0.2f * k : 0);
            using (var brush = new SolidBrush(c)) g.FillPolygon(brush, Silhouette(ps ? Ds4Half : XboxHalf, cx, cy, k));
            var mode = g.CompositingMode; g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                Action<float, float, float> hole = (x, y, r) => g.FillEllipse(clear, cx + (x - r) * k, cy + (y - r) * k, 2 * r * k, 2 * r * k);
                if (ps)
                {
                    // touchpad: half width, top, bottom (the DualSense's is larger), then the two sticks
                    float hw = style == "PAD:DS5" ? 8.6f : 7.5f, top = -10.7f, bottom = style == "PAD:DS5" ? -2.4f : -3.6f;
                    using (var pad = new GraphicsPath()) { RoundRect(pad, new RectangleF(cx - hw * k, cy + top * k, 2 * hw * k, (bottom - top) * k), 1.0f * k); g.FillPath(clear, pad); }
                    hole(-6.5f, 0.6f, 2.35f); hole(6.5f, 0.6f, 2.35f);
                }
                else { hole(-9.7f, -6.1f, 2.6f); hole(5.2f, -0.3f, 2.6f); }
            }
            g.CompositingMode = mode;
        }
        // "Percentage in the icon": the level as a number inside the ring, sized so "100" fits too
        static void DrawNumber(Graphics g, float B, int pct, Color c)
        {
            string text = Math.Max(0, Math.Min(100, pct)).ToString(CultureInfo.InvariantCulture);
            float em = B * (text.Length >= 3 ? 0.36f : 0.47f);
            using (var fam = new FontFamily("Segoe UI"))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(c))
            using (var pen = new Pen(c, B * 0.012f) { LineJoin = LineJoin.Round })
            {
                path.AddString(text, fam, (int)FontStyle.Bold, em, new RectangleF(0, B * 0.01f, B, B), sf);
                g.FillPath(brush, path); g.DrawPath(pen, path);
            }
        }

        static void RoundRect(GraphicsPath p, RectangleF r, float rad)
        {
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2); float d = 2 * rad;
            p.StartFigure();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
        }

        // icons still assigned to a NotifyIcon are not disposed (the tray keeps showing them)
        public static void Clear(IEnumerable<Icon> inUse = null)
        {
            var keep = new HashSet<Icon>(inUse ?? new Icon[0]);
            foreach (var k in Cache.Keys.ToList()) if (!keep.Contains(Cache[k])) { Cache[k].Dispose(); Cache.Remove(k); }
        }
    }

    // Windows 10/11 notifications (notification centre). An app without a package registers its AppUserModelID
    // under HKCU\Software\Classes\AppUserModelId with a name and an icon. When Windows has notifications turned
    // off for it, or the API is missing, Show() says false and the tray balloon is used instead.
    static class Toasts
    {
        const string Aumid = "Swarlex.SwarlexBattery";
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SetCurrentProcessExplicitAppUserModelID([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id);
        static global::Windows.UI.Notifications.ToastNotifier notifier;
        static bool failed;

        // for the diagnostics report: what Windows says about notifications for this app
        public static string Setting()
        {
            try { return Init() ? notifier.Setting.ToString() : "unavailable"; }
            catch (Exception e) { return "unavailable (" + e.Message + ")"; }
        }

        // registers the app with Windows once (name and icon) and gets its notifier
        static bool Init()
        {
            if (failed) return false;
            if (notifier != null) return true;
            // Windows 10 shows tray balloons as notifications itself; the registry-registered notifier is for 11
            if (!Win.IsWindows11) { failed = true; return false; }
            try
            {
                {
                    string icon = Path.Combine(Program.CacheDir, "icon.png");
                    if (!File.Exists(icon))
                        using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.png"))
                        using (var f = File.Create(icon)) s.CopyTo(f);
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
                    { k.SetValue("DisplayName", "SwarlexBattery"); k.SetValue("IconUri", icon); }
                    SetCurrentProcessExplicitAppUserModelID(Aumid);
                    notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid);
                }
                return true;
            }
            catch (Exception e) { failed = true; Log.Once("notification: " + e.Message); return false; }
        }

        public static bool Show(string title, string body)
        {
            if (!Init()) return false;
            try
            {
                if (notifier.Setting != global::Windows.UI.Notifications.NotificationSetting.Enabled) return false;
                var x = new global::Windows.Data.Xml.Dom.XmlDocument();
                x.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>" + System.Security.SecurityElement.Escape(title ?? "") +
                          "</text><text>" + System.Security.SecurityElement.Escape(body ?? "") + "</text></binding></visual></toast>");
                notifier.Show(new global::Windows.UI.Notifications.ToastNotification(x));
                return true;
            }
            catch (Exception e) { failed = true; Log.Once("notification: " + e.Message); return false; }
        }
    }

    class TrayIcons : IDisposable
    {
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, TraySpec> specs = new Dictionary<string, TraySpec>();
        public event Action<MouseButtons> Click;

        // Icons that are no longer needed are hidden and kept, and a new one takes a kept one first: Windows knows an
        // icon by its number, and a new number is a new icon it parks behind the ^ arrow (switching "Separate icon
        // per device" off and on would otherwise lose the place next to the clock)
        readonly List<NotifyIcon> spare = new List<NotifyIcon>();

        public void Sync(List<TraySpec> list)
        {
            var seen = new HashSet<string>(list.Select(x => x.Id));
            foreach (var id in icons.Keys.ToList())
                if (!seen.Contains(id)) { icons[id].Visible = false; spare.Add(icons[id]); icons.Remove(id); specs.Remove(id); }
            foreach (var sp in list)
            {
                NotifyIcon ni;
                if (!icons.TryGetValue(sp.Id, out ni))
                {
                    if (spare.Count > 0) { ni = spare[0]; spare.RemoveAt(0); }
                    else
                    {
                        ni = new NotifyIcon();
                        ni.MouseClick += (s, e) => { try { if (Click != null) Click(e.Button); } catch (Exception ex) { Log.Write("click: " + ex); } };
                    }
                    icons[sp.Id] = ni;
                }
                specs[sp.Id] = sp;
                var icon = TrayRenderer.Get(sp);
                if (ni.Icon != icon) ni.Icon = icon;
                var tip = (sp.Tooltip ?? "").Trim(); if (tip.Length > 63) tip = tip.Substring(0, 62) + "…";
                ni.Text = tip;
                if (!ni.Visible) ni.Visible = true;
            }
            UpdateAnimation();
        }

        // "Charging animation" (Preferences, on by default): the ring of a charging device fills from its level to
        // full, again and again. The frames are rounded to 5 % steps and cached, so a charge draws few new icons
        // and the timer runs only while something charges.
        System.Windows.Threading.DispatcherTimer anim; int frame;
        const int Frames = 14;                  // 10 filling + a short hold on full

        // nobody sees the tray (locked, a full-screen game in front): the animation waits, nine wake-ups a second for nothing
        bool paused;
        public void Pause(bool on) { if (on == paused) return; paused = on; UpdateAnimation(); }

        public void UpdateAnimation()
        {
            bool want = !paused && Config.Bool("chargeAnimation", true) && specs.Values.Any(s => s.RingCharging != null && s.RingCharging.Any(c => c));
            if (want && anim == null)
            {
                anim = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(110) };
                anim.Tick += (s, e) => { try { Step(); } catch (Exception ex) { Log.Once("animation: " + ex.Message); } };
                frame = 0; anim.Start();
            }
            else if (!want && anim != null)
            {
                anim.Stop(); anim = null;
                foreach (var kv in icons) kv.Value.Icon = TrayRenderer.Get(specs[kv.Key]);   // the still icon again
                TrayRenderer.Clear(icons.Values.Select(n => n.Icon));
            }
        }

        void Step()
        {
            frame = (frame + 1) % Frames;
            double t = Math.Min(1.0, frame / 10.0);
            foreach (var kv in icons)
            {
                var sp = specs[kv.Key];
                if (sp.RingCharging == null || !sp.RingCharging.Any(c => c)) continue;
                var f = sp.Copy();
                if (f.Rings != null)
                    for (int i = 0; i < f.Rings.Length && i < sp.RingCharging.Length; i++) { if (sp.RingCharging[i]) f.Rings[i] = Fill(f.Rings[i], t); }
                else if (f.Ring.HasValue && sp.RingCharging[0]) f.Ring = Fill(f.Ring.Value, t);
                var icon = TrayRenderer.Get(f);
                if (kv.Value.Icon != icon) kv.Value.Icon = icon;
            }
        }

        static double Fill(double level, double t) { return Math.Round((level + (1 - level) * t) * 20) / 20; }

        // taskbar theme switched: repaint everything and free the old images
        public void Repaint()
        {
            foreach (var kv in icons) kv.Value.Icon = TrayRenderer.Get(specs[kv.Key]);
            TrayRenderer.Clear(icons.Values.Select(n => n.Icon));
        }

        public void Balloon(string title, string body, ToolTipIcon kind)
        {
            if (Toasts.Show(title, body)) return;      // a Windows 10/11 notification; the balloon only as a fallback
            var ni = icons.Values.FirstOrDefault(n => n.Visible);
            if (ni != null) ni.ShowBalloonTip(5000, title, string.IsNullOrEmpty(body) ? " " : body, kind);
        }

        // Windows 11 parks new tray icons behind the ^ overflow. This flips the same per-icon switch as
        // Settings > Personalization > Taskbar > Other system tray icons, only for this exe's own icons.
        // "Keep the icon next to the clock" (Preferences, on by default). Turned off, the icon is handed back once
        // (SetPromoted(false)) and from then on stays wherever the user puts it in Windows' own settings.
        // true when an icon was switched on just now: Explorer reads the switch when an icon is added, so the icons
        // are then added again (Readd) to move next to the clock at once
        public static bool Promote()
        {
            return Config.Bool("alwaysShowInTray", true) && SetPromoted(true);
        }

        public void Readd()
        {
            foreach (var ni in icons.Values) if (ni.Visible) { ni.Visible = false; ni.Visible = true; }
        }

        public static bool SetPromoted(bool on)
        {
            bool changed = false;
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null) return false;
                string leaf = "\\" + Path.GetFileName(Program.ExePath);
                foreach (var name in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(name, true))
                    {
                        if (k == null) continue;
                        var exe = k.GetValue("ExecutablePath") as string;
                        if (exe == null) continue;
                        if (!(string.Equals(exe, Program.ExePath, StringComparison.OrdinalIgnoreCase) || exe.EndsWith(leaf, StringComparison.OrdinalIgnoreCase))) continue;
                        var v = k.GetValue("IsPromoted");
                        int want = on ? 1 : 0;
                        if (!(v is int && (int)v == want)) { k.SetValue("IsPromoted", want, RegistryValueKind.DWord); changed = true; }
                    }
                }
            }
            return changed;
        }

        public void Dispose()
        {
            foreach (var ni in icons.Values.Concat(spare)) { ni.Visible = false; ni.Dispose(); }
            icons.Clear(); spare.Clear();
        }
    }
}
