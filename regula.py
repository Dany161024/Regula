#!/usr/bin/env python3
"""Regula — pravítka a uhlomer na celú obrazovku.

Dole je panel, kde si vyberáš nástroj: centimetre, palce, pixely, cm + palce,
milimetrový papier, uhlomer 180° / 360°, priemer, polomer z 3 bodov,
uhlopriečka displeja, trasa (dĺžka a plocha), skrutky a rozteč pinov.
Mierka v mm ide z rozmeru, ktorý hlási displej (EDID), a dá sa dokalibrovať
kartou formátu ID-1 (85,60 mm).
"""

import json
import math
import os
import re
import signal
import sys
from collections import namedtuple
from pathlib import Path

from PySide6.QtCore import QByteArray, QEvent, QPointF, QRectF, QSize, Qt
from PySide6.QtGui import (QActionGroup, QColor, QCursor, QFont, QFontMetricsF, QGuiApplication, QIcon,
                           QKeySequence, QPainter, QPainterPath, QPen, QPixmap, QShortcut)
from PySide6.QtWidgets import (QApplication, QButtonGroup, QDialog, QFrame,
                               QGraphicsDropShadowEffect, QHBoxLayout, QLabel, QLineEdit, QMenu, QPushButton,
                               QSlider, QToolButton, QVBoxLayout, QWidget)

APP_ID = "sk.daniel.regula"
ICON_PATH = Path(__file__).resolve().parent / "regula.svg"
CONFIG_PATH = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "regula" / "config.json"


def _saved_lang():
    try:
        return json.loads(CONFIG_PATH.read_text()).get("lang")
    except (OSError, ValueError, AttributeError):
        return None


# jazyk rozhrania: REGULA_LANG alebo "lang" v nastaveniach; predvolene angličtina (tá ide na web)
SK = (os.environ.get("REGULA_LANG") or _saved_lang() or "en") == "sk"


def L(sk, en):
    """Text v jazyku rozhrania."""
    return sk if SK else en

MM_PER_IN = 25.4
CARD_MM = (85.60, 53.98)    # ID-1: platobná karta, občiansky, ISIC, kartička poistenca
CARD_CORNER_MM = 3.18
DOCK_RESERVE = 90           # px dole, ktoré zaberá panel (pre rozloženie uhlomera a kruhov)

ACCENT = QColor(31, 158, 222)   # Mint-Y Aqua
MARK = QColor(228, 69, 83)
INK = QColor(34, 34, 42)
INK_SOFT = QColor(112, 112, 124)

Theme = namedtuple("Theme", "bg panel ink soft glass")
THEMES = {
    "light": Theme(QColor(251, 251, 246), QColor(236, 236, 228), INK, INK_SOFT, False),
    "dark": Theme(QColor(29, 30, 37), QColor(43, 44, 54), QColor(226, 226, 234), QColor(150, 150, 166), False),
    # rezacia podložka: zelená s bielymi čiarami
    "green": Theme(QColor(46, 107, 79), QColor(37, 90, 65), QColor(242, 247, 238), QColor(186, 215, 197), False),
    "clear": Theme(QColor(255, 255, 255, 16), QColor(251, 251, 246, 225), INK, INK_SOFT, True),
}
BG_NAMES = {"light": L("Svetlé", "Light"), "dark": L("Tmavé", "Dark"), "green": L("Zelené", "Green"),
            "clear": L("Priehľadné", "Clear")}

# krok prichytávania pre úroveň 1–3 (0 = voľne): (hodnota v jednotke, popis)
SNAP = {
    "mm": ((0.5, L("0,5 mm", "0.5 mm")), (1, "1 mm"), (5, "5 mm")),
    "in": ((1 / 32, "1/32″"), (1 / 16, "1/16″"), (1 / 4, "1/4″")),
    "px": ((5, "5 px"), (10, "10 px"), (50, "50 px")),
    "deg": ((0.5, L("0,5°", "0.5°")), (1, "1°"), (5, "5°")),
}

TOOLS = (("cm", L("Centimetre", "Centimetres")), ("in", L("Palce", "Inches")), ("px", L("Pixely", "Pixels")),
         ("dual", L("cm + palce", "cm + in")), ("grid", L("Mm papier", "Mm paper")),
         ("prot", L("Uhlomer", "Protractor")), ("prot360", L("Uhlomer 360°", "Protractor 360°")),
         ("circle", L("Priemer", "Diameter")), ("radius", L("Polomer", "Radius")),
         ("diag", L("Uhlopriečka", "Diagonal")), ("path", L("Trasa", "Path")),
         ("screw", L("Skrutky", "Screws")), ("pitch", L("Rozteč", "Pin pitch")))
GROUP_STARTS = {"prot", "diag", "screw"}    # pred nimi je v paneli oddeľovač

# (názov, závit mm, stúpanie mm, matica = kľúč mm, imbus mm) — metrické ISO, hrubý závit
SCREWS = ((L("M1,6", "M1.6"), 1.6, 0.35, 3.2, 1.5), ("M2", 2.0, 0.40, 4.0, 1.5), (L("M2,5", "M2.5"), 2.5, 0.45, 5.0, 2.0),
          ("M3", 3.0, 0.50, 5.5, 2.5), ("M4", 4.0, 0.70, 7.0, 3.0), ("M5", 5.0, 0.80, 8.0, 4.0),
          ("M6", 6.0, 1.00, 10.0, 5.0), ("M8", 8.0, 1.25, 13.0, 6.0))

PITCHES = ((2.54, L("2,54 mm (0,1″) · lámacie lišty, Dupont, DIP", "2.54 mm (0.1″) · pin headers, Dupont, DIP")),
           (2.50, L("2,50 mm · JST XH", "2.50 mm · JST XH")),
           (2.00, L("2,00 mm · JST PH, lišty 2 mm", "2.00 mm · JST PH, 2 mm headers")),
           (1.50, L("1,50 mm · JST ZH", "1.50 mm · JST ZH")),
           (1.27, L("1,27 mm (0,05″) · SOIC, lišty 1,27", "1.27 mm (0.05″) · SOIC, 1.27 mm headers")),
           (1.25, L("1,25 mm · Molex PicoBlade", "1.25 mm · Molex PicoBlade")),
           (1.00, L("1,00 mm · JST SH, Qwiic / STEMMA QT", "1.00 mm · JST SH, Qwiic / STEMMA QT")),
           (0.80, L("0,80 mm · QFP", "0.80 mm · QFP")),
           (0.65, L("0,65 mm · TSSOP, QFP", "0.65 mm · TSSOP, QFP")),
           (0.50, L("0,50 mm · FFC/FPC káble, QFN", "0.50 mm · FFC/FPC cables, QFN")))

ASPECTS = ((1, 1), (5, 4), (4, 3), (3, 2), (16, 10), (16, 9), (18, 9), (19.5, 9), (20, 9), (21, 9))


class Config:
    def __init__(self):
        self.data = {"tool": "cm", "bg": "light", "snap": 0, "fullscreen": True, "window": None, "calib": {}}
        try:
            self.data.update(json.loads(CONFIG_PATH.read_text()))
        except (OSError, ValueError):
            pass

    def __getitem__(self, key):
        return self.data[key]

    def set(self, key, value):
        self.data[key] = value
        self.save()

    def save(self):
        try:
            CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
            CONFIG_PATH.write_text(json.dumps(self.data, indent=2))
        except OSError as e:
            print(f"regula: {L('nastavenia sa neuložili', 'could not save settings')}: {e}", file=sys.stderr)


cfg = Config()


# ---------- mierka ----------

def screen_key(screen):
    g = screen.geometry()
    return f"{screen.name()}@{g.width()}x{g.height()}"


def edid_ppm(screen):
    """px/mm podľa fyzického rozmeru, ktorý hlási displej (EDID cez RandR)."""
    g, mm = screen.geometry(), screen.physicalSize()
    if mm.width() >= 50:
        ppm = g.width() / mm.width()
        if 1.0 < ppm < 40.0:
            return ppm
    return screen.logicalDotsPerInch() / MM_PER_IN


def px_per_mm(screen):
    return cfg["calib"].get(screen_key(screen)) or edid_ppm(screen)


def scale_ticks(unit, ppm, length):
    """Čiarky stupnice: (pozícia v px, dĺžka čiarky, číslo alebo None)."""
    if unit == "px":
        step, divs, lens, every = 5.0, (20, 10, 2, 1), (19, 13, 8, 4), 20
        label = lambda i: str(i * 5)
    elif unit == "in":
        step, divs, lens, every = ppm * MM_PER_IN / 16, (16, 8, 4, 2, 1), (20, 15, 11, 8, 5), 16
        label = lambda i: str(i // 16)
    else:
        step, divs, lens, every = ppm, (10, 5, 1), (19, 12, 7), 10
        label = lambda i: str(i // 10)
    i = 0
    while i * step <= length:
        level = next(n for n, d in enumerate(divs) if i % d == 0)
        yield i * step, lens[level], (label(i) if i % every == 0 else None)
        i += 1


def fmt(v, dec):
    text = f"{v:.{dec}f}"
    return text.replace(".", ",") if SK else text


def frac16(inch):
    n = round(inch * 16)
    whole, rest = divmod(n, 16)
    if not rest:
        return f"{whole}″"
    g = math.gcd(rest, 16)
    fr = f"{rest // g}/{16 // g}"
    return f"{whole} {fr}″" if whole else f"{fr}″"


def readout(unit, ppm, px):
    if unit == "px":
        return f"{round(px)} px"
    mm = px / ppm
    inch = mm / MM_PER_IN
    if unit == "in":
        return f"{fmt(inch, 3)}″ ≈ {frac16(inch)}"
    if unit == "dual":
        return f"{fmt(mm, 1)} mm · {fmt(inch, 3)}″"
    return f"{fmt(mm, 1)} mm"


def fmt_g(v):
    text = f"{v:g}"
    return text.replace(".", ",") if SK else text


def mm_text(mm):
    return f"{fmt(mm, 1)} mm" if mm < 1000 else f"{fmt(mm / 1000, 3)} m"


def area_text(mm2):
    return f"{fmt(mm2, 1)} mm²" if mm2 < 100 else f"{fmt(mm2 / 100, 2)} cm²"


def aspect_name(w, h):
    long, short = max(w, h), min(w, h)
    if short <= 0:
        return ""
    r = long / short
    a, b = min(ASPECTS, key=lambda ab: abs(ab[0] / ab[1] - r))
    return f"{fmt_g(a)}:{b}" if abs(a / b - r) / r < 0.025 else f"{fmt(r, 2)}:1"


def pins_text(n):
    if not SK:
        return f"{n} pin" if n == 1 else f"{n} pins"
    return f"{n} pin" if n == 1 else f"{n} piny" if n < 5 else f"{n} pinov"


def circle_3(a, b, c):
    """Kružnica cez 3 body → (stred, polomer) alebo None, ak ležia na priamke."""
    ax, ay, bx, by, cx, cy = a.x(), a.y(), b.x(), b.y(), c.x(), c.y()
    d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by))
    if abs(d) < 1e-6:
        return None
    a2, b2, c2 = ax * ax + ay * ay, bx * bx + by * by, cx * cx + cy * cy
    ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d
    uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d
    return QPointF(ux, uy), math.hypot(ax - ux, ay - uy)


def near(p, q, r):
    return abs(p.x() - q.x()) <= r and abs(p.y() - q.y()) <= r


def rescaled(view, pts, k):
    """Body prepočítané na inú veľkosť pixelu (rovnaké mm) a posunuté, aby sa zmestili do okna."""
    pts = [q * k for q in pts]
    d = view.fit_shift(pts)
    return [q + d for q in pts]


# slovenská a česká klávesnica majú v hornom rade bez Shiftu písmená → preložia sa na číslice
SK_DIGITS = str.maketrans("+ľěščťřžýáíé", "122345567890")


def parse_numbers(text):
    """„20 x 50", „20,5×50", „ľé x ťé" → [20.0, 50.0]"""
    t = text.translate(SK_DIGITS).replace(",", ".")
    return [float(m) for m in re.findall(r"-?\d+(?:\.\d+)?|-?\.\d+", t)]


