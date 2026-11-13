"""Screenshot kalibrácie pre web: appka v pozadí (stmavená) + dialóg s kartou v strede."""
import os
import sys
import tempfile

os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["XDG_CONFIG_HOME"] = tempfile.mkdtemp()
sys.path.insert(0, os.path.expanduser("~/regula"))
import regula  # noqa: E402
from PySide6.QtCore import QRect, QRectF, Qt  # noqa: E402
from PySide6.QtGui import QColor, QImage, QPainter, QPainterPath  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

PPM = 1920 / 309
regula.edid_ppm = lambda s: PPM


class FakeScreen:
    def name(self): return "eDP"
    def geometry(self): return QRect(0, 0, 1920, 1080)


app = QApplication(["regula"])
v = regula.Regula()
v.setGeometry(0, 0, 1920, 1080)
v.show()
v.set_tool("cm")
app.processEvents()
bg = v.grab().toImage().convertToFormat(QImage.Format.Format_ARGB32_Premultiplied)

dlg = regula.CalibrationDialog(FakeScreen(), v)
dlg.slider.setValue(12)          # mierne pohnuté, nech je vidieť nastavovanie
dlg.adjustSize()
dlg.show()
app.processEvents()
card = dlg.grab().toImage()

p = QPainter(bg)
p.setRenderHint(QPainter.RenderHint.Antialiasing)
p.fillRect(bg.rect(), QColor(20, 22, 30, 110))
x, y = (bg.width() - card.width()) // 2, (bg.height() - card.height()) // 2 - 40
for i in range(14, 0, -2):                       # jemný tieň
    p.fillRect(QRectF(x - i, y - i + 6, card.width() + 2 * i, card.height() + 2 * i), QColor(0, 0, 0, 6))
clip = QPainterPath()
clip.addRoundedRect(QRectF(x, y, card.width(), card.height()), 10, 10)
p.setClipPath(clip)
p.drawImage(x, y, card)
p.end()
out = os.path.expanduser("~/regula/web/img/calibrate.webp")
bg.save(out, "webp", 88)
print("calibrate", os.path.getsize(out) // 1024, "KB", card.size())
