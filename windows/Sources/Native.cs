// Native.cs — volania Win32: monitory (EDID, DPI), kreslenie s priehľadnosťou cez DIB a DWM.
using System;
using System.Runtime.InteropServices;

namespace Regula
{
    static class Native
    {
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_ERASEBKGND = 0x0014;
        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        public const int DISPLAY_DEVICE_ACTIVE = 1;
        public const int SRCCOPY = 0x00CC0020;
        public const int ATTACH_PARENT_PROCESS = -1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public static RECT FromF(System.Drawing.RectangleF r, float s) => new RECT
            {
                Left = (int)Math.Round(r.Left * s), Top = (int)Math.Round(r.Top * s),
                Right = (int)Math.Round(r.Right * s), Bottom = (int)Math.Round(r.Bottom * s),
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int pid);

        // --- kreslenie: 32-bitová DIB s alfou → BitBlt do okna (alfa ostane zachovaná pre DWM)

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits,
                                                     IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

        [DllImport("gdi32.dll")]
        public static extern bool GdiFlush();

        // --- text cez GDI (ClearType ako v ostatných appkách Windowsu)

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TEXTMETRIC
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
                       tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        public const int TRANSPARENT = 1;
        public const uint DT_LEFT = 0, DT_WORDBREAK = 0x10, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800;

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, string text, uint count, IntPtr dx);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out SIZE size);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC tm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

        // --- priehľadné pozadie: DWM skladá okno podľa alfy v jeho pixeloch

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_BLURBEHIND
        {
            public int dwFlags;
            public bool fEnable;
            public IntPtr hRgnBlur;
            public bool fTransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND bb);

        /// Alfa v pixeloch okna sa použije (bez rozmazania: oblasť rozmazania je prázdna).
        public static void SetAlphaComposition(IntPtr hwnd, bool on)
        {
            IntPtr region = CreateRectRgn(0, 0, -1, -1);
            var bb = new DWM_BLURBEHIND { dwFlags = 1 | 2, fEnable = on, hRgnBlur = region };
            try { DwmEnableBlurBehindWindow(hwnd, ref bb); }
            catch (DllNotFoundException) { }
            DeleteObject(region);
        }
    }
}
