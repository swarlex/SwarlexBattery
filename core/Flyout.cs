// SPDX-License-Identifier: GPL-3.0-or-later
// The flyout: battery list on left click, menu on right click. An ordinary (non-transparent) window:
// Windows 11 draws its rounded corners, border and shadow (Win.FlyoutFrame). A transparent window with a
// hand-drawn frame used to get a second, system-drawn box around it now and then.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SwarlexBattery
{
    class Flyout
    {
        readonly Host host;
        readonly Window win;
        readonly ScrollViewer scroll;
        IntPtr hwnd;
        bool above = true;
        public string Open;                       // null, "panel" or "menu"
        string lastClosed; DateTime lastClosedAt = DateTime.MinValue;

        Brush fg, muted, accent, warn, error, track;
        readonly FontFamily iconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        Style rowStyle, rowStyleMarked;
        bool? light;                              // the theme the brushes were made for
        bool langOpen;                            // the menu's language list is expanded

        static Brush MakeBrush(string c) { var b = (Brush)new BrushConverter().ConvertFromString(c); b.Freeze(); return b; }

        // Light or dark like the taskbar (theme.mode: "auto", "light" or "dark"). The dark colours can be changed
        // with theme.*; the light ones are those of Windows 11's own light flyouts.
        void ApplyTheme()
        {
            var mode = Config.Str("theme.mode", "auto").ToLowerInvariant();
            bool l = mode == "light" || (mode != "dark" && TrayRenderer.LightTaskbar);
            if (light == l) return;
            light = l;
            string panel, hover;
            if (l)
            {
                fg = MakeBrush("#1b1b1b"); muted = MakeBrush("#5d5d5d"); accent = fg; warn = MakeBrush("#b25e00"); error = MakeBrush("#c42b1c");
                track = MakeBrush("#24000000"); panel = "f3f3f3"; hover = "#12000000";
            }
            else
            {
                fg = MakeBrush(Config.Str("theme.foreground", "#f2f2f2")); muted = MakeBrush(Config.Str("theme.muted", "#9a9a9a"));
                accent = MakeBrush(Config.Str("theme.accent", "#f2f2f2")); warn = MakeBrush(Config.Str("theme.warn", "#f5a524"));
                error = MakeBrush(Config.Str("theme.error", "#f04a4a")); track = MakeBrush("#2EFFFFFF");
                panel = Config.Str("theme.panel", "#202020").TrimStart('#'); hover = "#1FFFFFFF";
                if (panel.Length == 8) panel = panel.Substring(2);                        // the window is opaque: drop the alpha
            }
            // Row highlight follows IsMouseOver through a style trigger. (MouseEnter / MouseLeave handlers could
            // miss the "leave" when the menu was redrawn under the pointer, leaving two rows highlighted.)
            const string xaml = "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{0}\"/><Style.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Background\" Value=\"{1}\"/></Trigger></Style.Triggers></Style>";
            rowStyle = (Style)XamlReader.Parse(string.Format(xaml, "Transparent", hover));
            rowStyleMarked = (Style)XamlReader.Parse(string.Format(xaml, hover, hover));   // e.g. "Update": highlighted all the time
            win.Background = MakeBrush("#" + panel); win.Foreground = fg;
            if (hwnd != IntPtr.Zero) Win.FlyoutFrame(hwnd, !l);
        }

        public Flyout(Host host)
        {
            this.host = host;
            win = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = false,
                ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height,
                Width = Config.Num("panel.width", 320), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Title = "SwarlexBattery" };
            ApplyTheme();
            // nothing in the flyout takes keyboard focus: otherwise WPF draws its dotted focus rectangle around
            // the content when the window is activated (Escape still closes it: KeyDown is on the window)
            scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Config.Num("panel.maxHeight", 620),
                                        Focusable = false, FocusVisualStyle = null };
            win.Content = new Border { Padding = new Thickness(14), Child = scroll, Focusable = false, FocusVisualStyle = null };
            win.FocusVisualStyle = null;
            win.SourceInitialized += (s, e) => { hwnd = new WindowInteropHelper(win).Handle; Win.FlyoutFrame(hwnd, light != true); };
            win.Deactivated += (s, e) => { if (Open != null) Close(); };
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            // create the window now, so the first click opens it without WPF's cold-start delay
            new WindowInteropHelper(win).EnsureHandle();
            // SizeChanged comes before the native window has its new size (e.g. the language list opened): placed
            // with the new size at once, and once more after the resize, so the bottom stays on the taskbar
            win.SizeChanged += (s, e) =>
            {
                if (Open == null) return;
                Place(e.NewSize);
                win.Dispatcher.BeginInvoke(new Action(() => { if (Open != null) Place(); }), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        public void Close()
        {
            if (Open != null) { lastClosed = Open; lastClosedAt = DateTime.Now; }
            Open = null; win.Hide();
            TrimSoon();
        }

        // a few seconds after the flyout closed (and it stayed closed), give the drawing memory back
        System.Windows.Threading.DispatcherTimer trim;
        void TrimSoon()
        {
            if (trim == null)
            {
                trim = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                trim.Tick += (s, e) => { trim.Stop(); if (Open == null) Win.TrimMemory(); };
            }
            trim.Stop(); trim.Start();
        }

        // the click that deactivated (and closed) the flyout must not reopen it
        bool JustClosed(string what) { return lastClosed == what && (DateTime.Now - lastClosedAt).TotalMilliseconds < 400; }

        public void TogglePanel()
        {
            if (Open == "panel") { Close(); return; }
            if (JustClosed("panel")) return;
            Open = "panel";
            ApplyTheme();
            RenderPanel();
            Show(Config.Num("panel.width", 320));
            host.PollSoon();   // fresh data while open
        }

        public void ToggleMenu()
        {
            if (Open == "menu") { Close(); return; }
            if (JustClosed("menu")) return;
            Open = "menu";
            ApplyTheme(); langOpen = false;
            scroll.Content = BuildMenu();
            Show(270);
        }

        // opens next to the tray icon that was clicked (the cursor), above or below the taskbar
        void Show(double width)
        {
            win.Width = width;
            anchor = Forms.Cursor.Position;
            if (!win.IsVisible) { win.Left = -20000; win.Top = -20000; win.Show(); }
            win.UpdateLayout();
            Place();
            Win.ForceForeground(hwnd);
            win.Activate();
        }

        // Placed in physical pixels on the monitor of the clicked icon (where the cursor was), next to that
        // monitor's own taskbar - a taskbar on a second monitor included, and an auto-hiding one too: its size
        // is kept out of the area even while the work area covers the whole screen. Moving to a monitor with
        // another scale makes WPF resize the window; SizeChanged then places it again.
        System.Drawing.Point anchor;

        void Place() { Place(Size.Empty); }

        void Place(Size dip)
        {
            if (hwnd == IntPtr.Zero) return;
            var scr = Forms.Screen.FromPoint(anchor);
            var wa = Win.AreaOutsideTaskbar(scr.Bounds, scr.WorkingArea);
            int w, h; if (!Win.WindowSize(hwnd, out w, out h)) return;
            var src = PresentationSource.FromVisual(win);
            if (!dip.IsEmpty && src != null && src.CompositionTarget != null)
            {
                // the size the window is about to get, in physical pixels
                var t = src.CompositionTarget.TransformToDevice;
                w = (int)Math.Round(dip.Width * t.M11); h = (int)Math.Round(dip.Height * t.M22);
            }
            int m = (int)Math.Round(8 * Win.DpiScale(anchor));
            above = anchor.Y > (wa.Top + wa.Bottom) / 2;
            int x = Math.Max(wa.Left + m, Math.Min(wa.Right - w - m, anchor.X - w / 2));
            int y = above ? wa.Bottom - h - m : wa.Top + m;
            Win.MoveTo(hwnd, x, y);
        }

        // WPF loads its themes and templates the first time something is drawn, which made the first click
        // take over a second. Drawing the panel and the menu once off-screen (not activated) does that early.
        bool warmed;
        public void Warm()
        {
            if (warmed || Open != null) return;
            warmed = true;
            try
            {
                win.ShowActivated = false; win.Left = -20000; win.Top = -20000;
                win.Show();
                ApplyTheme();
                RenderPanel(); win.UpdateLayout();
                scroll.Content = BuildMenu(); win.UpdateLayout();
                win.Hide();
                TrimSoon();
            }
            catch (Exception e) { Log.Write("warm: " + e.Message); }
            finally { win.ShowActivated = true; }
        }

        public void Refresh()
        {
            if (Open == "panel") RenderPanel();
            else if (Open == "menu") scroll.Content = BuildMenu();
        }

        TextBlock Text(string t, Brush brush = null, double size = 13, bool semi = false)
        {
            return new TextBlock { Text = t, Foreground = brush ?? fg, FontSize = size, FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
                                   TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        TextBlock Glyph(string hex, Brush brush, double size)
        {
            if (hex.StartsWith("PAD:")) hex = "E7FC";   // the panel uses the font's gamepad for every controller
            var t = Text(((char)Convert.ToInt32(hex, 16)).ToString(), brush, size); t.FontFamily = iconFont; return t;
        }
        Brush StateBrush(string s) { return s == "warn" ? warn : s == "error" ? error : s == "off" ? muted : fg; }

        void RenderPanel()
        {
            var snap = host.Current;
            var root = new StackPanel();
            var title = Text(snap != null ? snap.Title : Strings.T("title"), fg, 15, true); title.Margin = new Thickness(0, 0, 0, 6); root.Children.Add(title);
            if (snap == null) root.Children.Add(Text(Strings.T("loading"), muted, 12));
            else
            {
                if (snap.Empty != null) root.Children.Add(Text(snap.Empty, muted, 12));
                foreach (var it in snap.Items)
                {
                    var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                    var ic = Glyph(it.Icon, StateBrush(it.State), 14); ic.Margin = new Thickness(0, 1, 9, 0); g.Children.Add(ic);
                    var sp = new StackPanel(); Grid.SetColumn(sp, 1);
                    sp.Children.Add(Text(it.Label));
                    if (!string.IsNullOrEmpty(it.Sub)) sp.Children.Add(Text(it.Sub, muted, 11));
                    g.Children.Add(sp);
                    var v = Text(it.Value, StateBrush(it.State), 12); v.TextWrapping = TextWrapping.NoWrap; v.Margin = new Thickness(10, 1, 0, 0); Grid.SetColumn(v, 2); g.Children.Add(v);
                    root.Children.Add(g);
                    root.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = it.Pct, Height = 4, Margin = new Thickness(0, 0, 0, 6),
                                                        Foreground = string.IsNullOrEmpty(it.State) ? fg : StateBrush(it.State), Background = track, BorderThickness = new Thickness(0) });
                }
            }
            scroll.Content = root;
        }

        Border MenuRow(string icon, string text, Action action, bool check = false, string sub = null, bool marked = false, string tail = null)
        {
            var b = new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                                 Style = marked ? rowStyleMarked : rowStyle };   // no local Background: it would override the trigger
            var g = new Grid();
            foreach (var w in new[] { new GridLength(26), new GridLength(1, GridUnitType.Star), GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            if (icon != null) { var ic = Glyph(icon, fg, 13); ic.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(ic); }
            if (tail != null) { var t = Glyph(tail, muted, 10); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 2); g.Children.Add(t); }
            UIElement label = Text(text, fg, 13);
            if (!string.IsNullOrEmpty(sub)) { var two = new StackPanel(); two.Children.Add(label); two.Children.Add(Text(sub, muted, 11)); label = two; }
            Grid.SetColumn(label, 1); g.Children.Add(label);
            if (check) { var ck = Glyph("E73E", accent, 12); ck.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(ck, 2); g.Children.Add(ck); }
            b.Child = g;
            b.MouseLeftButtonUp += (s, e) => { try { if (action != null) action(); } catch (Exception ex) { Log.Write("menu: " + ex); } };
            return b;
        }

        static string Ago(DateTime t)
        {
            int m = (int)(DateTime.Now - t).TotalMinutes;
            return m < 1 ? Strings.T("justNow") : m < 60 ? Strings.T("minutesAgo", m) : Strings.T("hoursAgo", m / 60);
        }

        StackPanel BuildMenu()
        {
            var root = new StackPanel();
            var h = Text(Strings.T("title"), muted, 11, true); h.Margin = new Thickness(10, 0, 0, 4); root.Children.Add(h);
            if (host.Update != null)
                root.Children.Add(MenuRow("E896", host.UpdateBusy == "install" ? Strings.T("downloading", host.Update.Version) : Strings.T("update", host.Update.Version),
                                          () => { Close(); host.InstallUpdate(); }, false, null, true));
            root.Children.Add(MenuRow("E72C", Strings.T("refresh"), () => { Close(); host.PollSoon(); }));
            root.Children.Add(new Border { Height = 1, Background = track, Margin = new Thickness(4, 5, 4, 5) });
            root.Children.Add(MenuRow("E7E8", Strings.T("startWithWindows"), () => { Close(); host.ToggleAutostart(); }, host.Autostart));
            // "Language" opens the list of languages right below it (each in its own language)
            root.Children.Add(MenuRow("E774", Strings.T("language"), () => { langOpen = !langOpen; Refresh(); }, false, null, false, langOpen ? "E70E" : "E70D"));
            if (langOpen)
                foreach (var l in Strings.Languages)
                {
                    var code = l[0];
                    root.Children.Add(MenuRow(null, l[1], () => { Close(); host.SetLanguage(code); }, Strings.Lang == code));
                }
            root.Children.Add(MenuRow("E8EF", Strings.T("iconPercent"), () => { host.ToggleIconPercent(); Refresh(); }, Config.Bool("iconPercent", false)));   // keeps the menu open: the tray icon changes right away
            if (Config.Str("update.repo", "") != "") root.Children.Add(MenuRow("E7C1", Strings.T("beta"), () => { host.ToggleBeta(); Refresh(); }, Config.Bool("update.beta", false)));
            if (Config.Str("update.repo", "") != "")
            {
                // the result of the last check is shown right here, not only as a notification
                string sub = host.LastCheck.HasValue ? Strings.T("lastChecked", Ago(host.LastCheck.Value)) : null;
                Action check = () => { host.CheckUpdates(true); Refresh(); };   // keeps the menu open
                if (host.UpdateBusy == "check") root.Children.Add(MenuRow("E895", Strings.T("checking"), null));
                else if (host.LastResult == "uptodate") root.Children.Add(MenuRow("E895", Strings.T("upToDateRow", Program.AppVersion), check, true, sub));
                else if (host.LastResult == "error") root.Children.Add(MenuRow("E7BA", Strings.T("checkFailedRow"), check, false, sub));
                else root.Children.Add(MenuRow("E895", Strings.T("checkUpdates", Program.AppVersion), check, false, sub));
            }
            root.Children.Add(MenuRow("E9D9", Strings.T("diagnostics"), () => { Close(); host.Diagnostics(); }));
            root.Children.Add(MenuRow("E8BB", Strings.T("exit"), () => { Close(); host.Exit(); }));
            return root;
        }

        public void Dispose() { try { win.Close(); } catch { } }
    }
}
