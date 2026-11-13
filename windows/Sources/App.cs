// App.cs — Regula pre Windows: okno (celá obrazovka alebo okno), menu, jazyk a vývojové snímky.
//
//   Regula.exe                              normálne spustenie
//   Regula.exe --snapshot out.png [voľby]   vykreslí plochu do PNG a skončí (overovanie cez SSH, bez okna)
//       --tool cm|in|px|dual|grid|prot|prot360|circle|radius|diag|path|screw|pitch
//       --bg light|dark|green|clear   --snap 0–3   --demo (ukážkový stav)   --typing (otvorené políčka)
//       --size 1280x800 (body)   --scale 1.25   --mm 286x179 (fyzická veľkosť namiesto EDID)
//       --lang en|sk   --calib (kalibrácia)   --menu (otvorené menu)   --hide (skrytý panel)
//   Regula.exe --info out.txt               zapíše monitory, mierku Windowsu a EDID
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using static Regula.U;

namespace Regula
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--snapshot"))
            {
                Snapshot.Run(args);
                return;
            }
            if (args.Contains("--info"))
            {
                Snapshot.Info(args);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), L("Regula — chyba", "Regula — error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
        }
    }

    /// Okno appky: celá obrazovka = bezrámové okno cez monitor, inak bežné okno, ktoré sa dá zväčšovať.
    sealed class MainForm : Form
    {
        readonly Canvas canvas;
        bool fullscreen;
        bool applying;              // práve sa mení poloha/rám — nereagovať na vlastné zmeny
        bool glassOn;
        Surface back;               // kresliaci zásobník: DIB s alfou, cez BitBlt do okna

        public MainForm()
        {
            Text = "Regula";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, false);

            var display = Display.FromPoint(Cursor.Position);
            canvas = new Canvas(display, display.Scale, display.Bounds.Width / display.Scale, display.Bounds.Height / display.Scale);
            canvas.Redraw = Invalidate;
            canvas.SetCursor = c =>
            {
                if (Cursor != c) Cursor = c;
            };
            canvas.OnToggleFullscreen = ToggleFullscreen;
            canvas.OnQuit = Close;
            canvas.MenuEntries = () => AppMenu.Build(this);
            SetFullscreen(Settings.Fullscreen, display);
        }

        public void ToggleFullscreen() => SetFullscreen(!fullscreen);

        // MARK: - celá obrazovka ↔ okno

        void SetFullscreen(bool on, Display d = null)
        {
            d = d ?? (IsHandleCreated ? Display.FromWindow(Handle) : Display.FromPoint(Cursor.Position));
            applying = true;
            if (on)
            {
                if (!fullscreen && IsHandleCreated && FormBorderStyle != FormBorderStyle.None && WindowState == FormWindowState.Normal) SaveFrame();
                MinimumSize = Size.Empty;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = d.Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                var frame = ParseFrame(Settings.WindowFrame);
                if (frame.Width < 200 || !Display.All().Any(x => x.WorkArea.IntersectsWith(frame)))
                {
                    var v = d.WorkArea;
                    var min = canvas.MinimumSize();
                    int w = (int)Math.Min(v.Width, Math.Max(min.Width * d.Scale, v.Width * 0.85f));
                    int h = (int)Math.Min(v.Height, Math.Max((min.Height + 32) * d.Scale, v.Height * 0.85f));
                    frame = new Rectangle(v.X + (v.Width - w) / 2, v.Y + (v.Height - h) / 2, w, h);
                }
                Bounds = frame;
            }
            fullscreen = on;
            Settings.Fullscreen = on;
            canvas.Dock.Find("window").Checked = !on;
            applying = false;
            UpdateMinimumSize();
            SyncGeometry();
        }

        static Rectangle ParseFrame(string text)
        {
            var parts = (text ?? "").Split(',');
            if (parts.Length != 4) return Rectangle.Empty;
            var n = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return Rectangle.Empty;
            }
            return new Rectangle(n[0], n[1], n[2], n[3]);
        }

        void SaveFrame()
        {
            var b = Bounds;
            Settings.WindowFrame = string.Join(",", new[] { b.X, b.Y, b.Width, b.Height }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }

        void UpdateMinimumSize()
        {
            if (fullscreen || !IsHandleCreated)
            {
                MinimumSize = Size.Empty;
                return;
            }
            float s = canvas.PxScale;
            var min = canvas.MinimumSize();
            MinimumSize = SizeFromClientSize(new Size((int)Math.Ceiling(min.Width * s), (int)Math.Ceiling(min.Height * s)));
        }

        /// Na ďalší monitor (celá obrazovka sa nedá potiahnuť myšou).
        public void MoveToNextDisplay()
        {
            var all = Display.All();
            if (all.Count < 2) return;
            int i = all.FindIndex(x => x.Device == canvas.Display.Device);
            var next = all[(i + 1) % all.Count];
            if (fullscreen)
            {
                applying = true;
                Bounds = next.Bounds;
                applying = false;
            }
            else
            {
                var v = next.WorkArea;
                int w = Math.Min(Width, v.Width), h = Math.Min(Height, v.Height);
                Bounds = new Rectangle(v.X + (v.Width - w) / 2, v.Y + (v.Height - h) / 2, w, h);
            }
            SyncGeometry();
        }

        /// Monitor, mierka a veľkosť plochy → Canvas (merania ostanú v mm aj na inom monitore).
        void SyncGeometry()
        {
            if (!IsHandleCreated || applying) return;
            var d = Display.FromWindow(Handle);
            if (fullscreen && Bounds != d.Bounds && WindowState == FormWindowState.Normal)
            {
                applying = true;
                Bounds = d.Bounds;
                applying = false;
            }
            var cs = ClientSize;
            if (cs.Width <= 0 || cs.Height <= 0) return;
            canvas.SetGeometry(d, d.Scale, cs.Width / d.Scale, cs.Height / d.Scale);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glassOn = false;
            UpdateMinimumSize();
            SyncGeometry();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncGeometry();
            if (!fullscreen && !applying && WindowState == FormWindowState.Normal && IsHandleCreated) SaveFrame();
            Invalidate();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            SyncGeometry();
            if (!fullscreen && !applying && WindowState == FormWindowState.Normal && IsHandleCreated) SaveFrame();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                // nová mierka monitora: Windows navrhne nový rám; plochu si prepočítame sami (WinForms nech nič neškáluje)
                var r = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                if (!fullscreen) Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                UpdateMinimumSize();
                SyncGeometry();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Native.WM_DISPLAYCHANGE)
            {
                Display.ClearCache();
                base.WndProc(ref m);
                SyncGeometry();
                return;
            }
            if (m.Msg == Native.WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // MARK: - kreslenie

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var cs = ClientSize;
            if (cs.Width <= 0 || cs.Height <= 0) return;
            bool glass = THEMES[canvas.Bg].Glass;
            if (glass != glassOn)           // priehľadné pozadie: DWM použije alfu pixelov okna
            {
                Native.SetAlphaComposition(Handle, glass);
                glassOn = glass;
            }
            if (back == null || back.Width != cs.Width || back.Height != cs.Height)
            {
                back?.Dispose();
                back = new Surface(cs.Width, cs.Height);
            }
            canvas.Render(back);
            var hdc = e.Graphics.GetHdc();
            try { Native.BitBlt(hdc, 0, 0, cs.Width, cs.Height, back.DC, 0, 0, Native.SRCCOPY); }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            back?.Dispose();
            back = null;
            base.OnFormClosed(e);
        }

        // MARK: - myš a klávesnica

        V ToV(Point p) => new V(p.X / canvas.PxScale, p.Y / canvas.PxScale);

        protected override void OnMouseMove(MouseEventArgs e) => canvas.MouseMove(ToV(e.Location), (e.Button & MouseButtons.Left) != 0);
        protected override void OnMouseDown(MouseEventArgs e) => canvas.MouseDown(ToV(e.Location), e.Button, e.Clicks);
        protected override void OnMouseUp(MouseEventArgs e) => canvas.MouseUp(ToV(e.Location), e.Button);
        protected override void OnMouseWheel(MouseEventArgs e) => canvas.Wheel(e.Delta / 120f);

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            canvas.MouseLeave();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0;
            if (!alt && canvas.KeyDown(key, shift, ctrl)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            canvas.KeyChar(e.KeyChar);
            e.Handled = true;
        }

        // MARK: - menu

        /// Jazyk sa prejaví po reštarte — appka sa hneď znova spustí.
        public void SetLanguage(bool sk)
        {
            Settings.Lang = sk ? "sk" : "en";
            if (sk == SK) return;
            Process.Start(Application.ExecutablePath);
            Close();
        }

        public void About() =>
            MessageBox.Show(this,
                $"Regula {VERSION}\n{L("Pravítka na obrazovku v skutočnej veľkosti.", "Screen rulers at real size.")}\n\nregula.is-my.app\n{L("Autor", "Made by")} Dany161024",
                L("O aplikácii Regula", "About Regula"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// Spoločné menu (tlačidlo Menu v paneli a pravé tlačidlo myši).
    static class AppMenu
    {
        public static List<PopupMenu.Entry> Build(MainForm form)
        {
            Action run(Action<MainForm> a) => () =>
            {
                if (form != null) a(form);
            };
            bool many = form == null || Display.All().Count > 1;
            return new List<PopupMenu.Entry>
            {
                new PopupMenu.Entry { Title = L("Okno / celá obrazovka", "Window / fullscreen"), Shortcut = "F11",
                                      Action = run(f => f.ToggleFullscreen()) },
                new PopupMenu.Entry { Title = L("Na ďalší monitor", "Move to next display"), Enabled = many, Action = run(f => f.MoveToNextDisplay()) },
                new PopupMenu.Entry { Separator = true },
                new PopupMenu.Entry { Title = "English", Checked = !SK, Action = run(f => f.SetLanguage(false)) },
                new PopupMenu.Entry { Title = "Slovenčina", Checked = SK, Action = run(f => f.SetLanguage(true)) },
                new PopupMenu.Entry { Separator = true },
                new PopupMenu.Entry { Title = L("V ponuke Štart", "In the Start menu"), Checked = StartMenu.Exists, Action = run(f => StartMenu.Toggle(f)) },
                new PopupMenu.Entry { Title = L("O aplikácii Regula", "About Regula"), Action = run(f => f.About()) },
                new PopupMenu.Entry { Title = L("Zavrieť", "Quit"), Shortcut = "Q", Action = run(f => f.Close()) },
            };
        }
    }

    /// Odkaz v ponuke Štart (Regula je jeden .exe bez inštalátora).
    static class StartMenu
    {
        static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Regula.lnk");

        public static bool Exists
        {
            get
            {
                try { return File.Exists(LinkPath); }
                catch (Exception) { return false; }
            }
        }

        public static void Toggle(IWin32Window owner)
        {
            try
            {
                if (Exists)
                {
                    File.Delete(LinkPath);
                    return;
                }
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                                      new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                                      new object[] { L("Pravítka na obrazovku", "Screen rulers") });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.ReleaseComObject(link);
                Marshal.ReleaseComObject(shell);
                MessageBox.Show(owner, L("Regula je v ponuke Štart. Ak presunieš Regula.exe, pridaj ju znova.",
                                         "Regula is in the Start menu. If you move Regula.exe, add it again."),
                                "Regula", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(owner, e.Message, "Regula", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // MARK: - vývojové snímky (cez SSH sa nedá urobiť snímka obrazovky)

    static class Snapshot
    {
        static string[] args = new string[0];

        static string Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static bool Flag(string name) => args.Contains(name);

        static (float, float)? Pair(string name)
        {
            var parts = Arg(name)?.Split('x');
            if (parts == null || parts.Length != 2) return null;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) return null;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return null;
            return (a, b);
        }

        static void Say(string text)
        {
            if (Arg("--log") is string log)
            {
                File.AppendAllText(log, text + Environment.NewLine);
                return;
            }
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            try { Console.WriteLine(text); }
            catch (Exception) { }
        }

        /// Ako rýchlo sa kreslí pri pohybe myši: myš prejde po ploche, každý krok jeden celý snímok.
        static void Bench(Canvas c, Surface target, int frames)
        {
            var times = new List<double>();
            var sw = new Stopwatch();
            for (int i = 0; i < frames; i++)
            {
                float t = (float)i / frames;
                c.MouseMove(new V(80 + t * (c.W - 200), 70 + t * (c.H - 220)), false);
                sw.Restart();
                c.Render(target);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Say($"bench {c.ToolName}/{c.Bg}: {frames} snímok, medián {times[frames / 2]:0.0} ms, 90 % {times[frames * 9 / 10]:0.0} ms, max {times[frames - 1]:0.0} ms");
        }

        public static void Info(string[] a)
        {
            args = a;
            var lines = new StringBuilder();
            foreach (var d in Display.All())
            {
                lines.AppendLine($"{d.Device} \"{d.Name}\" {d.Bounds.Width}x{d.Bounds.Height} @ {d.Bounds.X},{d.Bounds.Y} " +
                                 $"scale {d.Scale.ToString(CultureInfo.InvariantCulture)} EDID {d.MM.Width}x{d.MM.Height} mm " +
                                 $"→ {(EdidPPM(d) * d.Scale).ToString("0.000", CultureInfo.InvariantCulture)} px/mm, key {d.Key}");
            }
            var path = Arg("--info");
            if (path != null) File.WriteAllText(path, lines.ToString());
            Say(lines.ToString());
        }

        public static void Run(string[] a)
        {
            args = a;
            var out_ = Arg("--snapshot");
            var d = Display.FromPoint(Point.Empty);
            if (float.TryParse(Arg("--scale") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)) d.Scale = sc;
            float w = d.Bounds.Width / d.Scale, h = d.Bounds.Height / d.Scale;
            if (Pair("--size") is (float, float) size) (w, h) = size;
            int pw = (int)Math.Round(w * d.Scale), ph = (int)Math.Round(h * d.Scale);
            if (Pair("--mm") is (float, float) mm)
            {
                d.MM = new SizeF(mm.Item1, mm.Item2);
                d.Bounds = new Rectangle(0, 0, pw, ph);
                d.Key = "snapshot";
            }
            var canvas = new Canvas(d, d.Scale, w, h) { MenuEntries = () => AppMenu.Build(null) };
            canvas.SetTool(Arg("--tool") ?? "cm");
            canvas.SetBackground(Arg("--bg") ?? "light");
            canvas.SetSnap(int.TryParse(Arg("--snap"), out var snap) ? snap : 0);
            canvas.LayoutPanels();
            if (Flag("--hide")) canvas.ToggleDock();
            if (Flag("--demo")) Demo(canvas);
            if (Flag("--typing"))
            {
                canvas.InputBar.Open("20");
                canvas.InputBar.Prefill("20", "50");
                canvas.LayoutPanels();
            }
            if (Flag("--menu"))
            {
                canvas.Dock.Hovered = canvas.Dock.Items.FindIndex(i => i.Id == "more");
                canvas.Dock.Items.First(i => i.Id == "more").Action(canvas.Dock.RectOf(canvas.Dock.Hovered.Value));
            }
            if (Flag("--calib")) canvas.Calibrate();
            using (var target = new Surface(pw, ph))
            {
                canvas.Render(target);
                target.Save(out_, THEMES[canvas.Bg].Glass);
                if (int.TryParse(Arg("--bench") ?? "", out var frames) && frames > 0) Bench(canvas, target, frames);
            }
            Say($"snímka: {out_} ({pw}×{ph} px, {Fmt(canvas.Ppm * d.Scale, 3)} px/mm, mierka {d.Scale}), " +
                $"panel: {(canvas.Dock.Compact ? "len ikonky" : "s popismi")}, {Math.Round(canvas.Dock.PanelSize().Width)} bodov");
        }

        /// Ukážkový stav ako na webe: meranie, ramená, kurzor nad skrutkou…
        static void Demo(Canvas c)
        {
            float W = c.W, H = c.H, ppm = c.Ppm;
            c.Tool.Layout(W, H);
            switch (c.Tool)
            {
                case RulerTool t:
                    c.Raw = new V(Rnd(W * 0.3f), Rnd(H * 0.28f));
                    t.ApplyInput(t.Unit == "in" ? new List<double> { 2.4, 1.4 } : t.Unit == "px" ? new List<double> { 240, 140 } : new List<double> { 60, 35 });
                    c.Hover = t.b;
                    break;
                case ProtractorTool t:
                    t.arms = t.full ? new[] { 20f, 152 } : new[] { 12f, 64.5f };
                    break;
                case DiagonalTool t:
                    c.Raw = new V(Rnd(W * 0.62f), Rnd(H * 0.1f));
                    t.ApplyInput(new List<double> { 64, 138 });
                    c.Raw = null;
                    break;
                case PathTool t:
                    foreach (var (x, y) in new[] { (0.29f, 0.6f), (0.4f, 0.26f), (0.61f, 0.21f), (0.74f, 0.52f), (0.55f, 0.7f), (0.29f, 0.6f) })
                    {
                        var p = new V(Rnd(W * x), Rnd(H * y));
                        c.Raw = p;
                        t.DemoClick(p);
                    }
                    c.Raw = null;
                    break;
                case RadiusTool t:
                    {
                        var center = new V(W * 0.5f, H * 0.44f);
                        float r = 31 * ppm;
                        foreach (var ang in new[] { 25f, 80, 150 })
                        {
                            var p = Polar(center, ang, r);
                            t.Press(p, false);
                            t.Release(p);
                        }
                        break;
                    }
                case CircleTool t:
                    if (t.c is V cc) c.Hover = cc + new V(23.25f / 2 * ppm * 0.8f, -23.25f / 2 * ppm * 0.6f);
                    break;
                case ScrewTool _:
                    {
                        var (_, oy, cols) = ScrewTool.Geom(W, H, ppm);
                        c.Hover = new V(cols[3].Item1, oy + 40 * ppm);
                        break;
                    }
                case PitchTool _:
                    {
                        var (_, x0, oy) = PitchTool.Geom(W, H, ppm);
                        c.Hover = new V(x0 + 7 * 2.0f * ppm, oy + 2 * PitchTool.ROW_MM * ppm + 4 * ppm);
                        break;
                    }
            }
        }
    }
}
