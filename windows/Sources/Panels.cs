// Panels.cs — panel s nástrojmi, tlačidlo na jeho návrat, políčka na zadanie čísel, menu, bublinky a ikonky.
// Všetko je kreslené priamo na plochu (žiadne ovládacie prvky Windows Forms) — ostré pri každej mierke.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using static Regula.Gfx;
using static Regula.U;

namespace Regula
{
    static class PanelStyle
    {
        public static readonly Color BG = Rgb(252, 252, 249, 245);
        public static readonly Color EDGE = Rgb(0, 0, 0, 38);
        public static readonly Color TEXT = Rgb(42, 42, 51);

        /// Svetlá zaoblená plocha s jemným tieňom (panel, políčka, menu).
        public static void Draw(Graphics g, RectangleF rect, float radius)
        {
            Shadow(g, rect, radius);
            FillPath(g, RoundedPath(rect, radius), BG);
            FillPath(g, RoundedPath(Inset(rect, 0.5f, 0.5f), radius), null, EDGE);
        }
    }

    // MARK: - panel s nástrojmi

    sealed class Dock
    {
        public sealed class Item
        {
            public string Id, Title, Tooltip = "";
            public bool IsTool, SeparatorBefore, Checked, Enabled = true;
            public Action<RectangleF> Action;
        }

        public List<Item> Items = new List<Item>();
        public bool Compact;                // na užšej obrazovke len ikonky (názov je v bublinke)
        public float CompactWidth = 40;     // pri 150–200 % mierke na malom displeji sa tlačidlá ešte zúžia
        public bool Hidden;
        public RectangleF Panel;
        public int? Hovered, Pressed;
        readonly Font font = UiFont(10);
        readonly List<RectangleF> rects = new List<RectangleF>();
        readonly List<float> separators = new List<float>();

        float Width(Item item)
        {
            if (Compact) return CompactWidth;
            return Math.Max(item.Id == "snap" ? 66 : 46, (float)Math.Ceiling(TextSize(item.Title, font).Width) + 10);
        }

        float ItemHeight => Compact ? 40 : 50;

        public SizeF PanelSize()
        {
            float seps = Items.Count(i => i.SeparatorBefore) * 11;
            float w = Items.Sum(Width) + Math.Max(0, Items.Count - 1) * 2 + seps + 16;
            return new SizeF(w, ItemHeight + 12);
        }

