// Canvas.cs — hlavná plocha: kreslenie (statická vrstva v cache + dynamická), myš, klávesnica, krok.
// Nezávisí od okna — rovnako kreslí do okna aj do PNG pri --snapshot.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using static Regula.Gfx;
using static Regula.U;

namespace Regula
{
    sealed class Canvas
    {
        public readonly Dictionary<string, Tool> Tools;
        public string ToolName { get; private set; } = "cm";
        public Tool Tool => Tools[ToolName];
        public string Bg = "light";
        public V? Hover;                // poloha kurzora po prichytení na krok
        public V? Raw;                  // skutočná poloha kurzora (na hľadanie bodov a úchytov)
        public int SnapLevel { get; private set; }

        public readonly Dock Dock = new Dock();
        public readonly ShowDock ShowDock = new ShowDock();
        public readonly InputBar InputBar;
        PopupMenu popup;
        Calibration calibration;
        bool toolDragging;
        float panelTop;                 // horný okraj panela (nad ním je riadok s nápovedou)
        Surface staticSurf, chromeSurf;     // statická vrstva nástroja; to isté + panel a nápoveda
        string staticKey, chromeKey;
        RectangleF hintRect;
        float? lastPPM;
        int? tipIndex;
        bool tipShown;
        readonly Timer tipTimer = new Timer { Interval = 650 };
        readonly Timer caretTimer = new Timer { Interval = 530 };

        // okolie: nastaví ho okno (alebo --snapshot)
        public Display Display;
        public float PxScale = 1;       // pixely na bod
        public float W, H;              // veľkosť plochy v bodoch
        public Action Redraw = () => { };
        public Action<Cursor> SetCursor = _ => { };
        public Action OnToggleFullscreen = () => { };
        public Action OnQuit = () => { };
        public Func<List<PopupMenu.Entry>> MenuEntries = () => new List<PopupMenu.Entry>();

        public float Ppm => PtPerMM(Display);
        public bool ShiftDown => (Control.ModifierKeys & Keys.Shift) != 0;
        public bool Calibrating => calibration != null;

        public Canvas(Display display, float scale, float W, float H)
        {
            Display = display;
            PxScale = scale;
            Gfx.Scale = scale;
            this.W = W;
            this.H = H;
            InputBar = new InputBar(this);
            Tools = new Dictionary<string, Tool>
            {
                ["cm"] = new RulerTool(this, "cm"), ["in"] = new RulerTool(this, "in"), ["px"] = new RulerTool(this, "px"),
                ["dual"] = new RulerTool(this, "dual"), ["grid"] = new RulerTool(this, "cm", true),
                ["prot"] = new ProtractorTool(this, false), ["prot360"] = new ProtractorTool(this, true),
                ["circle"] = new CircleTool(this), ["radius"] = new RadiusTool(this),
                ["diag"] = new DiagonalTool(this), ["path"] = new PathTool(this),
                ["screw"] = new ScrewTool(this), ["pitch"] = new PitchTool(this),
            };
            Bg = THEMES.ContainsKey(Settings.Bg) ? Settings.Bg : "light";
            SnapLevel = Settings.Snap >= 0 && Settings.Snap <= 3 ? Settings.Snap : 0;
            BuildDock();
            tipTimer.Tick += (s, e) =>
            {
                tipTimer.Stop();
                tipShown = tipIndex != null;
                Redraw();
            };
            caretTimer.Tick += (s, e) =>
            {
                if (InputBar.Hidden) return;
                InputBar.CaretOn = !InputBar.CaretOn;
                Redraw();
            };
            caretTimer.Start();
            SetTool(Settings.Tool);
            lastPPM = Ppm;
        }

        // MARK: - panel