# ---------- kreslenie ----------

def ui_font(px, bold=False):
    f = QFont()
    f.setPixelSize(px)
    f.setBold(bold)
    return f


def tint(color, alpha):
    c = QColor(color)
    c.setAlpha(alpha)
    return c


def pill_size(text, px=12):
    fm = QFontMetricsF(ui_font(px, bold=True))
    return fm.horizontalAdvance(text) + 14, fm.height() + 4


def pill(p, cx, cy, text, color, lo=-1e9, hi=1e9, px=12):
    """Zaoblený štítok s textom, vodorovne držaný medzi lo a hi."""
    w, h = pill_size(text, px)
    x = min(max(cx - w / 2, lo), hi - w)
    r = QRectF(x, cy - h / 2, w, h)
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(color)
    p.drawRoundedRect(r, h / 2, h / 2)
    p.setPen(QColor(255, 255, 255))
    p.setFont(ui_font(px, bold=True))
    p.drawText(r, Qt.AlignmentFlag.AlignCenter, text)


def centered_text(p, at, text, font, color):
    fm = QFontMetricsF(font)
    p.setFont(font)
    p.setPen(color)
    p.drawText(QPointF(at.x() - fm.horizontalAdvance(text) / 2,
                       at.y() + (fm.ascent() - fm.descent()) / 2), text)


def polar(c, deg, r):
    a = math.radians(deg)
    return QPointF(c.x() + r * math.cos(a), c.y() - r * math.sin(a))


def dot(p, at, color, r=4.5):
    p.setPen(QPen(QColor(255, 255, 255), 1.5))
    p.setBrush(color)
    p.drawEllipse(at, r, r)


def backdrop(p, rect, th):
    """Pri priehľadnom pozadí podloží obsah, aby bol čitateľný."""
    if th.glass:
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(tint(th.panel, 215))
        p.drawRoundedRect(rect, 16, 16)


# ---------- nástroje ----------

class Tool:
    hint = ""
    snap_kind = None    # "point" = body sa prichytávajú na mriežku kroku, "angle" = uhly po kroku
    unit = "cm"
    input_fields = ()       # políčka na zadanie z klávesnice: (názov, jednotka, nepovinné); () = nepodporuje

    def __init__(self, view):
        self.view = view

    def layout(self, W, H): pass
    def relayout(self): pass
    def static_key(self): return ()
    def paint_static(self, p, W, H, th, ppm): pass
    def paint_dynamic(self, p, W, H, th, ppm): pass
    def press(self, pos, mods): pass
    def drag(self, pos, mods): pass
    def release(self, pos): pass
    def double(self, pos, mods): self.press(pos, mods)     # rýchle kliky nech sa nestratia
    def key(self, key): return False                        # True = kláves spotrebovaný
    def wheel(self, steps): pass
    def nudge(self, d): pass
    def reset(self): pass
    def apply_input(self, nums): return None       # vráti chybovú hlášku alebo None

    def rescale(self, k):
        """Okno prešlo na obrazovku s k-krát väčšou hustotou pixelov (k = nové px/mm ÷ staré)."""
    def cursor(self, pos): return Qt.CursorShape.CrossCursor


class RulerTool(Tool):
    """Stupnice hore a vľavo, nitkový kríž a meranie ťahaním (aj ako milimetrový papier)."""
    hint = L("ťahaj = zmeraj · píš čísla = presný rozmer · šípky = koniec o 0,1 mm · Shift = rovno · S = krok · Esc = zmazať",
             "drag = measure · type numbers = exact size · arrows = move end 0.1 mm · Shift = straight · S = snap · Esc = clear")
    snap_kind = "point"

    def __init__(self, view, unit, grid=False):
        super().__init__(view)
        self.unit, self.grid = unit, grid
        self.a = self.b = None
        self.dragging = False

    def band(self):
        return 84 if self.unit == "dual" else 50

    @property
    def input_fields(self):
        unit = {"in": "″", "px": "px"}.get(self.unit, "mm")
        return ((L("Šírka", "Width"), unit, False), (L("Výška", "Height"), unit, True))

    def apply_input(self, nums):
        if len(nums) not in (1, 2) or min(nums) < 0 or max(nums) <= 0:
            return L("Aspoň jedno číslo musí byť väčšie ako 0", "At least one number must be greater than 0")
        ppm = self.view.ppm()
        k = 1 if self.unit == "px" else ppm * MM_PER_IN if self.unit == "in" else ppm
        a = QPointF(self.a) if self.a is not None else self.view.input_origin()
        b = QPointF(a.x() + nums[0] * k, a.y() + (nums[1] * k if len(nums) == 2 else 0))
        d = self.view.fit_shift((a, b))
        self.a, self.b, self.dragging = a + d, b + d, False

    def paint_static(self, p, W, H, th, ppm):
        T = self.band()
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        if self.grid:
            self.paint_grid(p, W, H, th, ppm)
        p.fillRect(QRectF(0, 0, W, T), th.panel)
        p.fillRect(QRectF(0, 0, T, H), th.panel)
        p.setPen(QPen(tint(th.ink, 70), 1))
        p.drawLine(T, T, W, T)
        p.drawLine(T, T, T, H)
        font = ui_font(11)
        p.setFont(font)
        fm = QFontMetricsF(font)
        scales = (("cm", True), ("in", False)) if self.unit == "dual" else ((self.unit, True),)
        for unit, outer in scales:
            self.paint_scale(p, unit, ppm, W, T, th, fm, outer, horizontal=True)
            self.paint_scale(p, unit, ppm, H, T, th, fm, outer, horizontal=False)
        p.fillRect(QRectF(0, 0, T, T), th.panel)
        name = {"cm": "mm" if self.grid else "cm", "in": L("palce", "in"), "px": "px", "dual": "cm\nin"}[self.unit]
        p.setPen(th.soft)
        p.drawText(QRectF(0, 0, T, T), Qt.AlignmentFlag.AlignCenter, name)

    def paint_scale(self, p, unit, ppm, length, T, th, fm, outer, horizontal):
        p.setPen(QPen(th.ink, 1))
        for u, ln, label in scale_ticks(unit, ppm, length - 1):
            x = round(u)
            v0, v1 = (0, ln) if outer else (T - 1, T - 1 - ln)
            if horizontal:
                p.drawLine(x, v0, x, v1)
            else:
                p.drawLine(v0, x, v1, x)
            if label and u > 0:
                w = fm.horizontalAdvance(label)
                if horizontal:
                    y = ln + 2 + fm.ascent() if outer else T - ln - 4 - fm.descent()
                    p.drawText(QPointF(x - w / 2, y), label)
                else:
                    tx = ln + 3 if outer else T - ln - 4 - w
                    p.drawText(QPointF(tx, x + (fm.ascent() - fm.descent()) / 2), label)

    def paint_grid(self, p, W, H, th, ppm):
        pens = (QPen(tint(th.ink, 22), 1), QPen(tint(th.ink, 48), 1), QPen(tint(th.ink, 95), 1))
        for vertical, length, span in ((True, W, H), (False, H, W)):
            i = 0
            while i * ppm <= length:
                p.setPen(pens[2 if i % 10 == 0 else 1 if i % 5 == 0 else 0])
                x = round(i * ppm)
                if vertical:
                    p.drawLine(x, 0, x, span)
                else:
                    p.drawLine(0, x, span, x)
                i += 1

    def paint_dynamic(self, p, W, H, th, ppm):
        T = self.band()
        h = self.view.hover
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        if self.a is not None:      # konce merania premietnuté na stupnice
            p.setPen(QPen(MARK, 1))
            for pt in (self.a, self.b):
                x, y = round(pt.x()), round(pt.y())
                p.drawLine(x, 0, x, T)
                p.drawLine(0, y, T, y)
        if h is not None:
            x, y = round(h.x()), round(h.y())
            p.setPen(QPen(tint(ACCENT, 150), 1))
            p.drawLine(x, 0, x, H)
            p.drawLine(0, y, W, y)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if self.a is not None:
            self.paint_measure(p, W, ppm)
        if h is not None:
            pill(p, h.x(), T + 14, readout(self.unit, ppm, h.x()), ACCENT, T + 2, W - 2)
            text = readout(self.unit, ppm, h.y())
            pill(p, T + 6 + pill_size(text)[0] / 2, h.y(), text, ACCENT)

    def paint_measure(self, p, W, ppm):
        a, b = self.a, self.b
        dx, dy = abs(b.x() - a.x()), abs(b.y() - a.y())
        box = dx > 2 and dy > 2
        rect = QRectF(a, b).normalized()
        if box:
            p.setPen(QPen(ACCENT, 1, Qt.PenStyle.DashLine))
            p.setBrush(tint(ACCENT, 28))
            p.drawRect(rect)
        p.setPen(QPen(MARK, 2))
        p.drawLine(a, b)
        p.setPen(QPen(QColor(255, 255, 255), 1.5))
        p.setBrush(MARK)
        for pt in (a, b):
            p.drawEllipse(pt, 4.5, 4.5)
        if box:
            pill(p, rect.center().x(), rect.top() - 14, readout(self.unit, ppm, dx), ACCENT, 2, W - 2)
            text = readout(self.unit, ppm, dy)
            pill(p, rect.left() - 8 - pill_size(text)[0] / 2, rect.center().y(), text, ACCENT)
        mid = (a + b) / 2
        pill(p, mid.x(), mid.y() if box else mid.y() - 18, readout(self.unit, ppm, math.hypot(dx, dy)),
             MARK, 2, W - 2, px=14)

    def press(self, pos, mods):
        self.a, self.b, self.dragging = QPointF(pos), QPointF(pos), True

    def drag(self, pos, mods):
        if not self.dragging:
            return
        b = QPointF(pos)
        if mods & Qt.KeyboardModifier.ShiftModifier:
            if abs(b.x() - self.a.x()) >= abs(b.y() - self.a.y()):
                b.setY(self.a.y())
            else:
                b.setX(self.a.x())
        self.b = b

    def release(self, pos):
        self.dragging = False
        if self.a is not None and (self.b - self.a).manhattanLength() < 3:
            self.a = self.b = None      # obyčajný klik meranie zmaže

    def nudge(self, d):
        if self.b is not None:
            self.b += d

    def rescale(self, k):
        if self.a is not None:
            self.a, self.b = rescaled(self.view, (self.a, self.b), k)

    def reset(self):
        self.a = self.b = None