        public void Layout(RectangleF panel)
        {
            Panel = panel;
            rects.Clear();
            separators.Clear();
            float x = panel.X + 8;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].SeparatorBefore)
                {
                    separators.Add(x + 4);
                    x += 11;
                }
                float w = Width(Items[i]);
                rects.Add(new RectangleF(x, panel.Y + 6, w, ItemHeight));
                x += w + 2;
            }
        }

        public Item Find(string id) => Items.FirstOrDefault(i => i.Id == id);

        public void Check(string tool)
        {
            foreach (var item in Items.Where(i => i.IsTool)) item.Checked = item.Id == tool;
        }

        public void Update(string id, string title = null, bool? enabled = null, string tooltip = null)
        {
            var item = Find(id);
            if (item == null) return;
            if (title != null) item.Title = title;
            if (enabled is bool e) item.Enabled = e;
            if (tooltip != null) item.Tooltip = tooltip;
        }

        public int? IndexAt(V p)
        {
            int i = rects.FindIndex(r => r.Has(p));
            return i >= 0 ? i : (int?)null;
        }

        public RectangleF RectOf(int i) => rects[i];

        public string TipFor(int i)
        {
            var it = Items[i];
            if (it.Tooltip == "") return it.Title;
            return Compact && !it.Tooltip.StartsWith(it.Title) ? $"{it.Title} — {it.Tooltip}" : it.Tooltip;
        }

        public void Draw(Graphics g, float scale)
        {
            PanelStyle.Draw(g, Panel, 18);
            foreach (var x in separators)
                FillRect(g, new RectangleF(x, Panel.Y + 12, 1 / scale, Panel.Height - 24), PanelStyle.EDGE);
            for (int i = 0; i < Items.Count && i < rects.Count; i++)
            {
                var item = Items[i];
                var r = rects[i];
                Color? highlight = item.Checked ? Tint(ACCENT, 62)
                    : item.Enabled && (Hovered == i || Pressed == i) ? Tint(ACCENT, Pressed == i ? 45 : 28) : (Color?)null;
                if (highlight is Color h) FillPath(g, RoundedPath(r, 12), h);
                float iconY = Compact ? r.MidY() - 13 : r.Top + 5;
                Icons.Draw(g, item.Id, r.MidX() - 13, iconY, scale, item.Enabled ? 1 : 0.4f);
                if (!Compact)
                {
                    var color = !item.Enabled ? Rgb(160, 160, 170) : item.Checked ? Rgb(13, 94, 136) : PanelStyle.TEXT;
                    TextInRect(g, item.Title, new RectangleF(r.Left - 4, r.Top + 32, r.Width + 8, 14), font, color);
                }
            }
        }
    }

    /// Malé tlačidlo dole, keď je panel skrytý.
    sealed class ShowDock
    {
        public string Title = "";
        public RectangleF Frame;
        public bool Hovered;
        readonly Font font = UiFont(12);

        public SizeF IdealSize()
        {
            var s = TextSize(Title, font);
            return new SizeF((float)Math.Ceiling(s.Width) + 28, (float)Math.Ceiling(s.Height) + 10);
        }

        public void Draw(Graphics g)
        {
            var r = Inset(Frame, 0.5f, 0.5f);
            Shadow(g, r, 12, 2);
            FillPath(g, RoundedPath(r, 12), Hovered ? Rgb(240, 247, 252) : PanelStyle.BG, PanelStyle.EDGE);
            TextInRect(g, Title, Frame, font, PanelStyle.TEXT);
        }
    }

    // MARK: - políčka na zadanie čísel

    /// Políčka na presné zadanie z klávesnice: číslo, Enter, ďalšie číslo, Enter.
    sealed class InputBar
    {
        public const float Margin = 14;
        public bool Hidden = true;
        public RectangleF Frame;
        public bool CaretOn = true;
        readonly Canvas canvas;
        Field[] spec = new Field[0];
        readonly List<string> values = new List<string>();
        int focus;
        string help = "";
        bool helpError;
        // rozloženie (súradnice v rámci Frame)
        readonly List<(string, float, Font, Color)> labels = new List<(string, float, Font, Color)>();
        readonly List<RectangleF> fields = new List<RectangleF>();
        float helpX;

        static readonly Font labelFont = UiFont(13), fieldFont = UiFont(15), helpFont = UiFont(11), helpBold = UiFont(11, true);
        static readonly char[] SEPARATORS = { ' ', 'x', 'X', '×', '*' };

        public InputBar(Canvas canvas) { this.canvas = canvas; }

        public void Open(string text)
        {
            spec = canvas.Tool.InputFields;
            if (spec.Length == 0) return;
            values.Clear();
            values.AddRange(spec.Select(_ => ""));
            values[0] = ToDigits(text);
            focus = 0;
            Say(spec.Length > 1 ? L("Enter = ďalšie políčko, potom použiť · Esc = zrušiť", "Enter = next field, then apply · Esc = cancel")
                                : L("Enter = použiť · Esc = zrušiť", "Enter = apply · Esc = cancel"));
            Hidden = false;
            CaretOn = true;
            canvas.LayoutPanels();
            canvas.Redraw();
        }

        /// Len pre --snapshot: vyplní políčka bez písania.
        public void Prefill(params string[] texts)
        {
            for (int i = 0; i < texts.Length && i < values.Count; i++) values[i] = texts[i];
            focus = Math.Min(texts.Length, values.Count) - 1;
        }

        public SizeF IdealSize()
        {
            labels.Clear();
            fields.Clear();
            float mid = Margin + 22;
            float x = Margin + 16;
            for (int i = 0; i < spec.Length; i++)
            {
                labels.Add((spec[i].Name, x, labelFont, PanelStyle.TEXT));
                x += TextSize(spec[i].Name, labelFont).Width + 4 + 8;
                fields.Add(new RectangleF(x, mid - 13, 110, 26));
                x += 118;
                labels.Add((spec[i].Unit, x, labelFont, Rgb(90, 90, 102)));
                x += TextSize(spec[i].Unit, labelFont).Width + 4 + 18;
            }
            helpX = x;
            x += TextSize(help, helpError ? helpBold : helpFont).Width + 4 + 16;
            return new SizeF(x + Margin, 44 + 2 * Margin);
        }

        public void Draw(Graphics g, float scale)
        {
            var o = new V(Frame.X, Frame.Y);
            float mid = o.Y + Margin + 22;
            PanelStyle.Draw(g, Inset(Frame, Margin, Margin), 16);
            foreach (var (text, x, font, color) in labels)
                DrawText(g, text, o.X + x, mid + font.Size * 0.36f, font, color);
            for (int i = 0; i < fields.Count; i++)
            {
                var r = fields[i];
                r.Offset(o.X, o.Y);
                bool focused = i == focus;
                FillPath(g, RoundedPath(r, 6), WHITE, focused ? ACCENT : Rgb(0, 0, 0, 60), focused ? 2 : 1);
                float baseline = r.MidY() + fieldFont.Size * 0.36f;
                if (values[i] == "" && spec[i].Optional)
                    DrawText(g, L("nepovinné", "optional"), r.Left + 8, baseline, fieldFont, Rgb(170, 170, 178));
                var shown = values[i];
                DrawText(g, shown, r.Left + 8, baseline, fieldFont, INK);
                if (focused && CaretOn)
                {
                    float cx = r.Left + 8 + TextSize(shown, fieldFont).Width + 1;
                    FillRect(g, new RectangleF(cx, r.Top + 5, 1.2f, r.Height - 10), INK);
                }
            }
            DrawText(g, help, o.X + helpX, mid + helpFont.Size * 0.36f, helpError ? helpBold : helpFont,
                     helpError ? Rgb(210, 58, 72) : Rgb(122, 122, 134));
        }

        void Go(int i)
        {
            if (i < 0 || i >= values.Count) return;
            focus = i;
            CaretOn = true;
        }

        void NextOrApply(int i)
        {
            if (!spec[i].Optional && ParseNumbers(values[i]).Count == 0)
            {
                Say($"{L("Vyplň", "Fill in")}: {spec[i].Name}", true);     // z prázdneho povinného políčka sa nejde ďalej
                return;
            }
            if (i + 1 < values.Count) Go(i + 1);
            else Apply();
        }

        void Say(string text, bool error = false)
        {
            help = text;
            helpError = error;
            canvas.LayoutPanels();
        }

        public void Close()
        {
            Hidden = true;
            canvas.Redraw();
        }

        void Apply()
        {
            var nums = new List<double>();
            for (int i = 0; i < values.Count; i++)
            {
                var v = ParseNumbers(values[i]);
                if (v.Count == 0 && !spec[i].Optional)
                {
                    Say($"{L("Vyplň", "Fill in")}: {spec[i].Name}", true);
                    Go(i);
                    return;
                }
                nums.AddRange(v);
            }
            var error = canvas.Tool.ApplyInput(nums);
            if (error != null)
            {
                Say(error, true);
                return;
            }
            Close();
        }

        /// Klávesy s významom (Enter, Esc, Backspace, Tab); true = spotrebované.
        public bool Key(Keys key, bool shift)
        {
            switch (key)
            {
                case Keys.Enter:
                    NextOrApply(focus);
                    break;
                case Keys.Escape:
                    Close();
                    return true;
                case Keys.Back:
                    if (values[focus].Length > 0) values[focus] = values[focus].Substring(0, values[focus].Length - 1);
                    else Go(focus - 1);
                    break;
                case Keys.Tab:
                    Go(shift ? focus - 1 : focus + 1);
                    break;
                case Keys.Left:
                    Go(focus - 1);
                    break;
                case Keys.Right:
                    Go(focus + 1);
                    break;
                default:
                    return false;
            }
            CaretOn = true;
            canvas.Redraw();
            return true;
        }

        public void Char(char ch)
        {
            if (Array.IndexOf(SEPARATORS, ch) >= 0)
            {
                Go(focus + 1);                                      // medzera alebo x = ďalšie políčko
            }
            else if (IsDigitKey(ch) || ch == '-')
            {
                if (values[focus].Length < 14) values[focus] += ToDigit(ch);     // ľščť… → číslice
            }
            else
            {
                return;
            }
            CaretOn = true;
            canvas.Redraw();
        }

        public void Paste(string text)
        {
            foreach (var ch in text.Trim())
            {
                if (ch == '\r' || ch == '\n' || ch == '\t') Go(focus + 1);
                else Char(ch);
            }
        }

        public bool Contains(V p) => !Hidden && Inset(Frame, Margin, Margin).Has(p);

        public void Click(V p)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                var r = fields[i];
                r.Offset(Frame.X, Frame.Y);
                if (r.Has(p)) Go(i);
            }
            canvas.Redraw();
        }
    }

    // MARK: - vyskakovacie menu

    sealed class PopupMenu
    {
        public sealed class Entry
        {
            public string Title = "", Shortcut = "";
            public bool Checked, Enabled = true, Separator, Note;
            public Action Action;
        }

        public readonly List<Entry> Entries;
        public RectangleF Frame;
        int? hovered;
        const float ROW = 28, SEP = 9, PAD = 6;
        static readonly Font font = UiFont(13), noteFont = UiFont(11);

        PopupMenu(List<Entry> entries) { Entries = entries; }

        SizeF Size()
        {
            float w = Entries.Where(e => !e.Separator)
                .Select(e => TextSize(e.Title, e.Note ? noteFont : font).Width + (e.Shortcut != "" ? TextSize(e.Shortcut, font).Width + 28 : 0))
                .DefaultIfEmpty(0).Max();
            float h = Entries.Sum(e => e.Separator ? SEP : e.Note ? ROW - 6 : ROW);
            return new SizeF(Math.Max(170, w + 52), h + 2 * PAD);
        }

        /// Nad tlačidlom v paneli (panel je dole).
        public static PopupMenu Above(RectangleF anchor, List<Entry> entries, float W)
        {
            var m = new PopupMenu(entries);
            var s = m.Size();
            m.Frame = new RectangleF(Clamp(anchor.Left, 8, W - s.Width - 8), anchor.Top - 8 - s.Height, s.Width, s.Height);
            return m;
        }

        /// Pri kurzore (pravé tlačidlo myši).
        public static PopupMenu At(V p, List<Entry> entries, float W, float H)
        {
            var m = new PopupMenu(entries);
            var s = m.Size();
            float x = p.X + s.Width + 8 > W ? p.X - s.Width : p.X;
            float y = p.Y + s.Height + 8 > H ? p.Y - s.Height : p.Y;
            m.Frame = new RectangleF(Clamp(x, 8, W - s.Width - 8), Clamp(y, 8, H - s.Height - 8), s.Width, s.Height);
            return m;
        }

        IEnumerable<(int, RectangleF)> Rows()
        {
            float y = Frame.Y + PAD;
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                float h = e.Separator ? SEP : e.Note ? ROW - 6 : ROW;
                yield return (i, new RectangleF(Frame.X + PAD, y, Frame.Width - 2 * PAD, h));
                y += h;
            }
        }

        int? IndexAt(V p)
        {
            foreach (var (i, r) in Rows())
            {
                var e = Entries[i];
                if (r.Has(p) && !e.Separator && !e.Note && e.Enabled) return i;
            }
            return null;
        }

        public void Draw(Graphics g, float scale)
        {
            PanelStyle.Draw(g, Frame, 10);
            foreach (var (i, r) in Rows())
            {
                var e = Entries[i];
                if (e.Separator)
                {
                    FillRect(g, new RectangleF(r.Left + 8, r.MidY(), r.Width - 16, 1 / scale), PanelStyle.EDGE);
                    continue;
                }
                bool hot = hovered == i;
                if (hot) FillPath(g, RoundedPath(r, 6), ACCENT);
                var color = e.Note ? Rgb(130, 130, 140) : !e.Enabled ? Rgb(170, 170, 178) : hot ? WHITE : PanelStyle.TEXT;
                var f = e.Note ? noteFont : font;
                DrawText(g, e.Title, r.Left + 28, r.MidY() + f.Size * 0.36f, f, color);
                if (e.Shortcut != "")
                {
                    float sw = TextSize(e.Shortcut, font).Width;
                    DrawText(g, e.Shortcut, r.Right - 12 - sw, r.MidY() + f.Size * 0.36f, font, hot ? WHITE : Rgb(140, 140, 150));
                }
                if (e.Checked)
                {
                    var c = new V(r.Left + 14, r.MidY());
                    FillPath(g, PolygonPath(new[] { c + new V(-5, 0), c + new V(-1.5f, 4), c + new V(5, -4.5f) }, false), null,
                             hot ? WHITE : ACCENT, 2);
                }
            }
        }

        public bool Move(V p)
        {
            var h = IndexAt(p);
            if (h == hovered) return false;
            hovered = h;
            return true;
        }

        /// Klik: vráti akciu na spustenie (menu sa potom zavrie).
        public Action Click(V p) => IndexAt(p) is int i ? Entries[i].Action ?? (() => { }) : null;

        public Action Key(Keys key)
        {
            var usable = Enumerable.Range(0, Entries.Count).Where(i => !Entries[i].Separator && !Entries[i].Note && Entries[i].Enabled).ToList();
            if (usable.Count == 0) return null;
            int pos = hovered is int h ? usable.IndexOf(h) : -1;
            switch (key)
            {
                case Keys.Down:
                    hovered = usable[(pos + 1) % usable.Count];
                    break;
                case Keys.Up:
                    hovered = usable[pos <= 0 ? usable.Count - 1 : pos - 1];
                    break;
                case Keys.Enter:
                case Keys.Space:
                    if (hovered is int sel) return Entries[sel].Action ?? (() => { });
                    break;
            }
            return null;
        }
    }

    // MARK: - bublinka s popisom

    static class Tip
    {
        static readonly Font font = UiFont(12);

        /// Nad bodom (nad panelom), vycentrovaná a držaná v okne.
        public static void Draw(Graphics g, string text, V above, float W)
        {
            var s = TextSize(text, font);
            var r = new RectangleF(0, 0, s.Width + 16, s.Height + 8);
            r.X = Clamp(above.X - r.Width / 2, 6, W - r.Width - 6);
            r.Y = above.Y - r.Height - 8;
            FillPath(g, RoundedPath(r, 6), Rgb(40, 40, 48, 238));
            TextInRect(g, text, r, font, WHITE);
        }
    }

    // MARK: - kalibrácia kartou

    /// Kalibrácia priamo na ploche: karta ID-1 v skutočnej veľkosti, posuvník a tlačidlá.
    sealed class Calibration
    {
        readonly Canvas canvas;
        readonly Display display;
        readonly float basePPM, maxPPM;
        public float Ppm;
        int value;                              // stotiny percenta, ±15 %
        RectangleF frame, titleR, infoR, cardArea, slider, statusR, bReset, bCancel, bSave;
        bool dragging;
        string hot;
        const float PAD = 20, CARD_PAD = 12;
        static readonly Font titleFont = UiFont(17, true), infoFont = UiFont(13), statusFont = UiFont(12), buttonFont = UiFont(13);

        public Calibration(Canvas canvas, Display display)
        {
            this.canvas = canvas;
            this.display = display;
            basePPM = EdidPPM(display);
            Ppm = PtPerMM(display);
            maxPPM = basePPM * 1.16f;
            value = (int)Math.Round((Ppm / basePPM - 1) * 10000);
            value = Math.Max(-1500, Math.Min(1500, value));
        }

        string Info => L(
            $"Displej: {display.Name}. Prilož na obrazovku kartu formátu ID-1 — platobnú kartu, občiansky, ISIC alebo kartičku poistenca. Zarovnaj ju s ľavým horným rohom obdĺžnika a posúvaj, kým nesedí aj pravý okraj. Jemne: šípky ← →.",
            $"Display: {display.Name}. Hold an ID-1 size card to the screen — a bank card, ID card or student card. Line it up with the top-left corner of the outline and move the slider until the right edge matches too. Fine steps: arrow keys ← →.");

        string Status
        {
            get
            {
                float px = Ppm * display.Scale, edid = basePPM * display.Scale;
                return $"{Fmt(px, 3)} px/mm · {Fmt(px * MM_PER_IN, 1)} PPI   ({L("displej hlási", "display reports")} {Fmt(edid, 3)} px/mm)";
            }
        }

        public void Layout(float W, float H)
        {
            float cw = Rnd(CARD_MM.Width * maxPPM + 2 * CARD_PAD), ch = Rnd(CARD_MM.Height * maxPPM + 2 * CARD_PAD);
            float width = Math.Max(cw, 460);
            float infoH = WrappedHeight(Info, infoFont, width) + 2;
            float h = PAD + 26 + 10 + infoH + 12 + ch + 12 + 24 + 8 + 18 + 16 + 32 + PAD;
            frame = new RectangleF(Rnd((W - width - 2 * PAD) / 2), Rnd(Math.Max(8, (H - h) / 2)), width + 2 * PAD, h);
            float x = frame.X + PAD, y = frame.Y + PAD;
            titleR = new RectangleF(x, y, width, 26);
            y += 36;
            infoR = new RectangleF(x, y, width, infoH);
            y += infoH + 12;
            cardArea = new RectangleF(x, y, cw, ch);
            y += ch + 12;
            slider = new RectangleF(x, y, width, 24);
            y += 32;
            statusR = new RectangleF(x, y, width, 18);
            y += 34;
            float bw(string s) => TextSize(s, buttonFont).Width + 30;
            bReset = new RectangleF(x, y, bw(ResetText), 32);
            bSave = new RectangleF(x + width - bw(SaveText), y, bw(SaveText), 32);
            bCancel = new RectangleF(bSave.Left - 10 - bw(CancelText), y, bw(CancelText), 32);
        }

        static string ResetText => L("Podľa displeja (EDID)", "From display (EDID)");
        static string CancelText => L("Zrušiť", "Cancel");
        static string SaveText => L("Uložiť", "Save");

        void Changed()
        {
            Ppm = basePPM * (1 + value / 10000f);
            canvas.Redraw();
        }

        public void Draw(Graphics g, float W, float H, float scale)
        {
            Layout(W, H);
            FillRect(g, new RectangleF(0, 0, W, H), Color.FromArgb(70, 0, 0, 0));
            PanelStyle.Draw(g, frame, 14);
            DrawText(g, L("Kalibrácia mierky", "Calibrate the scale"), titleR.Left, titleR.Top + 20, titleFont, PanelStyle.TEXT);
            WrappedText(g, Info, infoR, infoFont, Rgb(70, 70, 82));

            // karta v skutočnej veľkosti podľa práve nastavenej mierky
            var card = new RectangleF(cardArea.Left + CARD_PAD, cardArea.Top + CARD_PAD, CARD_MM.Width * Ppm, CARD_MM.Height * Ppm);
            FillPath(g, RoundedPath(card, CARD_CORNER_MM * Ppm), Tint(ACCENT, 40), ACCENT);
            TextInRect(g, L("85,60 × 53,98 mm", "85.60 × 53.98 mm"), card, UiFont(13, true), INK);
            for (int mm = 0; mm <= 85; mm++)
            {
                float x = card.Left + mm * Ppm;
                CrispLine(g, x, card.Top, x, card.Top + (mm % 10 == 0 ? 12 : mm % 5 == 0 ? 8 : 4), INK);
                if (mm % 10 == 0 && mm > 0) DrawText(g, (mm / 10).ToString(), x - 3, card.Top + 25, UiFont(10), INK);
            }

            // posuvník: stred = podľa EDID
            float mid = slider.MidY(), t = (value + 1500) / 3000f, kx = slider.Left + 10 + t * (slider.Width - 20);
            FillPath(g, RoundedPath(new RectangleF(slider.Left + 10, mid - 2, slider.Width - 20, 4), 2), Rgb(0, 0, 0, 40));
            FillPath(g, RoundedPath(new RectangleF(Math.Min(kx, slider.MidX()), mid - 2, Math.Abs(kx - slider.MidX()), 4), 2), ACCENT);
            CrispLine(g, slider.MidX(), mid - 8, slider.MidX(), mid + 8, Rgb(0, 0, 0, 90));
            Shadow(g, new RectangleF(kx - 9, mid - 9, 18, 18), 9, 1);
            FillPath(g, CirclePath(new V(kx, mid), 9), WHITE, dragging || hot == "knob" ? ACCENT : Rgb(0, 0, 0, 70), 1.5f);

            DrawText(g, Status, statusR.Left, statusR.Top + 13, statusFont, Rgb(90, 90, 102));
            Button(g, bReset, ResetText, "reset", false);
            Button(g, bCancel, CancelText, "cancel", false);
            Button(g, bSave, SaveText, "save", true);
        }

        void Button(Graphics g, RectangleF r, string text, string id, bool primary)
        {
            bool h = hot == id;
            if (primary) FillPath(g, RoundedPath(r, 7), h ? Rgb(24, 140, 200) : ACCENT);
            else FillPath(g, RoundedPath(Inset(r, 0.5f, 0.5f), 7), h ? Rgb(236, 244, 250) : WHITE, Rgb(0, 0, 0, 55));
            TextInRect(g, text, r, buttonFont, primary ? WHITE : PanelStyle.TEXT);
        }

        string HitTest(V p)
        {
            if (bSave.Has(p)) return "save";
            if (bCancel.Has(p)) return "cancel";
            if (bReset.Has(p)) return "reset";
            if (RectangleF.Inflate(slider, 0, 4).Has(p)) return "knob";
            return null;
        }

        void SetFromX(float x)
        {
            float t = Clamp((x - slider.Left - 10) / (slider.Width - 20), 0, 1);
            value = (int)Math.Round(t * 3000 - 1500);
            Changed();
        }

        public void Move(V p)
        {
            if (dragging)
            {
                SetFromX(p.X);
                return;
            }
            var h = HitTest(p);
            if (h != hot)
            {
                hot = h;
                canvas.Redraw();
            }
        }

        public void Press(V p)
        {
            if (HitTest(p) == "knob")
            {
                dragging = true;
                SetFromX(p.X);
            }
        }

        public void Release(V p)
        {
            if (dragging)
            {
                dragging = false;
                canvas.Redraw();
                return;
            }
            switch (HitTest(p))
            {
                case "save": Save(); break;
                case "cancel": canvas.EndCalibration(); break;
                case "reset":
                    value = 0;
                    Changed();
                    break;
            }
        }

        public void Key(Keys key, bool shift)
        {
            int step = shift ? 100 : 10;          // 0,1 %, so Shiftom 1 %
            switch (key)
            {
                case Keys.Left:
                case Keys.Down:
                    value = Math.Max(-1500, value - step);
                    Changed();
                    break;
                case Keys.Right:
                case Keys.Up:
                    value = Math.Min(1500, value + step);
                    Changed();
                    break;
                case Keys.Enter:
                    Save();
                    break;
                case Keys.Escape:
                    canvas.EndCalibration();
                    break;
            }
        }

        void Save()
        {
            var calib = new Dictionary<string, double>(Settings.Calib);
            if (value == 0) calib.Remove(display.Key);
            else calib[display.Key] = Ppm * display.Scale;           // ukladajú sa pixely na mm
            Settings.Calib = calib;
            canvas.EndCalibration();
        }
    }

    // MARK: - ikonky v paneli (kreslené, 26 × 26 bodov)

    static class Icons
    {
        static readonly Dictionary<(string, float), Bitmap> cache = new Dictionary<(string, float), Bitmap>();

        /// Ikonka na pozícii v bodoch; obrázok je v skutočných pixeloch, aby bol ostrý.
        public static void Draw(Graphics g, string kind, float x, float y, float scale, float alpha)
        {
            var bmp = Get(kind, scale);
            var state = g.Save();
            g.ResetTransform();
            var dest = new Rectangle((int)Rnd(x * scale), (int)Rnd(y * scale), bmp.Width, bmp.Height);
            if (alpha >= 1)
            {
                g.DrawImage(bmp, dest);
            }
            else
            {
                using (var attrs = new ImageAttributes())
                {
                    attrs.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            g.Restore(state);
        }

        static Bitmap Get(string kind, float scale)
        {
            if (cache.TryGetValue((kind, scale), out var bmp)) return bmp;
            int size = (int)Math.Ceiling(26 * scale);
            bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            float old = Gfx.Scale;
            Gfx.Scale = scale;
            using (var g = Graphics.FromImage(bmp))
            {
                Prepare(g, scale, true);
                DrawIcon(g, kind);
            }
            Gfx.Scale = old;
            cache[(kind, scale)] = bmp;
            return bmp;
        }

        static void DrawIcon(Graphics g, string kind)
        {
            Color ink = Rgb(58, 58, 70), yellow = Rgb(246, 200, 62), lightBlue = Rgb(196, 230, 248);
            V P(float x, float y) => new V(x, y);
            RectangleF Rect(float x, float y, float w, float h) => new RectangleF(x, y, w, h);
            void Shape(GraphicsPath path, Color? fill, Color? stroke = null, float width = 1.5f) => FillPath(g, path, fill, stroke ?? ink, width);
            void Line(V a, V b, Color? color = null, float width = 1.5f, bool dash = false) => StrokeLine(g, a, b, color ?? ink, width, dash);
            void Box(RectangleF r, Color color)
            {
                using (var br = new SolidBrush(color)) g.FillRectangle(br, r);
            }
            var cmMarks = Enumerable.Range(0, 9).Select(i => (5 + 2f * i, i % 5 == 0 ? 5f : 3f)).ToList();
            var inMarks = new List<(float, float)> { (5, 6), (9, 2.5f), (13, 4), (17, 2.5f), (21, 6) };

            switch (kind)
            {
                case "cm":
                case "in":
                case "dual":
                    Shape(RoundedPath(Rect(2, 7, 22, 12), 2), yellow);
                    foreach (var (x, ln) in kind == "in" ? inMarks : cmMarks) Line(P(x, 7), P(x, 7 + ln));
                    if (kind == "dual") foreach (var (x, ln) in inMarks) Line(P(x, 19), P(x, 19 - ln * 0.7f));
                    break;
                case "px":
                    Shape(RoundedPath(Rect(3, 4, 20, 14), 2), null);
                    Line(P(13, 18), P(13, 22));
                    Line(P(9, 22), P(17, 22));
                    foreach (var (x, y) in new[] { (6.5f, 7.5f), (9.5f, 7.5f), (6.5f, 10.5f), (12.5f, 10.5f), (15.5f, 13.5f), (18.5f, 13.5f) })
                        Box(Rect(x, y, 2.4f, 2.4f), ACCENT);
                    break;
                case "grid":
                    Shape(RectPath(Rect(4, 4, 18, 18)), WHITE);
                    foreach (var k in new[] { 8.5f, 13, 17.5f })
                    {
                        Line(P(k, 4), P(k, 22), ink, 0.7f);
                        Line(P(4, k), P(22, k), ink, 0.7f);
                    }
                    break;
                case "prot":
                    {
                        var c = P(13, 19);
                        Shape(PolygonPath(Enumerable.Range(0, 37).Select(i => Polar(c, i * 5, 11))), lightBlue);
                        Line(c, Polar(c, 55, 10), MARK, 1.8f);
                        break;
                    }
                case "prot360":
                    Shape(CirclePath(P(13, 13), 10), lightBlue);
                    Line(P(13, 13), P(22, 13), ACCENT, 1.8f);
                    Line(P(13, 13), Polar(P(13, 13), 130, 9), MARK, 1.8f);
                    break;
                case "circle":
                    foreach (var r in new[] { 3.5f, 7, 10.5f }) Shape(CirclePath(P(13, 13), r), null);
                    FillPath(g, CirclePath(P(13, 13), 1), ink);
                    break;
                case "radius":
                    {
                        var c = P(6, 21);
                        Shape(PolygonPath(Enumerable.Range(0, 19).Select(i => Polar(c, i * 5, 16)), false), null);
                        Line(c, Polar(c, 45, 16), ink, 1, true);
                        foreach (var a in new[] { 8f, 45, 82 }) FillPath(g, CirclePath(Polar(c, a, 16), 2.2f), MARK);
                        break;
                    }
                case "diag":
                    Shape(RoundedPath(Rect(7, 2, 12, 22), 2.5f), lightBlue);
                    Line(P(9, 4.5f), P(17, 21.5f), MARK, 1.8f);
                    break;
                case "path":
                    {
                        var pts = new[] { P(3, 20), P(9, 9), P(15, 17), P(23, 5) };
                        Shape(PolygonPath(pts, false), null);
                        foreach (var q in pts) FillPath(g, CirclePath(q, 2.2f), MARK);
                        break;
                    }
                case "screw":
                    {
                        var metal = Rgb(200, 200, 208);
                        Shape(RoundedPath(Rect(2.5f, 8, 6, 10), 1.5f), metal);
                        Shape(RectPath(Rect(8.5f, 10.5f, 15, 5)), metal);
                        for (float x = 10.5f; x < 23; x += 2.5f) Line(P(x, 10.5f), P(x + 1.5f, 15.5f), ink, 1);
                        break;
                    }
                case "pitch":
                    foreach (var x in new[] { 6.5f, 10.5f, 14.5f, 18.5f })
                    {
                        Box(Rect(x - 0.9f, 3.5f, 1.8f, 5), Rgb(150, 150, 160));
                        Box(Rect(x - 0.9f, 17.5f, 1.8f, 5), Rgb(150, 150, 160));
                    }
                    FillPath(g, RoundedPath(Rect(4, 8, 18, 10), 1.5f), Rgb(60, 60, 72));
                    FillPath(g, CirclePath(P(7, 11), 1), WHITE);
                    break;
                case "snap":
                    foreach (var x in new[] { 5f, 13, 21 })
                        foreach (var y in new[] { 5f, 13, 21 }) FillPath(g, CirclePath(P(x, y), 1.4f), Rgb(150, 150, 160));
                    Line(P(13, 7), P(13, 19), ACCENT, 1.6f);
                    Line(P(7, 13), P(19, 13), ACCENT, 1.6f);
                    FillPath(g, CirclePath(P(13, 13), 2.6f), MARK);
                    break;
                case "type":
                    Shape(RoundedPath(Rect(2, 6, 22, 14), 2.5f), WHITE);
                    foreach (var y in new[] { 9f, 12.5f })
                        for (int k = 0; k < 5; k++) Box(Rect(4.6f + k * 3.6f, y, 2.2f, 2), ink);
                    Box(Rect(8, 16, 10, 1.8f), ink);
                    break;
                case "bg":
                    Shape(CirclePath(P(13, 13), 9), null);
                    FillPath(g, PolygonPath(new[] { P(13, 13) }.Concat(Enumerable.Range(0, 37).Select(i => Polar(P(13, 13), 90 + i * 5, 9)))), ink);
                    break;
                case "calib":
                    Shape(RoundedPath(Rect(3, 7, 20, 13), 2), lightBlue);
                    Line(P(3.5f, 11), P(22.5f, 11), ink, 2.2f);
                    break;
                case "window":
                    Shape(RoundedPath(Rect(3, 5, 20, 16), 2), WHITE);
                    Line(P(3, 9), P(23, 9));
                    Line(P(15, 18.5f), P(20.5f, 13), ACCENT, 1.4f);
                    Line(P(18, 18.5f), P(20.5f, 16), ACCENT, 1.4f);
                    break;
                case "more":
                    foreach (var x in new[] { 6f, 13, 20 }) FillPath(g, CirclePath(P(x, 13), 2.2f), ink);
                    break;
                case "hide":
                    Shape(PolygonPath(new[] { P(7, 10), P(13, 16), P(19, 10) }, false), null);
                    break;
                case "close":
                    Line(P(8, 8), P(18, 18));
                    Line(P(18, 8), P(8, 18));
                    break;
            }
        }
    }
}