        void BuildDock()
        {
            var items = new List<Dock.Item>();
            foreach (var (key, title) in TOOLS)
            {
                items.Add(new Dock.Item
                {
                    Id = key, Title = title, IsTool = true, SeparatorBefore = GROUP_STARTS.Contains(key),
                    Tooltip = $"{title}  (Tab / Shift+Tab = {L("ďalší / predošlý", "next / previous")})",
                    Action = _ => SetTool(key),
                });
            }
            items.Add(new Dock.Item { Id = "snap", Title = L("Voľne", "Free"), SeparatorBefore = true, Action = r => OpenPopup(SnapMenu(), r) });
            items.Add(new Dock.Item { Id = "type", Title = L("Zadať", "Type"), Action = _ => InputBar.Open("") });
            items.Add(new Dock.Item
            {
                Id = "bg", Title = BG_NAMES.Get(Bg, ""), Action = _ => CycleBg(),
                Tooltip = L("Pozadie: svetlé / tmavé / zelené / priehľadné (B)", "Background: light / dark / green / clear (B)"),
            });
            items.Add(new Dock.Item
            {
                Id = "calib", Title = L("Kalibrácia", "Calibrate"), Action = _ => Calibrate(),
                Tooltip = L("Dokalibrovať mierku kartou (K)", "Calibrate the scale with a card (K)"),
            });
            items.Add(new Dock.Item
            {
                Id = "window", Title = L("Okno", "Window"), Action = _ => OnToggleFullscreen(),
                Tooltip = L("Prepnúť okno ↔ celá obrazovka (F11) — okno sa dá zväčšovať a zmenšovať",
                            "Switch window ↔ fullscreen (F11) — the window is resizable"),
            });
            items.Add(new Dock.Item
            {
                Id = "more", Title = L("Menu", "Menu"), Action = r => OpenPopup(MenuEntries(), r),
                Tooltip = L("Jazyk, ďalší monitor, ponuka Štart… (aj pravé tlačidlo myši)",
                            "Language, next display, Start menu… (or right-click)"),
            });
            items.Add(new Dock.Item { Id = "hide", Title = L("Skryť", "Hide"), Tooltip = L("Skryť panel (H)", "Hide the panel (H)"), Action = _ => ToggleDock() });
            items.Add(new Dock.Item { Id = "close", Title = L("Zavrieť", "Close"), Tooltip = L("Zavrieť (Q)", "Close (Q)"), Action = _ => OnQuit() });
            Dock.Items = items;
            ShowDock.Title = L("▴  Pravítka", "▴  Tools");
        }

        List<PopupMenu.Entry> SnapMenu()
        {
            var menu = new List<PopupMenu.Entry>();
            var family = SnapFamily();
            if (family == null) return menu;
            var options = new[] { L("Voľne (bez kroku)", "Free (no snapping)") }.Concat(SNAP[family].Select(s => $"{L("Krok", "Snap")} {s.Item2}")).ToList();
            for (int level = 0; level < options.Count; level++)
            {
                int lv = level;
                menu.Add(new PopupMenu.Entry { Title = options[level], Checked = level == SnapLevel, Action = () => SetSnap(lv) });
            }
            menu.Add(new PopupMenu.Entry { Separator = true });
            menu.Add(new PopupMenu.Entry { Title = L("S = prepínanie", "S = cycle"), Note = true });
            return menu;
        }

        void OpenPopup(List<PopupMenu.Entry> entries, RectangleF anchor)
        {
            if (entries.Count == 0) return;
            popup = PopupMenu.Above(anchor, entries, W);
            HideTip();
            Redraw();
        }

        public void OpenMenuAt(V p)
        {
            popup = PopupMenu.At(p, MenuEntries(), W, H);
            HideTip();
            Redraw();
        }

        /// Najmenšie okno: panel len s ikonkami musí ostať celý.
        public SizeF MinimumSize()
        {
            bool compact = Dock.Compact;
            float width = Dock.CompactWidth;
            Dock.Compact = true;
            Dock.CompactWidth = 26;
            float w = Dock.PanelSize().Width;
            Dock.Compact = compact;
            Dock.CompactWidth = width;
            return new SizeF(w + 32, 520);
        }

