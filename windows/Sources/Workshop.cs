// Workshop.cs — dielenské nástroje: metrické skrutky a rozteč pinov.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using static Regula.Gfx;
using static Regula.U;

namespace Regula
{
    static class Workshop
    {
        // (názov, závit mm, stúpanie mm, matica = kľúč mm, imbus mm) — metrické ISO, hrubý závit
        public static readonly (string, double, double, double, double)[] SCREWS =
        {
            (L("M1,6", "M1.6"), 1.6, 0.35, 3.2, 1.5), ("M2", 2.0, 0.40, 4.0, 1.5), (L("M2,5", "M2.5"), 2.5, 0.45, 5.0, 2.0),
            ("M3", 3.0, 0.50, 5.5, 2.5), ("M4", 4.0, 0.70, 7.0, 3.0), ("M5", 5.0, 0.80, 8.0, 4.0),
            ("M6", 6.0, 1.00, 10.0, 5.0), ("M8", 8.0, 1.25, 13.0, 6.0),
        };

        public static readonly (double, string)[] PITCHES =
        {
            (2.54, L("2,54 mm (0,1″) · lámacie lišty, Dupont, DIP", "2.54 mm (0.1″) · pin headers, Dupont, DIP")),
            (2.50, L("2,50 mm · JST XH", "2.50 mm · JST XH")),
            (2.00, L("2,00 mm · JST PH, lišty 2 mm", "2.00 mm · JST PH, 2 mm headers")),
            (1.50, L("1,50 mm · JST ZH", "1.50 mm · JST ZH")),
            (1.27, L("1,27 mm (0,05″) · SOIC, lišty 1,27", "1.27 mm (0.05″) · SOIC, 1.27 mm headers")),
            (1.25, L("1,25 mm · Molex PicoBlade", "1.25 mm · Molex PicoBlade")),
            (1.00, L("1,00 mm · JST SH, Qwiic / STEMMA QT", "1.00 mm · JST SH, Qwiic / STEMMA QT")),
            (0.80, L("0,80 mm · QFP", "0.80 mm · QFP")),
            (0.65, L("0,65 mm · TSSOP, QFP", "0.65 mm · TSSOP, QFP")),
            (0.50, L("0,50 mm · FFC/FPC káble, QFN", "0.50 mm · FFC/FPC cables, QFN")),
        };

        /// Text zarovnaný vľavo a zvislo na stred obdĺžnika.
        public static void TextLeft(Graphics g, string s, RectangleF rect, Font font, Color color) =>
            DrawText(g, s, rect.Left, rect.MidY() + font.Size * (0.95f - 0.24f) / 2, font, color);
    }

    // MARK: - skrutky

    /// Metrické skrutky M1,6–M8: otvor, závit zboku, matica, dĺžka.
    sealed class ScrewTool : Tool
    {
        public const float TOTAL_MM = 96;

        public ScrewTool(Canvas canvas) : base(canvas) { }

        public override string Hint =>
            L("skrutku polož na kruh alebo zboku na pás · maticu na šesťuholník · dĺžku meraj od hrubej čiary · prejdi myšou = detaily",
              "stand a screw on a circle or lay it on a strip · a nut on the hexagon · measure length from the thick line · hover = details");

        /// (ľavý okraj, horný okraj, stĺpce (stred x, šírka))
        public static (float, float, List<(float, float)>) Geom(float W, float H, float ppm)
        {
            var widths = Workshop.SCREWS.Select(s => ((float)Math.Max(s.Item4, 9) + 5) * ppm).ToList();
            float ox = (W - widths.Sum()) / 2;
            float oy = Math.Max(16, (H - DOCK_RESERVE - TOTAL_MM * ppm) / 2);
            var cols = new List<(float, float)>();
            float x = ox;
            foreach (var w in widths)
            {
                cols.Add((x + w / 2, w));
                x += w;
            }
            return (ox, oy, cols);
        }

