#!/usr/bin/env python3
"""Regula — pravítko a uhlomer na obrazovku.

Pravítko: ťahanie = presun, ťahanie za koniec = dĺžka, klik = značka (potom meria Δ),
pravý klik alebo ≡ = menu. Mierka v mm ide z rozmeru, ktorý hlási displej (EDID),
a dá sa dokalibrovať kartou formátu ID-1 (85,60 mm).
Uhlomer: ťahaj ramená, koliesko = veľkosť, stred = vrchol uhla.
"""

import json
import math
import os
import signal
import sys
from pathlib import Path

from PySide6.QtCore import QPoint, QPointF, QRectF, Qt, QTimer
from PySide6.QtGui import (QAction, QActionGroup, QColor, QCursor, QFont, QFontMetricsF,
                           QGuiApplication, QIcon, QKeySequence, QPainter, QPainterPath, QPen)
from PySide6.QtWidgets import (QApplication, QDialog, QHBoxLayout, QLabel, QMenu, QPushButton,
                               QSlider, QVBoxLayout, QWidget)

APP_ID = "sk.daniel.regula"
ICON_PATH = Path(__file__).resolve().parent / "regula.svg"
CONFIG_PATH = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "regula" / "config.json"

MM_PER_IN = 25.4
CARD_MM = (85.60, 53.98)    # ID-1: platobná karta, občiansky, ISIC, kartička poistenca
CARD_CORNER_MM = 3.18

BG = QColor(251, 251, 246)
EDGE = QColor(40, 40, 48, 160)
INK = QColor(34, 34, 42)
INK_SOFT = QColor(112, 112, 124)
ACCENT = QColor(31, 158, 222)   # Mint-Y Aqua
MARK = QColor(228, 69, 83)

UNITS = (("cm", "Centimetre (mm)"), ("in", "Palce (1/16″)"),
         ("px", "Pixely"), ("dual", "Centimetre + palce"))

WINDOWS = set()     # drží referencie na otvorené okná


class Config:
    def __init__(self):
        self.data = {"unit": "cm", "alpha": 0.85, "calib": {}}
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
            print(f"regula: nastavenia sa neuložili: {e}", file=sys.stderr)


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
    return f"{v:.{dec}f}".replace(".", ",")


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


# ---------- kreslenie ----------

def ui_font(px, bold=False):
    f = QFont()
    f.setPixelSize(px)
    f.setBold(bold)
    return f


def pill(p, cx, cy, text, color, lo, hi):
    """Zaoblený štítok s textom, vodorovne držaný medzi lo a hi."""
    font = ui_font(12, bold=True)
    fm = QFontMetricsF(font)
    w, h = fm.horizontalAdvance(text) + 14, fm.height() + 4
    x = min(max(cx - w / 2, lo), hi - w)
    r = QRectF(x, cy - h / 2, w, h)
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(color)
    p.drawRoundedRect(r, h / 2, h / 2)
    p.setPen(QColor(255, 255, 255))
    p.setFont(font)
    p.drawText(r, Qt.AlignmentFlag.AlignCenter, text)


def centered_text(p, at, text, font, color):
    fm = QFontMetricsF(font)
    p.setFont(font)
    p.setPen(color)
    p.drawText(QPointF(at.x() - fm.horizontalAdvance(text) / 2,
                       at.y() + (fm.ascent() - fm.descent()) / 2), text)


def set_alpha(value):
    cfg.set("alpha", value)
    for w in WINDOWS:
        w.update()


# ---------- spoločný základ okien ----------

