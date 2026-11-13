// Tools.cs — nástroje: pravítka (aj mm papier), uhlomer, priemer, polomer, uhlopriečka, trasa.
// Každý nástroj má statickú vrstvu (kreslí sa raz, cachuje sa) a dynamickú (kurzor, meranie).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using static Regula.Gfx;
using static Regula.U;

namespace Regula
{
    enum SnapKind { None, Point, Angle }

    enum ToolKey { Enter, Backspace }

    abstract class Tool
    {
        protected readonly Canvas canvas;

        protected Tool(Canvas canvas) { this.canvas = canvas; }

        public virtual string Hint => "";
        public virtual SnapKind SnapKind => SnapKind.None;     // Point = body na mriežku kroku, Angle = uhly po kroku
        public virtual string Unit => "cm";
        public virtual Field[] InputFields => new Field[0];     // políčka na zadanie z klávesnice; prázdne = nástroj to nepodporuje

        public virtual void Layout(float W, float H) { }
        public virtual void Relayout() { }
        public virtual string StaticKey() => "";
        public virtual void PaintStatic(G g) { }
        public virtual void PaintDynamic(G g) { }
        public virtual void Press(V p, bool shift) { }
        public virtual void Drag(V p, bool shift) { }
        public virtual void Release(V p) { }
        public virtual void Double(V p, bool shift) => Press(p, shift);     // rýchle kliky nech sa nestratia
        public virtual bool Key(ToolKey k) => false;                        // true = kláves spotrebovaný
        public virtual void Wheel(float steps) { }
        public virtual void Nudge(V d) { }
        public virtual void Reset() { }
        public virtual string ApplyInput(List<double> nums) => null;       // chybová hláška alebo null
        /// Okno prešlo na obrazovku s k-krát väčšou hustotou bodov na mm.
        public virtual void Rescale(float k) { }
        public virtual Cursor Cursor(V p) => Cursors.Cross;

        /// Body prepočítané na inú veľkosť bodu (rovnaké mm) a posunuté, aby sa zmestili do okna.
        protected List<V> Rescaled(IEnumerable<V> pts, float k)
        {
            var scaled = pts.Select(p => p * k).ToList();
            var d = canvas.FitShift(scaled);
            return scaled.Select(p => p + d).ToList();
        }
    }

    static class Scales
    {
        /// Čiarky stupnice: (poloha v bodoch, dĺžka čiarky, číslo alebo null).
        public static List<(float, float, string)> Ticks(string unit, float ppm, float px, float length)
        {
            float step;
            int[] divs;
            float[] lens;
            int every;
            Func<int, string> label;
            switch (unit)
            {
                case "px":
                    int b = px >= 2 ? 10 : 5;                    // pri 200 % by 5 px bolo len 2,5 bodu
                    step = b / px;
                    divs = b == 10 ? new[] { 10, 5, 1 } : new[] { 20, 10, 2, 1 };
                    lens = b == 10 ? new[] { 19f, 12, 6 } : new[] { 19f, 13, 8, 4 };
                    every = b == 10 ? 10 : 20;
                    label = i => (i * b).ToString();
                    break;
                case "in":
                    step = ppm * MM_PER_IN / 16;
                    divs = new[] { 16, 8, 4, 2, 1 };
                    lens = new[] { 20f, 15, 11, 8, 5 };
                    every = 16;
                    label = i => (i / 16).ToString();
                    break;
                default:
                    step = ppm;
                    divs = new[] { 10, 5, 1 };
                    lens = new[] { 19f, 12, 7 };
                    every = 10;
                    label = i => (i / 10).ToString();
                    break;
            }
            var output = new List<(float, float, string)>();
            for (int i = 0; i * step <= length; i++)
            {
                int level = Array.FindIndex(divs, d => i % d == 0);
                if (level < 0) level = divs.Length - 1;
                output.Add((i * step, lens[level], i % every == 0 ? label(i) : null));
            }
            return output;
        }
    }

    // MARK: - pravítko

    /// Stupnice hore a vľavo, nitkový kríž a meranie ťahaním (aj ako milimetrový papier).
    sealed class RulerTool : Tool
    {
        readonly string kind;
        readonly bool grid;
        public V? a, b;
        bool dragging;

        public RulerTool(Canvas canvas, string unit, bool grid = false) : base(canvas)
        {
            kind = unit;
            this.grid = grid;
        }

        public override string Unit => kind;
        public override SnapKind SnapKind => SnapKind.Point;
        public override string Hint =>
            L("ťahaj = zmeraj · píš čísla = presný rozmer · šípky = koniec o 0,1 mm · Shift = rovno · S = krok · Esc = zmazať",
              "drag = measure · type numbers = exact size · arrows = move end 0.1 mm · Shift = straight · S = snap · Esc = clear");
        float Band => kind == "dual" ? 84 : 50;

        public override Field[] InputFields
        {
            get
            {
                var u = kind == "in" ? "″" : kind == "px" ? "px" : "mm";
                return new[] { new Field(L("Šírka", "Width"), u, false), new Field(L("Výška", "Height"), u, true) };
            }
        }

