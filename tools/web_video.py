"""Demo video Reguly pre web: snímky z appky (offscreen) + kurzor → ffmpeg (MP4 H.264 + WebM VP9)."""
import math
import os
import subprocess
import sys
import tempfile

os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["XDG_CONFIG_HOME"] = tempfile.mkdtemp()
sys.path.insert(0, os.path.expanduser("~/regula"))
import regula  # noqa: E402
from PySide6.QtCore import QPointF as P, QRect, Qt  # noqa: E402
from PySide6.QtGui import QColor, QImage, QPainter, QPen, QPolygonF  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

OUT = os.path.expanduser("~/regula/web/media")
W, H, FPS = 1280, 720, 30
PPM = 1920 / 309
regula.edid_ppm = lambda s: PPM


class FakeScreen:
    def name(self): return "eDP"
    def geometry(self): return QRect(0, 0, 1920, 1080)


regula.DiagonalTool.screen_info = lambda self: (FakeScreen(), QRect(0, 0, 1920, 1080))

app = QApplication(["regula"])
v = regula.Regula()
v.setGeometry(0, 0, 1920, 1080)
v.show()
app.processEvents()
NM = Qt.KeyboardModifier.NoModifier

common = ["-c:v"]
encoders = [
    ["ffmpeg", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", f"{W}x{H}", "-r", str(FPS),
     "-i", "-", "-c:v", "libx264", "-preset", "slow", "-tune", "animation", "-crf", "24",
     "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", f"{OUT}/demo.mp4"],
    ["ffmpeg", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", f"{W}x{H}", "-r", str(FPS),
     "-i", "-", "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "36", "-row-mt", "1", "-deadline", "good",
     "-pix_fmt", "yuv420p", "-an", f"{OUT}/demo.webm"],
]
procs = [subprocess.Popen(cmd, stdin=subprocess.PIPE) for cmd in encoders]
ARROW = QPolygonF([P(0, 0), P(0, 21), P(5, 16), P(9, 25), P(12, 24), P(8, 15), P(15, 15)])
frames = 0


def frame(cursor=None):
    """Jedna snímka: appka + kurzor, zmenšená na 1280×720."""
    global frames
    app.processEvents()
    img = v.grab().toImage().convertToFormat(QImage.Format.Format_ARGB32_Premultiplied)
    if cursor is not None:
        p = QPainter(img)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.translate(cursor)
        p.scale(1.35, 1.35)
        p.setPen(QPen(QColor(255, 255, 255), 1.6))
        p.setBrush(QColor(20, 20, 26))
        p.drawPolygon(ARROW)
        p.end()
    small = img.scaled(W, H, Qt.AspectRatioMode.IgnoreAspectRatio,
                       Qt.TransformationMode.SmoothTransformation).convertToFormat(QImage.Format.Format_RGBA8888)
    data = bytes(small.constBits())
    for pr in procs:
        pr.stdin.write(data)
    frames += 1


def ease(t):
    return t * t * (3 - 2 * t)


def lerp(a, b, t):
    return P(a.x() + (b.x() - a.x()) * t, a.y() + (b.y() - a.y()) * t)


def hover(raw):
    v.raw = raw
    v.hover = v.tool_pos(raw)
    return v.hover


def setup(tool, bg="light", snap=0):
    v.set_tool(tool)
    v.bg = bg
    v.dock.bg_button.setText(regula.BG_NAMES[bg])
    v.set_snap(snap)
    v.tool.reset()
    v.hover = v.raw = None
    v.repaint()
    return v.tool


def move(a, b, n):
    for i in range(n):
        pos = lerp(a, b, ease(i / (n - 1)))
        hover(pos)
        frame(pos)


def drag(t, a, b, n, angle_tool=False):
    t.press(hover(a), NM)
    for i in range(n):
        raw = lerp(a, b, ease(i / (n - 1)))
        t.drag(hover(raw), NM)
        frame(raw)
    t.release(hover(b))


def hold(n, cursor):
    for _ in range(n):
        frame(cursor)


# A) pravítko s krokom 1 mm: ťahaj 60 × 35 mm
t = setup("cm", snap=2)
a, b = P(560, 300), P(560 + 60 * PPM, 300 + 35 * PPM)
move(P(1350, 760), a, 18)
drag(t, a, b, 60)
hold(36, b)

# B) uhlomer: červené rameno zo 120° na 48°
t = setup("prot")
t.arms = [0.0, 120.0]
v.repaint()
knob = regula.polar(t.c, 120, t.R + 10)
move(P(1300, 820), knob, 16)
t.press(knob, NM)
for i in range(45):
    ang = 120 + (48 - 120) * ease(i / 44)
    pos = regula.polar(t.c, ang, t.R + 10)
    t.drag(pos, NM)
    frame(pos)
t.release(pos)
hold(36, pos)

# C) uhlopriečka: obdĺžnik cez displej mobilu
t = setup("diag")
a = P(1250, 96)
b = P(a.x() + 64 * PPM, a.y() + 138 * PPM)
move(P(900, 700), a, 16)
drag(t, a, b, 60)
hold(42, b)

for pr in procs:
    pr.stdin.close()
    pr.wait()
print("snímok:", frames, f"({frames / FPS:.1f} s)")
for name in ("demo.mp4", "demo.webm"):
    print(name, os.path.getsize(f"{OUT}/{name}") // 1024, "KB")