        public void LayoutPanels()
        {
            Dock.Compact = false;
            Dock.CompactWidth = 40;
            if (Dock.PanelSize().Width > W - 32)
            {
                Dock.Compact = true;
                float over = Dock.PanelSize().Width - (W - 32);
                if (over > 0) Dock.CompactWidth = Math.Max(26, 40 - over / Dock.Items.Count);
            }
            var ps = Dock.PanelSize();
            var panel = new RectangleF(Rnd((W - ps.Width) / 2), H - ps.Height - 14, ps.Width, ps.Height);
            Dock.Layout(panel);
            panelTop = panel.Top;
            var ss = ShowDock.IdealSize();
            ShowDock.Frame = new RectangleF(Rnd((W - ss.Width) / 2), H - ss.Height - 10, ss.Width, ss.Height);
            var ib = InputBar.IdealSize();
            float above = Dock.Hidden ? ShowDock.Frame.Top : panel.Top;
            InputBar.Frame = new RectangleF(Rnd((W - ib.Width) / 2), above - ib.Height + InputBar.Margin - 12, ib.Width, ib.Height);
        }

        /// Nová veľkosť plochy alebo iný monitor: merania ostanú v rovnakých mm, nie v rovnakých bodoch.
        public void SetGeometry(Display display, float scale, float w, float h)
        {
            bool resized = Math.Abs(w - W) > 0.01 || Math.Abs(h - H) > 0.01;
            Display = display;
            PxScale = scale;
            Gfx.Scale = scale;
            W = w;
            H = h;
            LayoutPanels();
            if (resized) foreach (var t in Tools.Values) t.Relayout();
            float now = Ppm;
            if (lastPPM is float old && Math.Abs(now / old - 1) > 1e-6)
            {
                foreach (var t in Tools.Values) t.Rescale(now / old);
                Hover = null;
                Raw = null;
            }
            lastPPM = now;
            Redraw();
        }

        // MARK: - stav

        public void SetTool(string name)
        {
            ToolName = Tools.ContainsKey(name) ? name : "cm";
            Settings.Tool = ToolName;
            InputBar.Hidden = true;
            Dock.Check(ToolName);
            bool fields = Tool.InputFields.Length > 0;
            Dock.Update("type", enabled: fields,
                        tooltip: fields ? L("Zadať presný rozmer z klávesnice (alebo rovno píš čísla)", "Type an exact size (or just start typing numbers)")
                                        : L("Tento nástroj nemá čo zadať", "Nothing to type for this tool"));
            UpdateSnapButton();
            Redraw();
        }

        public void StepTool(int delta)
        {
            var keys = TOOLS.Select(t => t.Item1).ToList();
            int i = Math.Max(0, keys.IndexOf(ToolName));
            SetTool(keys[(i + delta + keys.Count) % keys.Count]);
        }

        public void ResetTool()
        {
            if (!InputBar.Hidden)
            {
                InputBar.Close();
                return;
            }
            Tool.Reset();
            Redraw();
        }

        public void SetBackground(string name)
        {
            Bg = THEMES.ContainsKey(name) ? name : "light";
            Settings.Bg = Bg;
            Dock.Update("bg", title: BG_NAMES.Get(Bg, ""));
            LayoutPanels();
            Redraw();
        }

        public void CycleBg()
        {
            int i = Math.Max(0, Array.IndexOf(THEME_ORDER, Bg));
            SetBackground(THEME_ORDER[(i + 1) % THEME_ORDER.Length]);
        }

        public void ToggleDock()
        {
            Dock.Hidden = !Dock.Hidden;
            HideTip();
            LayoutPanels();
            Redraw();
        }

        public void Calibrate()
        {
            InputBar.Hidden = true;
            popup = null;
            HideTip();
            Hover = null;
            Raw = null;
            calibration = new Calibration(this, Display);
            Redraw();
        }

        public void EndCalibration()
        {
            calibration = null;
            lastPPM = Ppm;              // kalibrácia meria to isté miesto znova — body sa neprepočítavajú
            Redraw();
        }