        public override string ApplyInput(List<double> nums)
        {
            if (nums.Count < 1 || nums.Count > 2 || nums.Min() < 0 || nums.Max() <= 0)
                return L("Aspoň jedno číslo musí byť väčšie ako 0", "At least one number must be greater than 0");
            float ppm = canvas.Ppm;
            float k = kind == "px" ? 1 / canvas.PxScale : kind == "in" ? ppm * MM_PER_IN : ppm;
            var a0 = a ?? canvas.InputOrigin();
            var b0 = new V(a0.X + (float)nums[0] * k, a0.Y + (nums.Count == 2 ? (float)nums[1] * k : 0));
            var d = canvas.FitShift(new[] { a0, b0 });
            a = a0 + d;
            b = b0 + d;
            dragging = false;
            return null;
        }

        public override void PaintStatic(G g)
        {
            float T = Band;
            if (grid) PaintGrid(g);
            FillRect(g.g, new RectangleF(0, 0, g.W, T), g.th.Panel);
            FillRect(g.g, new RectangleF(0, 0, T, g.H), g.th.Panel);
            CrispLine(g.g, T, T, g.W, T, Tint(g.th.Ink, 70));
            CrispLine(g.g, T, T, T, g.H, Tint(g.th.Ink, 70));
            var font = UiFont(11);
            var scales = kind == "dual" ? new[] { ("cm", true), ("in", false) } : new[] { (kind, true) };
            foreach (var (u, outer) in scales)
            {
                PaintScale(g, u, g.W, T, font, outer, true);
                PaintScale(g, u, g.H, T, font, outer, false);
            }
            FillRect(g.g, new RectangleF(0, 0, T, T), g.th.Panel);
            var name = kind == "cm" ? (grid ? "mm" : "cm") : kind == "in" ? L("palce", "in") : kind == "px" ? "px" : "cm\nin";
            TextInRect(g.g, name, new RectangleF(0, 0, T, T), font, g.th.Soft);
        }

        void PaintScale(G g, string u, float length, float T, Font font, bool outer, bool horizontal)
        {
            float asc = font.Size * 0.95f, desc = font.Size * 0.24f;      // rozloženie ako na Macu
            foreach (var (pos, ln, label) in Scales.Ticks(u, g.ppm, g.px, length - 1))
            {
                float x = pos;
                float v0 = outer ? 0 : T - 1, v1 = outer ? ln : T - 1 - ln;
                if (horizontal) CrispLine(g.g, x, v0, x, v1, g.th.Ink);
                else CrispLine(g.g, v0, x, v1, x, g.th.Ink);
                if (label == null || pos <= 0) continue;
                float w = TextSize(label, font).Width;
                if (horizontal)
                    DrawText(g.g, label, x - w / 2, outer ? ln + 2 + asc : T - ln - 4 - desc, font, g.th.Ink);
                else
                    DrawText(g.g, label, outer ? ln + 3 : T - ln - 4 - w, x + (asc - desc) / 2, font, g.th.Ink);
            }
        }

        void PaintGrid(G g)
        {
            var colors = new[] { Tint(g.th.Ink, 22), Tint(g.th.Ink, 48), Tint(g.th.Ink, 95) };
            foreach (var vertical in new[] { true, false })
            {
                float length = vertical ? g.W : g.H, span = vertical ? g.H : g.W;
                for (int i = 0; i * g.ppm <= length; i++)
                {
                    var color = colors[i % 10 == 0 ? 2 : i % 5 == 0 ? 1 : 0];
                    float x = i * g.ppm;
                    if (vertical) CrispLine(g.g, x, 0, x, span, color);
                    else CrispLine(g.g, 0, x, span, x, color);
                }
            }
        }

        public override void PaintDynamic(G g)
        {
            float T = Band;
            if (a is V pa && b is V pb)         // konce merania premietnuté na stupnice
            {
                foreach (var p in new[] { pa, pb })
                {
                    CrispLine(g.g, p.X, 0, p.X, T, MARK);
                    CrispLine(g.g, 0, p.Y, T, p.Y, MARK);
                }
            }
            if (canvas.Hover is V h)
            {
                CrispLine(g.g, h.X, 0, h.X, g.H, Tint(ACCENT, 150));
                CrispLine(g.g, 0, h.Y, g.W, h.Y, Tint(ACCENT, 150));
            }
            PaintMeasure(g);
            if (canvas.Hover is V hv)
            {
                Pill(g.g, hv.X, T + 14, Readout(kind, g.ppm, hv.X, g.px), ACCENT, T + 2, g.W - 2);
                var text = Readout(kind, g.ppm, hv.Y, g.px);
                Pill(g.g, T + 6 + PillSize(text).Width / 2, hv.Y, text, ACCENT);
            }
        }

        void PaintMeasure(G g)
        {
            if (!(a is V pa) || !(b is V pb)) return;
            float dx = Math.Abs(pb.X - pa.X), dy = Math.Abs(pb.Y - pa.Y);
            bool box = dx > 2 && dy > 2;
            var rect = new RectangleF(Math.Min(pa.X, pb.X), Math.Min(pa.Y, pb.Y), dx, dy);
            if (box) FillPath(g.g, RectPath(rect), Tint(ACCENT, 28), ACCENT, 1, true);
            StrokeLine(g.g, pa, pb, MARK, 2);
            Dot(g.g, pa, MARK);
            Dot(g.g, pb, MARK);
            if (box)
            {
                Pill(g.g, rect.MidX(), rect.Top - 14, Readout(kind, g.ppm, dx, g.px), ACCENT, 2, g.W - 2);
                var text = Readout(kind, g.ppm, dy, g.px);
                Pill(g.g, rect.Left - 8 - PillSize(text).Width / 2, rect.MidY(), text, ACCENT);
            }
            var mid = (pa + pb) / 2;
            Pill(g.g, mid.X, box ? mid.Y : mid.Y - 18, Readout(kind, g.ppm, Hypot(dx, dy), g.px), MARK, 2, g.W - 2, 14);
        }