class ProtractorTool(Tool):
    hint = L("ťahaj ramená na strany uhla · napíš uhol = presne · ťahaj uhlomer = presun · koliesko = veľkosť"
             " · Shift = celé stupne · Esc = reset",
             "drag the arms onto the sides of the angle · type an angle = exact · drag the dial = move"
             " · wheel = size · Shift = whole degrees · Esc = reset")
    BASE = 40   # pás pod vrcholom pri polkruhu
    snap_kind = "angle"
    input_fields = ((L("Uhol", "Angle"), "°", False),)

    def __init__(self, view, full):
        super().__init__(view)
        self.full = full
        self.c = None
        self.R = 300.0
        self.arms = [0.0, 60.0]
        self.active = None
        self.grab = None

    def layout(self, W, H):
        if self.c is not None:
            return
        free = H - DOCK_RESERVE
        if self.full:
            self.R = min(W, free) / 2 - 40
            self.c = QPointF(round(W / 2), round(free / 2))
        else:
            self.R = min(W * 0.3, (free - self.BASE) * 0.75)
            self.c = QPointF(round(W / 2), round((free - self.R - self.BASE) / 2 + self.R))

    def relayout(self):
        self.c = None

    def static_key(self):
        return (self.c.x(), self.c.y(), self.R)

    def paint_static(self, p, W, H, th, ppm):
        c, R = self.c, self.R
        cx, cy = c.x(), c.y()
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        body = QPainterPath()
        if self.full:
            body.addEllipse(c, R, R)
        else:
            body.moveTo(cx + R, cy + self.BASE)
            body.lineTo(cx + R, cy)
            body.arcTo(QRectF(cx - R, cy - R, 2 * R, 2 * R), 0, 180)
            body.lineTo(cx - R, cy + self.BASE)
            body.closeSubpath()
        p.setPen(QPen(tint(th.ink, 90), 1))
        p.setBrush(tint(th.panel, 190) if th.glass else th.panel)
        p.drawPath(body)

        last = 360 if self.full else 180
        whole = R * math.radians(1) >= 3.2
        halves = R * math.radians(0.5) >= 4
        p.setPen(QPen(th.ink, 1))
        for k in range(last * 2 + (0 if self.full else 1)):
            d = k / 2
            if d % 10 == 0:
                ln = 22
            elif d % 5 == 0:
                ln = 15
            elif d % 1 == 0:
                ln = 9 if whole else 0
            else:
                ln = 5 if halves else 0
            if ln:
                p.drawLine(polar(c, d, R), polar(c, d, R - ln))

        size = 13 if R >= 300 else 11
        r_out = R - 24 - size
        r_in = r_out - size - 10
        step = 10 if r_in * math.radians(10) >= 28 else 30
        f_out, f_in = ui_font(size), ui_font(size - 2)
        for d in range(0, last, step) if self.full else range(0, last + 1, step):
            out = polar(c, d, r_out)
            if self.full:
                centered_text(p, out, str(d), f_out, th.ink)
                continue
            out.setY(min(out.y(), cy - 10))
            centered_text(p, out, str(d), f_out, th.ink)
            inner = polar(c, d, r_in)
            inner.setY(min(inner.y(), cy - 10))
            centered_text(p, inner, str(180 - d), f_in, th.soft)

        if self.full:
            p.setPen(QPen(tint(th.soft, 140), 1, Qt.PenStyle.DashLine))
            p.drawLine(polar(c, 0, R - 24), polar(c, 180, R - 24))
            p.drawLine(polar(c, 90, R - 24), polar(c, 270, R - 24))
        else:
            p.setPen(QPen(th.ink, 1))
            p.drawLine(polar(c, 0, R), polar(c, 180, R))

    def angle(self):
        a0, a1 = self.arms
        if self.full:
            sweep = (a1 - a0) % 360
            return min(sweep, 360 - sweep), (a0 if sweep <= 180 else a1)
        return abs(a1 - a0), min(a0, a1)

    def paint_dynamic(self, p, W, H, th, ppm):
        c, R = self.c, self.R
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        angle, start = self.angle()
        rw = min(90.0, R * 0.25)
        wedge = QPainterPath(c)
        wedge.arcTo(QRectF(c.x() - rw, c.y() - rw, 2 * rw, 2 * rw), start, angle)
        wedge.closeSubpath()
        p.setPen(QPen(ACCENT, 1.5))
        p.setBrush(tint(ACCENT, 50))
        p.drawPath(wedge)

        far = math.hypot(W, H)
        for i, color in ((0, ACCENT), (1, MARK)):
            knob = polar(c, self.arms[i], R + 10)
            p.setPen(QPen(tint(color, 170), 1, Qt.PenStyle.DashLine))
            p.drawLine(knob, polar(c, self.arms[i], far))     # predĺženie cez celú obrazovku
            p.setPen(QPen(color, 2))
            p.drawLine(c, knob)
            p.setPen(QPen(QColor(255, 255, 255), 2))
            p.setBrush(color)
            p.drawEllipse(knob, 9, 9)

        p.setPen(QPen(th.ink, 1))
        p.drawLine(QPointF(c.x() - 12, c.y()), QPointF(c.x() + 12, c.y()))
        p.drawLine(QPointF(c.x(), c.y() - 12), QPointF(c.x(), c.y() + (12 if self.full else 4)))
        p.setBrush(QColor(255, 255, 255))
        p.drawEllipse(c, 3.5, 3.5)

        tag = ACCENT if th.bg.lightness() < 128 else INK     # na tmavom pozadí by tmavý štítok zanikol
        if self.full:
            text = f"∠ {fmt(angle, 1)}°   ({fmt(360 - angle, 1)}°)"
            pill(p, c.x(), c.y() + R * 0.45, text, tag, px=16)
        else:
            pill(p, c.x(), c.y() + self.BASE / 2 + 2, f"∠ {fmt(angle, 1)}°", tag, px=16)

    def arm_at(self, pos):
        dx, dy = pos.x() - self.c.x(), self.c.y() - pos.y()
        best, best_d = None, 10.0
        for i in (1, 0):
            a = math.radians(self.arms[i])
            along = dx * math.cos(a) + dy * math.sin(a)
            dist = abs(dy * math.cos(a) - dx * math.sin(a))
            if along > 20 and dist < best_d:
                best, best_d = i, dist
        return best

    def angle_at(self, pos, step):
        a = math.degrees(math.atan2(self.c.y() - pos.y(), pos.x() - self.c.x()))
        if step:
            a = round(a / step) * step
        if self.full:
            return a % 360
        if a < 0:
            a = 0.0 if a > -90 else 180.0
        return a

    def press(self, pos, mods):
        self.active = self.arm_at(pos)
        if self.active is None:
            self.grab = pos - self.c
        else:
            self.view.setCursor(Qt.CursorShape.ClosedHandCursor)

    def drag(self, pos, mods):
        if self.active is not None:
            step = 1.0 if mods & Qt.KeyboardModifier.ShiftModifier else self.view.snap_value()
            self.arms[self.active] = self.angle_at(pos, step)
        elif self.grab is not None:
            self.c = pos - self.grab

    def release(self, pos):
        self.active = self.grab = None

    def wheel(self, steps):
        self.R = min(1200.0, max(120.0, self.R + 20 * steps))

    def nudge(self, d):
        self.c += d

    def rescale(self, k):
        self.c = None       # na novej obrazovke sa znova vycentruje a prispôsobí veľkosť; uhly ostanú

    def reset(self):
        self.arms = [0.0, 60.0]
        self.c = None

    def apply_input(self, nums):
        if len(nums) != 1:
            return L("Zadaj jedno číslo — uhol v stupňoch", "Enter one number — the angle in degrees")
        a, a0 = nums[0], self.arms[0]
        if self.full:
            self.arms[1] = (a0 + a) % 360
            return None
        if not 0 <= a <= 180:
            return L("Pri polkruhu môže byť uhol 0 až 180°", "The half-circle protractor goes from 0 to 180°")
        target = a0 + a if a0 + a <= 180 else a0 - a
        if target < 0:              # nezmestí sa na žiadnu stranu → základné rameno na 0°
            self.arms[0], target = 0.0, a
        self.arms[1] = target
        return None

    def cursor(self, pos):
        return Qt.CursorShape.OpenHandCursor if self.arm_at(pos) is not None else Qt.CursorShape.ArrowCursor


class CircleTool(Tool):
    hint = L("prilož guľatú vec (mincu, skrutku, vrchnák) na stred · napíš priemer = vyznačí kruh"
             " · ťahaj = presun stredu · šípky = 0,1 mm · Esc = na stred",
             "put something round (a coin, screw, bottle cap) on the centre · type a diameter = mark a circle"
             " · drag = move centre · arrows = 0.1 mm · Esc = re-centre")

    def __init__(self, view):
        super().__init__(view)
        self.c = None
        self.grab = None
        self.fixed = None       # zadaný priemer v mm

    input_fields = ((L("Priemer", "Diameter"), "mm", False),)

    def apply_input(self, nums):
        if len(nums) != 1 or nums[0] <= 0:
            return L("Zadaj jedno kladné číslo — priemer", "Enter one positive number — the diameter")
        self.fixed = nums[0]
        return None

    def layout(self, W, H):
        if self.c is None:
            self.c = QPointF(round(W / 2), round((H - DOCK_RESERVE) / 2))

    def relayout(self):
        self.c = None

    def static_key(self):
        return (self.c.x(), self.c.y())

    def paint_static(self, p, W, H, th, ppm):
        c = self.c
        far = max(math.hypot(c.x() - x, c.y() - y) for x in (0, W) for y in (0, H))
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if th.glass:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(tint(th.panel, 170))
            p.drawEllipse(c, 52 * ppm, 52 * ppm)

        p.setPen(QPen(tint(th.soft, 140), 1, Qt.PenStyle.DashLine))
        p.drawLine(QPointF(0, c.y()), QPointF(W, c.y()))
        p.drawLine(QPointF(c.x(), 0), QPointF(c.x(), H))

        p.setBrush(Qt.BrushStyle.NoBrush)
        faint, strong = QPen(tint(th.ink, 45), 1), QPen(tint(th.ink, 160), 1.2)
        d = 2       # priemer v mm
        while d / 2 * ppm <= far:
            p.setPen(strong if d % 10 == 0 else faint)
            p.drawEllipse(c, d / 2 * ppm, d / 2 * ppm)
            d += 2

        font = ui_font(10, bold=True)
        fm = QFontMetricsF(font)
        p.setFont(font)
        d = 10
        while d / 2 * ppm <= far:
            at = polar(c, 45, d / 2 * ppm)
            text = str(d)
            w = fm.horizontalAdvance(text) + 6
            box = QRectF(at.x() - w / 2, at.y() - 7, w, 14)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(th.panel if not th.glass else tint(th.panel, 235))
            p.drawRoundedRect(box, 4, 4)
            p.setPen(th.ink)
            p.drawText(box, Qt.AlignmentFlag.AlignCenter, text)
            d += 10
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(th.ink)
        p.drawEllipse(c, 2, 2)

    def paint_dynamic(self, p, W, H, th, ppm):
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if self.fixed:
            r = self.fixed / 2 * ppm
            p.setPen(QPen(MARK, 2))
            p.setBrush(tint(MARK, 25))
            p.drawEllipse(self.c, r, r)
            pill(p, self.c.x(), self.c.y() - r - 16, f"⌀ {fmt(self.fixed, 1)} mm", MARK, 2, W - 2, px=14)
        h = self.view.hover
        if h is None or self.grab is not None:
            return
        r = math.hypot(h.x() - self.c.x(), h.y() - self.c.y())
        p.setPen(QPen(ACCENT, 1.5))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawEllipse(self.c, r, r)
        pill(p, h.x(), h.y() - 22, f"⌀ {fmt(2 * r / ppm, 1)} mm", ACCENT, 2, W - 2, px=14)

    def press(self, pos, mods):
        self.grab = pos - self.c

    def drag(self, pos, mods):
        if self.grab is not None:
            self.c = pos - self.grab

    def release(self, pos):
        self.grab = None

    def nudge(self, d):
        self.c += d

    def rescale(self, k):
        self.c = None       # stred znova do stredu; zadaný priemer je v mm, ten ostane

    def reset(self):
        self.c = None
        self.fixed = None