        // MARK: - krok (prichytávanie)

        public string SnapFamily()
        {
            switch (Tool.SnapKind)
            {
                case SnapKind.Angle: return "deg";
                case SnapKind.Point: return Tool.Unit == "in" || Tool.Unit == "px" ? Tool.Unit : "mm";
                default: return null;
            }
        }

        /// Krok v jednotke nástroja (mm, palce, px, stupne) alebo null = voľne.
        public double? SnapValue()
        {
            var family = SnapFamily();
            if (family == null || SnapLevel == 0) return null;
            return SNAP[family][SnapLevel - 1].Item1;
        }

        /// Krok v bodoch obrazovky (len pre body, nie uhly).
        float? SnapPt()
        {
            var family = SnapFamily();
            if (!(SnapValue() is double value) || family == "deg") return null;
            if (family == "px") return (float)value / PxScale;
            return (float)value * Ppm * (family == "in" ? MM_PER_IN : 1);
        }

        public V ToolPos(V raw)
        {
            if (Tool.SnapKind != SnapKind.Point || !(SnapPt() is float s)) return raw;
            return new V(Rnd(raw.X / s) * s, Rnd(raw.Y / s) * s);
        }

        public void SetSnap(int level)
        {
            SnapLevel = level;
            Settings.Snap = level;
            UpdateSnapButton();
            Redraw();
        }

        public void CycleSnap()
        {
            if (SnapFamily() != null) SetSnap((SnapLevel + 1) % 4);
        }

        void UpdateSnapButton()
        {
            var family = SnapFamily();
            if (family != null)
            {
                Dock.Update("snap", SnapLevel > 0 ? $"{L("Krok", "Snap")} {SNAP[family][SnapLevel - 1].Item2}" : L("Voľne", "Free"), true,
                            L("Krok pohybu — body skáču na presné hodnoty, napr. 20 × 50 mm (S)",
                              "Snapping — points jump to exact values, e.g. 20 × 50 mm (S)"));
            }
            else
            {
                Dock.Update("snap", L("Krok –", "Snap –"), false, L("Tento nástroj krok nepoužíva", "This tool doesn't snap"));
            }
            LayoutPanels();
        }

        // MARK: - zadávanie z klávesnice

        /// Odkiaľ začne zadaný rozmer: od kurzora, inak z ľavej hornej tretiny.
        public V InputOrigin()
        {
            var p = Raw ?? new V(-1, -1);
            if (!(p.X >= 0 && p.X < W && p.Y >= 0 && p.Y < H - DOCK_RESERVE)) p = new V(Rnd(W * 0.3f), Rnd(H * 0.3f));
            return ToolPos(p);
        }

        /// Posun, aby sa zadaný rozmer zmestil do okna (ak sa dá).
        public V FitShift(IList<V> pts)
        {
            float w = W - 20, h = H - DOCK_RESERVE - 20;
            float maxX = pts.Max(p => p.X), minX = pts.Min(p => p.X), maxY = pts.Max(p => p.Y), minY = pts.Min(p => p.Y);
            float dx = maxX > w ? w - maxX : Math.Max(0, 60 - minX);
            float dy = maxY > h ? h - maxY : Math.Max(0, 60 - minY);
            return new V(dx, dy);
        }

        // MARK: - myš

        bool OverPanels(V p) =>
            (!Dock.Hidden && Dock.Panel.Has(p)) || InputBar.Contains(p) || (Dock.Hidden && ShowDock.Frame.Has(p));

        void HideTip()
        {
            tipTimer.Stop();
            tipIndex = null;
            tipShown = false;
        }

        void HoverDock(V? p)
        {
            int? i = p is V q && !Dock.Hidden ? Dock.IndexAt(q) : null;
            if (i == Dock.Hovered) return;
            Dock.Hovered = i;
            bool wasShown = tipShown;
            HideTip();
            tipIndex = i;
            if (i != null)
            {
                if (wasShown) tipShown = true;          // bublinky idú hneď za sebou, keď už jedna bola
                else tipTimer.Start();
            }
            Redraw();
        }