class Overlay(QWidget):
    """Bezrámové priesvitné okno navrchu: presun ťahaním, šípky, menu."""

    def __init__(self, title):
        super().__init__(None, Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint
                         | Qt.WindowType.WindowStaysOnTopHint)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        self.setMouseTracking(True)
        self.setWindowTitle(title)
        self.menu = QMenu(self)
        self._drag = None
        WINDOWS.add(self)

    def act(self, text, shortcut, slot, menu=True, checkable=False):
        a = QAction(text, self)
        if shortcut:
            a.setShortcut(QKeySequence(shortcut))
            a.setShortcutVisibleInContextMenu(True)
        a.setCheckable(checkable)
        a.triggered.connect(lambda _=False: slot())
        self.addAction(a)
        if menu is True:
            self.menu.addAction(a)
        elif menu:
            menu.addAction(a)
        return a

    def add_common_menu(self):
        self.menu.addSeparator()
        self.act("Nové pravítko", "N", lambda: spawn_ruler(self.spawn_anchor()))
        self.act("Nový uhlomer", "U", lambda: spawn_protractor(QCursor.pos()))
        alpha_menu = self.menu.addMenu("Krytie pozadia")
        grp = QActionGroup(self)
        self._alpha = []
        for pct in (100, 85, 70, 55, 40):
            a = self.act(f"{pct} %", None, lambda v=pct / 100: set_alpha(v), menu=alpha_menu, checkable=True)
            grp.addAction(a)
            self._alpha.append((pct / 100, a))
        self.menu.aboutToShow.connect(self._sync_alpha)
        self.menu.addSeparator()
        self.act("Zavrieť", "Ctrl+W", self.close)
        self.act("Ukončiť Regulu", "Ctrl+Q", QApplication.quit)

    def _sync_alpha(self):
        for value, a in self._alpha:
            a.setChecked(abs(value - cfg["alpha"]) < 0.01)

    def spawn_anchor(self):
        return self.geometry().center() + QPoint(40, 40)

    def place(self, anchor):
        """Vycentruje okno na bod a udrží ho na obrazovke."""
        a = (QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()).availableGeometry()
        x = anchor.x() - self.width() // 2
        y = anchor.y() - self.height() // 2
        self.move(min(max(x, a.left()), a.right() + 1 - self.width()),
                  min(max(y, a.top()), a.bottom() + 1 - self.height()))

    # presun: po pár px ťahania ho prevezme správca okien
    def press_drag(self, e):
        self._drag = (e.globalPosition(), self.pos(), False)

    def move_drag(self, e):
        """True, ak sa okno práve presúva."""
        if not self._drag:
            return False
        g0, pos0, moving = self._drag
        d = e.globalPosition() - g0
        if not moving:
            if abs(d.x()) + abs(d.y()) < 5:
                return False
            if self.windowHandle().startSystemMove():
                self._drag = None
                return True
            self._drag = (g0, pos0, True)
        self.move(pos0 + d.toPoint())
        return True

    def release_drag(self):
        """True, ak to bol obyčajný klik bez presunu."""
        click = self._drag is not None and not self._drag[2]
        self._drag = None
        return click

    def keyPressEvent(self, e):
        step = 10 if e.modifiers() & Qt.KeyboardModifier.ShiftModifier else 1
        d = {Qt.Key.Key_Left: QPoint(-step, 0), Qt.Key.Key_Right: QPoint(step, 0),
             Qt.Key.Key_Up: QPoint(0, -step), Qt.Key.Key_Down: QPoint(0, step)}.get(e.key())
        if d is None:
            return super().keyPressEvent(e)
        self.move(self.pos() + d)

    def closeEvent(self, e):
        WINDOWS.discard(self)
        super().closeEvent(e)


# ---------- pravítko ----------