class RadiusTool(Tool):
    """Kružnica cez 3 body — polomer oblúka, zaobleného rohu alebo kusu kruhu."""
    hint = L("klikni 3 body na oblúku (roh mobilu, okraj kolesa…) · body sa dajú ťahať"
             " · šípky = posledný bod o 0,1 mm · ďalší klik = nové meranie · Esc = zmazať",
             "click 3 points on an arc (a phone corner, a wheel…) · points can be dragged"
             " · arrows = last point 0.1 mm · next click = new measurement · Esc = clear")

    def __init__(self, view):
        super().__init__(view)
        self.pts = []
        self.grab = None

    def point_at(self, pos):
        return next((i for i, q in enumerate(self.pts) if near(pos, q, 8)), None)

    def press(self, pos, mods):
        i = self.point_at(pos)
        if i is None:
            if len(self.pts) >= 3:
                self.pts = []
            self.pts.append(QPointF(pos))
            i = len(self.pts) - 1
        self.grab = i

    def drag(self, pos, mods):
        if self.grab is not None:
            self.pts[self.grab] = QPointF(pos)

    def release(self, pos):
        self.grab = None

    def nudge(self, d):
        if self.pts:
            self.pts[-1] += d

    def rescale(self, k):
        if self.pts:
            self.pts = rescaled(self.view, self.pts, k)

    def reset(self):
        self.pts = []

    def cursor(self, pos):
        return Qt.CursorShape.OpenHandCursor if self.point_at(pos) is not None else Qt.CursorShape.CrossCursor

    def paint_dynamic(self, p, W, H, th, ppm):
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        h = self.view.hover
        pts = list(self.pts)
        preview = len(pts) < 3 and h is not None and self.grab is None
        if preview and pts:
            pts.append(h)
        if len(pts) == 2:
            p.setPen(QPen(tint(MARK, 150), 1, Qt.PenStyle.DashLine))
            p.drawLine(pts[0], pts[1])
        elif len(pts) == 3:
            fit = circle_3(*pts)
            if fit is None:
                pill(p, pts[2].x(), pts[2].y() - 24, L("body sú na jednej priamke", "the points are on one line"), QColor(70, 72, 86))
            else:
                c, r = fit
                p.setPen(QPen(ACCENT, 1.5, Qt.PenStyle.DashLine if preview else Qt.PenStyle.SolidLine))
                p.setBrush(tint(ACCENT, 0 if preview else 22))
                p.drawEllipse(c, r, r)
                p.setPen(QPen(tint(MARK, 170), 1, Qt.PenStyle.DashLine))
                p.drawLine(c, pts[0])
                p.setPen(QPen(MARK, 1.5))
                p.drawLine(c - QPointF(8, 0), c + QPointF(8, 0))
                p.drawLine(c - QPointF(0, 8), c + QPointF(0, 8))
                on_screen = 0 <= c.x() <= W and 0 <= c.y() <= H - DOCK_RESERVE
                at = c if on_screen else (pts[0] + pts[1] + pts[2]) / 3
                pill(p, at.x(), at.y() - 24, f"R {fmt(r / ppm, 1)} mm", MARK, 2, W - 2, px=15)
                pill(p, at.x(), at.y() + 24, f"⌀ {fmt(2 * r / ppm, 1)} mm", ACCENT, 2, W - 2)
        for q in self.pts:
            dot(p, q, MARK)


class DiagonalTool(Tool):
    """Uhlopriečka obrazovky (mobil, tablet…) — obdĺžnik cez displej + údaje o vlastnom displeji."""
    hint = L("polož mobil či tablet na displej a potiahni obdĺžnik cez jeho obrazovku"
             " · píš čísla = presný rozmer · rohy a hrany sa dajú ťahať · šípky = 0,1 mm · Esc = zmazať",
             "lay a phone or tablet on the screen and drag a box over its display"
             " · type numbers = exact size · corners and edges can be dragged · arrows = 0.1 mm · Esc = clear")
    snap_kind = "point"

    def __init__(self, view):
        super().__init__(view)
        self.r = None           # [x0, y0, x1, y1]
        self.grab = ()          # indexy súradníc, ktoré sa ťahajú
        self.last = (2, 3)

    def handle_at(self, pos):
        if self.r is None:
            return ()
        x, y = pos.x(), pos.y()
        for ix, iy in ((0, 1), (2, 1), (0, 3), (2, 3)):
            if abs(x - self.r[ix]) <= 10 and abs(y - self.r[iy]) <= 10:
                return ix, iy
        lo_x, hi_x = sorted((self.r[0], self.r[2]))
        lo_y, hi_y = sorted((self.r[1], self.r[3]))
        for ix in (0, 2):
            if abs(x - self.r[ix]) <= 6 and lo_y < y < hi_y:
                return (ix,)
        for iy in (1, 3):
            if abs(y - self.r[iy]) <= 6 and lo_x < x < hi_x:
                return (iy,)
        return ()

    def press(self, pos, mods):
        raw = self.view.raw if self.view.raw is not None else pos
        self.grab = self.handle_at(raw)                # úchyty podľa skutočného kurzora, nie kroku
        if not self.grab:
            self.r = [pos.x(), pos.y(), pos.x(), pos.y()]
            self.grab = (2, 3)
        self.last = self.grab

    def drag(self, pos, mods):
        if self.r is not None:
            for i in self.grab:
                self.r[i] = pos.x() if i % 2 == 0 else pos.y()

    def release(self, pos):
        self.grab = ()
        if self.r and abs(self.r[2] - self.r[0]) < 3 and abs(self.r[3] - self.r[1]) < 3:
            self.r = None

    def nudge(self, d):
        if self.r:
            for i in self.last:
                self.r[i] += d.x() if i % 2 == 0 else d.y()

    def rescale(self, k):
        if self.r:
            a, b = rescaled(self.view, (QPointF(self.r[0], self.r[1]), QPointF(self.r[2], self.r[3])), k)
            self.r = [a.x(), a.y(), b.x(), b.y()]

    def reset(self):
        self.r = None

    input_fields = ((L("Šírka", "Width"), "mm", False), (L("Výška", "Height"), "mm", False))

    def apply_input(self, nums):
        if len(nums) != 2 or min(nums) <= 0:
            return L("Zadaj dve kladné čísla: šírka × výška", "Enter two positive numbers: width × height")
        ppm = self.view.ppm()
        if self.r:
            o = QPointF(min(self.r[0], self.r[2]), min(self.r[1], self.r[3]))
        else:
            o = self.view.input_origin()
        b = QPointF(o.x() + nums[0] * ppm, o.y() + nums[1] * ppm)
        d = self.view.fit_shift((o, b))
        self.r = [o.x() + d.x(), o.y() + d.y(), b.x() + d.x(), b.y() + d.y()]
        self.last = (2, 3)
        return None

    def cursor(self, pos):
        h = self.handle_at(pos)
        if len(h) == 2:
            return Qt.CursorShape.SizeAllCursor
        if h:
            return Qt.CursorShape.SizeHorCursor if h[0] % 2 == 0 else Qt.CursorShape.SizeVerCursor
        return Qt.CursorShape.CrossCursor

    def screen_info(self):
        scr = self.view.screen() or QGuiApplication.primaryScreen()
        return scr, scr.geometry()

    def static_key(self):
        scr, g = self.screen_info()
        return (scr.name(), g.width(), g.height())

    def paint_static(self, p, W, H, th, ppm):
        scr, g = self.screen_info()
        w_mm, h_mm = g.width() / ppm, g.height() / ppm
        source = L("kalibrované", "calibrated") if screen_key(scr) in cfg["calib"] else L("podľa EDID", "from EDID")
        title = f"{L('Tento displej', 'This display')}: {fmt(math.hypot(w_mm, h_mm) / MM_PER_IN, 1)}″"
        info = (f"{fmt(w_mm, 1)} × {fmt(h_mm, 1)} mm · {g.width()} × {g.height()} px",
                f"{fmt(ppm * MM_PER_IN, 1)} PPI · {aspect_name(g.width(), g.height())} · {source}")
        f_title, f_info = ui_font(16, bold=True), ui_font(12)
        w = max(QFontMetricsF(f_title).horizontalAdvance(title),
                *(QFontMetricsF(f_info).horizontalAdvance(t) for t in info)) + 44
        card = QRectF((W - w) / 2, 24, w, 88)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(QPen(tint(th.ink, 40), 1))
        p.setBrush(tint(th.panel, 235))
        p.drawRoundedRect(card, 14, 14)
        p.setFont(f_title)
        p.setPen(th.ink)
        p.drawText(QRectF(card.x(), card.y() + 12, w, 24), Qt.AlignmentFlag.AlignCenter, title)
        p.setFont(f_info)
        p.setPen(th.soft)
        for k, t in enumerate(info):
            p.drawText(QRectF(card.x(), card.y() + 42 + k * 20, w, 20), Qt.AlignmentFlag.AlignCenter, t)

    def paint_dynamic(self, p, W, H, th, ppm):
        h = self.view.hover
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        if h is not None and not self.grab:
            p.setPen(QPen(tint(ACCENT, 90), 1))
            p.drawLine(round(h.x()), 0, round(h.x()), H)
            p.drawLine(0, round(h.y()), W, round(h.y()))
        if self.r is None:
            return
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = QRectF(QPointF(self.r[0], self.r[1]), QPointF(self.r[2], self.r[3])).normalized()
        w_mm, h_mm = rect.width() / ppm, rect.height() / ppm
        diag = math.hypot(w_mm, h_mm)
        p.setPen(QPen(ACCENT, 1.5))
        p.setBrush(tint(ACCENT, 30))
        p.drawRect(rect)
        p.setPen(QPen(MARK, 2))
        p.drawLine(rect.topLeft(), rect.bottomRight())
        p.setPen(QPen(ACCENT, 1.5))
        p.setBrush(QColor(255, 255, 255))
        for c in (rect.topLeft(), rect.topRight(), rect.bottomLeft(), rect.bottomRight()):
            p.drawRect(QRectF(c.x() - 4, c.y() - 4, 8, 8))
        pill(p, rect.center().x(), rect.top() - 14, mm_text(w_mm), ACCENT, 2, W - 2)
        text = mm_text(h_mm)
        pill(p, rect.left() - 8 - pill_size(text)[0] / 2, rect.center().y(), text, ACCENT, 2)
        if diag > 1:
            c = rect.center()
            pill(p, c.x(), c.y() - 16, f"{fmt(diag / MM_PER_IN, 1)}″", MARK, px=24)
            pill(p, c.x(), c.y() + 20, f"{fmt(diag / MM_PER_IN, 2)}″ · {fmt(diag / 10, 1)} cm · "
                 f"{aspect_name(w_mm, h_mm)}", QColor(70, 72, 86))