        public void MouseMove(V p, bool leftDown)
        {
            if (calibration != null)
            {
                calibration.Move(p);
                SetCursor(Cursors.Default);
                return;
            }
            if (popup != null)
            {
                if (popup.Move(p)) Redraw();
                SetCursor(Cursors.Default);
                return;
            }
            if (leftDown && toolDragging)
            {
                Raw = p;
                var pos = ToolPos(p);
                Hover = pos;
                Tool.Drag(pos, ShiftDown);
                Redraw();
                return;
            }
            bool showHot = Dock.Hidden && ShowDock.Frame.Has(p);
            if (showHot != ShowDock.Hovered)
            {
                ShowDock.Hovered = showHot;
                Redraw();
            }
            if (OverPanels(p))                  // nitkový kríž nemá ostať visieť pod panelom
            {
                Hover = null;
                Raw = null;
                SetCursor(Cursors.Default);
                HoverDock(p);
                Redraw();
                return;
            }
            HoverDock(null);
            Raw = p;
            Hover = ToolPos(p);
            SetCursor(Tool.Cursor(p));
            Redraw();
        }

        public void MouseLeave()
        {
            if (toolDragging) return;
            Hover = null;
            Raw = null;
            ShowDock.Hovered = false;
            HoverDock(null);
            Redraw();
        }

        public void MouseDown(V p, MouseButtons button, int clicks)
        {
            if (calibration != null)
            {
                if (button == MouseButtons.Left) calibration.Press(p);
                return;
            }
            if (popup != null)
            {
                var action = popup.Frame.Has(p) ? popup.Click(p) : null;
                if (!popup.Frame.Has(p) || action != null)
                {
                    popup = null;
                    Redraw();
                    action?.Invoke();
                }
                return;
            }
            HideTip();
            if (InputBar.Contains(p))
            {
                InputBar.Click(p);
                return;
            }
            if (!Dock.Hidden && Dock.Panel.Has(p))
            {
                if (button == MouseButtons.Left && Dock.IndexAt(p) is int i && Dock.Items[i].Enabled) Dock.Pressed = i;
                Redraw();
                return;
            }
            if (Dock.Hidden && ShowDock.Frame.Has(p))
            {
                ToggleDock();
                return;
            }
            if (button == MouseButtons.Right)
            {
                OpenMenuAt(p);
                return;
            }
            if (button != MouseButtons.Left) return;
            if (!InputBar.Hidden) InputBar.Close();
            Raw = p;
            var pos = ToolPos(p);
            toolDragging = true;
            if (clicks >= 2) Tool.Double(pos, ShiftDown);
            else Tool.Press(pos, ShiftDown);
            SetCursor(Tool.Cursor(p));
            Redraw();
        }

        public void MouseUp(V p, MouseButtons button)
        {
            if (calibration != null)
            {
                if (button == MouseButtons.Left) calibration.Release(p);
                return;
            }
            if (Dock.Pressed is int i)
            {
                Dock.Pressed = null;
                Redraw();
                if (Dock.IndexAt(p) == i) Dock.Items[i].Action(Dock.RectOf(i));
                return;
            }
            if (!toolDragging || button != MouseButtons.Left) return;
            toolDragging = false;
            Raw = p;
            var pos = ToolPos(p);
            Tool.Release(pos);
            SetCursor(Tool.Cursor(p));
            Redraw();
        }

        public void Wheel(float steps)
        {
            if (calibration != null || popup != null || steps == 0) return;
            Tool.Wheel(steps);
            Redraw();
        }

        // MARK: - klávesnica