        public override void Press(V p, bool shift)
        {
            a = p;
            b = p;
            dragging = true;
        }

        public override void Drag(V p, bool shift)
        {
            if (!dragging || !(a is V pa)) return;
            var nb = p;
            if (shift)
            {
                if (Math.Abs(nb.X - pa.X) >= Math.Abs(nb.Y - pa.Y)) nb.Y = pa.Y;
                else nb.X = pa.X;
            }
            b = nb;
        }

        public override void Release(V p)
        {
            dragging = false;
            if (a is V pa && b is V pb && Math.Abs(pb.X - pa.X) + Math.Abs(pb.Y - pa.Y) < 3)
            {
                a = null;           // obyčajný klik meranie zmaže
                b = null;
            }
        }

        public override void Nudge(V d)
        {
            if (b is V pb) b = pb + d;
        }

        public override void Rescale(float k)
        {
            if (!(a is V pa) || !(b is V pb)) return;
            var r = Rescaled(new[] { pa, pb }, k);
            a = r[0];
            b = r[1];
        }

        public override void Reset()
        {
            a = null;
            b = null;
        }
    }

    // MARK: - uhlomer

    sealed class ProtractorTool : Tool
    {
        public readonly bool full;
        public V? c;
        float R = 300;
        public float[] arms = { 0, 60 };
        int? active;
        V? grab;
        const float BASE = 40;          // pás pod vrcholom pri polkruhu

        public ProtractorTool(Canvas canvas, bool full) : base(canvas) { this.full = full; }

        public override SnapKind SnapKind => SnapKind.Angle;
        public override string Hint =>
            L("ťahaj ramená na strany uhla · napíš uhol = presne · ťahaj uhlomer = presun · koliesko = veľkosť · Shift = celé stupne · Esc = reset",
              "drag the arms onto the sides of the angle · type an angle = exact · drag the dial = move · wheel = size · Shift = whole degrees · Esc = reset");
        public override Field[] InputFields => new[] { new Field(L("Uhol", "Angle"), "°", false) };

        public override void Layout(float W, float H)
        {
            if (c != null) return;
            float free = H - DOCK_RESERVE;
            if (full)
            {
                R = Math.Min(W, free) / 2 - 40;
                c = new V(Rnd(W / 2), Rnd(free / 2));
            }
            else
            {
                R = Math.Min(W * 0.3f, (free - BASE) * 0.75f);
                c = new V(Rnd(W / 2), Rnd((free - R - BASE) / 2 + R));
            }
        }

        public override void Relayout() => c = null;
        public override string StaticKey() => c is V p ? $"{p.X},{p.Y},{R}" : "";

        public override void PaintStatic(G g)
        {
            if (!(c is V cc)) return;
            GraphicsPath body;
            if (full)
            {
                body = CirclePath(cc, R);
            }
            else
            {
                var pts = new List<V> { new V(cc.X + R, cc.Y + BASE) };
                for (int d = 0; d <= 180; d++) pts.Add(Polar(cc, d, R));
                pts.Add(new V(cc.X - R, cc.Y + BASE));
                body = PolygonPath(pts);
            }
            FillPath(g.g, body, g.th.Glass ? Tint(g.th.Panel, 190) : g.th.Panel, Tint(g.th.Ink, 90));

            int last = full ? 360 : 180;
            bool whole = R * Math.PI / 180 >= 3.2, halves = R * Math.PI / 360 >= 4;
            using (var ticks = new GraphicsPath())
            {
                for (int k = 0; k < last * 2 + (full ? 0 : 1); k++)          // po pol stupni
                {
                    float d = k / 2f;
                    float ln = k % 20 == 0 ? 22 : k % 10 == 0 ? 15 : k % 2 == 0 ? (whole ? 9 : 0) : (halves ? 5 : 0);
                    if (ln <= 0) continue;
                    ticks.StartFigure();
                    ticks.AddLine(Polar(cc, d, R), Polar(cc, d, R - ln));
                }
                using (var pen = new Pen(g.th.Ink, 1)) g.g.DrawPath(pen, ticks);
            }

            float size = R >= 300 ? 13 : 11;
            float rOut = R - 24 - size, rIn = rOut - size - 10;
            int step = rIn * Math.PI / 180 * 10 >= 28 ? 10 : 30;
            Font fOut = UiFont(size), fIn = UiFont(size - 2);
            for (int d = 0; d <= (full ? last - 1 : last); d += step)
            {
                var o = Polar(cc, d, rOut);
                if (full)
                {
                    CenteredText(g.g, d.ToString(), o, fOut, g.th.Ink);
                    continue;
                }
                o.Y = Math.Min(o.Y, cc.Y - 10);
                CenteredText(g.g, d.ToString(), o, fOut, g.th.Ink);
                var inner = Polar(cc, d, rIn);
                inner.Y = Math.Min(inner.Y, cc.Y - 10);
                CenteredText(g.g, (180 - d).ToString(), inner, fIn, g.th.Soft);
            }

            if (full)
            {
                StrokeLine(g.g, Polar(cc, 0, R - 24), Polar(cc, 180, R - 24), Tint(g.th.Soft, 140), 1, true);
                StrokeLine(g.g, Polar(cc, 90, R - 24), Polar(cc, 270, R - 24), Tint(g.th.Soft, 140), 1, true);
            }
            else
            {
                StrokeLine(g.g, Polar(cc, 0, R), Polar(cc, 180, R), g.th.Ink);
            }
        }