class PathTool(Tool):
    """Lomená čiara: dĺžka trasy; po uzavretí obvod a plocha."""
    hint = L("klikaj body · píš čísla = presný úsek · dvojklik/Enter = koniec"
             " · klik na prvý bod = plocha · Backspace = späť · Shift = po 15° · Esc = zmazať",
             "click points · type numbers = exact segment · double-click/Enter = finish"
             " · click the first point = area · Backspace = undo · Shift = 15° steps · Esc = clear")
    snap_kind = "point"

    def __init__(self, view):
        super().__init__(view)
        self.pts = []
        self.closed = False
        self.done = False
        self.grab = None

    def active(self):
        return bool(self.pts) and not (self.done or self.closed)

    def point_at(self, pos):
        return next((i for i, q in enumerate(self.pts) if near(pos, q, 8)), None)

    def can_close(self, pos):
        return self.active() and len(self.pts) >= 3 and self.point_at(pos) == 0

    def snapped(self, pos, mods):
        if not (mods & Qt.KeyboardModifier.ShiftModifier) or not self.active():
            return QPointF(pos)
        o = self.pts[-1]
        a = round(math.degrees(math.atan2(o.y() - pos.y(), pos.x() - o.x())) / 15) * 15
        return polar(o, a, math.hypot(pos.x() - o.x(), pos.y() - o.y()))

    def press(self, pos, mods):
        raw = self.view.raw if self.view.raw is not None else pos     # body podľa skutočného kurzora, nie kroku
        if self.can_close(raw):
            self.closed = True
            return
        i = self.point_at(raw)
        if i is not None:
            self.grab = i
            return
        if not self.active():
            self.pts, self.closed, self.done = [], False, False
        self.pts.append(self.snapped(pos, mods))

    def drag(self, pos, mods):
        if self.grab is not None:
            self.pts[self.grab] = QPointF(pos)

    def release(self, pos):
        self.grab = None

    def double(self, pos, mods):
        if len(self.pts) >= 2 and self.active():
            self.done = True

    def key(self, key):
        if key in (Qt.Key.Key_Return, Qt.Key.Key_Enter) and len(self.pts) >= 2 and self.active():
            self.done = True
        elif key == Qt.Key.Key_Backspace and self.pts:
            if self.closed:
                self.closed = False
            else:
                self.pts.pop()
            self.done = False
        else:
            return False
        return True

    def nudge(self, d):
        if self.pts:
            self.pts[-1] += d

    def rescale(self, k):
        if self.pts:
            self.pts = rescaled(self.view, self.pts, k)

    def reset(self):
        self.pts, self.closed, self.done = [], False, False

    input_fields = ((L("Dĺžka", "Length"), "mm", False), (L("Uhol", "Angle"), "°", True))

    def apply_input(self, nums):
        if len(nums) not in (1, 2) or nums[0] <= 0:
            return L("Zadaj dĺžku (kladnú), prípadne aj uhol v stupňoch", "Enter a positive length, optionally an angle in degrees")
        if not self.active():
            self.pts, self.closed, self.done = [self.view.input_origin()], False, False
        o, h = self.pts[-1], self.view.raw
        if len(nums) == 2:
            angle = nums[1]
        elif h is not None and not near(h, o, 2):      # smerom ku kurzoru
            angle = math.degrees(math.atan2(o.y() - h.y(), h.x() - o.x()))
        else:
            angle = 0.0
        self.pts.append(polar(o, angle, nums[0] * self.view.ppm()))
        return None

    def cursor(self, pos):
        if self.can_close(pos):
            return Qt.CursorShape.PointingHandCursor
        return Qt.CursorShape.OpenHandCursor if self.point_at(pos) is not None else Qt.CursorShape.CrossCursor

    def paint_dynamic(self, p, W, H, th, ppm):
        if not self.pts:
            return
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        h = self.view.hover
        pts = list(self.pts)
        rubber = self.active() and h is not None and self.grab is None
        if rubber:
            pts.append(self.snapped(h, QGuiApplication.keyboardModifiers()))
        segs = list(zip(pts, pts[1:])) + ([(pts[-1], pts[0])] if self.closed else [])
        total = sum(math.hypot(b.x() - a.x(), b.y() - a.y()) for a, b in segs) / ppm

        if self.closed:
            poly = QPainterPath(pts[0])
            for q in pts[1:]:
                poly.lineTo(q)
            poly.closeSubpath()
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(tint(ACCENT, 40))
            p.drawPath(poly)
        for k, (a, b) in enumerate(segs):
            dashed = rubber and k == len(segs) - 1
            p.setPen(QPen(MARK, 2, Qt.PenStyle.DashLine if dashed else Qt.PenStyle.SolidLine))
            p.drawLine(a, b)
            length = math.hypot(b.x() - a.x(), b.y() - a.y())
            if length > 70:
                mid = (a + b) / 2
                pill(p, mid.x(), mid.y() - 14, mm_text(length / ppm), QColor(70, 72, 86), px=11)
        closable = self.view.raw is not None and self.can_close(self.view.raw)
        for k, q in enumerate(self.pts):
            dot(p, q, ACCENT if k == 0 and closable else MARK, 7 if k == 0 and closable else 4.5)

        if self.closed:
            area = abs(sum(a.x() * b.y() - b.x() * a.y() for a, b in segs)) / 2 / ppm ** 2
            cen = sum(pts, QPointF()) / len(pts)
            pill(p, cen.x(), cen.y(), f"{L('obvod', 'perimeter')} {mm_text(total)} · {L('plocha', 'area')} {area_text(area)}", MARK, 2, W - 2, px=14)
        else:
            end = pts[-1]
            pill(p, end.x(), end.y() - 28, f"{L('spolu', 'total')} {mm_text(total)}", MARK, 2, W - 2, px=14)


class ScrewTool(Tool):
    """Metrické skrutky M1,6–M8: otvor, závit zboku, matica, dĺžka."""
    hint = L("skrutku polož na kruh alebo zboku na pás · maticu na šesťuholník · dĺžku meraj od hrubej čiary"
             " · prejdi myšou = detaily",
             "stand a screw on a circle or lay it on a strip · a nut on the hexagon · measure length from the thick line"
             " · hover = details")
    TOTAL_MM = 96

    def geom(self, W, H, ppm):
        widths = [(max(s[3], 9) + 5) * ppm for s in SCREWS]
        ox = (W - sum(widths)) / 2
        oy = max(16.0, (H - DOCK_RESERVE - self.TOTAL_MM * ppm) / 2)
        cols, x = [], ox
        for w in widths:
            cols.append((x + w / 2, w))
            x += w
        return ox, oy, cols

    def paint_static(self, p, W, H, th, ppm):
        ox, oy, cols = self.geom(W, H, ppm)
        Y = lambda mm: oy + mm * ppm
        right = cols[-1][0] + cols[-1][1] / 2
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        backdrop(p, QRectF(ox - 80, oy - 14, right - ox + 100, self.TOTAL_MM * ppm + 34), th)
        small = ui_font(11)
        fm = QFontMetricsF(small)
        p.setFont(small)
        p.setPen(th.soft)
        for text, mm in ((L("otvor", "hole"), 11), (L("zboku", "side"), 34), (L("matica", "nut"), 59),
                         (L("dĺžka", "length"), 86)):
            p.drawText(QPointF(ox - 12 - fm.horizontalAdvance(text), Y(mm) + 4), text)

        fill = tint(th.ink, 28)
        for (name, d, pitch, af, key), (cx, cw) in zip(SCREWS, cols):
            r = d / 2 * ppm
            centered_text(p, QPointF(cx, Y(3)), name, ui_font(14, bold=True), th.ink)
            p.setPen(QPen(th.ink, 1))
            p.setBrush(fill)
            p.drawEllipse(QPointF(cx, Y(11)), r, r)
            p.drawRect(QRectF(cx - r, Y(19), 2 * r, 30 * ppm))
            center, corner = QPointF(cx, Y(59)), af / math.sqrt(3) * ppm   # kľúč = vzdialenosť plôch
            nut = QPainterPath(polar(center, 0, corner))
            for a in range(60, 360, 60):
                nut.lineTo(polar(center, a, corner))
            nut.closeSubpath()
            p.drawPath(nut)
            p.setBrush(th.panel if th.glass else th.bg)
            p.drawEllipse(center, r, r)
            centered_text(p, QPointF(cx, Y(69.5)), f"{L('kľúč', 'wrench')} {fmt_g(af)}", ui_font(10), th.soft)
            centered_text(p, QPointF(cx, Y(73)), f"{L('imbus', 'hex key')} {fmt_g(key)}", ui_font(10), th.soft)

        # dĺžka: hlava sa opiera o hrubú čiaru
        base = round(Y(92))
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setPen(QPen(th.ink, 3))
        p.drawLine(round(ox), round(Y(80)), round(ox), base)
        p.setPen(QPen(th.ink, 1))
        p.drawLine(round(ox), base, round(ox + 70 * ppm), base)
        for mm in range(71):
            x = round(ox + mm * ppm)
            p.setPen(QPen(th.ink, 1))
            p.drawLine(x, base, x, base - (14 if mm % 10 == 0 else 10 if mm % 5 == 0 else 6))
            if mm % 5 == 0 and mm:
                centered_text(p, QPointF(x, base + 11), str(mm), ui_font(10), th.ink)
        p.setFont(small)
        p.setPen(th.soft)
        p.drawText(QPointF(ox + 73 * ppm, base - 4), L("← hlavu skrutky oprieť o hrubú čiaru", "← put the screw head against the thick line"))

    def paint_dynamic(self, p, W, H, th, ppm):
        h = self.view.hover
        if h is None:
            return
        ox, oy, cols = self.geom(W, H, ppm)
        Y = lambda mm: oy + mm * ppm
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if Y(0) <= h.y() <= Y(75):
            for (name, d, pitch, af, key), (cx, cw) in zip(SCREWS, cols):
                if abs(h.x() - cx) <= cw / 2:
                    p.setPen(Qt.PenStyle.NoPen)
                    p.setBrush(tint(ACCENT, 34))
                    p.drawRoundedRect(QRectF(cx - cw / 2 + 2, Y(-1), cw - 4, 77 * ppm), 10, 10)
                    pill(p, h.x(), h.y() - 26, L(f"{name} · závit {fmt_g(d)} mm · stúpanie {fmt_g(pitch)} mm"
                           f" · matica {fmt_g(af)} mm · imbus {fmt_g(key)} mm",
                           f"{name} · thread {fmt_g(d)} mm · pitch {fmt_g(pitch)} mm"
                           f" · nut {fmt_g(af)} mm · hex key {fmt_g(key)} mm"), ACCENT, 2, W - 2)
        elif Y(78) <= h.y() <= Y(96) and ox <= h.x() <= ox + 70 * ppm:
            x = round(h.x())
            p.setPen(QPen(ACCENT, 1))
            p.drawLine(QPointF(x, Y(78)), QPointF(x, Y(92)))
            pill(p, h.x(), Y(78) - 14, f"{L('dĺžka', 'length')} {fmt((h.x() - ox) / ppm, 1)} mm", ACCENT, 2, W - 2)


class PitchTool(Tool):
    """Hrebene bežných roztečí pinov — na určenie konektora či púzdra čipu."""
    hint = L("prilož konektor alebo čip k hrebeňu · prejdi myšou po hrebeni = počet pinov a dĺžka",
             "hold a connector or chip to a comb · hover along the comb = pin count and length")
    ROW_MM, COMB_MM, LABEL_W = 9.0, 60.0, 330

    def geom(self, W, H, ppm):
        left = (W - self.LABEL_W - self.COMB_MM * ppm) / 2
        oy = max(16.0, (H - DOCK_RESERVE - len(PITCHES) * self.ROW_MM * ppm) / 2)
        return left, left + self.LABEL_W, oy

    def paint_pins(self, p, x0, top, pitch, ppm, color, count=None):
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(color)
        step = pitch * ppm
        pw = max(1, round(pitch * 0.45 * ppm))
        y, ph = round(top + 2 * ppm), round(4 * ppm)
        n = int(self.COMB_MM / pitch + 1e-9) + 1 if count is None else count
        for k in range(n):
            p.drawRect(QRectF(round(x0 + k * step - pw / 2), y, pw, ph))
        p.setRenderHint(QPainter.RenderHint.Antialiasing)

    def paint_static(self, p, W, H, th, ppm):
        left, x0, oy = self.geom(W, H, ppm)
        row = self.ROW_MM * ppm
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        backdrop(p, QRectF(left - 20, oy - 14, x0 - left + self.COMB_MM * ppm + 44, len(PITCHES) * row + 28), th)
        for i, (pitch, text) in enumerate(PITCHES):
            top = oy + i * row
            p.setFont(ui_font(12))
            p.setPen(th.ink)
            p.drawText(QRectF(left, top + ppm, self.LABEL_W - 16, 6 * ppm),
                       Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, text)
            self.paint_pins(p, x0, top, pitch, ppm, th.ink)
            y = round(top + 6 * ppm) + 1
            p.setPen(QPen(tint(th.ink, 110), 1))
            p.drawLine(QPointF(x0 - 4, y), QPointF(x0 + self.COMB_MM * ppm + 4, y))

    def paint_dynamic(self, p, W, H, th, ppm):
        h = self.view.hover
        if h is None:
            return
        left, x0, oy = self.geom(W, H, ppm)
        row = self.ROW_MM * ppm
        i = math.floor((h.y() - oy) / row)
        if not 0 <= i < len(PITCHES):
            return
        pitch = PITCHES[i][0]
        step = pitch * ppm
        if not x0 - step <= h.x() <= x0 + self.COMB_MM * ppm + step:
            return
        total = int(self.COMB_MM / pitch + 1e-9) + 1
        n = max(1, min(total, math.floor((h.x() - x0) / step + 0.5) + 1))
        top = oy + i * row
        self.paint_pins(p, x0, top, pitch, ppm, ACCENT, n)
        pill(p, h.x(), top + 7.6 * ppm, f"{pins_text(n)} · {fmt((n - 1) * pitch, 2)} mm", ACCENT, 2, W - 2)


# ---------- ikonky v paneli ----------