        /// Klávesy s významom (šípky, Tab, Enter, Esc, Backspace, F11, Ctrl+V); true = spotrebované.
        public bool KeyDown(Keys key, bool shift, bool ctrl)
        {
            if (calibration != null)
            {
                calibration.Key(key, shift);
                return true;
            }
            if (popup != null)
            {
                if (key == Keys.Escape)
                {
                    popup = null;
                    Redraw();
                    return true;
                }
                var action = popup.Key(key);
                if (action != null)
                {
                    popup = null;
                    action();
                }
                Redraw();
                return true;
            }
            if (ctrl && key == Keys.V)
            {
                string text = "";
                try { text = Clipboard.GetText(); }
                catch (Exception) { }
                if (InputBar.Hidden && Tool.InputFields.Length > 0) InputBar.Open("");
                if (!InputBar.Hidden) InputBar.Paste(text);
                return true;
            }
            if (ctrl) return false;
            if (!InputBar.Hidden)
            {
                if (key == Keys.F11)
                {
                    OnToggleFullscreen();
                    return true;
                }
                if (InputBar.Key(key, shift)) return true;
                return key == Keys.Up || key == Keys.Down;      // šípky v políčku nehýbu meraním; znaky idú cez KeyChar
            }
            switch (key)
            {
                case Keys.Tab:
                    StepTool(shift ? -1 : 1);
                    return true;
                case Keys.Escape:
                    ResetTool();
                    return true;
                case Keys.Enter:
                    if (Tool.Key(ToolKey.Enter)) Redraw();
                    else if (Tool.InputFields.Length > 0) InputBar.Open("");
                    return true;
                case Keys.Back:
                case Keys.Delete:
                    if (Tool.Key(ToolKey.Backspace)) Redraw();
                    return true;
                case Keys.F11:
                    OnToggleFullscreen();
                    return true;
                case Keys.Apps:
                    OpenMenuAt(new V(W / 2, H / 2));
                    return true;
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                    {
                        // šípky: o 0,1 mm (pri pixeloch o 1 px), so Shiftom 10×
                        float step = Tool.Unit == "px" && Tool.SnapKind == SnapKind.Point ? 1 / PxScale : 0.1f * Ppm;
                        if (shift) step *= 10;
                        var d = key == Keys.Left ? new V(-step, 0) : key == Keys.Right ? new V(step, 0)
                              : key == Keys.Down ? new V(0, step) : new V(0, -step);
                        Tool.Nudge(d);
                        Redraw();
                        return true;
                    }
            }
            return false;
        }

        /// Napísaný znak (už podľa rozloženia klávesnice).
        public void KeyChar(char ch)
        {
            if (calibration != null || popup != null || char.IsControl(ch)) return;
            if (!InputBar.Hidden)
            {
                InputBar.Char(ch);
                return;
            }
            if (Tool.InputFields.Length > 0 && IsDigitKey(ch))
            {
                InputBar.Open(ch.ToString());
                return;
            }
            switch (char.ToLowerInvariant(ch))
            {
                case 'h': ToggleDock(); break;
                case 'b': CycleBg(); break;
                case 'k': Calibrate(); break;
                case 's': CycleSnap(); break;
                case 'q': OnQuit(); break;
            }
        }

        // MARK: - kreslenie