class Ruler(Overlay):
    GRIP = 12   # zóna na konci, za ktorú sa ťahá dĺžka

    def __init__(self, unit=None):
        super().__init__("Regula — pravítko")
        self.unit = unit or cfg["unit"]
        self.vertical = False
        self.length = None
        self.hover = None
        self.mark = None
        self.hint = True
        self._resize = None

        grp = QActionGroup(self)
        self.unit_actions = {}
        for key, text in UNITS:
            a = self.act(text, None, lambda k=key: self.set_unit(k), checkable=True)
            grp.addAction(a)
            self.unit_actions[key] = a
        self.unit_actions[self.unit].setChecked(True)
        self.act("Ďalšia jednotka", "Tab", self.next_unit)
        self.menu.addSeparator()
        self.act("Otočiť (vodorovne / zvislo)", "R", self.toggle_orientation)
        length_menu = self.menu.addMenu("Dĺžka")
        for cm in (10, 15, 20, 30):
            self.act(f"{cm} cm", None, lambda cm=cm: self.set_length_mm(cm * 10), menu=length_menu)
        self.act("Na celú obrazovku", None, self.fill_screen, menu=length_menu)
        self.act("Zrušiť značku", "Esc", self.clear_mark)
        self.act("Kalibrácia mierky…", "K", self.calibrate)
        self.add_common_menu()

        t = QTimer(self)
        t.setSingleShot(True)
        t.timeout.connect(self._hide_hint)
        t.start(8000)

    # --- rozmery ---
    def ppm(self):
        return px_per_mm(self.screen() or QGuiApplication.primaryScreen())

    def thickness(self):
        return 104 if self.unit == "dual" else 72

    def dims(self):
        """(dĺžka, hrúbka) v px bez ohľadu na otočenie."""
        return (self.height(), self.width()) if self.vertical else (self.width(), self.height())

    def apply_geometry(self):
        t, l = self.thickness(), round(self.length)
        if self.vertical:
            self.setFixedSize(t, l)
        else:
            self.setFixedSize(l, t)

    def available(self):
        return (self.screen() or QGuiApplication.primaryScreen()).availableGeometry()

    def keep_on_screen(self):
        a = self.available()
        self.move(min(max(self.x(), a.left()), a.right() + 1 - self.width()),
                  min(max(self.y(), a.top()), a.bottom() + 1 - self.height()))

    def place(self, anchor):
        if self.length is None:
            scr = QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()
            self.length = 150 * px_per_mm(scr)
            self.apply_geometry()
        super().place(anchor)

    def spawn_anchor(self):
        c = self.geometry().center()
        return c + (QPoint(self.width() + 12, 0) if self.vertical else QPoint(0, self.height() + 12))

    # --- akcie ---
    def set_unit(self, unit):
        self.unit = unit
        cfg.set("unit", unit)
        self.unit_actions[unit].setChecked(True)
        self.apply_geometry()
        self.update()

    def next_unit(self):
        keys = [k for k, _ in UNITS]
        self.set_unit(keys[(keys.index(self.unit) + 1) % len(keys)])

    def toggle_orientation(self):
        self.vertical = not self.vertical
        self.hover = None
        self.apply_geometry()
        self.keep_on_screen()

    def set_length_mm(self, mm):
        a = self.available()
        self.length = min(mm * self.ppm(), a.height() if self.vertical else a.width())
        self.apply_geometry()
        self.keep_on_screen()

    def fill_screen(self):
        a = self.available()
        if self.vertical:
            self.length = a.height()
            self.apply_geometry()
            self.move(self.x(), a.top())
        else:
            self.length = a.width()
            self.apply_geometry()
            self.move(a.left(), self.y())

    def clear_mark(self):
        self.mark = None
        self.update()

    def calibrate(self):
        CalibrationDialog(self.screen() or QGuiApplication.primaryScreen(), self).exec()

    def _hide_hint(self):
        self.hint = False
        self.update()

    # --- myš ---
    def to_uv(self, pos):
        """Lokálny bod → (pozdĺž pravítka, naprieč)."""
        return (pos.y(), self.width() - pos.x()) if self.vertical else (pos.x(), pos.y())

    def button_pos(self):
        L, T = self.dims()
        return L - self.GRIP - 16, (T / 2 if self.unit == "dual" else T - 17)

    def mousePressEvent(self, e):
        u, v = self.to_uv(e.position())
        if e.button() == Qt.MouseButton.RightButton:
            self.menu.popup(e.globalPosition().toPoint())
        elif e.button() == Qt.MouseButton.LeftButton:
            bu, bv = self.button_pos()
            if math.hypot(u - bu, v - bv) <= 11:
                self.menu.popup(e.globalPosition().toPoint())
            elif u >= self.dims()[0] - self.GRIP:
                self._resize = (e.globalPosition(), self.length)
            else:
                self.press_drag(e)

    def mouseMoveEvent(self, e):
        u, _ = self.to_uv(e.position())
        if self._resize:
            g0, len0 = self._resize
            d = e.globalPosition() - g0
            self.length = max(120, len0 + (d.y() if self.vertical else d.x()))
            self.apply_geometry()
        elif self.move_drag(e):
            return
        else:
            at_end = u >= self.dims()[0] - self.GRIP
            shape = (Qt.CursorShape.SizeVerCursor if self.vertical else Qt.CursorShape.SizeHorCursor) \
                if at_end else Qt.CursorShape.ArrowCursor
            self.setCursor(shape)
        self.hover = min(max(u, 0), self.dims()[0] - 1)
        self.update()

    def mouseReleaseEvent(self, e):
        if self._resize:
            self._resize = None
            return
        if self.release_drag() and e.button() == Qt.MouseButton.LeftButton:
            u = round(self.to_uv(e.position())[0])
            self.mark = None if self.mark is not None and abs(u - self.mark) <= 3 else u
            self.update()

    def leaveEvent(self, _):
        self.hover = None
        self.update()

    def moveEvent(self, _):
        self.update()   # iná obrazovka = iná mierka

    # --- kreslenie ---
    def paintEvent(self, _):
        L, T = self.dims()
        ppm = self.ppm()
        p = QPainter(self)
        if self.vertical:   # kreslí sa vodorovne a otočí sa o 90° (nula hore)
            p.translate(T, 0)
            p.rotate(90)

        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = 6
        body = QPainterPath()
        body.moveTo(0.5, 0.5)
        body.lineTo(L - r, 0.5)
        body.quadTo(L - 0.5, 0.5, L - 0.5, r)
        body.lineTo(L - 0.5, T - r)
        body.quadTo(L - 0.5, T - 0.5, L - r, T - 0.5)
        body.lineTo(0.5, T - 0.5)
        body.closeSubpath()
        bg = QColor(BG)
        bg.setAlphaF(cfg["alpha"])
        p.setPen(QPen(EDGE, 1))
        p.setBrush(bg)
        p.drawPath(body)

        p.setPen(QPen(INK_SOFT, 1))     # úchyt na konci
        for du in (4, 7):
            p.drawLine(QPointF(L - du, T * 0.38), QPointF(L - du, T * 0.62))

        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setFont(ui_font(11))
        if self.unit == "dual":
            self.draw_scale(p, "cm", ppm, L, T, top=True)
            self.draw_scale(p, "in", ppm, L, T, top=False)
        else:
            self.draw_scale(p, self.unit, ppm, L, T, top=True)

        if self.mark is not None:
            p.setPen(QPen(MARK, 1))
            p.drawLine(self.mark, 0, self.mark, T)
        if self.hover is not None:
            p.setPen(QPen(ACCENT, 1))
            h = round(self.hover)
            p.drawLine(h, 0, h, T)

        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        bu, bv = self.button_pos()
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(INK_SOFT.red(), INK_SOFT.green(), INK_SOFT.blue(), 50))
        p.drawEllipse(QPointF(bu, bv), 9, 9)
        p.setPen(QPen(INK, 1.4))
        for dy in (-3, 0, 3):
            p.drawLine(QPointF(bu - 4, bv + dy), QPointF(bu + 4, bv + dy))

        mid = T / 2 if self.unit == "dual" else T - 17
        if self.hover is not None:
            if self.mark is not None:
                pill(p, self.hover, mid, "Δ " + readout(self.unit, ppm, abs(self.hover - self.mark)),
                     MARK, 2, bu - 13)
            else:
                pill(p, self.hover, mid, readout(self.unit, ppm, self.hover), ACCENT, 2, bu - 13)
        elif self.hint and L > 480:
            p.setPen(INK_SOFT)
            p.setFont(ui_font(10))
            p.drawText(QPointF(40, mid + 4),
                       "ťahaj = presun · koniec = dĺžka · klik = značka · pravý klik = menu")

    def draw_scale(self, p, unit, ppm, L, T, top):
        fm = QFontMetricsF(p.font())
        p.setPen(QPen(INK, 1))
        for u, ln, label in scale_ticks(unit, ppm, L - 1):
            x = round(u)
            if top:
                p.drawLine(x, 0, x, ln)
            else:
                p.drawLine(x, T - 1, x, T - 1 - ln)
            if label and u > 0:
                w = fm.horizontalAdvance(label)
                if x + w / 2 > L - self.GRIP - 2:
                    continue
                y = ln + 2 + fm.ascent() if top else T - ln - 4 - fm.descent()
                p.drawText(QPointF(x - w / 2, y), label)
        p.setPen(INK_SOFT)
        name = {"cm": "cm", "in": "palce", "px": "px"}[unit]
        p.drawText(QPointF(4, 21 + fm.ascent() if top else T - 25 - fm.descent()), name)