def tool_icon(kind):
    pm = QPixmap(52, 52)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.scale(2, 2)       # kreslí sa v 26×26
    ink = QColor(58, 58, 70)
    pen = QPen(ink, 1.5)
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    yellow, blue, light_blue = QColor(246, 200, 62), ACCENT, QColor(196, 230, 248)
    cm_marks = [(5 + 2 * i, 5 if i % 5 == 0 else 3) for i in range(9)]
    in_marks = [(5, 6), (9, 2.5), (13, 4), (17, 2.5), (21, 6)]

    if kind in ("cm", "in", "dual"):
        p.setBrush(yellow)
        p.drawRoundedRect(QRectF(2, 7, 22, 12), 2, 2)
        for x, ln in (in_marks if kind == "in" else cm_marks):
            p.drawLine(QPointF(x, 7), QPointF(x, 7 + ln))
        if kind == "dual":
            for x, ln in in_marks:
                p.drawLine(QPointF(x, 19), QPointF(x, 19 - ln * 0.7))
    elif kind == "px":
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawRoundedRect(QRectF(3, 4, 20, 14), 2, 2)
        p.drawLine(QPointF(13, 18), QPointF(13, 22))
        p.drawLine(QPointF(9, 22), QPointF(17, 22))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(blue)
        for x, y in ((6.5, 7.5), (9.5, 7.5), (6.5, 10.5), (12.5, 10.5), (15.5, 13.5), (18.5, 13.5)):
            p.drawRect(QRectF(x, y, 2.4, 2.4))
    elif kind == "grid":
        p.setBrush(QColor(255, 255, 255))
        p.drawRect(QRectF(4, 4, 18, 18))
        p.setPen(QPen(ink, 0.7))
        for k in (8.5, 13, 17.5):
            p.drawLine(QPointF(k, 4), QPointF(k, 22))
            p.drawLine(QPointF(4, k), QPointF(22, k))
    elif kind == "prot":
        half = QPainterPath()
        half.moveTo(24, 19)
        half.arcTo(QRectF(2, 8, 22, 22), 0, 180)
        half.closeSubpath()
        p.setBrush(light_blue)
        p.drawPath(half)
        p.setPen(QPen(MARK, 1.8))
        p.drawLine(QPointF(13, 19), polar(QPointF(13, 19), 55, 10))
    elif kind == "prot360":
        p.setBrush(light_blue)
        p.drawEllipse(QPointF(13, 13), 10, 10)
        p.setPen(QPen(blue, 1.8))
        p.drawLine(QPointF(13, 13), QPointF(22, 13))
        p.setPen(QPen(MARK, 1.8))
        p.drawLine(QPointF(13, 13), polar(QPointF(13, 13), 130, 9))
    elif kind == "circle":
        p.setBrush(Qt.BrushStyle.NoBrush)
        for r in (3.5, 7, 10.5):
            p.drawEllipse(QPointF(13, 13), r, r)
        p.setBrush(ink)
        p.drawEllipse(QPointF(13, 13), 1, 1)
    elif kind == "bg":
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawEllipse(QPointF(13, 13), 9, 9)
        p.setBrush(ink)
        p.drawPie(QRectF(4, 4, 18, 18), 90 * 16, 180 * 16)
    elif kind == "calib":
        p.setBrush(light_blue)
        p.drawRoundedRect(QRectF(3, 7, 20, 13), 2, 2)
        p.setPen(QPen(ink, 2.2))
        p.drawLine(QPointF(3.5, 11), QPointF(22.5, 11))
    elif kind == "hide":
        p.drawPolyline([QPointF(7, 10), QPointF(13, 16), QPointF(19, 10)])
    elif kind == "radius":
        c = QPointF(6, 21)
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawArc(QRectF(c.x() - 16, c.y() - 16, 32, 32), 0, 90 * 16)
        p.setPen(QPen(ink, 1, Qt.PenStyle.DashLine))
        p.drawLine(c, polar(c, 45, 16))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(MARK)
        for a in (8, 45, 82):
            p.drawEllipse(polar(c, a, 16), 2.2, 2.2)
    elif kind == "diag":
        p.setBrush(light_blue)
        p.drawRoundedRect(QRectF(7, 2, 12, 22), 2.5, 2.5)
        p.setPen(QPen(MARK, 1.8))
        p.drawLine(QPointF(9, 4.5), QPointF(17, 21.5))
    elif kind == "path":
        pts = [QPointF(3, 20), QPointF(9, 9), QPointF(15, 17), QPointF(23, 5)]
        p.drawPolyline(pts)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(MARK)
        for q in pts:
            p.drawEllipse(q, 2.2, 2.2)
    elif kind == "screw":
        p.setBrush(QColor(200, 200, 208))
        p.drawRoundedRect(QRectF(2.5, 8, 6, 10), 1.5, 1.5)
        p.drawRect(QRectF(8.5, 10.5, 15, 5))
        p.setPen(QPen(ink, 1))
        x = 10.5
        while x < 23:
            p.drawLine(QPointF(x, 10.5), QPointF(x + 1.5, 15.5))
            x += 2.5
    elif kind == "pitch":
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(150, 150, 160))
        for x in (6.5, 10.5, 14.5, 18.5):
            p.drawRect(QRectF(x - 0.9, 3.5, 1.8, 5))
            p.drawRect(QRectF(x - 0.9, 17.5, 1.8, 5))
        p.setBrush(QColor(60, 60, 72))
        p.drawRoundedRect(QRectF(4, 8, 18, 10), 1.5, 1.5)
        p.setBrush(QColor(255, 255, 255))
        p.drawEllipse(QPointF(7, 11), 1, 1)
    elif kind == "snap":
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(150, 150, 160))
        for x in (5, 13, 21):
            for y in (5, 13, 21):
                p.drawEllipse(QPointF(x, y), 1.4, 1.4)
        p.setPen(QPen(blue, 1.6))
        p.drawLine(QPointF(13, 7), QPointF(13, 19))
        p.drawLine(QPointF(7, 13), QPointF(19, 13))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(MARK)
        p.drawEllipse(QPointF(13, 13), 2.6, 2.6)
    elif kind == "type":
        p.setBrush(QColor(255, 255, 255))
        p.drawRoundedRect(QRectF(2, 6, 22, 14), 2.5, 2.5)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(ink)
        for y in (9, 12.5):
            for k in range(5):
                p.drawRect(QRectF(4.6 + k * 3.6, y, 2.2, 2))
        p.drawRect(QRectF(8, 16, 10, 1.8))
    elif kind == "window":
        p.setBrush(QColor(255, 255, 255))
        p.drawRoundedRect(QRectF(3, 5, 20, 16), 2, 2)
        p.drawLine(QPointF(3, 9), QPointF(23, 9))
        p.setPen(QPen(blue, 1.4))
        p.drawLine(QPointF(15, 18.5), QPointF(20.5, 13))
        p.drawLine(QPointF(18, 18.5), QPointF(20.5, 16))
    elif kind == "close":
        p.drawLine(QPointF(8, 8), QPointF(18, 18))
        p.drawLine(QPointF(18, 8), QPointF(8, 18))
    p.end()
    return QIcon(pm)


DOCK_CSS = """
#dock { background: rgba(252, 252, 249, 245); border: 1px solid rgba(0, 0, 0, 38); border-radius: 18px; }
#dock QToolButton { border: none; border-radius: 12px; padding: 5px 4px 4px 4px; color: #2a2a33;
                    font-size: 12px; }
#dock QToolButton:hover { background: rgba(31, 158, 222, 28); }
#dock QToolButton:checked { background: rgba(31, 158, 222, 62); color: #0d5e88; }
#dock QToolButton:disabled { color: #a0a0aa; }
#dock QToolButton::menu-indicator { image: none; width: 0px; }
#dock #sep { background: rgba(0, 0, 0, 30); margin: 10px 0; }
#showDock { background: rgba(252, 252, 249, 235); border: 1px solid rgba(0, 0, 0, 38);
            border-radius: 12px; padding: 4px 14px; color: #2a2a33; font-size: 12px; }
#showDock:hover { background: rgb(232, 244, 251); }
#inputBar { background: rgba(252, 252, 249, 248); border: 1px solid rgba(0, 0, 0, 45); border-radius: 16px; }
#inputLabel { color: #2a2a33; font-size: 13px; }
#inputEdit { border: 1px solid rgba(31, 158, 222, 170); border-radius: 9px; padding: 5px 10px; font-size: 15px;
             background: white; color: #1a1a22; selection-background-color: rgb(31, 158, 222); }
#inputUnit { color: #5a5a66; font-size: 13px; margin-right: 10px; }
#inputHelp { color: #7a7a86; font-size: 11px; }
"""


class Dock(QFrame):
    """Panel dole: výber pravítka + pozadie, kalibrácia, skryť, zavrieť."""

    def __init__(self, view):
        super().__init__(view)
        self.view = view
        self.setObjectName("dock")
        lay = QHBoxLayout(self)
        lay.setContentsMargins(8, 6, 8, 6)
        lay.setSpacing(2)
        self.group = QButtonGroup(self)
        self.buttons = {}
        for key, text in TOOLS:
            if key in GROUP_STARTS:
                self.separator(lay)
            b = self.button(lay, text, tool_icon(key), checkable=True)
            b.setToolTip(f"{text}  (Tab / Shift+Tab = {L('ďalší / predošlý', 'next / previous')})")
            b.clicked.connect(lambda _=False, k=key: view.set_tool(k))
            self.group.addButton(b)
            self.buttons[key] = b
        self.separator(lay)
        self.snap_button = self.button(lay, L("Voľne", "Free"), tool_icon("snap"))
        self.snap_menu = QMenu(self.snap_button)
        self.snap_menu.aboutToShow.connect(self.fill_snap_menu)
        self.snap_button.setMenu(self.snap_menu)
        self.snap_button.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.type_button = self.button(lay, L("Zadať", "Type"), tool_icon("type"))
        self.type_button.clicked.connect(lambda: view.input_bar.open())
        self.bg_button = self.button(lay, BG_NAMES[view.bg], tool_icon("bg"))
        self.bg_button.setToolTip(L("Pozadie: svetlé / tmavé / zelené / priehľadné (B)", "Background: light / dark / green / clear (B)"))
        self.bg_button.clicked.connect(view.cycle_bg)
        b = self.button(lay, L("Kalibrácia", "Calibrate"), tool_icon("calib"))
        b.setToolTip(L("Dokalibrovať mierku kartou (K)", "Calibrate the scale with a card (K)"))
        b.clicked.connect(view.calibrate)
        self.window_button = self.button(lay, L("Okno", "Window"), tool_icon("window"), checkable=True)
        self.window_button.setToolTip(L("Prepnúť okno ↔ celá obrazovka (F11) — okno sa dá zväčšovať a zmenšovať",
                                       "Switch window ↔ fullscreen (F11) — the window is resizable"))
        self.window_button.clicked.connect(view.toggle_fullscreen)
        b = self.button(lay, L("Skryť", "Hide"), tool_icon("hide"))
        b.setToolTip(L("Skryť panel (H)", "Hide the panel (H)"))
        b.clicked.connect(view.toggle_dock)
        b = self.button(lay, L("Zavrieť", "Close"), tool_icon("close"))
        b.setToolTip(L("Zavrieť (Q)", "Close (Q)"))
        b.clicked.connect(view.close)

        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(26)
        shadow.setOffset(0, 4)
        shadow.setColor(QColor(0, 0, 0, 70))
        self.setGraphicsEffect(shadow)

    def button(self, lay, text, icon, checkable=False):
        b = QToolButton(self)
        b.setText(text)
        b.setIcon(icon)
        b.setIconSize(QSize(26, 26))
        b.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextUnderIcon)
        b.setCheckable(checkable)
        b.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        b.setCursor(Qt.CursorShape.PointingHandCursor)
        b.setMinimumWidth(60)
        lay.addWidget(b)
        return b

    def set_compact(self, compact):
        """Na užšej obrazovke len ikonky (názov je v bublinke)."""
        style = Qt.ToolButtonStyle.ToolButtonIconOnly if compact else Qt.ToolButtonStyle.ToolButtonTextUnderIcon
        for b in self.findChildren(QToolButton):
            b.setToolButtonStyle(style)
            b.setMinimumWidth(40 if compact else 60)
        self.snap_button.setMinimumWidth(40 if compact else 82)    # nech panel neskáče pri zmene kroku
        self.adjustSize()

    def fill_snap_menu(self):
        m = self.snap_menu
        m.clear()
        family = self.view.snap_family()
        group = QActionGroup(m)
        options = [L("Voľne (bez kroku)", "Free (no snapping)")] + [f"{L('Krok', 'Snap')} {text}" for _, text in SNAP[family]]
        for level, text in enumerate(options):
            a = m.addAction(text)
            a.setCheckable(True)
            a.setChecked(level == self.view.snap_level)
            group.addAction(a)
            a.triggered.connect(lambda _=False, lv=level: self.view.set_snap(lv))
        m.addSeparator()
        m.addAction(L("S = prepínanie", "S = cycle")).setEnabled(False)

    def separator(self, lay):
        s = QFrame(self)
        s.setObjectName("sep")
        s.setFixedWidth(1)
        lay.addSpacing(5)
        lay.addWidget(s)
        lay.addSpacing(5)

    def enterEvent(self, e):
        self.view.hover = self.view.raw = None      # nitkový kríž nemá ostať visieť pod panelom
        self.view.update()
        super().enterEvent(e)


