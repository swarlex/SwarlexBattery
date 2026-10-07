// SPDX-License-Identifier: GPL-3.0-or-later
// The flyout: battery list on left click, menu on right click. An ordinary (non-transparent) window:
// Windows 11 draws its rounded corners, border and shadow (Win.FlyoutFrame). A transparent window with a
// hand-drawn frame used to get a second, system-drawn box around it now and then.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        Brush fg, muted, accent, warn, error, track, ok;
        Style barStyle;                           // the thin scroll bar of the theme in use
        readonly FontFamily iconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        Style rowStyle, rowStyleMarked;
        bool? light;                              // the theme the brushes were made for

        // Preferences: a second window beside the menu that a click does not activate, so the menu stays open
        Window side; IntPtr sideHwnd; ScrollViewer sideScroll; bool sideOpen;
        bool hiddenOpen;                          // the menu's list of hidden devices is expanded
        string actionsFor, renaming;              // the panel: a device's options shown, its name being edited
        string graphFor; bool graphWeek;          // the panel: a device's battery history shown (a click), over 24 h or 7 days
        long graphHover;                          // the time under the pointer in the graph (0: none); kept when the panel redraws

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
            // "Coloured icon": the panel's bars and pictograms are green while the level is fine, like the tray icon
            ok = MakeBrush(l ? "#1e9646" : "#3fd16a");
            // a thin scroll bar in the theme's colours instead of WPF's light grey one (only when a list is taller
            // than the screen)
            const string bar = "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ScrollBar\">" +
                "<Setter Property=\"Width\" Value=\"6\"/><Setter Property=\"MinWidth\" Value=\"6\"/><Setter Property=\"Margin\" Value=\"4,0,0,0\"/>" +
                "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"ScrollBar\">" +
                "<Track x:Name=\"PART_Track\" IsDirectionReversed=\"True\" Orientation=\"Vertical\"><Track.Thumb><Thumb><Thumb.Template>" +
                "<ControlTemplate TargetType=\"Thumb\"><Border CornerRadius=\"3\" Background=\"{0}\"/></ControlTemplate>" +
                "</Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate></Setter.Value></Setter></Style>";
            barStyle = (Style)XamlReader.Parse(bar.Replace("{0}", l ? "#40000000" : "#50FFFFFF"));
            foreach (var sv in new[] { scroll, sideScroll }) if (sv != null) sv.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = barStyle;
            win.Background = MakeBrush("#" + panel); win.Foreground = fg;
            if (hwnd != IntPtr.Zero) Win.FlyoutFrame(hwnd, !l);
            if (side != null) { side.Background = win.Background; side.Foreground = fg; if (sideHwnd != IntPtr.Zero) Win.FlyoutFrame(sideHwnd, !l); }
        }

        public Flyout(Host host)
        {
            this.host = host;
            win = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = false,
                ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height,
                Width = Config.Num("panel.width", 320), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Title = "SwarlexBattery" };
            side = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = false, ShowActivated = false,
                ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height,
                Width = 330, FontFamily = win.FontFamily, Title = "SwarlexBattery" };
            sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Config.Num("panel.maxHeight", 620), Focusable = false, FocusVisualStyle = null };
            side.Content = new Border { Padding = new Thickness(8), Child = sideScroll, Focusable = false };
            side.SourceInitialized += (s, e) => { sideHwnd = new WindowInteropHelper(side).Handle; Win.ToolWindow(sideHwnd); Win.FlyoutFrame(sideHwnd, light != true); };
            side.Deactivated += (s, e) => CloseUnlessFocused();
            side.KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            new WindowInteropHelper(side).EnsureHandle();
            side.SizeChanged += (s, e) =>
            {
                if (!sideOpen) return;
                PlaceSide(e.NewSize);
                side.Dispatcher.BeginInvoke(new Action(() => { if (sideOpen) PlaceSide(Size.Empty); }), System.Windows.Threading.DispatcherPriority.Background);
            };
            ApplyTheme();
            // nothing in the flyout takes keyboard focus: otherwise WPF draws its dotted focus rectangle around
            // the content when the window is activated (Escape still closes it: KeyDown is on the window)
            scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Config.Num("panel.maxHeight", 620),
                                        Focusable = false, FocusVisualStyle = null };
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = barStyle;   // (the theme was applied before this list existed)
            win.Content = new Border { Padding = new Thickness(14), Child = scroll, Focusable = false, FocusVisualStyle = null };
            win.FocusVisualStyle = null;
            win.SourceInitialized += (s, e) => { hwnd = new WindowInteropHelper(win).Handle; Win.FlyoutFrame(hwnd, light != true); };
            // closes when another window takes the focus - not when it goes to the Preferences window beside it:
            // checked once the activation has settled
            win.Deactivated += (s, e) => CloseUnlessFocused();
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            // create the window now, so the first click opens it without WPF's cold-start delay
            new WindowInteropHelper(win).EnsureHandle();
            // SizeChanged comes before the native window has its new size (e.g. the language list opened): placed
            // with the new size at once, and once more after the resize, so the bottom stays on the taskbar
            win.SizeChanged += (s, e) =>
            {
                if (Open == null) return;
                Place(e.NewSize);
                win.Dispatcher.BeginInvoke(new Action(() => { if (Open != null) Place(); if (sideOpen) PlaceSide(Size.Empty); }), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        public void Close()
        {
            if (Open != null) { lastClosed = Open; lastClosedAt = DateTime.Now; }
            Open = null; win.Hide();
            CloseSide();
            renaming = null; graphHover = 0;
            TrimSoon();
        }

        void CloseSide() { sideOpen = false; side.Hide(); }

        void CloseUnlessFocused()
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Open != null && !win.IsActive && !(sideOpen && side.IsActive)) Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // the window beside the menu shows Preferences ("prefs") or the languages ("lang"); the same row closes it
        string sideKind;

        void ToggleSide(string kind, PanelItem item = null)
        {
            if (sideOpen && sideKind == kind && (item == null || (sideItem != null && sideItem.Id == item.Id))) { CloseSide(); Refresh(); return; }
            // another kind (e.g. Language while Preferences are open): hidden first and opened again, so the window
            // never shows the old size in the new place for a moment
            if (sideOpen) side.Hide();
            bool was = false;
            sideOpen = true; sideKind = kind; sideItem = item;
            side.Width = kind == "prefs" ? 330 : 230;
            // as tall as the screen allows: a scroll bar only when even that is too short
            var scr = Forms.Screen.FromPoint(anchor);
            var area = Win.AreaOutsideTaskbar(scr.Bounds, scr.WorkingArea);
            sideScroll.MaxHeight = Math.Max(300, area.Height / Win.DpiScale(anchor) - 40);
            sideScroll.Content = SideContent();
            if (!side.IsVisible) { side.Left = -20000; side.Top = -20000; side.Show(); }
            side.UpdateLayout();
            PlaceSide(Size.Empty);
            if (!was) OpenAnimation(side);
            Refresh();
        }

        PanelItem sideItem;                       // the device whose icon or low level the window beside the panel sets

        StackPanel SideContent()
        {
            switch (sideKind)
            {
                case "lang": return BuildLanguages();
                case "icon": return BuildIconPicker(sideItem);
                case "low": return BuildLowPicker(sideItem);
                default: return BuildPrefs();
            }
        }

        // a device's icon, beside the panel
        StackPanel BuildIconPicker(PanelItem it)
        {
            var root = new StackPanel();
            if (it == null) return root;
            root.Children.Add(MenuRow(null, Strings.T("iconAuto"), () => { CloseSide(); host.SetIcon(it.Id, null); }, it.IconChoice == null, null, false, null, true));
            foreach (var k in BatteryReader.IconChoices)
            {
                var kind = k;
                root.Children.Add(MenuRow(BatteryReader.IconFor(kind), KindLabel(kind), () => { CloseSide(); host.SetIcon(it.Id, kind); }, it.IconChoice == kind));
            }
            return root;
        }

        // a device's own low battery level, beside the panel
        StackPanel BuildLowPicker(PanelItem it)
        {
            var root = new StackPanel();
            if (it == null) return root;
            string own; Config.Map("plugins.gadgets.lowLevels").TryGetValue(it.Id, out own);
            int general = (int)Config.Num("plugins.gadgets.lowThreshold", 15);
            root.Children.Add(MenuRow(null, Strings.T("lowGeneral", Strings.T("percent", general)), () => { CloseSide(); host.SetDeviceLow(it.Id, null); }, own == null));
            foreach (var p in Host.LowLevels)
            {
                int pct = p;
                root.Children.Add(MenuRow(null, Strings.T("percent", pct), () => { CloseSide(); host.SetDeviceLow(it.Id, pct); }, own == pct.ToString()));
            }
            return root;
        }

        // each language in its own language; picking one closes the menu (every text changes)
        StackPanel BuildLanguages()
        {
            var root = new StackPanel();
            foreach (var l in Strings.Languages)
            {
                var code = l[0];
                root.Children.Add(MenuRow(null, l[1], () => { Close(); host.SetLanguage(code); }, Strings.Lang == code));
            }
            return root;
        }

        // A theme picked in Preferences: the open windows' background turns into the new colour and their content
        // fades in, instead of everything flipping from black to white at once
        void ThemeChange(string mode)
        {
            var old = win.Background as SolidColorBrush;
            var from = old != null ? old.Color : Colors.Black;
            host.SetTheme(mode); ApplyTheme(); Refresh();
            if (!Config.Bool("openAnimation", true)) return;
            var dur = TimeSpan.FromMilliseconds(280);
            foreach (var w in new[] { win, side })
            {
                var target = w.Background as SolidColorBrush;
                if (!w.IsVisible || target == null || target.Color == from) continue;
                var b = new SolidColorBrush(from); w.Background = b;
                b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, target.Color, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                var el = w.Content as UIElement;
                if (el != null) el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1, dur));
            }
        }

        // "Opening animation" (Preferences, on by default): the window's content fades in and slides into place
        void OpenAnimation(Window w)
        {
            var el = w.Content as FrameworkElement;
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; el.RenderTransform = null;
            if (!Config.Bool("openAnimation", true)) return;
            var tt = new TranslateTransform(0, above ? 12 : -12); el.RenderTransform = tt;
            var dur = TimeSpan.FromMilliseconds(180); var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(tt.Y, 0, dur) { EasingFunction = ease });
        }

        // beside the menu: on its right when there is room (like a submenu), else on its left; its bottom level
        // with the menu's bottom when the taskbar is below, its top with the menu's top when the taskbar is above;
        // inside the work area
        void PlaceSide(Size dip)
        {
            if (sideHwnd == IntPtr.Zero || hwnd == IntPtr.Zero) return;
            var scr = Forms.Screen.FromPoint(anchor);
            var wa = Win.AreaOutsideTaskbar(scr.Bounds, scr.WorkingArea);
            var r = Win.WindowRect(hwnd);
            int w, h; if (!Win.WindowSize(sideHwnd, out w, out h)) return;
            var src = PresentationSource.FromVisual(side);
            if (!dip.IsEmpty && src != null && src.CompositionTarget != null)
            {
                var t = src.CompositionTarget.TransformToDevice;
                w = (int)Math.Round(dip.Width * t.M11); h = (int)Math.Round(dip.Height * t.M22);
            }
            double scale = Win.DpiScale(anchor);
            int gap = (int)Math.Round(4 * scale), m = (int)Math.Round(8 * scale);
            int x = r.Right + gap;
            if (x + w > wa.Right - m) x = Math.Max(wa.Left + m, r.Left - w - gap);
            int y = above ? r.Bottom - h : r.Top;
            y = Math.Max(wa.Top + m, Math.Min(wa.Bottom - h - m, y));
            Win.MoveTo(sideHwnd, x, y);
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
            CloseSide();   // Preferences belong to the menu
            actionsFor = null; renaming = null;
            RenderPanel();
            Show(Config.Num("panel.width", 320));
            host.PollSoon();   // fresh data while open
        }

        public void ToggleMenu()
        {
            if (Open == "menu") { Close(); return; }
            if (JustClosed("menu")) return;
            Open = "menu";
            ApplyTheme(); hiddenOpen = false;
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
            OpenAnimation(win);
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
            if (Open == "panel") { RenderPanel(); if (sideOpen) sideScroll.Content = SideContent(); }
            else if (Open == "menu") { scroll.Content = BuildMenu(); if (sideOpen) sideScroll.Content = SideContent(); }
        }

        TextBlock Text(string t, Brush brush = null, double size = 13, bool semi = false)
        {
            return new TextBlock { Text = t, Foreground = brush ?? fg, FontSize = size, FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
                                   TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        TextBlock Glyph(string hex, Brush brush, double size)
        {
            if (hex.StartsWith("PAD:")) hex = "E7FC";   // the panel uses the font's gamepad for every controller
            if (hex == TrayRenderer.Pods)
            {
                // AirPods: the tray icon's earbuds, drawn at the glyph's size (the font has no earbuds)
                var geo = new GeometryGroup { FillRule = FillRule.Nonzero };
                var s = TrayRenderer.PodShape();
                for (int i = 0; i < s.Length; i++)
                {
                    var r = new Rect(s[i].X * size, s[i].Y * size, s[i].Width * size, s[i].Height * size);
                    if (i % 2 == 0) geo.Children.Add(new EllipseGeometry(r));
                    else geo.Children.Add(new RectangleGeometry(r, r.Width / 2, r.Width / 2));
                }
                var box = new TextBlock { Width = size, Height = size };
                box.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new System.Windows.Shapes.Path { Data = geo, Fill = brush, Width = size, Height = size, Margin = new Thickness(0, size * 0.15, 0, 0) }) { BaselineAlignment = BaselineAlignment.Center });
                return box;
            }
            var t = Text(((char)Convert.ToInt32(hex, 16)).ToString(), brush, size); t.FontFamily = iconFont; return t;
        }
        Brush StateBrush(string s) { return s == "warn" ? warn : s == "error" ? error : s == "off" ? muted : s == "ok" ? ok : fg; }

        static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "mouse": return Strings.T("kindMouse");
                case "headphones": return Strings.T("kindHeadset");
                case "earbuds": return Strings.T("kindEarbuds");
                case "keyboard": return Strings.T("kindKeyboard");
                case "gamepad": return Strings.T("kindController");
                case "speaker": return Strings.T("kindSpeaker");
                default: return Strings.T("kindOther");
            }
        }

        // the panel. A right click on a device shows its options below it (rename, icon, hide); while its name is
        // being edited the regular refreshes leave the panel alone (force = redraw anyway)
        void RenderPanel(bool force = false)
        {
            if (renaming != null && !force) return;
            var snap = host.Current;
            var root = new StackPanel();
            var title = Text(snap != null ? snap.Title : Strings.T("title"), fg, 15, true); title.Margin = new Thickness(0, 0, 0, 6); root.Children.Add(title);
            TextBox editor = null;
            if (snap == null) root.Children.Add(Text(Strings.T("loading"), muted, 12));
            else
            {
                if (snap.Empty != null) root.Children.Add(Text(snap.Empty, muted, 12));
                foreach (var it in snap.Items)
                {
                    var item = it;
                    var box = new StackPanel { Background = Brushes.Transparent };   // a background, so a right click anywhere on the row is seen
                    var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                    // the pictogram and the number stay plain; only a low (red) or sleeping (grey) device colours them,
                    // "Coloured icon" colours the bar alone
                    var plain = it.State == "ok" || it.State == "warn" ? fg : StateBrush(it.State);
                    var ic = Glyph(it.Icon, plain, 14); ic.Margin = new Thickness(0, 1, 9, 0); g.Children.Add(ic);
                    var sp = new StackPanel(); Grid.SetColumn(sp, 1);
                    if (renaming != null && renaming == it.Id)
                    {
                        editor = new TextBox { Text = it.Label, FontSize = 13, Foreground = fg, Background = Brushes.Transparent, CaretBrush = fg, BorderBrush = accent,
                                               BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0), FocusVisualStyle = null };
                        var ed = editor;
                        ed.KeyDown += (s, e) =>
                        {
                            if (e.Key == Key.Enter)
                            {
                                e.Handled = true; renaming = null;
                                var name = ed.Text.Trim();
                                host.Rename(item.Id, name == "" || name == (item.OwnName ?? "") ? null : name);
                                RenderPanel(true);
                            }
                            else if (e.Key == Key.Escape) { e.Handled = true; renaming = null; RenderPanel(true); }
                        };
                        sp.Children.Add(ed);
                    }
                    else sp.Children.Add(Text(it.Label));
                    if (!string.IsNullOrEmpty(it.Sub)) sp.Children.Add(Text(it.Sub, muted, 11));
                    g.Children.Add(sp);
                    var v = Text(it.Value, plain, 12); v.TextWrapping = TextWrapping.NoWrap; v.Margin = new Thickness(10, 1, 0, 0); Grid.SetColumn(v, 2); g.Children.Add(v);
                    // a small chevron: the row opens (the battery history below it); up while it is open
                    if (it.Id != null) { var chev = Glyph(graphFor == it.Id ? "E70E" : "E70D", muted, 9); chev.Margin = new Thickness(7, 4, 0, 0); Grid.SetColumn(chev, 3); g.Children.Add(chev); }
                    box.Children.Add(g);
                    if (it.Pct < 0) g.Margin = new Thickness(0, 3, 0, 9);   // no level known: no bar
                    else box.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = it.Pct, Height = 4, Margin = new Thickness(0, 0, 0, 6),
                                                            Foreground = string.IsNullOrEmpty(it.State) ? fg : StateBrush(it.State), Background = track, BorderThickness = new Thickness(0) });
                    if (it.Id != null)
                    {
                        box.MouseRightButtonUp += (s, e) => { actionsFor = actionsFor == item.Id ? null : item.Id; CloseSide(); renaming = null; RenderPanel(true); e.Handled = true; };
                        box.MouseLeftButtonUp += (s, e) => { if (renaming != null) return; graphFor = graphFor == item.Id ? null : item.Id; RenderPanel(true); e.Handled = true; };
                        box.Cursor = Cursors.Hand;
                    }
                    // the row lights up under the pointer like the menu's rows, so it reads as something to click
                    root.Children.Add(it.Id == null ? (UIElement)box : new Border { Child = box, Style = rowStyle, CornerRadius = new CornerRadius(6),
                                                                                    Margin = new Thickness(-6, 0, -6, 0), Padding = new Thickness(6, 0, 6, 0) });
                    if (graphFor != null && graphFor == it.Id) root.Children.Add(Graph(it));
                    if (actionsFor != null && actionsFor == it.Id) DeviceActions(root, it);
                }
                if (snap.Items.Count > 0 && actionsFor == null)
                {
                    var hint = Text(Strings.T("deviceHint"), muted, 11); hint.Margin = new Thickness(0, 4, 0, 0); root.Children.Add(hint);
                }
            }
            scroll.Content = root;
            if (editor != null)
            {
                var ed = editor;
                ed.Dispatcher.BeginInvoke(new Action(() => { ed.Focus(); Keyboard.Focus(ed); ed.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // A device's battery history below it (a left click on the device): the level over the last 24 hours or 7
        // days, the line broken where it was off or asleep, dashed while it charged. With a time-left estimate a
        // quarter of the width is the future: a dashed line from now down to where the estimate says it runs out.
        // The pointer shows the time and the level under it.
        UIElement Graph(PanelItem it)
        {
            const double H = 54;
            double W = Math.Max(160, win.ActualWidth > 0 ? win.ActualWidth - 30 : Config.Num("panel.width", 320) - 30);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), span = graphWeek ? 7 * 86400 : 86400, from = now - span;
            bool filling = it.HoursToFull > 0;                          // charging, with an estimate of when it is full
            long ahead = it.HoursLeft > 0 || filling ? span / 4 : 0;    // the future part, when there is an estimate
            double nowX = W * span / (span + ahead);
            var pts = History.Since(it.Id, from - History.Gap);
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            // the range: two small switches on the right; the reading under the pointer on the left
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var week in new[] { true, false })
            {
                bool w = week;
                var t = Text(Strings.T(w ? "hist7d" : "hist24h"), graphWeek == w ? fg : muted, 11);
                t.FontWeight = graphWeek == w ? FontWeights.SemiBold : FontWeights.Normal; t.Margin = new Thickness(10, 0, 0, 0); t.Cursor = Cursors.Hand;
                t.MouseLeftButtonUp += (s, e) => { graphWeek = w; graphHover = 0; RenderPanel(true); e.Handled = true; };
                DockPanel.SetDock(t, Dock.Right); head.Children.Add(t);
            }
            var hoverText = Text("", fg, 11); hoverText.TextWrapping = TextWrapping.NoWrap; DockPanel.SetDock(hoverText, Dock.Left); head.Children.Add(hoverText);
            box.Children.Add(head);
            // the reading shown now continues the line to "now" when the last point is recent
            if (pts.Count > 0 && it.Pct >= 0 && now - pts[pts.Count - 1][0] <= History.Gap)
                pts.Add(new[] { now, (long)Math.Round(it.Pct * 100), pts[pts.Count - 1][2] });
            // less than half an hour recorded: a line would be a dot at the right edge
            if (pts.Count < 2 || pts[pts.Count - 1][0] - pts[0][0] < 1800) { box.Children.Add(Text(Strings.T("histEmpty"), muted, 11)); return box; }

            var c = new Canvas { Width = W, Height = H, Background = Brushes.Transparent, ClipToBounds = true };
            Func<long, double> x = t => Math.Max(0, Math.Min(W, (t - from) * nowX / span));
            Func<double, double> y = p => H - p * (H - 2) / 100.0 - 1;
            foreach (var lvl in new[] { 0.0, 50.0, 100.0 })   // 0, 50 and 100 %
                c.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = W, Y1 = y(lvl), Y2 = y(lvl), Stroke = muted, StrokeThickness = 1, Opacity = 0.55, StrokeDashArray = new DoubleCollection { 3, 3 } });
            System.Windows.Shapes.Polyline line = null; long prevT = 0, prevC = -1;
            foreach (var p in pts)
            {
                // a new piece after a gap, or where charging starts or stops (dashed)
                if (line == null || p[0] - prevT > History.Gap || p[2] != prevC)
                {
                    var carry = line != null && p[0] - prevT <= History.Gap ? line.Points[line.Points.Count - 1] : (Point?)null;
                    line = new System.Windows.Shapes.Polyline { Stroke = fg, StrokeThickness = 1.6, StrokeDashArray = p[2] != 0 ? new DoubleCollection { 2, 1.5 } : null,
                                                                StrokeLineJoin = PenLineJoin.Round };
                    if (carry.HasValue) line.Points.Add(carry.Value);
                    c.Children.Add(line);
                }
                // steps: a level holds until the next reading
                if (line.Points.Count > 0) line.Points.Add(new Point(x(p[0]), line.Points[line.Points.Count - 1].Y));
                line.Points.Add(new Point(x(p[0]), y(p[1])));
                prevT = p[0]; prevC = p[2];
            }
            if (ahead > 0)
            {
                // the future: a thin line at "now", and the estimate as a dashed slope (it may run past the edge)
                c.Children.Add(new System.Windows.Shapes.Line { X1 = nowX, X2 = nowX, Y1 = 0, Y2 = H, Stroke = muted, StrokeThickness = 1, Opacity = 0.55 });
                double endX = nowX + (W - nowX) * (filling ? it.HoursToFull : it.HoursLeft) * 3600 / ahead;
                c.Children.Add(new System.Windows.Shapes.Line { X1 = nowX, Y1 = y(it.Pct * 100), X2 = endX, Y2 = y(filling ? 100 : 0), Stroke = muted, StrokeThickness = 1.4, StrokeDashArray = new DoubleCollection { 3, 2 } });
            }
            // the pointer: a thin line, and "14:30 - 72 %" above the graph (the reading at that time, or the estimate)
            var cursor = new System.Windows.Shapes.Line { Y1 = 0, Y2 = H, Stroke = fg, StrokeThickness = 1, Opacity = 0.5, Visibility = Visibility.Collapsed };
            c.Children.Add(cursor);
            Func<double, long> timeAt = px => px <= nowX ? from + (long)(px / nowX * span) : now + (long)((px - nowX) / Math.Max(1, W - nowX) * ahead);
            Action<long> show = t =>
            {
                bool future = t > now && ahead > 0;
                // a time past now kept from before a redraw that has no future part any more (it stopped charging)
                if (t > now && ahead == 0) { cursor.Visibility = Visibility.Collapsed; hoverText.Text = ""; return; }
                long[] at = null;
                foreach (var p in pts) { if (p[0] <= t) at = p; else break; }
                if (!future && (at == null || t - at[0] > History.Gap)) { cursor.Visibility = Visibility.Collapsed; hoverText.Text = ""; return; }
                var when = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;
                string clock = graphWeek ? when.ToString("ddd HH:mm", Strings.Culture) : when.ToString("HH:mm", Strings.Culture);
                if (future)
                {
                    double p0 = it.Pct * 100, f = (t - now) / ((filling ? it.HoursToFull : it.HoursLeft) * 3600);
                    double lvl = filling ? p0 + (100 - p0) * f : p0 * (1 - f);
                    hoverText.Text = clock + " - ~" + Strings.T("percent", (int)Math.Max(0, Math.Min(100, Math.Round(lvl))));
                }
                else hoverText.Text = clock + " - " + Strings.T("percent", at[1]) + (at[2] != 0 ? " " + Strings.T("shortCharging").Trim() : "");
                double cx = future ? nowX + (t - now) * (W - nowX) / ahead : x(t);
                cursor.X1 = cursor.X2 = cx; cursor.Visibility = Visibility.Visible;
            };
            c.MouseMove += (s, e) => { graphHover = timeAt(e.GetPosition(c).X); show(graphHover); };
            c.MouseLeave += (s, e) => { graphHover = 0; cursor.Visibility = Visibility.Collapsed; hoverText.Text = ""; };
            if (graphHover > 0) show(graphHover);   // the panel was redrawn under the pointer
            box.Children.Add(c);
            var foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nowX) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Text(Strings.T(graphWeek ? "hist7dAgo" : "hist24hAgo"), muted, 10); l.HorizontalAlignment = HorizontalAlignment.Left; foot.Children.Add(l);
            var r = Text(Strings.T("histNow"), muted, 10); r.HorizontalAlignment = HorizontalAlignment.Right; foot.Children.Add(r);
            if (ahead > 0)
            {
                var f = Text(Strings.T(graphWeek ? "histAhead7d" : "histAhead24h"), muted, 10); f.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(f, 1); foot.Children.Add(f);
            }
            box.Children.Add(foot);
            return box;
        }

        // a device's options, below it in the panel
        void DeviceActions(StackPanel root, PanelItem it)
        {
            var box = new StackPanel { Margin = new Thickness(14, 0, 0, 6) };
            box.Children.Add(MenuRow("E8AC", Strings.T("rename"), () => { CloseSide(); renaming = it.Id; RenderPanel(true); }));
            if (it.OwnName != null && it.OwnName != it.Label)
                box.Children.Add(MenuRow("E7A7", Strings.T("resetName"), () => host.Rename(it.Id, null)));
            // the icon and the low battery level are picked in the window beside the panel, so the panel stays short
            box.Children.Add(MenuRow("E790", Strings.T("iconOpt", it.IconChoice == null ? Strings.T("iconAuto") : KindLabel(it.IconChoice)),
                                     () => ToggleSide("icon", it), false, null, sideOpen && sideKind == "icon", "E76C"));
            if (it.Pct >= 0)
            {
                string own; Config.Map("plugins.gadgets.lowLevels").TryGetValue(it.Id, out own);
                int general = (int)Config.Num("plugins.gadgets.lowThreshold", 15);
                string cur = own != null ? Strings.T("percent", own) : Strings.T("lowGeneral", Strings.T("percent", general));
                box.Children.Add(MenuRow("EBA0", Strings.T("deviceLow", cur), () => ToggleSide("low", it), false, null, sideOpen && sideKind == "low", "E76C"));
            }
            box.Children.Add(MenuRow("ED1A", Strings.T("hide"), () => { CloseSide(); actionsFor = null; host.Hide(it.Id, it.OwnName ?? it.Label); }));
            root.Children.Add(box);
        }

        // The icon column (26 px) is there when the row has an icon, or "indent" keeps it empty: rows without icons line
        // up with the ones that have one (the icon picker) or sit inside the row above (hidden devices). Lists with no
        // icons at all (Preferences, the languages) start at the edge, level with the counters.
        Border MenuRow(string icon, string text, Action action, bool check = false, string sub = null, bool marked = false, string tail = null, bool indent = false)
        {
            var b = new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                                 Style = marked ? rowStyleMarked : rowStyle };   // no local Background: it would override the trigger
            var g = new Grid();
            foreach (var w in new[] { new GridLength(icon != null || indent ? 26 : 0), new GridLength(1, GridUnitType.Star), GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
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
            root.Children.Add(Separator());
            // Preferences open in their own window beside the menu
            root.Children.Add(MenuRow("E774", Strings.T("language"), () => ToggleSide("lang"), false, null, sideOpen && sideKind == "lang", "E76C"));
            root.Children.Add(MenuRow("E713", Strings.T("prefs"), () => ToggleSide("prefs"), false, null, sideOpen && sideKind == "prefs", "E76C"));
            // devices hidden from the panel: shown again from here
            var hidden = Config.Map("plugins.gadgets.hidden");
            if (hidden.Count > 0)
            {
                root.Children.Add(MenuRow("ED1A", Strings.T("hiddenDevices", hidden.Count), () => { hiddenOpen = !hiddenOpen; Refresh(); }, false, null, false, hiddenOpen ? "E70E" : "E70D"));
                if (hiddenOpen)
                    foreach (var kv in hidden.OrderBy(x => x.Value))
                    {
                        var id = kv.Key;
                        root.Children.Add(MenuRow(null, Strings.T("showAgain", kv.Value), () => { host.Unhide(id); Refresh(); }, false, null, false, null, true));
                    }
            }
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

        Border Separator() { return new Border { Height = 1, Background = track, Margin = new Thickness(4, 5, 4, 5) }; }

        // a small - or + of a counter row
        Border Step(string glyph, Action action)
        {
            var b = new Border { Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Style = rowStyle, VerticalAlignment = VerticalAlignment.Center };
            b.Child = Glyph(glyph, fg, 11);
            b.MouseLeftButtonUp += (s, e) => { try { action(); } catch (Exception ex) { Log.Write("prefs: " + ex); } };
            return b;
        }

        // A short note below each setting that needs one (the owner may not want them: false hides them all)
        static readonly bool PrefNotes = true;
        static string Note(string key) { return PrefNotes ? Strings.T(key) : null; }

        // "label   -  value  +", with an optional note below the label
        Grid Counter(string label, string value, Action minus, Action plus, string note = null)
        {
            var g = new Grid { Margin = new Thickness(10, 6, 4, 6) };   // level with the rows below: their text starts 10 px in
            foreach (var w in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(76), GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            UIElement l = Text(label, fg, 13);
            if (!string.IsNullOrEmpty(note)) { var two = new StackPanel(); two.Children.Add(l); two.Children.Add(Text(note, muted, 11)); l = two; }
            ((FrameworkElement)l).VerticalAlignment = VerticalAlignment.Center; g.Children.Add(l);
            var m = Step("E738", minus); Grid.SetColumn(m, 1); g.Children.Add(m);
            var v = Text(value, fg, 13); v.TextAlignment = TextAlignment.Center; v.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(v, 2); g.Children.Add(v);
            var p = Step("E710", plus); Grid.SetColumn(p, 3); g.Children.Add(p);
            return g;
        }

        static string IntervalText(int s) { return s < 60 ? Strings.T("seconds", s) : Strings.T("minutes", s / 60); }

        // the Preferences window: every setting, each change at once (the window stays open)
        StackPanel BuildPrefs()
        {
            var root = new StackPanel();
            Action redo = () => Refresh();
            root.Children.Add(Counter(Strings.T("interval"), IntervalText(BatteryReader.PollSeconds),
                () => { host.Step("plugins.gadgets.interval", Host.Intervals, 30, -1); redo(); }, () => { host.Step("plugins.gadgets.interval", Host.Intervals, 30, 1); redo(); }, Note("intervalNote")));
            root.Children.Add(Counter(Strings.T("lowAlert"), Strings.T("percent", (int)Config.Num("plugins.gadgets.lowThreshold", 15)),
                () => { host.Step("plugins.gadgets.lowThreshold", Host.LowLevels, 15, -1); redo(); }, () => { host.Step("plugins.gadgets.lowThreshold", Host.LowLevels, 15, 1); redo(); }, Note("lowAlertNote")));
            // a switch; "on" shows the check (inverted: the setting is the opposite of the text, e.g. monochrome)
            Func<string, string, bool, string, bool, Border> toggle = (text, path, def, note, inverted) =>
                MenuRow(null, text, () => { host.Toggle(path, def); redo(); }, Config.Bool(path, def) != inverted, Note(note));
            root.Children.Add(toggle(Strings.T("timeLeftOpt"), "plugins.gadgets.timeLeft", true, "timeLeftNote", false));
            root.Children.Add(toggle(Strings.T("quietGaming"), "quietWhileGaming", true, "quietGamingNote", false));
            root.Children.Add(toggle(Strings.T("lowSound"), "lowSound", false, "lowSoundNote", false));
            root.Children.Add(toggle(Strings.T("fullNotify"), "fullNotify", true, "fullNotifyNote", false));
            root.Children.Add(toggle(Strings.T("limitNotify"), "limitNotify", false, "limitNotifyNote", false));
            root.Children.Add(Separator());
            root.Children.Add(toggle(Strings.T("bluetoothOpt"), "plugins.gadgets.bluetooth", true, "bluetoothNote", false));
            root.Children.Add(toggle(Strings.T("pinIcon"), "alwaysShowInTray", true, "pinIconNote", false));
            root.Children.Add(toggle(Strings.T("separateIcons"), "plugins.gadgets.combine", true, "separateIconsNote", true));
            root.Children.Add(toggle(Strings.T("iconPercent"), "iconPercent", false, "iconPercentNote", false));
            root.Children.Add(toggle(Strings.T("colorIcon"), "monochrome", true, "colorIconNote", true));
            root.Children.Add(toggle(Strings.T("chargeAnim"), "chargeAnimation", true, "chargeAnimNote", false));
            root.Children.Add(toggle(Strings.T("openAnim"), "openAnimation", true, "openAnimNote", false));
            // the theme: automatic -> light -> dark. Each has a note, so the row (and the window) keeps its height: a
            // window that grows while it moves up shows bits of its old picture below for a moment, and from dark to
            // automatic (dark too) no colour fade hides that
            var mode = Config.Str("theme.mode", "auto").ToLowerInvariant();
            string next = mode == "light" ? "dark" : mode == "dark" ? "auto" : "light";
            root.Children.Add(MenuRow(null, Strings.T("themeOpt", Strings.T(mode == "light" ? "themeLight" : mode == "dark" ? "themeDark" : "themeAuto")),
                () => ThemeChange(next), false, Strings.T(mode == "light" ? "themeLightSub" : mode == "dark" ? "themeDarkSub" : "themeAutoSub")));
            root.Children.Add(Separator());
            root.Children.Add(toggle(Strings.T("statusFileOpt"), "statusFile", false, "statusFileNote", false));
            root.Children.Add(MenuRow(null, Strings.T("startWithWindows"), () => { host.ToggleAutostart(); redo(); }, host.Autostart));
            if (Config.Str("update.repo", "") != "")
            {
                root.Children.Add(toggle(Strings.T("autoUpdate"), "update.check", true, "autoUpdateNote", false));
                root.Children.Add(toggle(Strings.T("beta"), "update.beta", false, "betaNote", false));
            }
            return root;
        }

        public void Dispose() { try { win.Close(); } catch { } }
    }
}