        public override void PaintStatic(G g)
        {
            float ppm = g.ppm;
            var (ox, oy, cols) = Geom(g.W, g.H, ppm);
            float Y(double mm) => oy + (float)mm * ppm;
            var lastCol = cols[cols.Count - 1];
            float right = lastCol.Item1 + lastCol.Item2 / 2;
            Backdrop(g.g, new RectangleF(ox - 80, oy - 14, right - ox + 100, TOTAL_MM * ppm + 34), g.th);
            var small = UiFont(11);
            foreach (var (text, mm) in new[] { (L("otvor", "hole"), 11.0), (L("zboku", "side"), 34), (L("matica", "nut"), 59), (L("dĺžka", "length"), 86) })
                DrawText(g.g, text, ox - 12 - TextSize(text, small).Width, Y(mm) + 4, small, g.th.Soft);

            var fill = Tint(g.th.Ink, 28);
            for (int i = 0; i < Workshop.SCREWS.Length; i++)
            {
                var (name, d, _, af, key) = Workshop.SCREWS[i];
                float cx = cols[i].Item1, r = (float)d / 2 * ppm;
                CenteredText(g.g, name, new V(cx, Y(3)), UiFont(14, true), g.th.Ink);
                FillPath(g.g, CirclePath(new V(cx, Y(11)), r), fill, g.th.Ink);
                FillPath(g.g, RectPath(new RectangleF(cx - r, Y(19), 2 * r, 30 * ppm)), fill, g.th.Ink);
                var center = new V(cx, Y(59));
                float corner = (float)(af / Math.Sqrt(3)) * ppm;           // kľúč = vzdialenosť plôch
                FillPath(g.g, PolygonPath(Enumerable.Range(0, 6).Select(k => Polar(center, k * 60, corner))), fill, g.th.Ink);
                FillPath(g.g, CirclePath(center, r), g.th.Glass ? g.th.Panel : g.th.Bg, g.th.Ink);
                CenteredText(g.g, $"{L("kľúč", "wrench")} {FmtG(af)}", new V(cx, Y(69.5)), UiFont(10), g.th.Soft);
                CenteredText(g.g, $"{L("imbus", "hex key")} {FmtG(key)}", new V(cx, Y(73)), UiFont(10), g.th.Soft);
            }

            // dĺžka: hlava sa opiera o hrubú čiaru
            float baseY = Y(92), x0 = ox;
            var state = g.g.Save();
            g.g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(g.th.Ink, 3)) g.g.DrawLine(pen, x0, Y(80), x0, baseY);
            g.g.Restore(state);
            CrispLine(g.g, x0, baseY, ox + 70 * ppm, baseY, g.th.Ink);
            for (int mm = 0; mm <= 70; mm++)
            {
                float x = ox + mm * ppm;
                CrispLine(g.g, x, baseY, x, baseY - (mm % 10 == 0 ? 14 : mm % 5 == 0 ? 10 : 6), g.th.Ink);
                if (mm % 5 == 0 && mm > 0) CenteredText(g.g, mm.ToString(), new V(x, baseY + 11), UiFont(10), g.th.Ink);
            }
            DrawText(g.g, L("← hlavu skrutky oprieť o hrubú čiaru", "← put the screw head against the thick line"),
                     ox + 73 * ppm, baseY - 4, small, g.th.Soft);
        }

