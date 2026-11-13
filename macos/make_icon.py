#!/usr/bin/env python3
"""make_icon.py — macos/AppIcon.png z ../regula.svg, na mriežke ikon macOS.

Ikony macOS nejdú po okraj: na plátne 1024 px je tvar 824 px s jemným tieňom pod ním,
aby bola Regula v Docku a vo Finderi rovnako veľká ako ostatné appky.
Spúšťať cez venv s Pillow (~/.venvs/navod); SVG vykreslí Qt z ~/.venvs/pyside6.
build.sh používa už hotový AppIcon.png.
"""
import math
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
CANVAS, SHAPE, OVERSAMPLE = 1024, 824, 4
ART = 640                      # kresba (uhlomer na pravítku) vo vnútri tvaru
QT_PYTHON = Path.home() / ".venvs/pyside6/bin/python"

RENDER_SVG = """
import sys
from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QGuiApplication, QImage, QPainter
from PySide6.QtSvg import QSvgRenderer
app = QGuiApplication(["x"])
size = int(sys.argv[3])
img = QImage(size, size, QImage.Format.Format_ARGB32)
img.fill(Qt.GlobalColor.transparent)
p = QPainter(img)
p.setRenderHint(QPainter.RenderHint.Antialiasing)
QSvgRenderer(sys.argv[1]).render(p, QRectF(0, 0, size, size))
p.end()
img.save(sys.argv[2])
"""


def squircle_mask(size: int) -> Image.Image:
    """Tvar ikony macOS s plynulými rohmi, priblížený superelipsou (|x|^5 + |y|^5 = 1)."""
    big = size * OVERSAMPLE
    r = big / 2
    points = []
    for i in range(720):
        t = i / 720 * math.tau
        c, s = math.cos(t), math.sin(t)
        points.append((r + r * math.copysign(abs(c) ** 0.4, c), r + r * math.copysign(abs(s) ** 0.4, s)))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def background(size: int) -> Image.Image:
    """Svetlé „papierové" pozadie s jemným prechodom zhora nadol."""
    top, bottom = (253, 253, 250), (226, 232, 238)
    bg = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(bg)
    for y in range(size):
        t = y / (size - 1)
        draw.line([(0, y), (size, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return bg


def main() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        art_png = Path(tmp) / "art.png"
        subprocess.run([str(QT_PYTHON), "-c", RENDER_SVG, str(HERE.parent / "regula.svg"), str(art_png), str(ART * 2)],
                       check=True, env={"QT_QPA_PLATFORM": "offscreen"})
        art = Image.open(art_png).convert("RGBA")
    art = art.crop(art.getchannel("A").getbbox())
    scale = ART / max(art.size)
    art = art.resize((round(art.width * scale), round(art.height * scale)), Image.LANCZOS)

    mask = squircle_mask(SHAPE)
    tile = background(SHAPE)
    # kresba trochu nad stred — pravítko dole je opticky ťažké
    tile.alpha_composite(art, ((SHAPE - art.width) // 2, (SHAPE - art.height) // 2 - 10))
    tile.putalpha(mask)

    origin = (CANVAS - SHAPE) // 2
    shadow = Image.new("L", (CANVAS, CANVAS), 0)
    shadow.paste(mask, (origin, origin + 10))
    shadow = shadow.filter(ImageFilter.GaussianBlur(12)).point(lambda v: v * 30 // 100)
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    icon.putalpha(shadow)
    icon.alpha_composite(tile, (origin, origin))
    icon.save(HERE / "AppIcon.png", optimize=True)
    print(f"wrote {HERE / 'AppIcon.png'}")


if __name__ == "__main__":
    main()