        static float Mod360(float v) => (v % 360 + 360) % 360;

        /// (uhol medzi ramenami, začiatok výseku)
        public (float, float) Angle()
        {
            float a0 = arms[0], a1 = arms[1];
            if (full)
            {
                float sweep = Mod360(a1 - a0);
                return (Math.Min(sweep, 360 - sweep), sweep <= 180 ? a0 : a1);
            }
            return (Math.Abs(a1 - a0), Math.Min(a0, a1));
        }

        public override void PaintDynamic(G g)
        {
            if (!(c is V cc)) return;
            var (angle, start) = Angle();
            float rw = Math.Min(90, R * 0.25f);
            int n = Math.Max(2, (int)Math.Ceiling(angle) + 1);
            var wedge = new List<V> { cc };
            for (int i = 0; i < n; i++) wedge.Add(Polar(cc, start + angle * i / (n - 1), rw));
            FillPath(g.g, PolygonPath(wedge), Tint(ACCENT, 50), ACCENT, 1.5f);

            float far = Hypot(g.W, g.H);
            foreach (var (i, color) in new[] { (0, ACCENT), (1, MARK) })
            {
                var knob = Polar(cc, arms[i], R + 10);
                StrokeLine(g.g, knob, Polar(cc, arms[i], far), Tint(color, 170), 1, true);    // predĺženie cez celú obrazovku
                StrokeLine(g.g, cc, knob, color, 2);
                FillPath(g.g, CirclePath(knob, 9), color, WHITE, 2);
            }
            StrokeLine(g.g, new V(cc.X - 12, cc.Y), new V(cc.X + 12, cc.Y), g.th.Ink);
            StrokeLine(g.g, new V(cc.X, cc.Y - 12), new V(cc.X, cc.Y + (full ? 12 : 4)), g.th.Ink);
            FillPath(g.g, CirclePath(cc, 3.5f), WHITE, g.th.Ink);

            var tag = g.th.Dark ? ACCENT : INK;      // na tmavom pozadí by tmavý štítok zanikol
            if (full)
                Pill(g.g, cc.X, cc.Y + R * 0.45f, $"∠ {Fmt(angle, 1)}°   ({Fmt(360 - angle, 1)}°)", tag, size: 16);
            else
                Pill(g.g, cc.X, cc.Y + BASE / 2 + 2, $"∠ {Fmt(angle, 1)}°", tag, size: 16);
        }

        int? ArmAt(V p)
        {
            if (!(c is V cc)) return null;
            float dx = p.X - cc.X, dy = cc.Y - p.Y;
            int? best = null;
            float bestD = 10;
            foreach (var i in new[] { 1, 0 })
            {
                double a = arms[i] * Math.PI / 180;
                double along = dx * Math.Cos(a) + dy * Math.Sin(a);
                double dist = Math.Abs(dy * Math.Cos(a) - dx * Math.Sin(a));
                if (along > 20 && dist < bestD)
                {
                    best = i;
                    bestD = (float)dist;
                }
            }
            return best;
        }

        float AngleAt(V p, double? step)
        {
            if (!(c is V cc)) return 0;
            float a = AngleOf(cc, p);
            if (step is double s) a = Rnd(a / (float)s) * (float)s;
            if (full) return Mod360(a);
            if (a < 0) a = a > -90 ? 0 : 180;
            return a;
        }

        public override void Press(V p, bool shift)
        {
            active = ArmAt(p);
            if (active == null && c is V cc) grab = p - cc;
        }

        public override void Drag(V p, bool shift)
        {
            if (active is int i) arms[i] = AngleAt(p, shift ? 1 : canvas.SnapValue());
            else if (grab is V gr) c = p - gr;
        }

        public override void Release(V p)
        {
            active = null;
            grab = null;
        }

        public override void Wheel(float steps) => R = Math.Min(1200, Math.Max(120, R + 20 * steps));

        public override void Nudge(V d)
        {
            if (c is V cc) c = cc + d;
        }

        public override void Reset()
        {
            arms = new float[] { 0, 60 };
            c = null;
        }

        public override void Rescale(float k) => c = null;     // na novej obrazovke znova do stredu; uhly ostanú

        public override string ApplyInput(List<double> nums)
        {
            if (nums.Count != 1) return L("Zadaj jedno číslo — uhol v stupňoch", "Enter one number — the angle in degrees");
            float a = (float)nums[0], a0 = arms[0];
            if (full)
            {
                arms[1] = Mod360(a0 + a);
                return null;
            }
            if (a < 0 || a > 180) return L("Pri polkruhu môže byť uhol 0 až 180°", "The half-circle protractor goes from 0 to 180°");
            float target = a0 + a <= 180 ? a0 + a : a0 - a;
            if (target < 0)                 // nezmestí sa na žiadnu stranu → základné rameno na 0°
            {
                arms[0] = 0;
                target = a;
            }
            arms[1] = target;
            return null;
        }

        public override Cursor Cursor(V p) => active != null || ArmAt(p) != null ? Cursors.Hand : Cursors.Default;
    }

    // MARK: - kruhová mierka (priemer)

    sealed class CircleTool : Tool
    {
        public V? c;
        V? grab;
        double? fixedD;             // zadaný priemer v mm

        public CircleTool(Canvas canvas) : base(canvas) { }