        public override void PaintDynamic(G g)
        {
            if (!(canvas.Hover is V h)) return;
            float ppm = g.ppm;
            var (ox, oy, cols) = Geom(g.W, g.H, ppm);
            float Y(double mm) => oy + (float)mm * ppm;
            if (h.Y >= Y(0) && h.Y <= Y(75))
            {
                for (int i = 0; i < Workshop.SCREWS.Length; i++)
                {
                    var col = cols[i];
                    if (Math.Abs(h.X - col.Item1) > col.Item2 / 2) continue;
                    var (name, d, pitch, af, key) = Workshop.SCREWS[i];
                    FillPath(g.g, RoundedPath(new RectangleF(col.Item1 - col.Item2 / 2 + 2, Y(-1), col.Item2 - 4, 77 * ppm), 10), Tint(ACCENT, 34));
                    var text = L($"{name} · závit {FmtG(d)} mm · stúpanie {FmtG(pitch)} mm · matica {FmtG(af)} mm · imbus {FmtG(key)} mm",
                                 $"{name} · thread {FmtG(d)} mm · pitch {FmtG(pitch)} mm · nut {FmtG(af)} mm · hex key {FmtG(key)} mm");
                    Pill(g.g, h.X, h.Y - 26, text, ACCENT, 2, g.W - 2);
                }
            }
            else if (h.Y >= Y(78) && h.Y <= Y(96) && h.X >= ox && h.X <= ox + 70 * ppm)
            {
                CrispLine(g.g, h.X, Y(78), h.X, Y(92), ACCENT);
                Pill(g.g, h.X, Y(78) - 14, $"{L("dĺžka", "length")} {Fmt((h.X - ox) / ppm, 1)} mm", ACCENT, 2, g.W - 2);
            }
        }
    }

    // MARK: - rozteč pinov

    /// Hrebene bežných roztečí pinov — na určenie konektora či púzdra čipu.
    sealed class PitchTool : Tool
    {
        public const float ROW_MM = 9, COMB_MM = 60, LABEL_W = 330;

        public PitchTool(Canvas canvas) : base(canvas) { }

        public override string Hint =>
            L("prilož konektor alebo čip k hrebeňu · prejdi myšou po hrebeni = počet pinov a dĺžka",
              "hold a connector or chip to a comb · hover along the comb = pin count and length");

        /// (ľavý okraj textov, začiatok hrebeňov, horný okraj)
        public static (float, float, float) Geom(float W, float H, float ppm)
        {
            float left = (W - LABEL_W - COMB_MM * ppm) / 2;
            float oy = Math.Max(16, (H - DOCK_RESERVE - Workshop.PITCHES.Length * ROW_MM * ppm) / 2);
            return (left, left + LABEL_W, oy);
        }

        static int PinCount(double pitch) => (int)(COMB_MM / pitch + 1e-9) + 1;

        /// Piny v skutočných pixeloch — rovnako široké a ostré.
        static void PaintPins(Graphics g, float x0, float top, double pitch, float ppm, Color color, int count = -1)
        {
            float s = Gfx.Scale, step = (float)pitch * ppm * s;
            float pw = Math.Max(1, Rnd((float)pitch * 0.45f * ppm * s));
            float y = Rnd((top + 2 * ppm) * s), ph = Rnd(4 * ppm * s);
            var state = g.Save();
            g.ResetTransform();
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var b = new SolidBrush(color))
            {
                int n = count >= 0 ? count : PinCount(pitch);
                for (int k = 0; k < n; k++) g.FillRectangle(b, Rnd(x0 * s + k * step - pw / 2), y, pw, ph);
            }
            g.Restore(state);
        }

        public override void PaintStatic(G g)
        {
            float ppm = g.ppm;
            var (left, x0, oy) = Geom(g.W, g.H, ppm);
            float row = ROW_MM * ppm;
            Backdrop(g.g, new RectangleF(left - 20, oy - 14, x0 - left + COMB_MM * ppm + 44, Workshop.PITCHES.Length * row + 28), g.th);
            for (int i = 0; i < Workshop.PITCHES.Length; i++)
            {
                var (pitch, text) = Workshop.PITCHES[i];
                float top = oy + i * row;
                Workshop.TextLeft(g.g, text, new RectangleF(left, top + ppm, LABEL_W - 16, 6 * ppm), UiFont(12), g.th.Ink);
                PaintPins(g.g, x0, top, pitch, ppm, g.th.Ink);
                float y = top + 6 * ppm + 1;
                CrispLine(g.g, x0 - 4, y, x0 + COMB_MM * ppm + 4, y, Tint(g.th.Ink, 110));
            }
        }

        public override void PaintDynamic(G g)
        {
            if (!(canvas.Hover is V h)) return;
            float ppm = g.ppm;
            var (_, x0, oy) = Geom(g.W, g.H, ppm);
            float row = ROW_MM * ppm;
            int i = (int)Math.Floor((h.Y - oy) / row);
            if (i < 0 || i >= Workshop.PITCHES.Length) return;
            double pitch = Workshop.PITCHES[i].Item1;
            float step = (float)pitch * ppm;
            if (h.X < x0 - step || h.X > x0 + COMB_MM * ppm + step) return;
            int n = Math.Max(1, Math.Min(PinCount(pitch), (int)Math.Floor((h.X - x0) / step + 0.5f) + 1));
            float top = oy + i * row;
            PaintPins(g.g, x0, top, pitch, ppm, ACCENT, n);
            Pill(g.g, h.X, top + 7.6f * ppm, $"{PinsText(n)} · {Fmt((n - 1) * pitch, 2)} mm", ACCENT, 2, g.W - 2);
        }
    }
}
