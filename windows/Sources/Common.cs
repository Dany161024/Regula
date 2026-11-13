// Common.cs — jazyk, nastavenia, monitory (EDID), mierka obrazovky, formátovanie, farby a tabuľky.
// Rovnaká logika ako regula.py (Linux) a macOS verzia, len vo Windows Forms / GDI+.
//
// Súradnice sú v bodoch (DIP, 1/96 palca pri 100 %), ako body na Macu: ppm = body na mm, px = pixely na bod.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Regula
{
    // MARK: - nastavenia (register HKCU\Software\Regula)

    static class Settings
    {
        /// pri --snapshot a --info sa nesiaha na skutočné nastavenia
        public static readonly bool Scratch = Environment.GetCommandLineArgs().Any(a => a == "--snapshot" || a == "--info");
        const string KEY = @"Software\Regula";
        static readonly Dictionary<string, object> memory = new Dictionary<string, object>();
        static Dictionary<string, double> calib;

        static object Get(string name)
        {
            if (Scratch) return memory.TryGetValue(name, out var v) ? v : null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KEY)) return k?.GetValue(name);
            }
            catch (Exception) { return null; }
        }

        static void Set(string name, object value)
        {
            if (Scratch)
            {
                memory[name] = value;
                return;
            }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KEY)) k.SetValue(name, value);
            }
            catch (Exception) { }
        }

        public static string Lang { get => Get("lang") as string; set => Set("lang", value); }
        public static string Tool { get => Get("tool") as string ?? "cm"; set => Set("tool", value); }
        public static string Bg { get => Get("bg") as string ?? "light"; set => Set("bg", value); }
        public static int Snap { get => Get("snap") is int i ? i : 0; set => Set("snap", value); }
        public static bool Fullscreen { get => !(Get("fullscreen") is int i) || i != 0; set => Set("fullscreen", value ? 1 : 0); }
        /// „x,y,šírka,výška" v pixeloch
        public static string WindowFrame { get => Get("windowFrame") as string; set => Set("windowFrame", value); }

        /// Kalibrácia: pixely na mm pre konkrétny monitor v konkrétnom rozlíšení (nezávisí od mierky Windowsu).
        public static Dictionary<string, double> Calib
        {
            get
            {
                if (calib != null) return calib;
                calib = new Dictionary<string, double>();
                if (Scratch) return calib;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(KEY + @"\Calib"))
                    {
                        if (k == null) return calib;
                        foreach (var name in k.GetValueNames())
                        {
                            if (double.TryParse(k.GetValue(name) as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                                calib[name] = v;
                        }
                    }
                }
                catch (Exception) { }
                return calib;
            }
            set
            {
                calib = new Dictionary<string, double>(value);
                if (Scratch) return;
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(KEY + @"\Calib", false);
                    using (var k = Registry.CurrentUser.CreateSubKey(KEY + @"\Calib"))
                    {
                        foreach (var kv in calib) k.SetValue(kv.Key, kv.Value.ToString("R", CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception) { }
            }
        }
    }

    // MARK: - monitory

    /// Jeden monitor: poloha v pixeloch, mierka Windowsu a fyzická veľkosť z EDID.
    sealed class Display
    {
        public IntPtr Handle;
        public string Device = "";              // \\.\DISPLAY1
        public string Name = "";                // názov z EDID, napr. „Color LCD"
        public Rectangle Bounds;                // pixely
        public Rectangle WorkArea;
        public float Scale = 1;                 // pixely na bod (125 % = 1,25)
        public SizeF MM;                        // fyzická veľkosť obrazu z EDID; 0 = nevie sa
        public string Key = "";                 // kľúč kalibrácie

        public static Display FromWindow(IntPtr hwnd) => FromMonitor(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));

        public static Display FromPoint(Point p) =>
            FromMonitor(Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST));

        public static List<Display> All()
        {
            var list = new List<Display>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
            {
                list.Add(FromMonitor(h));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static Display FromMonitor(IntPtr hmon)
        {
            var d = new Display { Handle = hmon };
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
            if (Native.GetMonitorInfo(hmon, ref info))
            {
                d.Device = info.szDevice ?? "";
                d.Bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
                d.WorkArea = Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            }
            else
            {
                d.Bounds = d.WorkArea = new Rectangle(0, 0, 1280, 800);
            }
            try
            {
                if (Native.GetDpiForMonitor(hmon, 0, out uint dpi, out _) == 0 && dpi >= 48) d.Scale = dpi / 96f;
            }
            catch (Exception) { }           // Windows staršie ako 8.1: 100 %

            byte[] edid = null;
            string pnp = "";
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE)) };
            for (uint i = 0; d.Device != "" && Native.EnumDisplayDevices(d.Device, i, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME); i++)
            {
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0 || i == 0)
                {
                    pnp = dd.DeviceString ?? "";
                    edid = ReadEdid(dd.DeviceID ?? "") ?? edid;
                    if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0) break;
                }
                dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            }
            string id = d.Device;
            if (edid != null && edid.Length >= 128)
            {
                d.MM = EdidSize(edid);
                d.Name = EdidName(edid);
                id = BitConverter.ToString(edid, 8, 8).Replace("-", "");     // výrobca, model, sériové číslo
            }
            if (d.Name == "") d.Name = pnp != "" ? pnp : d.Device.TrimStart('\\', '.');
            d.Key = $"{id}@{d.Bounds.Width}x{d.Bounds.Height}";
            return d;
        }

        static readonly Dictionary<string, byte[]> edidCache = new Dictionary<string, byte[]>();

        /// Po zmene monitorov (pripojenie, rozlíšenie) sa EDID číta znova.
        public static void ClearCache() => edidCache.Clear();

        /// \\?\DISPLAY#APP9CC4#4&2b6b1b2d&0&UID265988#{…} → EDID z registra (čítanie nepotrebuje správcu).
        static byte[] ReadEdid(string deviceId)
        {
            if (edidCache.TryGetValue(deviceId, out var cached)) return cached;
            var parts = deviceId.Split('#');
            byte[] edid = null;
            if (parts.Length >= 3)
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters"))
                        edid = k?.GetValue("EDID") as byte[];
                }
                catch (Exception) { }
            }
            edidCache[deviceId] = edid;
            return edid;
        }

        /// Veľkosť obrazu v mm: z prvého detailného časovania (na mm), inak zo základu EDID (na cm).
        static SizeF EdidSize(byte[] e)
        {
            int w = e[66] | ((e[68] & 0xF0) << 4), h = e[67] | ((e[68] & 0x0F) << 8);
            if (w >= 50 && h >= 30 && w < 5000) return new SizeF(w, h);
            if (e[21] > 0 && e[22] > 0) return new SizeF(e[21] * 10, e[22] * 10);
            return SizeF.Empty;
        }

        static string EdidName(byte[] e)
        {
            for (int o = 54; o <= 108; o += 18)
            {
                if (e[o] == 0 && e[o + 1] == 0 && e[o + 3] == 0xFC)
                    return Encoding.ASCII.GetString(e, o + 5, 13).Split('\n')[0].Trim();
            }
            return "";
        }
    }

    // MARK: - geometria

    struct V
    {
        public float X, Y;

        public V(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static readonly V Zero = new V(0, 0);
        public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y);
        public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y);
        public static V operator *(V a, float k) => new V(a.X * k, a.Y * k);
        public static V operator /(V a, float k) => new V(a.X / k, a.Y / k);
        public static implicit operator PointF(V v) => new PointF(v.X, v.Y);
        public float Dist(V o) => U.Hypot(X - o.X, Y - o.Y);
    }

    sealed class Theme
    {
        public Color Bg, Panel, Ink, Soft;
        public bool Glass;
        /// na tmavom pozadí by tmavý štítok zanikol
        public bool Dark => Math.Max(Bg.R, Math.Max(Bg.G, Bg.B)) < 128;
    }

    sealed class Field
    {
        public string Name, Unit;
        public bool Optional;

        public Field(string name, string unit, bool optional)
        {
            Name = name;
            Unit = unit;
            Optional = optional;
        }
    }

    // MARK: - spoločné funkcie a tabuľky (using static Regula.U)

    static class U
    {
        /// Jazyk rozhrania: --lang / REGULA_LANG / nastavenie „lang"; predvolene angličtina (tá ide na web).
        public static readonly bool SK = DetectSK();

        static bool DetectSK()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--lang");
            if (i >= 0 && i + 1 < args.Length) return args[i + 1] == "sk";
            var env = Environment.GetEnvironmentVariable("REGULA_LANG");
            if (!string.IsNullOrEmpty(env)) return env == "sk";
            return Settings.Lang == "sk";
        }

        /// Text v jazyku rozhrania.
        public static string L(string sk, string en) => SK ? sk : en;

        public const string VERSION = "1.0";
        public const float MM_PER_IN = 25.4f;
        public static readonly SizeF CARD_MM = new SizeF(85.60f, 53.98f);   // ID-1: platobná karta, občiansky, ISIC
        public const float CARD_CORNER_MM = 3.18f;
        public const float DOCK_RESERVE = 90;                                // miesto dole pre panel

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // MARK: mierka obrazovky

        /// Body na mm podľa fyzickej veľkosti, ktorú hlási monitor (EDID).
        public static float EdidPPM(Display d)
        {
            float wmm = d.MM.Width, hmm = d.MM.Height;
            if (wmm >= 50)
            {
                if (d.Bounds.Width < d.Bounds.Height != wmm < hmm) (wmm, hmm) = (hmm, wmm);    // monitor na výšku
                float ppm = d.Bounds.Width / wmm / d.Scale;
                if (ppm > 1 && ppm < 40) return ppm;
            }
            return 96 / MM_PER_IN;
        }

        public static float PtPerMM(Display d) =>
            Settings.Calib.TryGetValue(d.Key, out var pxPerMM) ? (float)pxPerMM / d.Scale : EdidPPM(d);

        // MARK: formátovanie

        public static string Fmt(double v, int dec)
        {
            var text = v.ToString("F" + dec, Inv);
            return SK ? text.Replace('.', ',') : text;
        }

        public static string FmtG(double v)
        {
            var text = v.ToString("0.#####", Inv);
            return SK ? text.Replace('.', ',') : text;
        }

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        public static string Frac16(double inch)
        {
            int n = (int)Math.Round(inch * 16, MidpointRounding.AwayFromZero);
            int whole = n / 16, rest = n % 16;
            if (rest == 0) return $"{whole}″";
            int g = Gcd(rest, 16);
            var fr = $"{rest / g}/{16 / g}";
            return whole > 0 ? $"{whole} {fr}″" : $"{fr}″";
        }

        /// Hodnota merania v jednotke nástroja; pt = body na obrazovke, px = pixely na bod.
        public static string Readout(string unit, float ppm, float pt, float px)
        {
            if (unit == "px") return $"{Rnd(pt * px):0} px";
            double mm = pt / ppm, inch = mm / MM_PER_IN;
            if (unit == "in") return $"{Fmt(inch, 3)}″ ≈ {Frac16(inch)}";
            if (unit == "dual") return $"{Fmt(mm, 1)} mm · {Fmt(inch, 3)}″";
            return $"{Fmt(mm, 1)} mm";
        }

        public static string MmText(double mm) => mm < 1000 ? $"{Fmt(mm, 1)} mm" : $"{Fmt(mm / 1000, 3)} m";

        public static string AreaText(double mm2) => mm2 < 100 ? $"{Fmt(mm2, 1)} mm²" : $"{Fmt(mm2 / 100, 2)} cm²";

        static readonly (double, int)[] ASPECTS = { (1, 1), (5, 4), (4, 3), (3, 2), (16, 10), (16, 9), (18, 9), (19.5, 9), (20, 9), (21, 9) };

        public static string AspectName(double w, double h)
        {
            double longer = Math.Max(w, h), shorter = Math.Min(w, h);
            if (shorter <= 0) return "";
            double r = longer / shorter;
            var best = ASPECTS.OrderBy(a => Math.Abs(a.Item1 / a.Item2 - r)).First();
            return Math.Abs(best.Item1 / best.Item2 - r) / r < 0.025 ? $"{FmtG(best.Item1)}:{best.Item2}" : $"{Fmt(r, 2)}:1";
        }

        public static string PinsText(int n)
        {
            if (!SK) return n == 1 ? "1 pin" : $"{n} pins";
            return n == 1 ? "1 pin" : n < 5 ? $"{n} piny" : $"{n} pinov";
        }

        // slovenská a česká klávesnica majú v hornom rade bez Shiftu písmená → preložia sa na číslice
        static readonly Dictionary<char, char> skDigits = new Dictionary<char, char>
        {
            ['+'] = '1', ['ľ'] = '2', ['ě'] = '2', ['š'] = '3', ['č'] = '4', ['ť'] = '5',
            ['ř'] = '5', ['ž'] = '6', ['ý'] = '7', ['á'] = '8', ['í'] = '9', ['é'] = '0',
        };

        public static char ToDigit(char c) => skDigits.TryGetValue(c, out var d) ? d : c;

        public static string ToDigits(string text) => new string(text.Select(ToDigit).ToArray());

        public static bool IsDigitKey(char c) => char.IsDigit(c) || skDigits.ContainsKey(c) || c == '.' || c == ',';

        static readonly Regex numberRe = new Regex(@"-?\d+(?:\.\d+)?|-?\.\d+");

        /// „20 x 50", „20,5×50", „ľé x ťé" → [20, 50]
        public static List<double> ParseNumbers(string text)
        {
            var t = ToDigits(text).Replace(',', '.');
            return numberRe.Matches(t).Cast<Match>().Select(m => double.Parse(m.Value, Inv)).ToList();
        }

        // MARK: geometria

        /// Zaokrúhlenie ako na Macu (polovica od nuly).
        public static float Rnd(float v) => (float)Math.Round(v, MidpointRounding.AwayFromZero);

        public static float Hypot(float x, float y) => (float)Math.Sqrt(x * x + y * y);

        public static float Clamp(float v, float lo, float hi) => Math.Min(Math.Max(v, lo), hi);

        /// Bod na kružnici; uhol v stupňoch proti smeru hodiniek od osi x (y obrazovky ide dole).
        public static V Polar(V c, float deg, float r)
        {
            double a = deg * Math.PI / 180;
            return new V(c.X + r * (float)Math.Cos(a), c.Y - r * (float)Math.Sin(a));
        }

        public static bool Near(V p, V q, float r) => Math.Abs(p.X - q.X) <= r && Math.Abs(p.Y - q.Y) <= r;

        /// Uhol bodu p od c v stupňoch (proti smeru hodiniek, y dole).
        public static float AngleOf(V c, V p) => (float)(Math.Atan2(c.Y - p.Y, p.X - c.X) * 180 / Math.PI);

        /// Kružnica cez 3 body → (stred, polomer); false, ak ležia na priamke.
        public static bool Circle3(V a, V b, V c, out V center, out float r)
        {
            double d = 2.0 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            center = V.Zero;
            r = 0;
            if (Math.Abs(d) < 1e-6) return false;
            double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
            double ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            double uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            center = new V((float)ux, (float)uy);
            r = Hypot(a.X - (float)ux, a.Y - (float)uy);
            return true;
        }

        // MARK: farby a témy

        public static Color Rgb(int r, int g, int b, int a = 255) => Color.FromArgb(a, r, g, b);

        public static Color Tint(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        public static readonly Color ACCENT = Rgb(31, 158, 222);     // Mint-Y Aqua — rovnaká modrá ako na Linuxe
        public static readonly Color MARK = Rgb(228, 69, 83);
        public static readonly Color INK = Rgb(34, 34, 42);
        public static readonly Color INK_SOFT = Rgb(112, 112, 124);
        public static readonly Color WHITE = Color.White;
        public static readonly Color LABEL_DARK = Rgb(70, 72, 86);

        public static readonly Dictionary<string, Theme> THEMES = new Dictionary<string, Theme>
        {
            ["light"] = new Theme { Bg = Rgb(251, 251, 246), Panel = Rgb(236, 236, 228), Ink = INK, Soft = INK_SOFT },
            ["dark"] = new Theme { Bg = Rgb(29, 30, 37), Panel = Rgb(43, 44, 54), Ink = Rgb(226, 226, 234), Soft = Rgb(150, 150, 166) },
            // rezacia podložka: zelená s bielymi čiarami
            ["green"] = new Theme { Bg = Rgb(46, 107, 79), Panel = Rgb(37, 90, 65), Ink = Rgb(242, 247, 238), Soft = Rgb(186, 215, 197) },
            ["clear"] = new Theme { Bg = Rgb(255, 255, 255, 16), Panel = Rgb(251, 251, 246, 225), Ink = INK, Soft = INK_SOFT, Glass = true },
        };
        public static readonly string[] THEME_ORDER = { "light", "dark", "green", "clear" };
        public static readonly Dictionary<string, string> BG_NAMES = new Dictionary<string, string>
        {
            ["light"] = L("Svetlé", "Light"), ["dark"] = L("Tmavé", "Dark"), ["green"] = L("Zelené", "Green"),
            ["clear"] = L("Priehľadné", "Clear"),
        };

        // krok prichytávania pre úroveň 1–3 (0 = voľne): (hodnota v jednotke, popis)
        public static readonly Dictionary<string, (double, string)[]> SNAP = new Dictionary<string, (double, string)[]>
        {
            ["mm"] = new[] { (0.5, L("0,5 mm", "0.5 mm")), (1.0, "1 mm"), (5.0, "5 mm") },
            ["in"] = new[] { (1.0 / 32, "1/32″"), (1.0 / 16, "1/16″"), (1.0 / 4, "1/4″") },
            ["px"] = new[] { (5.0, "5 px"), (10.0, "10 px"), (50.0, "50 px") },
            ["deg"] = new[] { (0.5, L("0,5°", "0.5°")), (1.0, "1°"), (5.0, "5°") },
        };

        public static readonly (string, string)[] TOOLS =
        {
            ("cm", L("Centimetre", "Centimetres")), ("in", L("Palce", "Inches")), ("px", L("Pixely", "Pixels")),
            ("dual", L("cm + palce", "cm + in")), ("grid", L("Mm papier", "Mm paper")),
            ("prot", L("Uhlomer", "Protractor")), ("prot360", L("Uhlomer 360°", "Protractor 360°")),
            ("circle", L("Priemer", "Diameter")), ("radius", L("Polomer", "Radius")),
            ("diag", L("Uhlopriečka", "Diagonal")), ("path", L("Trasa", "Path")),
            ("screw", L("Skrutky", "Screws")), ("pitch", L("Rozteč", "Pin pitch")),
        };
        public static readonly HashSet<string> GROUP_STARTS = new HashSet<string> { "prot", "diag", "screw" };  // pred nimi je oddeľovač

        public static TV Get<TK, TV>(this Dictionary<TK, TV> d, TK key, TV fallback) => d.TryGetValue(key, out var v) ? v : fallback;
    }
}