        public override string Hint =>
            L("prilož guľatú vec (mincu, skrutku, vrchnák) na stred · napíš priemer = vyznačí kruh · ťahaj = presun stredu · šípky = 0,1 mm · Esc = na stred",
              "put something round (a coin, screw, bottle cap) on the centre · type a diameter = mark a circle · drag = move centre · arrows = 0.1 mm · Esc = re-centre");
        public override Field[] InputFields => new[] { new Field(L("Priemer", "Diameter"), "mm", false) };

        public override string ApplyInput(List<double> nums)
        {
            if (nums.Count != 1 || nums[0] <= 0) return L("Zadaj jedno kladné číslo — priemer", "Enter one positive number — the diameter");
            fixedD = nums[0];
            return null;
        }

        public override void Layout(float W, float H)
        {
            if (c == null) c = new V(Rnd(W / 2), Rnd((H - DOCK_RESERVE) / 2));
        }

        public override void Relayout() => c = null;
        public override string StaticKey() => c is V p ? $"{p.X},{p.Y}" : "";

        public override void PaintStatic(G g)
        {
            if (!(c is V cc)) return;
            float far = new[] { new V(0, 0), new V(g.W, 0), new V(0, g.H), new V(g.W, g.H) }.Max(q => cc.Dist(q));
            if (g.th.Glass) FillPath(g.g, CirclePath(cc, 52 * g.ppm), Tint(g.th.Panel, 170));

            StrokeLine(g.g, new V(0, cc.Y), new V(g.W, cc.Y), Tint(g.th.Soft, 140), 1, true);
            StrokeLine(g.g, new V(cc.X, 0), new V(cc.X, g.H), Tint(g.th.Soft, 140), 1, true);

            for (int d = 2; d / 2f * g.ppm <= far; d += 2)      // priemer v mm
            {
                bool strong = d % 10 == 0;
                FillPath(g.g, CirclePath(cc, d / 2f * g.ppm), null, strong ? Tint(g.th.Ink, 160) : Tint(g.th.Ink, 45), strong ? 1.2f : 1);
            }

            var font = UiFont(10, true);
            for (int d = 10; d / 2f * g.ppm <= far; d += 10)
            {
                var at = Polar(cc, 45, d / 2f * g.ppm);
                var text = d.ToString();
                float w = TextSize(text, font).Width + 6;
                var box = new RectangleF(at.X - w / 2, at.Y - 7, w, 14);
                FillPath(g.g, RoundedPath(box, 4), g.th.Glass ? Tint(g.th.Panel, 235) : g.th.Panel);
                TextInRect(g.g, text, box, font, g.th.Ink);
            }
            FillPath(g.g, CirclePath(cc, 2), g.th.Ink);
        }

        public override void PaintDynamic(G g)
        {
            if (!(c is V cc)) return;
            if (fixedD is double fd)
            {
                float r = (float)fd / 2 * g.ppm;
                FillPath(g.g, CirclePath(cc, r), Tint(MARK, 25), MARK, 2);
                Pill(g.g, cc.X, cc.Y - r - 16, $"⌀ {Fmt(fd, 1)} mm", MARK, 2, g.W - 2, 14);
            }
            if (!(canvas.Hover is V h) || grab != null) return;
            float rh = h.Dist(cc);
            FillPath(g.g, CirclePath(cc, rh), null, ACCENT, 1.5f);
            Pill(g.g, h.X, h.Y - 22, $"⌀ {Fmt(2 * rh / g.ppm, 1)} mm", ACCENT, 2, g.W - 2, 14);
        }

        public override void Press(V p, bool shift)
        {
            if (c is V cc) grab = p - cc;
        }

        public override void Drag(V p, bool shift)
        {
            if (grab is V gr) c = p - gr;
        }

        public override void Release(V p) => grab = null;

        public override void Nudge(V d)
        {
            if (c is V cc) c = cc + d;
        }

        public override void Reset()
        {
            c = null;
            fixedD = null;
        }

        public override void Rescale(float k) => c = null;     // stred znova do stredu; zadaný priemer je v mm
    }

    // MARK: - polomer z 3 bodov

    /// Kružnica cez 3 body — polomer oblúka, zaobleného rohu alebo kusu kruhu.
    sealed class RadiusTool : Tool
    {
        List<V> pts = new List<V>();
        int? grab;

        public RadiusTool(Canvas canvas) : base(canvas) { }

        public override string Hint =>
            L("klikni 3 body na oblúku (roh mobilu, okraj kolesa…) · body sa dajú ťahať · šípky = posledný bod o 0,1 mm · ďalší klik = nové meranie · Esc = zmazať",
              "click 3 points on an arc (a phone corner, a wheel…) · points can be dragged · arrows = last point 0.1 mm · next click = new measurement · Esc = clear");

        int? PointAt(V p)
        {
            int i = pts.FindIndex(q => Near(p, q, 8));
            return i >= 0 ? i : (int?)null;
        }

        public override void Press(V p, bool shift)
        {
            if (PointAt(p) is int i)
            {
                grab = i;
                return;
            }
            if (pts.Count >= 3) pts = new List<V>();
            pts.Add(p);
            grab = pts.Count - 1;
        }

        public override void Drag(V p, bool shift)
        {
            if (grab is int i) pts[i] = p;
        }

        public override void Release(V p) => grab = null;

        public override void Nudge(V d)
        {
            if (pts.Count > 0) pts[pts.Count - 1] = pts[pts.Count - 1] + d;
        }

        public override void Reset() => pts = new List<V>();