class NumberEdit(QLineEdit):
    """Políčko na číslo: medzera alebo x skočí do ďalšieho, Backspace v prázdnom späť."""

    def __init__(self, bar, index):
        super().__init__(bar)
        self.bar, self.index = bar, index
        self.setObjectName("inputEdit")
        self.setFixedWidth(116)
        self.textEdited.connect(self.to_digits)
        self.returnPressed.connect(lambda: bar.next_or_apply(index))

    def to_digits(self, text):
        """ľščť… z horného radu SK klávesnice rovno prepíše na číslice."""
        fixed = text.translate(SK_DIGITS)
        if fixed != text:
            pos = self.cursorPosition()
            self.setText(fixed)
            self.setCursorPosition(pos)

    def keyPressEvent(self, e):
        if e.text() in (" ", "x", "X", "×", "*") and self.bar.go(self.index + 1):
            return
        if e.key() == Qt.Key.Key_Backspace and not self.text() and self.bar.go(self.index - 1):
            return
        super().keyPressEvent(e)


class InputBar(QFrame):
    """Políčka na presné zadanie z klávesnice: číslo, Enter, ďalšie číslo, Enter."""

    def __init__(self, view):
        super().__init__(view)
        self.view = view
        self.setObjectName("inputBar")
        self.lay = QHBoxLayout(self)
        self.lay.setContentsMargins(16, 8, 10, 8)
        self.lay.setSpacing(8)
        self.widgets, self.edits = [], []
        self.help = QLabel(self)
        self.help.setObjectName("inputHelp")
        self.lay.addWidget(self.help)
        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(22)
        shadow.setOffset(0, 3)
        shadow.setColor(QColor(0, 0, 0, 60))
        self.setGraphicsEffect(shadow)
        self.hide()

    def build(self, fields):
        for w in self.widgets:
            self.lay.removeWidget(w)
            w.hide()
            w.deleteLater()
        self.widgets, self.edits = [], []
        for i, (name, unit, optional) in enumerate(fields):
            label = QLabel(name, self)
            label.setObjectName("inputLabel")
            edit = NumberEdit(self, i)
            if optional:
                edit.setPlaceholderText(L("nepovinné", "optional"))
            unit_label = QLabel(unit, self)
            unit_label.setObjectName("inputUnit")
            for w in (label, edit, unit_label):
                self.lay.insertWidget(len(self.widgets), w)
                self.widgets.append(w)
            self.edits.append(edit)

    def go(self, i):
        """Presun do i-teho políčka; False, ak také nie je."""
        if not 0 <= i < len(self.edits):
            return False
        self.edits[i].setFocus()
        self.edits[i].selectAll()
        return True

    def next_or_apply(self, i):
        name, _, optional = self.view.tool.input_fields[i]
        if not optional and not parse_numbers(self.edits[i].text()):
            self.say(f"{L('Vyplň', 'Fill in')}: {name}", error=True)     # z prázdneho povinného políčka sa nejde ďalej
            return
        if not self.go(i + 1):
            self.apply()

    def say(self, text, error=False):
        self.help.setText(text)
        self.help.setStyleSheet("color: #d23a48; font-weight: bold;" if error else "")

    def open(self, text=""):
        fields = self.view.tool.input_fields
        if not fields:
            return
        self.build(fields)
        self.edits[0].setText(text.translate(SK_DIGITS))
        self.say(L("Enter = ďalšie políčko, potom použiť · Esc = zrušiť", "Enter = next field, then apply · Esc = cancel") if len(fields) > 1
                 else L("Enter = použiť · Esc = zrušiť", "Enter = apply · Esc = cancel"))
        self.show()
        self.view.layout_dock()
        self.raise_()
        self.edits[0].setFocus()

    def close_bar(self):
        self.hide()
        self.view.setFocus()
        self.view.update()

    def keyPressEvent(self, e):
        # QLineEdit Enter po returnPressed pustí ďalej — nesmie dôjsť do okna (znova by otvoril pole)
        if e.key() == Qt.Key.Key_Escape:
            self.close_bar()
        elif e.key() not in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
            super().keyPressEvent(e)

    def apply(self):
        nums = []
        for edit, (name, unit, optional) in zip(self.edits, self.view.tool.input_fields):
            values = parse_numbers(edit.text())
            if not values and not optional:
                self.say(f"{L('Vyplň', 'Fill in')}: {name}", error=True)
                edit.setFocus()
                return
            nums += values
        error = self.view.tool.apply_input(nums)
        if error:
            self.say(error, error=True)
            return
        self.close_bar()


# ---------- hlavné okno ----------

