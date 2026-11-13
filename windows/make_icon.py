#!/usr/bin/env python3
"""make_icon.py — windows/Regula.ico z ../regula.svg.

Ikony Windowsu nemajú podklad ako na Macu: len kresba (uhlomer na pravítku) s malým okrajom.
Každá veľkosť sa kreslí zvlášť, aby boli malé ikonky (16, 24 px) ostré.
Spúšťať cez venv s Pillow (~/.venvs/navod); SVG vykreslí Qt z ~/.venvs/pyside6.
"""
import subprocess
import tempfile
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
QT_PYTHON = Path.home() / ".venvs/pyside6/bin/python"

RENDER_SVG = """
import sys
from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QGuiApplication, QImage, QPainter
from PySide6.QtSvg import QSvgRenderer
app = QGuiApplication(["x"])
svg = QSvgRenderer(sys.argv[1])
for arg in sys.argv[3:]:
    size = int(arg)
    pad = max(0.5, size / 32)              # malý okraj, nech sa kresba nedotýka hrany
    img = QImage(size, size, QImage.Format.Format_ARGB32)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    svg.render(p, QRectF(pad, pad, size - 2 * pad, size - 2 * pad))
    p.end()
    img.save(f"{sys.argv[2]}/{size}.png")
"""


def main() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([str(QT_PYTHON), "-c", RENDER_SVG, str(HERE.parent / "regula.svg"), tmp, *map(str, SIZES)],
                       check=True, env={"QT_QPA_PLATFORM": "offscreen"})
        images = [Image.open(f"{tmp}/{s}.png").convert("RGBA") for s in SIZES]
        biggest = images[-1]
        biggest.save(HERE / "Regula.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print(f"wrote {HERE / 'Regula.ico'}")


if __name__ == "__main__":
    main()
