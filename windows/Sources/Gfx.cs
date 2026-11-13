// Gfx.cs — kresliace pomôcky nad GDI+ (súradnice v bodoch zhora dole, ako na Macu a Linuxe).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using static Regula.U;

namespace Regula
{
    /// Všetko, čo nástroj potrebuje na kreslenie.
    sealed class G
    {
        public Graphics g;
        public float W, H;
        public Theme th;
        public float ppm;       // body na mm
        public float px;        // pixely na bod (125 % = 1,25)
    }

    /// Kresliaca plocha v pamäti: 32-bitová DIB. Tvary kreslí GDI+ (s alfou), text GDI (ClearType), kopíruje sa cez BitBlt.
    sealed class Surface : IDisposable
    {
        public readonly int Width, Height;
        public readonly IntPtr DC, Bits;
        public readonly Bitmap Bitmap;
        readonly IntPtr dib, old;

        public Surface(int w, int h)
        {
            Width = Math.Max(1, w);
            Height = Math.Max(1, h);
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32,
            };
            dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out Bits, IntPtr.Zero, 0);
            DC = Native.CreateCompatibleDC(IntPtr.Zero);
            old = Native.SelectObject(DC, dib);
            Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, Bits);
        }

        public void CopyFrom(Surface src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Math.Min(Width, src.Width), Math.Min(Height, src.Height)));
            if (r.Width > 0 && r.Height > 0) Native.BitBlt(DC, r.X, r.Y, r.Width, r.Height, src.DC, r.X, r.Y, Native.SRCCOPY);
            Native.GdiFlush();
        }

        public void CopyFrom(Surface src) => CopyFrom(src, new Rectangle(0, 0, Width, Height));

        /// GDI pri ClearType zapíše do alfy nuly; na nepriehľadnom pozadí sa vráti plná alfa
        /// (inak by GDI+ pri polopriehľadnom prekrytí text zle prepočítalo a zčernel by).
        public unsafe void FixAlpha(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Width, Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            Native.GdiFlush();
            for (int y = r.Top; y < r.Bottom; y++)
            {
                byte* p = (byte*)Bits + (y * Width + r.Left) * 4 + 3;
                for (int x = 0; x < r.Width; x++, p += 4) *p = 255;
            }
        }

        /// PNG; bez alfy pri nepriehľadnom pozadí (text z GDI má v alfe nuly).
        public void Save(string path, bool alpha)
        {
            Native.GdiFlush();
            using (var view = new Bitmap(Width, Height, Width * 4, alpha ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppRgb, Bits))
                view.Save(path, ImageFormat.Png);
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            Native.SelectObject(DC, old);
            Native.DeleteDC(DC);
            Native.DeleteObject(dib);
        }
    }

    static class Gfx
    {
        /// Pixely na bod pri práve kreslenom obrázku — ostré čiary sa zarovnávajú na skutočné pixely.
        public static float Scale = 1;
        /// Kam ide text cez GDI (ClearType); null = text cez GDI+ (priehľadné pozadie potrebuje alfu).
        public static Surface TextSurface;

        // Segoe UI nemá všetky značky (⌀, ∠, ▴) — text s nimi ide celý písmom Segoe UI Symbol
        const string SYMBOLS = "⌀∠▴";
        static readonly Dictionary<(float, bool, bool), Font> fonts = new Dictionary<(float, bool, bool), Font>();
        static readonly StringFormat typo = MakeFormat();
        static readonly IntPtr measureDC = Native.CreateCompatibleDC(IntPtr.Zero);

        sealed class GdiFont
        {
            public IntPtr Handle;
            public int Ascent, Height;
        }

        static readonly Dictionary<(string, int, bool), GdiFont> gdiFonts = new Dictionary<(string, int, bool), GdiFont>();

        static StringFormat MakeFormat()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        public static Font UiFont(float size, bool bold = false) => GetFont(size, bold, false);

        static Font GetFont(float size, bool bold, bool symbol)
        {
            var key = (size, bold, symbol);
            if (fonts.TryGetValue(key, out var f)) return f;
            var family = symbol ? "Segoe UI Symbol" : "Segoe UI";
            try { f = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (ArgumentException) { f = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel); }
            fonts[key] = f;
            return f;
        }

        /// Písmo pre konkrétny text: so značkami, ktoré Segoe UI nemá, ide Segoe UI Symbol.
        static Font For(string s, Font f)
        {
            if (s.IndexOfAny(SYMBOLS.ToCharArray()) < 0 || f.Name == "Segoe UI Symbol") return f;
            return GetFont(f.Size, f.Bold, true);
        }

        /// Písmo GDI v skutočných pixeloch pri aktuálnej mierke.
        static GdiFont Hfont(Font f)
        {
            int px = Math.Max(1, (int)Math.Round(f.Size * Scale));
            var key = (f.Name, px, f.Bold);
            if (gdiFonts.TryGetValue(key, out var gf)) return gf;
            using (var dev = new Font(f.FontFamily, px, f.Style, GraphicsUnit.Pixel)) gf = new GdiFont { Handle = dev.ToHfont() };
            var old = Native.SelectObject(measureDC, gf.Handle);
            Native.GetTextMetricsW(measureDC, out var tm);
            Native.SelectObject(measureDC, old);
            gf.Ascent = tm.tmAscent;
            gf.Height = tm.tmHeight;
            gdiFonts[key] = gf;
            return gf;
        }

        static float GdiWidth(string line, GdiFont gf)
        {
            if (line.Length == 0) return 0;
            var old = Native.SelectObject(measureDC, gf.Handle);
            Native.GetTextExtentPoint32W(measureDC, line, line.Length, out var size);
            Native.SelectObject(measureDC, old);
            return size.cx / Scale;
        }

        static float GdipAscent(Font f) =>
            f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

        public static float LineHeight(Font f) => Hfont(f).Height / Scale;

        /// Rozmery textu v bodoch — meria GDI, rovnako ako potom kreslí.
        public static SizeF TextSize(string s, Font f)
        {
            f = For(s, f);
            var gf = Hfont(f);
            var lines = s.Split('\n');
            return new SizeF(lines.Max(line => GdiWidth(line, gf)), gf.Height / Scale * lines.Length);
        }

        static bool UseGdi(Color c) => TextSurface != null && c.A == 255;

        /// Jeden riadok cez GDI s ľavým horným rohom v bodoch (Graphics má len mierku, bez posunu).
        static void GdiOut(Graphics g, string s, float x, float y, GdiFont gf, Color c)
        {
            g.Flush(FlushIntention.Sync);
            var dc = TextSurface.DC;
            int px = (int)Math.Round(x * Scale), py = (int)Math.Round(y * Scale);
            var old = Native.SelectObject(dc, gf.Handle);
            Native.SetBkMode(dc, Native.TRANSPARENT);
            Native.SetTextColor(dc, c.R | (c.G << 8) | (c.B << 16));
            Native.ExtTextOutW(dc, px, py, 0, IntPtr.Zero, s, (uint)s.Length, IntPtr.Zero);
            Native.GetTextExtentPoint32W(dc, s, s.Length, out var size);
            Native.SelectObject(dc, old);
            TextSurface.FixAlpha(new Rectangle(px - 3, py - 1, size.cx + 6, gf.Height + 2));
        }

        /// Jeden riadok s ľavým horným rohom (hornou hranou riadku) na x, y.
        static void Line(Graphics g, string s, float x, float y, Font f, Color color)
        {
            if (UseGdi(color))
            {
                GdiOut(g, s, x, y, Hfont(f), color);
                return;
            }
            // GDI+: rovnaké účiarie ako pri GDI
            float baseline = y + Hfont(f).Ascent / Scale;
            using (var b = new SolidBrush(color)) g.DrawString(s, f, b, x, baseline - GdipAscent(f), typo);
        }

        /// Výška textu zalomeného na šírku.
        public static float WrappedHeight(string s, Font f, float width)
        {
            var gf = Hfont(For(s, f));
            var r = new Native.RECT { Right = (int)(width * Scale), Bottom = 100000 };
            var old = Native.SelectObject(measureDC, gf.Handle);
            Native.DrawTextW(measureDC, s, s.Length, ref r, Native.DT_WORDBREAK | Native.DT_NOPREFIX | Native.DT_CALCRECT);
            Native.SelectObject(measureDC, old);
            return r.Bottom / Scale;
        }

        /// Text s účiarím na y.
        public static void DrawText(Graphics g, string s, float x, float baseline, Font font, Color color)
        {
            font = For(s, font);
            Line(g, s, x, baseline - Hfont(font).Ascent / Scale, font, color);
        }

        /// Text vycentrovaný na bod.
        public static void CenteredText(Graphics g, string s, V p, Font font, Color color)
        {
            var size = TextSize(s, font);
            Line(g, s, p.X - size.Width / 2, p.Y - size.Height / 2, For(s, font), color);
        }

        /// Text (aj viacriadkový) vycentrovaný v obdĺžniku.
        public static void TextInRect(Graphics g, string s, RectangleF rect, Font font, Color color)
        {
            font = For(s, font);
            var lines = s.Split('\n');
            float lh = LineHeight(font), y = rect.MidY() - lh * lines.Length / 2;
            foreach (var line in lines)
            {
                Line(g, line, rect.MidX() - TextSize(line, font).Width / 2, y, font, color);
                y += lh;
            }
        }

        /// Text zalomený na šírku, zarovnaný vľavo.
        public static void WrappedText(Graphics g, string s, RectangleF rect, Font font, Color color)
        {
            font = For(s, font);
            if (UseGdi(color))
            {
                g.Flush(FlushIntention.Sync);
                var gf = Hfont(font);
                var dc = TextSurface.DC;
                var r = Native.RECT.FromF(rect, Scale);
                var old = Native.SelectObject(dc, gf.Handle);
                Native.SetBkMode(dc, Native.TRANSPARENT);
                Native.SetTextColor(dc, color.R | (color.G << 8) | (color.B << 16));
                Native.DrawTextW(dc, s, s.Length, ref r, Native.DT_WORDBREAK | Native.DT_NOPREFIX);
                Native.SelectObject(dc, old);
                TextSurface.FixAlpha(Rectangle.FromLTRB(r.Left - 3, r.Top - 1, r.Right + 3, r.Bottom + gf.Height));
                return;
            }
            var format = (StringFormat)typo.Clone();
            format.FormatFlags &= ~StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(color)) g.DrawString(s, font, b, rect, format);
        }

        public static void StrokeLine(Graphics g, V a, V b, Color color, float width = 1, bool dash = false)
        {
            using (var pen = new Pen(color, width))
            {
                if (dash)
                {
                    pen.DashPattern = new[] { 4f, 3f };         // GDI+ násobí šírkou čiary → 4w, 3w ako na Macu
                }
                else
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                }
                g.DrawLine(pen, a, b);
            }
        }

        /// Ostrá čiara široká 1 pixel bez vyhladzovania (čiarky stupníc, nitkový kríž).
        public static void CrispLine(Graphics g, float x0, float y0, float x1, float y1, Color color)
        {
            var state = g.Save();
            g.ResetTransform();
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            float s = Scale;
            using (var pen = new Pen(color, 1))
                g.DrawLine(pen, Rnd(x0 * s), Rnd(y0 * s), Rnd(x1 * s), Rnd(y1 * s));
            g.Restore(state);
        }

        /// Výplň obdĺžnika zarovnaná na celé pixely (ostré hrany).
        public static void FillRect(Graphics g, RectangleF r, Color color)
        {
            var state = g.Save();
            g.ResetTransform();
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float s = Scale;
            float x0 = Rnd(r.Left * s), y0 = Rnd(r.Top * s), x1 = Rnd(r.Right * s), y1 = Rnd(r.Bottom * s);
            using (var b = new SolidBrush(color)) g.FillRectangle(b, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
            g.Restore(state);
        }

        public static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float rad = Math.Min(radius, Math.Min(r.Width / 2, r.Height / 2));
            if (rad <= 0.01f)
            {
                path.AddRectangle(r);
                return path;
            }
            float d = 2 * rad;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath RectPath(RectangleF r)
        {
            var path = new GraphicsPath();
            path.AddRectangle(r);
            return path;
        }

        public static GraphicsPath CirclePath(V c, float r)
        {
            var path = new GraphicsPath();
            path.AddEllipse(c.X - r, c.Y - r, 2 * r, 2 * r);
            return path;
        }

        public static GraphicsPath PolygonPath(IEnumerable<V> pts, bool close = true)
        {
            var path = new GraphicsPath();
            path.AddLines(pts.Select(p => (PointF)p).ToArray());
            if (close) path.CloseFigure();
            return path;
        }

        public static void FillPath(Graphics g, GraphicsPath path, Color? fill, Color? stroke = null, float width = 1, bool dash = false)
        {
            using (path)
            {
                if (fill is Color f)
                {
                    using (var b = new SolidBrush(f)) g.FillPath(b, path);
                }
                if (stroke is Color s)
                {
                    using (var pen = new Pen(s, width))
                    {
                        pen.LineJoin = LineJoin.Round;
                        if (dash) pen.DashPattern = new[] { 4 / width, 3 / width };
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        public static SizeF PillSize(string text, float size = 12)
        {
            var s = TextSize(text, UiFont(size, true));
            return new SizeF(s.Width + 14, s.Height + 4);
        }

        /// Zaoblený štítok s bielym textom, vodorovne držaný medzi lo a hi.
        public static void Pill(Graphics g, float cx, float cy, string text, Color color,
                                float lo = -1e9f, float hi = 1e9f, float size = 12)
        {
            var s = PillSize(text, size);
            float x = Math.Min(Math.Max(cx - s.Width / 2, lo), hi - s.Width);
            var r = new RectangleF(x, cy - s.Height / 2, s.Width, s.Height);
            FillPath(g, RoundedPath(r, s.Height / 2), color);
            TextInRect(g, text, r, UiFont(size, true), WHITE);
        }

        public static void Dot(Graphics g, V p, Color color, float r = 4.5f) =>
            FillPath(g, CirclePath(p, r), color, WHITE, 1.5f);

        /// Pri priehľadnom pozadí podloží obsah, aby bol čitateľný.
        public static void Backdrop(Graphics g, RectangleF rect, Theme th)
        {
            if (th.Glass) FillPath(g, RoundedPath(rect, 16), Tint(th.Panel, 215));
        }

        /// Jemný tieň pod zaobleným panelom (GDI+ nevie rozmazať — niekoľko priesvitných vrstiev).
        public static void Shadow(Graphics g, RectangleF r, float radius, float dy = 4)
        {
            for (int i = 6; i >= 1; i--)
            {
                var layer = RectangleF.Inflate(new RectangleF(r.X, r.Y + dy, r.Width, r.Height), i * 1.6f, i * 1.6f);
                FillPath(g, RoundedPath(layer, radius + i * 1.6f), Color.FromArgb(9, 0, 0, 0));
            }
        }

        public static RectangleF Inset(RectangleF r, float dx, float dy) => RectangleF.Inflate(r, -dx, -dy);

        public static float MidX(this RectangleF r) => r.X + r.Width / 2;
        public static float MidY(this RectangleF r) => r.Y + r.Height / 2;
        public static bool Has(this RectangleF r, V p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

        /// Nastavenie Graphics pre kreslenie v bodoch.
        public static void Prepare(Graphics g, float scale, bool glass)
        {
            g.ResetTransform();
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
    }
}