class Regula(QWidget):
    def __init__(self, start_tool=None):
        super().__init__()
        self.setWindowTitle("Regula")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)   # kvôli priehľadnému pozadiu
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setStyleSheet(DOCK_CSS)
        self.bg = cfg["bg"] if cfg["bg"] in THEMES else "light"
        self.tools = {
            "cm": RulerTool(self, "cm"), "in": RulerTool(self, "in"), "px": RulerTool(self, "px"),
            "dual": RulerTool(self, "dual"), "grid": RulerTool(self, "cm", grid=True),
            "prot": ProtractorTool(self, False), "prot360": ProtractorTool(self, True),
            "circle": CircleTool(self), "radius": RadiusTool(self),
            "diag": DiagonalTool(self), "path": PathTool(self),
            "screw": ScrewTool(self), "pitch": PitchTool(self),
        }
        self.hover = None       # poloha kurzora po prichytení na krok
        self.raw = None         # skutočná poloha kurzora (na hľadanie bodov a úchytov)
        self._ppm = None        # px/mm obrazovky, na ktorej okno naposledy bolo
        self.snap_level = cfg["snap"] if cfg["snap"] in range(len(SNAP["mm"]) + 1) else 0
        self._cache = (None, None)

        self.dock = Dock(self)
        self.show_dock = QPushButton(L("▴  Pravítka", "▴  Tools"), self)
        self.show_dock.setObjectName("showDock")
        self.show_dock.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        self.show_dock.setCursor(Qt.CursorShape.PointingHandCursor)
        self.show_dock.clicked.connect(self.toggle_dock)
        self.show_dock.hide()
        self.input_bar = InputBar(self)
        self.dock.set_compact(True)     # okno nesmie byť užšie než panel s ikonkami
        self.setMinimumSize(self.dock.width() + 32, 520)
        self.dock.set_compact(False)

        for keys, slot in ((("Esc",), self.reset_tool), (("H",), self.toggle_dock), (("B",), self.cycle_bg),
                           (("K",), self.calibrate), (("S",), self.cycle_snap), (("F11",), self.toggle_fullscreen),
                           (("Q", "Ctrl+Q", "Ctrl+W"), self.close)):
            for k in keys:
                QShortcut(QKeySequence(k), self).activated.connect(slot)

        name = start_tool or cfg["tool"]
        self.set_tool(name if name in self.tools else "cm")

    # --- stav ---
    def ppm(self):
        return px_per_mm(self.screen() or QGuiApplication.primaryScreen())

    def set_tool(self, name):
        self.tool_name = name
        self.tool = self.tools[name]
        self.dock.buttons[name].setChecked(True)
        cfg.set("tool", name)
        self.input_bar.hide()
        self.dock.type_button.setEnabled(bool(self.tool.input_fields))
        self.dock.type_button.setToolTip(L("Zadať presný rozmer z klávesnice (alebo rovno píš čísla)", "Type an exact size (or just start typing numbers)")
                                         if self.tool.input_fields else L("Tento nástroj nemá čo zadať", "Nothing to type for this tool"))
        self.update_snap_button()
        self.update()

    # --- zadávanie z klávesnice ---
    def input_origin(self):
        """Odkiaľ začne zadaný rozmer: od kurzora, inak z ľavej hornej tretiny."""
        p = self.raw
        if p is None or not (0 <= p.x() < self.width() and 0 <= p.y() < self.height() - DOCK_RESERVE):
            p = QPointF(round(self.width() * 0.3), round(self.height() * 0.3))
        return self.tool_pos(p)

    def fit_shift(self, pts):
        """Posun, aby sa zadaný rozmer zmestil do okna (ak sa dá)."""
        W, H = self.width() - 20, self.height() - DOCK_RESERVE - 20
        xs, ys = [q.x() for q in pts], [q.y() for q in pts]
        dx = W - max(xs) if max(xs) > W else max(0.0, 60 - min(xs))
        dy = H - max(ys) if max(ys) > H else max(0.0, 60 - min(ys))
        return QPointF(dx, dy)

    # --- krok (prichytávanie) ---
    def snap_family(self):
        if self.tool.snap_kind == "angle":
            return "deg"
        if self.tool.snap_kind == "point":
            return self.tool.unit if self.tool.unit in ("in", "px") else "mm"
        return None

    def snap_value(self):
        """Krok v jednotke nástroja (mm, palce, px, stupne) alebo None = voľne."""
        family = self.snap_family()
        return SNAP[family][self.snap_level - 1][0] if family and self.snap_level else None

    def snap_px(self):
        value, family = self.snap_value(), self.snap_family()
        if value is None or family == "deg":
            return None
        return value if family == "px" else value * self.ppm() * (MM_PER_IN if family == "in" else 1)

    def tool_pos(self, raw):
        step = self.snap_px() if self.tool.snap_kind == "point" else None
        if not step:
            return raw
        return QPointF(round(raw.x() / step) * step, round(raw.y() / step) * step)

    def set_snap(self, level):
        self.snap_level = level
        cfg.set("snap", level)
        self.update_snap_button()
        self.update()

    def cycle_snap(self):
        if self.snap_family():
            self.set_snap((self.snap_level + 1) % (len(SNAP["mm"]) + 1))

    def update_snap_button(self):
        family = self.snap_family()
        b = self.dock.snap_button
        b.setEnabled(family is not None)
        if family is None:
            b.setText(L("Krok –", "Snap –"))
            b.setToolTip(L("Tento nástroj krok nepoužíva", "This tool doesn't snap"))
        else:
            b.setText(f"{L('Krok', 'Snap')} {SNAP[family][self.snap_level - 1][1]}" if self.snap_level else L("Voľne", "Free"))
            b.setToolTip(L("Krok pohybu — body skáču na presné hodnoty, napr. 20 × 50 mm (S)",
                           "Snapping — points jump to exact values, e.g. 20 × 50 mm (S)"))
        self.layout_dock()

    def step_tool(self, delta):
        keys = [k for k, _ in TOOLS]
        self.set_tool(keys[(keys.index(self.tool_name) + delta) % len(keys)])

    def reset_tool(self):
        if self.input_bar.isVisible():
            self.input_bar.close_bar()
            return
        self.tool.reset()
        self.update()

    def cycle_bg(self):
        order = list(THEMES)
        self.bg = order[(order.index(self.bg) + 1) % len(order)]
        cfg.set("bg", self.bg)
        self.dock.bg_button.setText(BG_NAMES[self.bg])
        self.update()

    def toggle_dock(self):
        visible = not self.dock.isVisible()
        self.dock.setVisible(visible)
        self.show_dock.setVisible(not visible)
        self.update()

    def toggle_fullscreen(self):
        if self.isFullScreen():
            self.showNormal()
            self.restore_window()
            cfg.set("fullscreen", False)
        else:
            self.save_window()
            self.showFullScreen()
            cfg.set("fullscreen", True)
        self.dock.window_button.setChecked(not cfg["fullscreen"])

    def restore_window(self):
        """Posledná veľkosť a poloha okna, inak 80 % obrazovky v strede."""
        saved = cfg["window"]
        if not (saved and self.restoreGeometry(QByteArray.fromBase64(saved.encode()))):
            a = (self.screen() or QGuiApplication.primaryScreen()).availableGeometry()
            w, h = max(self.minimumWidth(), round(a.width() * 0.8)), round(a.height() * 0.8)
            self.setGeometry(a.x() + max(0, (a.width() - w) // 2), a.y() + max(0, (a.height() - h) // 2), w, h)

    def save_window(self):
        if not self.isFullScreen():
            cfg.set("window", bytes(self.saveGeometry().toBase64()).decode())

    def closeEvent(self, e):
        self.save_window()
        super().closeEvent(e)

    def calibrate(self):
        CalibrationDialog(self.screen() or QGuiApplication.primaryScreen(), self).exec()
        self._ppm = self.ppm()      # kalibrácia meria to isté miesto znova — body sa neprepočítavajú
        self.update()

    def showEvent(self, e):
        super().showEvent(e)
        if self._ppm is None:
            self._ppm = self.ppm()
            self.windowHandle().screenChanged.connect(self.screen_changed)

    def screen_changed(self, screen):
        """Presun na inú obrazovku: merania ostanú v rovnakých mm, nie v rovnakých pixeloch."""
        new = px_per_mm(screen)
        if self._ppm and abs(new / self._ppm - 1) > 1e-6:
            for t in self.tools.values():
                t.rescale(new / self._ppm)
            self.hover = self.raw = None
        self._ppm = new
        self.update()

    # --- rozloženie ---
    def layout_dock(self):
        self.dock.set_compact(False)
        if self.dock.width() > self.width() - 32:
            self.dock.set_compact(True)
        self.dock.move((self.width() - self.dock.width()) // 2, self.height() - self.dock.height() - 14)
        self.show_dock.adjustSize()
        self.show_dock.move((self.width() - self.show_dock.width()) // 2,
                            self.height() - self.show_dock.height() - 10)
        self.input_bar.adjustSize()
        above = self.dock.y() if self.dock.isVisible() else self.show_dock.y()
        self.input_bar.move((self.width() - self.input_bar.width()) // 2, above - self.input_bar.height() - 12)

    def resizeEvent(self, e):
        self.layout_dock()
        for t in self.tools.values():
            t.relayout()
        super().resizeEvent(e)

    # --- myš a klávesy ---
    def track(self, e):
        """Zapamätá skutočný kurzor a vráti polohu po prichytení na krok."""
        self.raw = QPointF(round(e.position().x()), round(e.position().y()))
        return self.tool_pos(self.raw)

    def mousePressEvent(self, e):
        if self.input_bar.isVisible():
            self.input_bar.close_bar()
        if e.button() == Qt.MouseButton.LeftButton:
            self.tool.press(self.track(e), e.modifiers())
            self.update()

    def mouseMoveEvent(self, e):
        pos = self.track(e)
        self.hover = pos
        if e.buttons() & Qt.MouseButton.LeftButton:
            self.tool.drag(pos, e.modifiers())
        else:
            self.setCursor(self.tool.cursor(self.raw))
        self.update()

    def mouseReleaseEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self.tool.release(self.track(e))
            self.setCursor(self.tool.cursor(self.raw))
            self.update()

    def mouseDoubleClickEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self.tool.double(self.track(e), e.modifiers())
            self.update()

    def wheelEvent(self, e):
        steps = e.angleDelta().y() / 120
        if steps:
            self.tool.wheel(steps)
            self.update()

    def leaveEvent(self, _):
        self.hover = self.raw = None
        self.update()

    def event(self, e):
        # Tab inak Qt použije na presun fokusu
        if e.type() == QEvent.Type.KeyPress and e.key() in (Qt.Key.Key_Tab, Qt.Key.Key_Backtab):
            self.step_tool(-1 if e.key() == Qt.Key.Key_Backtab else 1)
            return True
        return super().event(e)

    def keyPressEvent(self, e):
        if self.tool.key(e.key()):
            self.update()
            return
        typed = e.text()[:1]
        if self.tool.input_fields and (e.key() in (Qt.Key.Key_Return, Qt.Key.Key_Enter)
                                       or typed.isdigit() or (typed and typed in "+ľěščťřžýáíé.,")):
            self.input_bar.open("" if e.key() in (Qt.Key.Key_Return, Qt.Key.Key_Enter) else typed)
            return
        # šípky: o 0,1 mm (pri pixeloch o 1 px), so Shiftom 10×
        step = 1.0 if self.tool.unit == "px" and self.tool.snap_kind == "point" else 0.1 * self.ppm()
        if e.modifiers() & Qt.KeyboardModifier.ShiftModifier:
            step *= 10
        d = {Qt.Key.Key_Left: QPointF(-step, 0), Qt.Key.Key_Right: QPointF(step, 0),
             Qt.Key.Key_Up: QPointF(0, -step), Qt.Key.Key_Down: QPointF(0, step)}.get(e.key())
        if d is None:
            return super().keyPressEvent(e)
        self.tool.nudge(d)
        self.update()

    # --- kreslenie ---
    def paintEvent(self, _):
        W, H = self.width(), self.height()
        th, ppm, tool = THEMES[self.bg], self.ppm(), self.tool
        tool.layout(W, H)
        dpr = self.devicePixelRatioF()
        key = (self.tool_name, W, H, round(ppm, 6), self.bg, dpr) + tool.static_key()
        if self._cache[0] != key:   # statická vrstva sa kreslí len pri zmene, nie pri každom pohybe myši
            pm = QPixmap(round(W * dpr), round(H * dpr))
            pm.setDevicePixelRatio(dpr)
            pm.fill(Qt.GlobalColor.transparent)
            sp = QPainter(pm)
            sp.fillRect(QRectF(0, 0, W, H), th.bg)
            tool.paint_static(sp, W, H, th, ppm)
            sp.end()
            self._cache = (key, pm)

        p = QPainter(self)
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_Source)
        p.drawPixmap(0, 0, self._cache[1])
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceOver)
        tool.paint_dynamic(p, W, H, th, ppm)

        if self.dock.isVisible() and tool.hint and not self.input_bar.isVisible():
            p.setRenderHint(QPainter.RenderHint.Antialiasing)
            font = ui_font(12)
            w = QFontMetricsF(font).horizontalAdvance(tool.hint) + 28
            r = QRectF((W - w) / 2, self.dock.y() - 34, w, 24)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(tint(th.panel, 235))
            p.drawRoundedRect(r, 12, 12)
            p.setPen(th.soft)
            p.setFont(font)
            p.drawText(r, Qt.AlignmentFlag.AlignCenter, tool.hint)


# ---------- kalibrácia ----------

class CardView(QWidget):
    PAD = 12

    def __init__(self, dlg):
        super().__init__(dlg)
        self.dlg = dlg
        top = dlg.base * 1.16
        self.setFixedSize(round(CARD_MM[0] * top + 2 * self.PAD), round(CARD_MM[1] * top + 2 * self.PAD))

    def paintEvent(self, _):
        ppm = self.dlg.ppm
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        card = QRectF(self.PAD, self.PAD, CARD_MM[0] * ppm, CARD_MM[1] * ppm)
        p.setPen(QPen(ACCENT, 1))
        p.setBrush(tint(ACCENT, 40))
        p.drawRoundedRect(card, CARD_CORNER_MM * ppm, CARD_CORNER_MM * ppm)
        p.setFont(ui_font(13, bold=True))
        p.setPen(INK)
        p.drawText(card, Qt.AlignmentFlag.AlignCenter, L("85,60 × 53,98 mm", "85.60 × 53.98 mm"))

        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setFont(ui_font(10))
        for mm in range(86):
            x = round(self.PAD + mm * ppm)
            ln = 12 if mm % 10 == 0 else 8 if mm % 5 == 0 else 4
            p.drawLine(x, self.PAD, x, self.PAD + ln)
            if mm % 10 == 0 and mm:
                p.drawText(QPointF(x - 3, self.PAD + 25), str(mm // 10))


class CalibrationDialog(QDialog):
    def __init__(self, screen, parent):
        super().__init__(parent)
        self.setWindowTitle(L("Kalibrácia mierky", "Calibrate the scale"))
        self.scr = screen
        self.base = edid_ppm(screen)
        self.ppm = px_per_mm(screen)

        info = QLabel(L(f"Displej <b>{screen.name()}</b>. Prilož na obrazovku kartu formátu ID-1 — platobnú "
                        "kartu, občiansky, ISIC alebo kartičku poistenca. Zarovnaj ju s ľavým horným rohom "
                        "obdĺžnika a posúvaj, kým nesedí aj pravý okraj. Jemne: šípky ← →.",
                        f"Display <b>{screen.name()}</b>. Hold an ID-1 size card to the screen — a bank card, "
                        "ID card or student card. Line it up with the top-left corner of the outline and move "
                        "the slider until the right edge matches too. Fine steps: arrow keys ← →."))
        info.setWordWrap(True)
        self.view = CardView(self)
        self.slider = QSlider(Qt.Orientation.Horizontal)
        self.slider.setRange(-1500, 1500)   # stotiny percenta → ±15 %
        self.slider.setSingleStep(10)
        self.slider.setPageStep(100)
        self.slider.setValue(round((self.ppm / self.base - 1) * 10000))
        self.slider.valueChanged.connect(self.changed)
        self.status = QLabel()

        reset = QPushButton(L("Podľa displeja (EDID)", "From display (EDID)"))
        reset.clicked.connect(lambda: self.slider.setValue(0))
        cancel = QPushButton(L("Zrušiť", "Cancel"))
        cancel.clicked.connect(self.reject)
        save = QPushButton(L("Uložiť", "Save"))
        save.setDefault(True)
        save.clicked.connect(self.save)
        row = QHBoxLayout()
        row.addWidget(reset)
        row.addStretch(1)
        row.addWidget(cancel)
        row.addWidget(save)

        lay = QVBoxLayout(self)
        lay.addWidget(info)
        lay.addWidget(self.view)
        lay.addWidget(self.slider)
        lay.addWidget(self.status)
        lay.addLayout(row)
        self.changed(self.slider.value())
        self.slider.setFocus()

    def changed(self, v):
        self.ppm = self.base * (1 + v / 10000)
        self.status.setText(f"{fmt(self.ppm, 3)} px/mm · {fmt(self.ppm * MM_PER_IN, 1)} DPI"
                            f"   ({L('displej hlási', 'display reports')} {fmt(self.base, 3)} px/mm)")
        self.view.update()

    def save(self):
        key = screen_key(self.scr)
        if self.slider.value() == 0:
            cfg["calib"].pop(key, None)
        else:
            cfg["calib"][key] = self.ppm
        cfg.save()
        self.accept()


# ---------- štart ----------

def main():
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    QApplication.setApplicationName("Regula")
    app = QApplication(["regula"] + sys.argv[1:])
    QGuiApplication.setDesktopFileName(APP_ID)
    app.setWindowIcon(QIcon(str(ICON_PATH)))
    view = Regula("prot" if "--uhlomer" in sys.argv else None)
    scr = QGuiApplication.screenAt(QCursor.pos()) or QGuiApplication.primaryScreen()
    if cfg["fullscreen"]:
        view.setGeometry(scr.geometry())
        view.showFullScreen()
    else:
        view.restore_window()
        view.show()
    view.dock.window_button.setChecked(not cfg["fullscreen"])
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