# ---------- uhlomer ----------

class Protractor(Overlay):
    M = 24      # okraj okolo stupnice (úchyty ramien)
    BASE = 34   # pás pod vrcholom pri polkruhu

    def __init__(self, full=False):
        super().__init__("Regula — uhlomer")
        self.full = full
        self.R = 200.0
        self.arms = [0.0, 60.0]
        self.active = None

        grp = QActionGroup(self)
        self.mode_actions = (
            self.act("Polkruh 180°", None, lambda: self.set_full(False), checkable=True),
            self.act("Kruh 360°", None, lambda: self.set_full(True), checkable=True))
        for a in self.mode_actions:
            grp.addAction(a)
        self.mode_actions[full].setChecked(True)
        self.act("Prepnúť 180° / 360°", "Tab", lambda: self.set_full(not self.full), menu=False)
        self.menu.addSeparator()
        self.act("Väčší", "+", lambda: self.resize_dial(20))
        self.act("Menší", "-", lambda: self.resize_dial(-20))
        self.act("Vynulovať ramená", "Esc", self.reset_arms)
        self.add_common_menu()
        self.apply_geometry()

    def center(self):
        return QPointF(self.R + self.M, self.R + self.M)

    def apply_geometry(self):
        w = round(2 * (self.R + self.M))
        self.setFixedSize(w, w if self.full else round(self.R + self.M + self.BASE + 2))

    def keep_center(self, change):
        gc = self.mapToGlobal(self.center().toPoint())
        change()
        self.apply_geometry()
        self.move(gc - self.center().toPoint())
        self.update()

    def set_full(self, full):
        def change():
            self.full = full
            if not full:
                self.arms = [min(max(a, 0.0), 180.0) if a <= 270 else 0.0 for a in self.arms]
        self.mode_actions[full].setChecked(True)
        self.keep_center(change)

    def resize_dial(self, dr):
        def change():
            self.R = min(480.0, max(120.0, self.R + dr))
        self.keep_center(change)

    def reset_arms(self):
        self.arms = [0.0, 60.0]
        self.update()

    # --- myš ---
    def angle_at(self, pos, snap):
        c = self.center()
        a = math.degrees(math.atan2(c.y() - pos.y(), pos.x() - c.x()))
        if snap:
            a = float(round(a))
        if self.full:
            return a % 360
        if a < 0:
            a = 0.0 if a > -90 else 180.0
        return a

    def arm_at(self, pos):
        c = self.center()
        dx, dy = pos.x() - c.x(), c.y() - pos.y()
        best, best_d = None, 9.0
        for i in (1, 0):
            a = math.radians(self.arms[i])
            along = dx * math.cos(a) + dy * math.sin(a)
            dist = abs(dy * math.cos(a) - dx * math.sin(a))
            if 15 < along < self.R + self.M and dist < best_d:
                best, best_d = i, dist
        return best

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.RightButton:
            self.menu.popup(e.globalPosition().toPoint())
        elif e.button() == Qt.MouseButton.LeftButton:
            self.active = self.arm_at(e.position())
            if self.active is None:
                self.press_drag(e)
            else:
                self.setCursor(Qt.CursorShape.ClosedHandCursor)

    def mouseMoveEvent(self, e):
        if self.active is not None:
            snap = bool(e.modifiers() & Qt.KeyboardModifier.ShiftModifier)
            self.arms[self.active] = self.angle_at(e.position(), snap)
            self.update()
        elif not self.move_drag(e):
            over = self.arm_at(e.position()) is not None
            self.setCursor(Qt.CursorShape.OpenHandCursor if over else Qt.CursorShape.ArrowCursor)

    def mouseReleaseEvent(self, e):
        self.active = None
        self.release_drag()
        self.setCursor(Qt.CursorShape.ArrowCursor)

    def wheelEvent(self, e):
        steps = e.angleDelta().y() / 120
        if steps:
            self.resize_dial(12 * steps)

    # --- kreslenie ---
    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        c, R = self.center(), self.R
        cx, cy = c.x(), c.y()

        def pt(deg, r):
            a = math.radians(deg)
            return QPointF(cx + r * math.cos(a), cy - r * math.sin(a))

        body = QPainterPath()
        if self.full:
            body.addEllipse(c, R, R)
        else:
            body.moveTo(cx + R, cy + self.BASE)
            body.lineTo(cx + R, cy)
            body.arcTo(QRectF(cx - R, cy - R, 2 * R, 2 * R), 0, 180)
            body.lineTo(cx - R, cy + self.BASE)
            body.closeSubpath()
        bg = QColor(BG)
        bg.setAlphaF(cfg["alpha"])
        p.setPen(QPen(EDGE, 1))
        p.setBrush(bg)
        p.drawPath(body)

        # stupnica
        last = 359 if self.full else 180
        fine = R * math.radians(1) >= 3.2
        p.setPen(QPen(INK, 1))
        for d in range(last + 1):
            if d % 10 == 0:
                ln = 16
            elif d % 5 == 0:
                ln = 10
            elif fine:
                ln = 6
            else:
                continue
            p.drawLine(pt(d, R), pt(d, R - ln))

        step = 10 if (R - 46) * math.radians(10) >= 24 else 30
        f_out, f_in = ui_font(11), ui_font(9)
        for d in range(0, last + 1, step):
            out = pt(d, R - 28)
            if self.full:
                centered_text(p, out, str(d), f_out, INK)
            else:
                out.setY(min(out.y(), cy - 8))
                centered_text(p, out, str(d), f_out, INK)
                inner = pt(d, R - 46)
                inner.setY(min(inner.y(), cy - 8))
                centered_text(p, inner, str(180 - d), f_in, INK_SOFT)

        # vodiace čiary
        if self.full:
            p.setPen(QPen(INK_SOFT, 1, Qt.PenStyle.DashLine))
            p.drawLine(pt(0, R - 18), pt(180, R - 18))
            p.drawLine(pt(90, R - 18), pt(270, R - 18))
        else:
            p.setPen(QPen(INK, 1))
            p.drawLine(pt(0, R), pt(180, R))

        # výsek medzi ramenami
        a0, a1 = self.arms
        if self.full:
            sweep = (a1 - a0) % 360
            angle = min(sweep, 360 - sweep)
            start = a0 if sweep <= 180 else a1
        else:
            angle = abs(a1 - a0)
            start = min(a0, a1)
        rw = min(56.0, R * 0.3)
        wedge = QPainterPath(c)
        wedge.arcTo(QRectF(cx - rw, cy - rw, 2 * rw, 2 * rw), start, angle)
        wedge.closeSubpath()
        fill = QColor(ACCENT)
        fill.setAlpha(50)
        p.setPen(QPen(ACCENT, 1.5))
        p.setBrush(fill)
        p.drawPath(wedge)

        # ramená
        for i, color in ((0, ACCENT), (1, MARK)):
            end = pt(self.arms[i], R + 8)
            p.setPen(QPen(color, 2))
            p.drawLine(c, end)
            p.setPen(QPen(QColor(255, 255, 255), 1.5))
            p.setBrush(color)
            p.drawEllipse(end, 7, 7)

        # vrchol
        p.setPen(QPen(INK, 1))
        p.drawLine(QPointF(cx - 10, cy), QPointF(cx + 10, cy))
        p.drawLine(QPointF(cx, cy - 10), QPointF(cx, cy + (10 if self.full else 3)))
        p.setBrush(QColor(255, 255, 255))
        p.drawEllipse(c, 3, 3)

        if self.full:
            pill(p, cx, cy + R * 0.55, f"∠ {fmt(angle, 1)}°   ({fmt(360 - angle, 1)}°)", INK, 4, self.width() - 4)
        else:
            pill(p, cx, cy + self.BASE / 2 + 3, f"∠ {fmt(angle, 1)}°", INK, 4, self.width() - 4)


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
        fill = QColor(ACCENT)
        fill.setAlpha(40)
        p.setPen(QPen(ACCENT, 1))
        p.setBrush(fill)
        p.drawRoundedRect(card, CARD_CORNER_MM * ppm, CARD_CORNER_MM * ppm)
        p.setFont(ui_font(13, bold=True))
        p.setPen(INK)
        p.drawText(card, Qt.AlignmentFlag.AlignCenter, "85,60 × 53,98 mm")

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
        self.setWindowTitle("Kalibrácia mierky")
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint)
        self.scr = screen
        self.base = edid_ppm(screen)
        self.ppm = px_per_mm(screen)

        info = QLabel(f"Displej <b>{screen.name()}</b>. Prilož na obrazovku kartu formátu ID-1 — platobnú "
                      "kartu, občiansky, ISIC alebo kartičku poistenca. Zarovnaj ju s ľavým horným rohom "
                      "obdĺžnika a posúvaj, kým nesedí aj pravý okraj. Jemne: šípky ← →.")
        info.setWordWrap(True)
        self.view = CardView(self)
        self.slider = QSlider(Qt.Orientation.Horizontal)
        self.slider.setRange(-1500, 1500)   # stotiny percenta → ±15 %
        self.slider.setSingleStep(10)
        self.slider.setPageStep(100)
        self.slider.setValue(round((self.ppm / self.base - 1) * 10000))
        self.slider.valueChanged.connect(self.changed)
        self.status = QLabel()

        reset = QPushButton("Podľa displeja (EDID)")
        reset.clicked.connect(lambda: self.slider.setValue(0))
        cancel = QPushButton("Zrušiť")
        cancel.clicked.connect(self.reject)
        save = QPushButton("Uložiť")
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
                            f"   (displej hlási {fmt(self.base, 3)} px/mm)")
        self.view.update()

    def save(self):
        key = screen_key(self.scr)
        if self.slider.value() == 0:
            cfg["calib"].pop(key, None)
        else:
            cfg["calib"][key] = self.ppm
        cfg.save()
        for w in WINDOWS:
            w.update()
        self.accept()


# ---------- štart ----------

def spawn_ruler(anchor, unit=None):
    w = Ruler(unit)
    w.place(anchor)
    w.show()
    w.raise_()
    w.activateWindow()
    return w


def spawn_protractor(anchor, full=False):
    w = Protractor(full)
    w.place(anchor)
    w.show()
    w.raise_()
    w.activateWindow()
    return w


def main():
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    QApplication.setApplicationName("Regula")
    app = QApplication(["regula"] + sys.argv[1:])
    QGuiApplication.setDesktopFileName(APP_ID)
    app.setWindowIcon(QIcon(str(ICON_PATH)))
    scr = QGuiApplication.screenAt(QCursor.pos()) or QGuiApplication.primaryScreen()
    anchor = scr.availableGeometry().center()
    if "--uhlomer" in sys.argv:
        spawn_protractor(anchor)
    else:
        spawn_ruler(anchor)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