        /// Celá plocha do target (pixely). Statická vrstva a panel sú v cache; pri pohybe myši sa kreslí len meranie.
        public void Render(Surface target)
        {
            float s = PxScale;
            Gfx.Scale = s;
            var th = THEMES[Bg];
            float ppm = Ppm;
            bool gdiText = !th.Glass;       // ClearType potrebuje nepriehľadné pozadie
            int pw = target.Width, ph = target.Height;
            Tool.Layout(W, H);

            var skey = $"{ToolName}|{pw}x{ph}|{ppm}|{Bg}|{s}|{Tool.StaticKey()}";
            if (staticSurf == null || staticSurf.Width != pw || staticSurf.Height != ph)
            {
                staticSurf?.Dispose();
                staticSurf = new Surface(pw, ph);
                staticKey = null;
            }
            if (staticKey != skey)          // statická vrstva sa kreslí len pri zmene, nie pri každom pohybe myši
            {
                Paint(staticSurf, th, gdiText, g =>
                {
                    g.Clear(th.Bg);
                    Tool.PaintStatic(new G { g = g, W = W, H = H, th = th, ppm = ppm, px = s });
                });
                staticKey = skey;
                chromeKey = null;
            }

            bool hint = !Dock.Hidden && InputBar.Hidden && Tool.Hint != "" && calibration == null;
            if (chromeSurf == null || chromeSurf.Width != pw || chromeSurf.Height != ph)
            {
                chromeSurf?.Dispose();
                chromeSurf = new Surface(pw, ph);
                chromeKey = null;
            }
            var ckey = ChromeKey(hint);
            if (chromeKey != ckey)          // panel a nápoveda: len keď sa zmenia (prejdenie myšou, iný nástroj…)
            {
                chromeSurf.CopyFrom(staticSurf);
                Paint(chromeSurf, th, gdiText, g =>
                {
                    if (hint) DrawHint(g, th);
                    if (Dock.Hidden) ShowDock.Draw(g);
                    else Dock.Draw(g, s);
                });
                chromeKey = ckey;
            }

            target.CopyFrom(chromeSurf);
            Paint(target, th, gdiText, g =>
            {
                Tool.PaintDynamic(new G { g = g, W = W, H = H, th = th, ppm = ppm, px = s });
                g.Flush(FlushIntention.Sync);
                // panel ostane nad meraním a nitkovým krížom (ako na Macu)
                target.CopyFrom(chromeSurf, ToPx(Dock.Hidden ? ShowDock.Frame : Dock.Panel, s));
                if (hint) target.CopyFrom(chromeSurf, ToPx(hintRect, s));
                if (!InputBar.Hidden) InputBar.Draw(g, s);
                if (tipShown && tipIndex is int i && !Dock.Hidden && popup == null && i < Dock.Items.Count)
                    Tip.Draw(g, Dock.TipFor(i), new V(Dock.RectOf(i).MidX(), Dock.Panel.Top), W);
                popup?.Draw(g, s);
                calibration?.Draw(g, W, H, s);
            });
        }

        void Paint(Surface surface, Theme th, bool gdiText, Action<Graphics> body)
        {
            using (var g = Graphics.FromImage(surface.Bitmap))
            {
                Gfx.TextSurface = gdiText ? surface : null;
                Prepare(g, PxScale, th.Glass);
                body(g);
                g.Flush(FlushIntention.Sync);
            }
            Gfx.TextSurface = null;
            Native.GdiFlush();
        }

        static Rectangle ToPx(RectangleF r, float s) =>
            Rectangle.FromLTRB((int)Math.Floor(r.Left * s) - 1, (int)Math.Floor(r.Top * s) - 1,
                               (int)Math.Ceiling(r.Right * s) + 1, (int)Math.Ceiling(r.Bottom * s) + 1);

        void DrawHint(Graphics g, Theme th)
        {
            var font = UiFont(12);
            float w = TextSize(Tool.Hint, font).Width + 28;
            hintRect = new RectangleF(Rnd((W - w) / 2), panelTop - 34, w, 24);
            FillPath(g, RoundedPath(hintRect, 12), Tint(th.Panel, 235));
            TextInRect(g, Tool.Hint, hintRect, font, th.Soft);
        }

        string ChromeKey(bool hint)
        {
            var sb = new StringBuilder(staticKey);
            sb.Append('|').Append(Dock.Hidden).Append(Dock.Compact).Append(Dock.CompactWidth).Append('|').Append(Dock.Hovered)
              .Append('|').Append(Dock.Pressed).Append('|').Append(ShowDock.Hovered).Append(hint).Append(Tool.Hint)
              .Append(Dock.Panel).Append(ShowDock.Frame).Append(ShowDock.Title);
            foreach (var item in Dock.Items) sb.Append('|').Append(item.Title).Append(item.Checked).Append(item.Enabled);
            return sb.ToString();
        }
    }
}