        public override void Rescale(float k)
        {
            if (pts.Count > 0) pts = Rescaled(pts, k);
        }

        public override Cursor Cursor(V p) => PointAt(p) != null ? Cursors.SizeAll : Cursors.Cross;

        public override void PaintDynamic(G g)
        {
            var all = new List<V>(pts);
            bool preview = pts.Count < 3 && canvas.Hover != null && grab == null;
            if (preview && pts.Count > 0 && canvas.Hover is V h) all.Add(h);
            if (all.Count == 2)
            {
                StrokeLine(g.g, all[0], all[1], Tint(MARK, 150), 1, true);
            }
            else if (all.Count == 3)
            {
                if (Circle3(all[0], all[1], all[2], out var c, out var r))
                {
                    FillPath(g.g, CirclePath(c, r), preview ? (Color?)null : Tint(ACCENT, 22), ACCENT, 1.5f, preview);
                    StrokeLine(g.g, c, all[0], Tint(MARK, 170), 1, true);
                    StrokeLine(g.g, new V(c.X - 8, c.Y), new V(c.X + 8, c.Y), MARK, 1.5f);
                    StrokeLine(g.g, new V(c.X, c.Y - 8), new V(c.X, c.Y + 8), MARK, 1.5f);
                    bool onScreen = c.X >= 0 && c.X <= g.W && c.Y >= 0 && c.Y <= g.H - DOCK_RESERVE;
                    var at = onScreen ? c : (all[0] + all[1] + all[2]) / 3;
                    Pill(g.g, at.X, at.Y - 24, $"R {Fmt(r / g.ppm, 1)} mm", MARK, 2, g.W - 2, 15);
                    Pill(g.g, at.X, at.Y + 24, $"⌀ {Fmt(2 * r / g.ppm, 1)} mm", ACCENT, 2, g.W - 2);
                }
                else
                {
                    Pill(g.g, all[2].X, all[2].Y - 24, L("body sú na jednej priamke", "the points are on one line"), LABEL_DARK);
                }
            }
            foreach (var p in pts) Dot(g.g, p, MARK);
        }
    }

    // MARK: - uhlopriečka displeja

    /// Obdĺžnik cez displej mobilu/tabletu → uhlopriečka v palcoch + údaje o vlastnom displeji.
    sealed class DiagonalTool : Tool
    {
        float[] r;                          // [x0, y0, x1, y1]
        int[] grabbed = new int[0];         // indexy súradníc, ktoré sa ťahajú
        int[] last = { 2, 3 };

        public DiagonalTool(Canvas canvas) : base(canvas) { }

        public override SnapKind SnapKind => SnapKind.Point;
        public override string Hint =>
            L("polož mobil či tablet na displej a potiahni obdĺžnik cez jeho obrazovku · píš čísla = presný rozmer · rohy a hrany sa dajú ťahať · šípky = 0,1 mm · Esc = zmazať",
              "lay a phone or tablet on the screen and drag a box over its display · type numbers = exact size · corners and edges can be dragged · arrows = 0.1 mm · Esc = clear");
        public override Field[] InputFields =>
            new[] { new Field(L("Šírka", "Width"), "mm", false), new Field(L("Výška", "Height"), "mm", false) };

        int[] HandleAt(V p)
        {
            if (r == null) return new int[0];
            foreach (var (ix, iy) in new[] { (0, 1), (2, 1), (0, 3), (2, 3) })
            {
                if (Math.Abs(p.X - r[ix]) <= 10 && Math.Abs(p.Y - r[iy]) <= 10) return new[] { ix, iy };
            }
            float loX = Math.Min(r[0], r[2]), hiX = Math.Max(r[0], r[2]), loY = Math.Min(r[1], r[3]), hiY = Math.Max(r[1], r[3]);
            foreach (var ix in new[] { 0, 2 })
            {
                if (Math.Abs(p.X - r[ix]) <= 6 && p.Y > loY && p.Y < hiY) return new[] { ix };
            }
            foreach (var iy in new[] { 1, 3 })
            {
                if (Math.Abs(p.Y - r[iy]) <= 6 && p.X > loX && p.X < hiX) return new[] { iy };
            }
            return new int[0];
        }

        public override void Press(V p, bool shift)
        {
            grabbed = HandleAt(canvas.Raw ?? p);        // úchyty podľa skutočného kurzora, nie kroku
            if (grabbed.Length == 0)
            {
                r = new[] { p.X, p.Y, p.X, p.Y };
                grabbed = new[] { 2, 3 };
            }
            last = grabbed;
        }

        public override void Drag(V p, bool shift)
        {
            if (r == null) return;
            foreach (var i in grabbed) r[i] = i % 2 == 0 ? p.X : p.Y;
        }

        public override void Release(V p)
        {
            grabbed = new int[0];
            if (r != null && Math.Abs(r[2] - r[0]) < 3 && Math.Abs(r[3] - r[1]) < 3) r = null;
        }

        public override void Nudge(V d)
        {
            if (r == null) return;
            foreach (var i in last) r[i] += i % 2 == 0 ? d.X : d.Y;
        }

        public override void Reset() => r = null;

        public override void Rescale(float k)
        {
            if (r == null) return;
            var p = Rescaled(new[] { new V(r[0], r[1]), new V(r[2], r[3]) }, k);
            r = new[] { p[0].X, p[0].Y, p[1].X, p[1].Y };
        }

