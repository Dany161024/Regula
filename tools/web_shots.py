"""Screenshoty a ikonky Reguly pre web (offscreen, simuluje ThinkPad eDP 1920×1080, 309 mm)."""
import math
import os
import sys
import tempfile

os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["XDG_CONFIG_HOME"] = tempfile.mkdtemp()      # nesiahať na skutočný config
sys.path.insert(0, os.path.expanduser("~/regula"))
import regula  # noqa: E402
from PySide6.QtCore import QPointF as P, QRect, Qt  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

OUT = os.path.expanduser("~/regula/web/img")
PPM = 1920 / 309
regula.edid_ppm = lambda s: PPM


class FakeScreen:           # karta „Tento displej" má ukázať ThinkPad, nie offscreen 800×800
    def name(self): return "eDP"
    def geometry(self): return QRect(0, 0, 1920, 1080)


regula.DiagonalTool.screen_info = lambda self: (FakeScreen(), QRect(0, 0, 1920, 1080))

app = QApplication(["regula"])
v = regula.Regula()
v.setGeometry(0, 0, 1920, 1080)
v.show()
app.processEvents()
NM = Qt.KeyboardModifier.NoModifier


def setup(tool, bg="light", snap=0):
    v.input_bar.hide()
    v.set_tool(tool)
    v.bg = bg
    v.dock.bg_button.setText(regula.BG_NAMES[bg])
    v.set_snap(snap)
    v.hover = v.raw = None
    v.repaint()                 # rozloženie uhlomera/kruhov
    return v.tool


def save(name, quality=88):
    app.processEvents()
    img = v.grab().toImage()
    img.save(f"{OUT}/{name}.webp", "webp", quality)
    print(name, os.path.getsize(f"{OUT}/{name}.webp") // 1024, "KB")


# 1) pravítko: 20 × 50 mm, kurzor na konci
t = setup("cm", snap=2)
t.reset()
v.raw = P(560, 300)
t.apply_input([60, 35])
v.hover = P(t.b)
save("ruler")

# 2) rezacia podložka + mm papier
t = setup("grid", "green", snap=2)
t.reset()
v.raw = P(700, 320)
t.apply_input([45, 30])
v.hover = P(t.b)
save("mat")

# 3) uhlomer 180°
t = setup("prot")
t.arms = [12.0, 64.5]
save("protractor")

# 4) uhlomer 360° tmavý
t = setup("prot360", "dark")
t.arms = [20.0, 152.0]
save("protractor360")

# 5) uhlopriečka mobilu
t = setup("diag")
t.reset()
v.raw = P(1250, 125)          # vpravo od karty „Tento displej"
t.apply_input([64, 138])
v.hover = None
save("diagonal")

# 6) trasa s plochou
t = setup("path")
t.reset()
for x, y in ((560, 650), (760, 280), (1180, 230), (1420, 560), (1050, 760)):
    t.press(P(x, y), NM)
    t.release(P(x, y))
t.press(P(560, 650), NM)
t.release(P(560, 650))
save("path")

# 7) polomer z 3 bodov
t = setup("radius")
t.reset()
c, r = P(980, 470), 31 * PPM
for a in (25, 80, 150):
    p = P(c.x() + r * math.cos(math.radians(a)), c.y() - r * math.sin(math.radians(a)))
    t.press(p, NM)
    t.release(p)
save("radius")

# 8) priemer s kurzorom
t = setup("circle")
v.hover = P(t.c.x() + 23.25 / 2 * PPM * 0.8, t.c.y() - 23.25 / 2 * PPM * 0.6)
save("diameter", 60)

# 9) skrutky, kurzor nad M3
t = setup("screw")
ox, oy, cols = t.geom(1920, 1080, PPM)
v.hover = P(cols[3][0], oy + 40 * PPM)
save("screws")

# 10) rozteč, kurzor na 2,00 mm
t = setup("pitch")
left, x0, oy = t.geom(1920, 1080, PPM)
v.hover = P(x0 + 7 * 2.0 * PPM, oy + 2 * t.ROW_MM * PPM + 4 * PPM)
save("pitch")

# 11) zadávanie z klávesnice
t = setup("cm")
t.reset()
v.raw = P(560, 300)
t.apply_input([20, 50])
v.input_bar.open("20")
v.input_bar.edits[1].setText("50")
v.input_bar.edits[1].setFocus()
v.layout_dock()
save("typing")

# ikonky z panela (52×52 = ostré pri 26 px)
for key, _ in regula.TOOLS:
    regula.tool_icon(key).pixmap(52, 52).save(f"{OUT}/icons/{key}.png")
print("ikonky:", len(regula.TOOLS))
