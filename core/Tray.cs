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
            return LightTaskbar ? Color.FromArgb(28, 28, 28) : Color.FromArgb(245, 245, 245);
        }

        public static Icon Get(TraySpec spec)
        {
            var rings = spec.Rings ?? (spec.Ring.HasValue ? new[] { spec.Ring.Value } : new double[0]);
            bool hasRing = rings.Length > 0;
            var inv = CultureInfo.InvariantCulture;
            string key = spec.Icon + "|" + string.Join(";", rings.Select(r => r.ToString("0.00", inv))) + "|" + spec.State + "|" + spec.Charging + "|" + spec.Dim + "|" + LightTaskbar;
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
                    if (spec.Icon == "E7FC" && !(spec.Charging && hasRing)) DrawGamepad(g, B, hasRing, glyphColor);
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

        // The controller, drawn as a shape: the Fluent gamepad glyph is too wide for the ring, and the outline
        // that makes glyphs solid at 16 px fills its buttons in. A body with two grips, with a D-pad and two
        // buttons cut out of it, keeps its proportions at every size.
        static void DrawGamepad(Graphics g, float B, bool ring, Color c)
        {
            float w = ring ? B * 0.64f : B * 0.94f, h = w * 0.66f;
            float x = (B - w) / 2, y = (B - h) / 2 + h * 0.04f;
            using (var body = new GraphicsPath(FillMode.Winding))
            {
                RoundRect(body, new RectangleF(x + w * 0.04f, y, w * 0.92f, h * 0.64f), h * 0.32f);   // top bar
                RoundRect(body, new RectangleF(x, y + h * 0.18f, w * 0.40f, h * 0.82f), w * 0.20f);    // left grip
                RoundRect(body, new RectangleF(x + w * 0.60f, y + h * 0.18f, w * 0.40f, h * 0.82f), w * 0.20f); // right grip
                using (var brush = new SolidBrush(c)) g.FillPath(brush, body);
            }
            // cut-outs: transparent, so they read on any taskbar colour
            var mode = g.CompositingMode; g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                float cx = x + w * 0.26f, cy = y + h * 0.36f, arm = w * 0.13f, t = w * 0.095f;
                g.FillRectangle(clear, cx - arm, cy - t / 2, 2 * arm, t);
                g.FillRectangle(clear, cx - t / 2, cy - arm, t, 2 * arm);
                float r = w * 0.075f;
                g.FillEllipse(clear, x + w * 0.67f - r, y + h * 0.24f - r, 2 * r, 2 * r);
                g.FillEllipse(clear, x + w * 0.80f - r, y + h * 0.47f - r, 2 * r, 2 * r);
            }
            g.CompositingMode = mode;
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

    class TrayIcons : IDisposable
    {
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, TraySpec> specs = new Dictionary<string, TraySpec>();
        public event Action<MouseButtons> Click;

        public void Sync(List<TraySpec> list)
        {
            var seen = new HashSet<string>();
            foreach (var sp in list)
            {
                seen.Add(sp.Id);
                NotifyIcon ni;
                if (!icons.TryGetValue(sp.Id, out ni))
                {
                    ni = new NotifyIcon();
                    ni.MouseClick += (s, e) => { try { if (Click != null) Click(e.Button); } catch (Exception ex) { Log.Write("click: " + ex); } };
                    icons[sp.Id] = ni;
                }
                specs[sp.Id] = sp;
                var icon = TrayRenderer.Get(sp);
                if (ni.Icon != icon) ni.Icon = icon;
                var tip = (sp.Tooltip ?? "").Trim(); if (tip.Length > 63) tip = tip.Substring(0, 62) + "…";
                ni.Text = tip;
                if (!ni.Visible) ni.Visible = true;
            }
            foreach (var id in icons.Keys.ToList())
                if (!seen.Contains(id)) { icons[id].Visible = false; icons[id].Dispose(); icons.Remove(id); specs.Remove(id); }
        }

        // taskbar theme switched: repaint everything and free the old images
        public void Repaint()
        {
            foreach (var kv in icons) kv.Value.Icon = TrayRenderer.Get(specs[kv.Key]);
            TrayRenderer.Clear(icons.Values.Select(n => n.Icon));
        }

        public void Balloon(string title, string body, ToolTipIcon kind)
        {
            var ni = icons.Values.FirstOrDefault(n => n.Visible);
            if (ni != null) ni.ShowBalloonTip(5000, title, string.IsNullOrEmpty(body) ? " " : body, kind);
        }

        // Windows 11 parks new tray icons behind the ^ overflow. This flips the same per-icon switch as
        // Settings > Personalization > Taskbar > Other system tray icons, only for this exe's own icons.
        public static void Promote()
        {
            if (!Config.Bool("alwaysShowInTray", true)) return;
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null) return;
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
                        if (!(v is int && (int)v == 1)) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var ni in icons.Values) { ni.Visible = false; ni.Dispose(); }
            icons.Clear();
        }
    }
}