        public override string ApplyInput(List<double> nums)
        {
            if (nums.Count != 2 || nums.Min() <= 0) return L("Zadaj dve kladné čísla: šírka × výška", "Enter two positive numbers: width × height");
            float ppm = canvas.Ppm;
            var o = r != null ? new V(Math.Min(r[0], r[2]), Math.Min(r[1], r[3])) : canvas.InputOrigin();
            var b = new V(o.X + (float)nums[0] * ppm, o.Y + (float)nums[1] * ppm);
            var d = canvas.FitShift(new[] { o, b });
            r = new[] { o.X + d.X, o.Y + d.Y, b.X + d.X, b.Y + d.Y };
            last = new[] { 2, 3 };
            return null;
        }

        public override Cursor Cursor(V p)
        {
            var h = HandleAt(p);
            if (h.Length == 2) return Cursors.SizeAll;
            if (h.Length == 1) return h[0] % 2 == 0 ? Cursors.SizeWE : Cursors.SizeNS;
            return Cursors.Cross;
        }

        public override string StaticKey() => $"{canvas.Display.Key},{canvas.PxScale}";

        public override void PaintStatic(G g)
        {
            var s = canvas.Display;
            float wMM = s.Bounds.Width / g.px / g.ppm, hMM = s.Bounds.Height / g.px / g.ppm;
            int wPx = s.Bounds.Width, hPx = s.Bounds.Height;
            var source = Settings.Calib.ContainsKey(s.Key) ? L("kalibrované", "calibrated")
                : s.MM.Width >= 50 ? L("podľa EDID", "from EDID") : L("odhad, bez EDID", "estimate, no EDID");
            var title = $"{L("Tento displej", "This display")}: {Fmt(Hypot(wMM, hMM) / MM_PER_IN, 1)}″";
            var info = new[]
            {
                $"{Fmt(wMM, 1)} × {Fmt(hMM, 1)} mm · {wPx} × {hPx} px",
                $"{Fmt(g.ppm * MM_PER_IN * g.px, 1)} PPI · {AspectName(wPx, hPx)} · {source}",
            };
            Font fTitle = UiFont(16, true), fInfo = UiFont(12);
            float w = Math.Max(TextSize(title, fTitle).Width, info.Max(t => TextSize(t, fInfo).Width)) + 44;
            var card = new RectangleF((g.W - w) / 2, 24, w, 88);
            FillPath(g.g, RoundedPath(card, 14), Tint(g.th.Panel, 235), Tint(g.th.Ink, 40));
            TextInRect(g.g, title, new RectangleF(card.Left, card.Top + 12, w, 24), fTitle, g.th.Ink);
            for (int k = 0; k < info.Length; k++)
                TextInRect(g.g, info[k], new RectangleF(card.Left, card.Top + 42 + k * 20, w, 20), fInfo, g.th.Soft);
        }

        public override void PaintDynamic(G g)
        {
            if (canvas.Hover is V h && grabbed.Length == 0)
            {
                CrispLine(g.g, h.X, 0, h.X, g.H, Tint(ACCENT, 90));
                CrispLine(g.g, 0, h.Y, g.W, h.Y, Tint(ACCENT, 90));
            }
            if (r == null) return;
            var rect = new RectangleF(Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Abs(r[2] - r[0]), Math.Abs(r[3] - r[1]));
            float wMM = rect.Width / g.ppm, hMM = rect.Height / g.ppm;
            float diag = Hypot(wMM, hMM);
            FillPath(g.g, RectPath(rect), Tint(ACCENT, 30), ACCENT, 1.5f);
            StrokeLine(g.g, new V(rect.Left, rect.Top), new V(rect.Right, rect.Bottom), MARK, 2);
            foreach (var c in new[] { new V(rect.Left, rect.Top), new V(rect.Right, rect.Top), new V(rect.Left, rect.Bottom), new V(rect.Right, rect.Bottom) })
                FillPath(g.g, RectPath(new RectangleF(c.X - 4, c.Y - 4, 8, 8)), WHITE, ACCENT, 1.5f);
            Pill(g.g, rect.MidX(), rect.Top - 14, MmText(wMM), ACCENT, 2, g.W - 2);
            var text = MmText(hMM);
            Pill(g.g, rect.Left - 8 - PillSize(text).Width / 2, rect.MidY(), text, ACCENT, 2);
            if (diag > 1)
            {
                Pill(g.g, rect.MidX(), rect.MidY() - 16, $"{Fmt(diag / MM_PER_IN, 1)}″", MARK, size: 24);
                Pill(g.g, rect.MidX(), rect.MidY() + 20,
                     $"{Fmt(diag / MM_PER_IN, 2)}″ · {Fmt(diag / 10, 1)} cm · {AspectName(wMM, hMM)}", LABEL_DARK);
            }
        }
    }

    // MARK: - trasa (dĺžka a plocha)

    /// Lomená čiara: dĺžka trasy; po uzavretí obvod a plocha.
    sealed class PathTool : Tool
    {
        List<V> pts = new List<V>();
        bool closed, done;
        int? grab;

        public PathTool(Canvas canvas) : base(canvas) { }

        public override SnapKind SnapKind => SnapKind.Point;
        public override string Hint =>
            L("klikaj body · píš čísla = presný úsek · dvojklik/Enter = koniec · klik na prvý bod = plocha · Backspace = späť · Shift = po 15° · Esc = zmazať",
              "click points · type numbers = exact segment · double-click/Enter = finish · click the first point = area · Backspace = undo · Shift = 15° steps · Esc = clear");
        public override Field[] InputFields =>
            new[] { new Field(L("Dĺžka", "Length"), "mm", false), new Field(L("Uhol", "Angle"), "°", true) };

        bool Active => pts.Count > 0 && !(done || closed);

        int? PointAt(V p)
        {
            int i = pts.FindIndex(q => Near(p, q, 8));
            return i >= 0 ? i : (int?)null;
        }

        bool CanClose(V p) => Active && pts.Count >= 3 && PointAt(p) == 0;

        V Snapped(V p, bool shift)
        {
            if (!shift || !Active) return p;
            var o = pts[pts.Count - 1];
            float a = Rnd(AngleOf(o, p) / 15) * 15;
            return Polar(o, a, p.Dist(o));
        }

        public override void Press(V p, bool shift)
        {
            var raw = canvas.Raw ?? p;                  // body podľa skutočného kurzora, nie kroku
            if (CanClose(raw))
            {
                closed = true;
                return;
            }
            if (PointAt(raw) is int i)
            {
                grab = i;
                return;
            }
            if (!Active)
            {
                pts = new List<V>();
                closed = false;
                done = false;
            }
            pts.Add(Snapped(p, shift));
        }

        public override void Drag(V p, bool shift)
        {
            if (grab is int i) pts[i] = p;
        }

        public override void Release(V p) => grab = null;

        public override void Double(V p, bool shift)
        {
            if (pts.Count >= 2 && Active) done = true;
        }

        public override bool Key(ToolKey k)
        {
            switch (k)
            {
                case ToolKey.Enter:
                    if (!(pts.Count >= 2 && Active)) return false;
                    done = true;
                    break;
                case ToolKey.Backspace:
                    if (pts.Count == 0) return false;
                    if (closed) closed = false;
                    else pts.RemoveAt(pts.Count - 1);
                    done = false;
                    break;
            }
            return true;
        }

        public override void Nudge(V d)
        {
            if (pts.Count > 0) pts[pts.Count - 1] = pts[pts.Count - 1] + d;
        }

        public override void Rescale(float k)
        {
            if (pts.Count > 0) pts = Rescaled(pts, k);
        }

        public override void Reset()
        {
            pts = new List<V>();
            closed = false;
            done = false;
        }

        public override string ApplyInput(List<double> nums)
        {
            if (nums.Count < 1 || nums.Count > 2 || nums[0] <= 0)
                return L("Zadaj dĺžku (kladnú), prípadne aj uhol v stupňoch", "Enter a positive length, optionally an angle in degrees");
            if (!Active)
            {
                pts = new List<V> { canvas.InputOrigin() };
                closed = false;
                done = false;
            }
            var o = pts[pts.Count - 1];
            float angle;
            if (nums.Count == 2) angle = (float)nums[1];
            else if (canvas.Raw is V h && !Near(h, o, 2)) angle = AngleOf(o, h);        // smerom ku kurzoru
            else angle = 0;
            pts.Add(Polar(o, angle, (float)nums[0] * canvas.Ppm));
            return null;
        }

        public override Cursor Cursor(V p)
        {
            if (CanClose(p)) return Cursors.Hand;
            return PointAt(p) != null ? Cursors.SizeAll : Cursors.Cross;
        }

        public override void PaintDynamic(G g)
        {
            if (pts.Count == 0) return;
            var all = new List<V>(pts);
            bool rubber = Active && canvas.Hover != null && grab == null;
            if (rubber && canvas.Hover is V h) all.Add(Snapped(h, canvas.ShiftDown));
            var segs = new List<(V, V)>();
            for (int i = 0; i + 1 < all.Count; i++) segs.Add((all[i], all[i + 1]));
            if (closed) segs.Add((all[all.Count - 1], all[0]));
            float total = segs.Sum(s => s.Item1.Dist(s.Item2)) / g.ppm;

            if (closed) FillPath(g.g, PolygonPath(all), Tint(ACCENT, 40));
            for (int k = 0; k < segs.Count; k++)
            {
                var (a, b) = segs[k];
                StrokeLine(g.g, a, b, MARK, 2, rubber && k == segs.Count - 1);
                float length = a.Dist(b);
                if (length > 70)
                {
                    var mid = (a + b) / 2;
                    Pill(g.g, mid.X, mid.Y - 14, MmText(length / g.ppm), LABEL_DARK, size: 11);
                }
            }
            bool closable = canvas.Raw is V raw && CanClose(raw);
            for (int k = 0; k < pts.Count; k++)
                Dot(g.g, pts[k], k == 0 && closable ? ACCENT : MARK, k == 0 && closable ? 7 : 4.5f);
            if (closed)
            {
                float cross = segs.Sum(s => s.Item1.X * s.Item2.Y - s.Item2.X * s.Item1.Y);
                float area = Math.Abs(cross) / 2 / (g.ppm * g.ppm);
                var cen = all.Aggregate(V.Zero, (sum, q) => sum + q) / all.Count;
                Pill(g.g, cen.X, cen.Y, $"{L("obvod", "perimeter")} {MmText(total)} · {L("plocha", "area")} {AreaText(area)}",
                     MARK, 2, g.W - 2, 14);
            }
            else
            {
                var end = all[all.Count - 1];
                Pill(g.g, end.X, end.Y - 28, $"{L("spolu", "total")} {MmText(total)}", MARK, 2, g.W - 2, 14);
            }
        }

        /// Len pre --snapshot --demo.
        public void DemoClick(V p)
        {
            Press(p, false);
            Release(p);
        }
    }
}
