// Tools.swift — nástroje: pravítka (aj mm papier), uhlomer, priemer, polomer, uhlopriečka, trasa.
// Každý nástroj má statickú vrstvu (kreslí sa raz, cachuje sa) a dynamickú (kurzor, meranie).
import AppKit

enum SnapKind { case point, angle }
enum ToolKey { case enter, backspace }

struct Field {
    let name: String
    let unit: String
    let optional: Bool
}

@MainActor
class Tool {
    unowned let canvas: Canvas

    init(_ canvas: Canvas) { self.canvas = canvas }

    var hint: String { "" }
    var snapKind: SnapKind? { nil }     // .point = body na mriežku kroku, .angle = uhly po kroku
    var unit: String { "cm" }
    var inputFields: [Field] { [] }     // políčka na zadanie z klávesnice; prázdne = nástroj to nepodporuje

    func layout(_ W: CGFloat, _ H: CGFloat) {}
    func relayout() {}
    func staticKey() -> String { "" }
    func paintStatic(_ g: G) {}
    func paintDynamic(_ g: G) {}
    func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {}
    func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {}
    func release(_ p: CGPoint) {}
    func double(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) { press(p, mods) }   // rýchle kliky nech sa nestratia
    func key(_ k: ToolKey) -> Bool { false }                                       // true = kláves spotrebovaný
    func wheel(_ steps: CGFloat) {}
    func nudge(_ d: CGPoint) {}
    func reset() {}
    func applyInput(_ nums: [Double]) -> String? { nil }                          // chybová hláška alebo nil
    /// Okno prešlo na obrazovku s k-krát väčšou hustotou bodov na mm.
    func rescale(_ k: CGFloat) {}
    func cursor(_ p: CGPoint) -> NSCursor { .crosshair }
}

/// Body prepočítané na inú veľkosť bodu (rovnaké mm) a posunuté, aby sa zmestili do okna.
@MainActor
func rescaled(_ canvas: Canvas, _ pts: [CGPoint], _ k: CGFloat) -> [CGPoint] {
    let scaled = pts.map { $0 * k }
    let d = canvas.fitShift(scaled)
    return scaled.map { $0 + d }
}

/// Čiarky stupnice: (poloha v bodoch, dĺžka čiarky, číslo alebo nil).
func scaleTicks(_ unit: String, _ ppm: CGFloat, _ px: CGFloat, _ length: CGFloat) -> [(CGFloat, CGFloat, String?)] {
    let step: CGFloat, divs: [Int], lens: [CGFloat], every: Int, label: (Int) -> String
    switch unit {
    case "px":
        let base = px >= 2 ? 10 : 5                          // na Retine by 5 px bolo len 2,5 bodu
        step = CGFloat(base) / px
        divs = base == 10 ? [10, 5, 1] : [20, 10, 2, 1]
        lens = base == 10 ? [19, 12, 6] : [19, 13, 8, 4]
        every = base == 10 ? 10 : 20
        label = { "\($0 * base)" }
    case "in":
        step = ppm * MM_PER_IN / 16
        divs = [16, 8, 4, 2, 1]; lens = [20, 15, 11, 8, 5]; every = 16
        label = { "\($0 / 16)" }
    default:
        step = ppm
        divs = [10, 5, 1]; lens = [19, 12, 7]; every = 10
        label = { "\($0 / 10)" }
    }
    var out: [(CGFloat, CGFloat, String?)] = []
    var i = 0
    while CGFloat(i) * step <= length {
        let level = divs.firstIndex { i % $0 == 0 } ?? divs.count - 1
        out.append((CGFloat(i) * step, lens[level], i % every == 0 ? label(i) : nil))
        i += 1
    }
    return out
}

extension Theme {
    var dark: Bool { (bg.usingColorSpace(.sRGB)?.brightnessComponent ?? 1) < 0.5 }
}

// MARK: - pravítko

/// Stupnice hore a vľavo, nitkový kríž a meranie ťahaním (aj ako milimetrový papier).
final class RulerTool: Tool {
    let kind: String
    let grid: Bool
    var a: CGPoint?
    var b: CGPoint?
    var dragging = false

    init(_ canvas: Canvas, unit: String, grid: Bool = false) {
        kind = unit
        self.grid = grid
        super.init(canvas)
    }

    override var unit: String { kind }
    override var snapKind: SnapKind? { .point }
    override var hint: String {
        L("ťahaj = zmeraj · píš čísla = presný rozmer · šípky = koniec o 0,1 mm · Shift = rovno · S = krok · Esc = zmazať",
          "drag = measure · type numbers = exact size · arrows = move end 0.1 mm · Shift = straight · S = snap · Esc = clear")
    }
    var band: CGFloat { kind == "dual" ? 84 : 50 }

    override var inputFields: [Field] {
        let u = kind == "in" ? "″" : kind == "px" ? "px" : "mm"
        return [Field(name: L("Šírka", "Width"), unit: u, optional: false),
                Field(name: L("Výška", "Height"), unit: u, optional: true)]
    }

    override func applyInput(_ nums: [Double]) -> String? {
        guard (1...2).contains(nums.count), nums.min()! >= 0, nums.max()! > 0 else {
            return L("Aspoň jedno číslo musí byť väčšie ako 0", "At least one number must be greater than 0")
        }
        let ppm = canvas.ppm
        let k: CGFloat = kind == "px" ? 1 / canvas.pxScale : kind == "in" ? ppm * MM_PER_IN : ppm
        let a0 = a ?? canvas.inputOrigin()
        let b0 = CGPoint(x: a0.x + CGFloat(nums[0]) * k, y: a0.y + (nums.count == 2 ? CGFloat(nums[1]) * k : 0))
        let d = canvas.fitShift([a0, b0])
        a = a0 + d
        b = b0 + d
        dragging = false
        return nil
    }

    override func paintStatic(_ g: G) {
        let T = band, ctx = g.ctx
        if grid { paintGrid(g) }
        fillRect(ctx, CGRect(x: 0, y: 0, width: g.W, height: T), g.th.panel)
        fillRect(ctx, CGRect(x: 0, y: 0, width: T, height: g.H), g.th.panel)
        crispLine(ctx, x0: T, y0: T, x1: g.W, y1: T, tint(g.th.ink, 70))
        crispLine(ctx, x0: T, y0: T, x1: T, y1: g.H, tint(g.th.ink, 70))
        let font = uiFont(11)
        let scales: [(String, Bool)] = kind == "dual" ? [("cm", true), ("in", false)] : [(kind, true)]
        for (u, outer) in scales {
            paintScale(g, u, g.W, T, font, outer, horizontal: true)
            paintScale(g, u, g.H, T, font, outer, horizontal: false)
        }
        fillRect(ctx, CGRect(x: 0, y: 0, width: T, height: T), g.th.panel)
        let name = ["cm": grid ? "mm" : "cm", "in": L("palce", "in"), "px": "px", "dual": "cm\nin"][kind] ?? ""
        textInRect(name, CGRect(x: 0, y: 0, width: T, height: T), font: font, color: g.th.soft)
    }

    private func paintScale(_ g: G, _ u: String, _ length: CGFloat, _ T: CGFloat, _ font: NSFont,
                            _ outer: Bool, horizontal: Bool) {
        let asc = font.ascender, desc = -font.descender
        for (pos, ln, label) in scaleTicks(u, g.ppm, g.px, length - 1) {
            let x = pos.rounded()
            let v0: CGFloat = outer ? 0 : T - 1, v1: CGFloat = outer ? ln : T - 1 - ln
            if horizontal {
                crispLine(g.ctx, x0: x, y0: v0, x1: x, y1: v1, g.th.ink)
            } else {
                crispLine(g.ctx, x0: v0, y0: x, x1: v1, y1: x, g.th.ink)
            }
            guard let label, pos > 0 else { continue }
            let w = textSize(label, font).width
            if horizontal {
                drawText(label, x: x - w / 2, baseline: outer ? ln + 2 + asc : T - ln - 4 - desc, font: font, color: g.th.ink)
            } else {
                drawText(label, x: outer ? ln + 3 : T - ln - 4 - w, baseline: x + (asc - desc) / 2, font: font, color: g.th.ink)
            }
        }
    }

    private func paintGrid(_ g: G) {
        let colors = [tint(g.th.ink, 22), tint(g.th.ink, 48), tint(g.th.ink, 95)]
        for vertical in [true, false] {
            let length = vertical ? g.W : g.H, span = vertical ? g.H : g.W
            var i = 0
            while CGFloat(i) * g.ppm <= length {
                let color = colors[i % 10 == 0 ? 2 : i % 5 == 0 ? 1 : 0]
                let x = (CGFloat(i) * g.ppm).rounded()
                if vertical {
                    crispLine(g.ctx, x0: x, y0: 0, x1: x, y1: span, color)
                } else {
                    crispLine(g.ctx, x0: 0, y0: x, x1: span, y1: x, color)
                }
                i += 1
            }
        }
    }

    override func paintDynamic(_ g: G) {
        let T = band, ctx = g.ctx
        if let a, let b {               // konce merania premietnuté na stupnice
            for p in [a, b] {
                let x = p.x.rounded(), y = p.y.rounded()
                crispLine(ctx, x0: x, y0: 0, x1: x, y1: T, MARK)
                crispLine(ctx, x0: 0, y0: y, x1: T, y1: y, MARK)
            }
        }
        let h = canvas.hover
        if let h {
            let x = h.x.rounded(), y = h.y.rounded()
            crispLine(ctx, x0: x, y0: 0, x1: x, y1: g.H, tint(ACCENT, 150))
            crispLine(ctx, x0: 0, y0: y, x1: g.W, y1: y, tint(ACCENT, 150))
        }
        paintMeasure(g)
        if let h {
            pill(ctx, h.x, T + 14, readout(kind, g.ppm, h.x, px: g.px), ACCENT, lo: T + 2, hi: g.W - 2)
            let text = readout(kind, g.ppm, h.y, px: g.px)
            pill(ctx, T + 6 + pillSize(text).width / 2, h.y, text, ACCENT)
        }
    }

    private func paintMeasure(_ g: G) {
        guard let a, let b else { return }
        let ctx = g.ctx
        let dx = abs(b.x - a.x), dy = abs(b.y - a.y)
        let box = dx > 2 && dy > 2
        let rect = CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: dx, height: dy)
        if box { fillPath(ctx, CGPath(rect: rect, transform: nil), tint(ACCENT, 28), stroke: ACCENT, dash: true) }
        strokeLine(ctx, a, b, MARK, width: 2)
        dot(ctx, a, MARK)
        dot(ctx, b, MARK)
        if box {
            pill(ctx, rect.midX, rect.minY - 14, readout(kind, g.ppm, dx, px: g.px), ACCENT, lo: 2, hi: g.W - 2)
            let text = readout(kind, g.ppm, dy, px: g.px)
            pill(ctx, rect.minX - 8 - pillSize(text).width / 2, rect.midY, text, ACCENT)
        }
        let mid = (a + b) / 2
        pill(ctx, mid.x, box ? mid.y : mid.y - 18, readout(kind, g.ppm, hypot(dx, dy), px: g.px), MARK,
             lo: 2, hi: g.W - 2, size: 14)
    }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        a = p
        b = p
        dragging = true
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        guard dragging, let a else { return }
        var nb = p
        if mods.contains(.shift) {
            if abs(nb.x - a.x) >= abs(nb.y - a.y) { nb.y = a.y } else { nb.x = a.x }
        }
        b = nb
    }

    override func release(_ p: CGPoint) {
        dragging = false
        if let a, let b, abs(b.x - a.x) + abs(b.y - a.y) < 3 {
            self.a = nil        // obyčajný klik meranie zmaže
            self.b = nil
        }
    }

    override func nudge(_ d: CGPoint) {
        if let b { self.b = b + d }
    }

    override func rescale(_ k: CGFloat) {
        guard let a, let b else { return }
        let r = rescaled(canvas, [a, b], k)
        self.a = r[0]
        self.b = r[1]
    }

    override func reset() {
        a = nil
        b = nil
    }
}

// MARK: - uhlomer

final class ProtractorTool: Tool {
    let full: Bool
    var c: CGPoint?
    var R: CGFloat = 300
    var arms: [CGFloat] = [0, 60]
    var active: Int?
    var grab: CGPoint?
    let BASE: CGFloat = 40          // pás pod vrcholom pri polkruhu

    init(_ canvas: Canvas, full: Bool) {
        self.full = full
        super.init(canvas)
    }

    override var snapKind: SnapKind? { .angle }
    override var hint: String {
        L("ťahaj ramená na strany uhla · napíš uhol = presne · ťahaj uhlomer = presun · koliesko = veľkosť · Shift = celé stupne · Esc = reset",
          "drag the arms onto the sides of the angle · type an angle = exact · drag the dial = move · wheel = size · Shift = whole degrees · Esc = reset")
    }
    override var inputFields: [Field] { [Field(name: L("Uhol", "Angle"), unit: "°", optional: false)] }

    override func layout(_ W: CGFloat, _ H: CGFloat) {
        guard c == nil else { return }
        let free = H - DOCK_RESERVE
        if full {
            R = min(W, free) / 2 - 40
            c = CGPoint(x: (W / 2).rounded(), y: (free / 2).rounded())
        } else {
            R = min(W * 0.3, (free - BASE) * 0.75)
            c = CGPoint(x: (W / 2).rounded(), y: ((free - R - BASE) / 2 + R).rounded())
        }
    }

    override func relayout() { c = nil }
    override func staticKey() -> String { c.map { "\($0.x),\($0.y),\(R)" } ?? "" }

    override func paintStatic(_ g: G) {
        guard let c else { return }
        let ctx = g.ctx
        let body: CGPath
        if full {
            body = circlePath(c, R)
        } else {
            var pts = [CGPoint(x: c.x + R, y: c.y + BASE)]
            for d in 0...180 { pts.append(polar(c, CGFloat(d), R)) }
            pts.append(CGPoint(x: c.x - R, y: c.y + BASE))
            body = polygonPath(pts)
        }
        fillPath(ctx, body, g.th.glass ? tint(g.th.panel, 190) : g.th.panel, stroke: tint(g.th.ink, 90))

        let last = full ? 360 : 180
        let whole = R * .pi / 180 >= 3.2, halves = R * .pi / 360 >= 4
        var segs: [CGPoint] = []
        for k in 0..<(last * 2 + (full ? 0 : 1)) {          // po pol stupni
            let d = CGFloat(k) / 2
            let ln: CGFloat = k % 20 == 0 ? 22 : k % 10 == 0 ? 15 : k % 2 == 0 ? (whole ? 9 : 0) : (halves ? 5 : 0)
            if ln > 0 { segs += [polar(c, d, R), polar(c, d, R - ln)] }
        }
        ctx.saveGState()
        ctx.setShouldAntialias(true)
        ctx.setStrokeColor(g.th.ink.cgColor)
        ctx.setLineWidth(1)
        ctx.strokeLineSegments(between: segs)
        ctx.restoreGState()

        let size: CGFloat = R >= 300 ? 13 : 11
        let rOut = R - 24 - size, rIn = rOut - size - 10
        let step = rIn * .pi / 180 * 10 >= 28 ? 10 : 30
        let fOut = uiFont(size), fIn = uiFont(size - 2)
        for d in stride(from: 0, through: full ? last - 1 : last, by: step) {
            var out = polar(c, CGFloat(d), rOut)
            if full {
                centeredText("\(d)", at: out, font: fOut, color: g.th.ink)
                continue
            }
            out.y = min(out.y, c.y - 10)
            centeredText("\(d)", at: out, font: fOut, color: g.th.ink)
            var inner = polar(c, CGFloat(d), rIn)
            inner.y = min(inner.y, c.y - 10)
            centeredText("\(180 - d)", at: inner, font: fIn, color: g.th.soft)
        }

        if full {
            strokeLine(ctx, polar(c, 0, R - 24), polar(c, 180, R - 24), tint(g.th.soft, 140), dash: true)
            strokeLine(ctx, polar(c, 90, R - 24), polar(c, 270, R - 24), tint(g.th.soft, 140), dash: true)
        } else {
            strokeLine(ctx, polar(c, 0, R), polar(c, 180, R), g.th.ink)
        }
    }

    /// (uhol medzi ramenami, začiatok výseku)
    func angle() -> (CGFloat, CGFloat) {
        let a0 = arms[0], a1 = arms[1]
        if full {
            let sweep = ((a1 - a0).truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
            return (min(sweep, 360 - sweep), sweep <= 180 ? a0 : a1)
        }
        return (abs(a1 - a0), min(a0, a1))
    }

    override func paintDynamic(_ g: G) {
        guard let c else { return }
        let ctx = g.ctx
        let (angle, start) = self.angle()
        let rw = min(90, R * 0.25)
        let n = max(2, Int(angle.rounded(.up)) + 1)
        var wedge = [c]
        for i in 0..<n { wedge.append(polar(c, start + angle * CGFloat(i) / CGFloat(n - 1), rw)) }
        fillPath(ctx, polygonPath(wedge), tint(ACCENT, 50), stroke: ACCENT, width: 1.5)

        let far = hypot(g.W, g.H)
        for (i, color) in [(0, ACCENT), (1, MARK)] {
            let knob = polar(c, arms[i], R + 10)
            strokeLine(ctx, knob, polar(c, arms[i], far), tint(color, 170), dash: true)   // predĺženie cez celú obrazovku
            strokeLine(ctx, c, knob, color, width: 2)
            fillPath(ctx, circlePath(knob, 9), color, stroke: WHITE, width: 2)
        }
        strokeLine(ctx, CGPoint(x: c.x - 12, y: c.y), CGPoint(x: c.x + 12, y: c.y), g.th.ink)
        strokeLine(ctx, CGPoint(x: c.x, y: c.y - 12), CGPoint(x: c.x, y: c.y + (full ? 12 : 4)), g.th.ink)
        fillPath(ctx, circlePath(c, 3.5), WHITE, stroke: g.th.ink)

        let tag = g.th.dark ? ACCENT : INK      // na tmavom pozadí by tmavý štítok zanikol
        if full {
            pill(ctx, c.x, c.y + R * 0.45, "∠ \(fmt(angle, 1))°   (\(fmt(360 - angle, 1))°)", tag, size: 16)
        } else {
            pill(ctx, c.x, c.y + BASE / 2 + 2, "∠ \(fmt(angle, 1))°", tag, size: 16)
        }
    }

    private func armAt(_ p: CGPoint) -> Int? {
        guard let c else { return nil }
        let dx = p.x - c.x, dy = c.y - p.y
        var best: Int?, bestD: CGFloat = 10
        for i in [1, 0] {
            let a = arms[i] * .pi / 180
            let along = dx * cos(a) + dy * sin(a)
            let dist = abs(dy * cos(a) - dx * sin(a))
            if along > 20 && dist < bestD {
                best = i
                bestD = dist
            }
        }
        return best
    }

    private func angleAt(_ p: CGPoint, _ step: Double?) -> CGFloat {
        guard let c else { return 0 }
        var a = atan2(c.y - p.y, p.x - c.x) * 180 / .pi
        if let step { a = (a / CGFloat(step)).rounded() * CGFloat(step) }
        if full { return (a.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360) }
        if a < 0 { a = a > -90 ? 0 : 180 }
        return a
    }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        active = armAt(p)
        if active == nil, let c {
            grab = p - c
        } else {
            NSCursor.closedHand.set()
        }
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let active {
            arms[active] = angleAt(p, mods.contains(.shift) ? 1 : canvas.snapValue())
        } else if let grab {
            c = p - grab
        }
    }

    override func release(_ p: CGPoint) {
        active = nil
        grab = nil
    }

    override func wheel(_ steps: CGFloat) { R = min(1200, max(120, R + 20 * steps)) }

    override func nudge(_ d: CGPoint) {
        if let c { self.c = c + d }
    }

    override func reset() {
        arms = [0, 60]
        c = nil
    }

    override func rescale(_ k: CGFloat) { c = nil }    // na novej obrazovke znova do stredu; uhly ostanú

    override func applyInput(_ nums: [Double]) -> String? {
        guard nums.count == 1 else { return L("Zadaj jedno číslo — uhol v stupňoch", "Enter one number — the angle in degrees") }
        let a = CGFloat(nums[0]), a0 = arms[0]
        if full {
            arms[1] = ((a0 + a).truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
            return nil
        }
        guard (0...180).contains(a) else {
            return L("Pri polkruhu môže byť uhol 0 až 180°", "The half-circle protractor goes from 0 to 180°")
        }
        var target = a0 + a <= 180 ? a0 + a : a0 - a
        if target < 0 {                 // nezmestí sa na žiadnu stranu → základné rameno na 0°
            arms[0] = 0
            target = a
        }
        arms[1] = target
        return nil
    }

    override func cursor(_ p: CGPoint) -> NSCursor { armAt(p) != nil ? .openHand : .arrow }
}

// MARK: - kruhová mierka (priemer)

final class CircleTool: Tool {
    var c: CGPoint?
    var grab: CGPoint?
    var fixed: Double?          // zadaný priemer v mm

    override var hint: String {
        L("prilož guľatú vec (mincu, skrutku, vrchnák) na stred · napíš priemer = vyznačí kruh · ťahaj = presun stredu · šípky = 0,1 mm · Esc = na stred",
          "put something round (a coin, screw, bottle cap) on the centre · type a diameter = mark a circle · drag = move centre · arrows = 0.1 mm · Esc = re-centre")
    }
    override var inputFields: [Field] { [Field(name: L("Priemer", "Diameter"), unit: "mm", optional: false)] }

    override func applyInput(_ nums: [Double]) -> String? {
        guard nums.count == 1, nums[0] > 0 else {
            return L("Zadaj jedno kladné číslo — priemer", "Enter one positive number — the diameter")
        }
        fixed = nums[0]
        return nil
    }

    override func layout(_ W: CGFloat, _ H: CGFloat) {
        if c == nil { c = CGPoint(x: (W / 2).rounded(), y: ((H - DOCK_RESERVE) / 2).rounded()) }
    }

    override func relayout() { c = nil }
    override func staticKey() -> String { c.map { "\($0.x),\($0.y)" } ?? "" }

    override func paintStatic(_ g: G) {
        guard let c else { return }
        let ctx = g.ctx
        let far = [CGPoint(x: 0, y: 0), CGPoint(x: g.W, y: 0), CGPoint(x: 0, y: g.H), CGPoint(x: g.W, y: g.H)]
            .map { c.dist($0) }.max() ?? 0
        if g.th.glass { fillPath(ctx, circlePath(c, 52 * g.ppm), tint(g.th.panel, 170)) }

        strokeLine(ctx, CGPoint(x: 0, y: c.y), CGPoint(x: g.W, y: c.y), tint(g.th.soft, 140), dash: true)
        strokeLine(ctx, CGPoint(x: c.x, y: 0), CGPoint(x: c.x, y: g.H), tint(g.th.soft, 140), dash: true)

        var d: CGFloat = 2          // priemer v mm
        while d / 2 * g.ppm <= far {
            let strong = Int(d) % 10 == 0
            fillPath(ctx, circlePath(c, d / 2 * g.ppm), nil, stroke: strong ? tint(g.th.ink, 160) : tint(g.th.ink, 45),
                     width: strong ? 1.2 : 1)
            d += 2
        }

        let font = uiFont(10, bold: true)
        d = 10
        while d / 2 * g.ppm <= far {
            let at = polar(c, 45, d / 2 * g.ppm)
            let text = "\(Int(d))"
            let w = textSize(text, font).width + 6
            let box = CGRect(x: at.x - w / 2, y: at.y - 7, width: w, height: 14)
            fillPath(ctx, roundedPath(box, 4), g.th.glass ? tint(g.th.panel, 235) : g.th.panel)
            textInRect(text, box, font: font, color: g.th.ink)
            d += 10
        }
        fillPath(ctx, circlePath(c, 2), g.th.ink)
    }

    override func paintDynamic(_ g: G) {
        guard let c else { return }
        let ctx = g.ctx
        if let fixed {
            let r = CGFloat(fixed) / 2 * g.ppm
            fillPath(ctx, circlePath(c, r), tint(MARK, 25), stroke: MARK, width: 2)
            pill(ctx, c.x, c.y - r - 16, "⌀ \(fmt(CGFloat(fixed), 1)) mm", MARK, lo: 2, hi: g.W - 2, size: 14)
        }
        guard let h = canvas.hover, grab == nil else { return }
        let r = h.dist(c)
        fillPath(ctx, circlePath(c, r), nil, stroke: ACCENT, width: 1.5)
        pill(ctx, h.x, h.y - 22, "⌀ \(fmt(2 * r / g.ppm, 1)) mm", ACCENT, lo: 2, hi: g.W - 2, size: 14)
    }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let c { grab = p - c }
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let grab { c = p - grab }
    }

    override func release(_ p: CGPoint) { grab = nil }

    override func nudge(_ d: CGPoint) {
        if let c { self.c = c + d }
    }

    override func reset() {
        c = nil
        fixed = nil
    }

    override func rescale(_ k: CGFloat) { c = nil }    // stred znova do stredu; zadaný priemer je v mm
}

// MARK: - polomer z 3 bodov

/// Kružnica cez 3 body — polomer oblúka, zaobleného rohu alebo kusu kruhu.
final class RadiusTool: Tool {
    var pts: [CGPoint] = []
    var grab: Int?

    override var hint: String {
        L("klikni 3 body na oblúku (roh mobilu, okraj kolesa…) · body sa dajú ťahať · šípky = posledný bod o 0,1 mm · ďalší klik = nové meranie · Esc = zmazať",
          "click 3 points on an arc (a phone corner, a wheel…) · points can be dragged · arrows = last point 0.1 mm · next click = new measurement · Esc = clear")
    }

    private func pointAt(_ p: CGPoint) -> Int? { pts.firstIndex { near(p, $0, 8) } }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let i = pointAt(p) {
            grab = i
            return
        }
        if pts.count >= 3 { pts = [] }
        pts.append(p)
        grab = pts.count - 1
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let grab { pts[grab] = p }
    }

    override func release(_ p: CGPoint) { grab = nil }

    override func nudge(_ d: CGPoint) {
        if !pts.isEmpty { pts[pts.count - 1] = pts[pts.count - 1] + d }
    }

    override func reset() { pts = [] }

    override func rescale(_ k: CGFloat) {
        if !pts.isEmpty { pts = rescaled(canvas, pts, k) }
    }

    override func cursor(_ p: CGPoint) -> NSCursor { pointAt(p) != nil ? .openHand : .crosshair }

    override func paintDynamic(_ g: G) {
        let ctx = g.ctx
        var all = pts
        let preview = pts.count < 3 && canvas.hover != nil && grab == nil
        if preview, !pts.isEmpty, let h = canvas.hover { all.append(h) }
        if all.count == 2 {
            strokeLine(ctx, all[0], all[1], tint(MARK, 150), dash: true)
        } else if all.count == 3 {
            if let fit = circle3(all[0], all[1], all[2]) {
                let (c, r) = fit
                fillPath(ctx, circlePath(c, r), preview ? nil : tint(ACCENT, 22), stroke: ACCENT, width: 1.5, dash: preview)
                strokeLine(ctx, c, all[0], tint(MARK, 170), dash: true)
                strokeLine(ctx, CGPoint(x: c.x - 8, y: c.y), CGPoint(x: c.x + 8, y: c.y), MARK, width: 1.5)
                strokeLine(ctx, CGPoint(x: c.x, y: c.y - 8), CGPoint(x: c.x, y: c.y + 8), MARK, width: 1.5)
                let onScreen = c.x >= 0 && c.x <= g.W && c.y >= 0 && c.y <= g.H - DOCK_RESERVE
                let at = onScreen ? c : (all[0] + all[1] + all[2]) / 3
                pill(ctx, at.x, at.y - 24, "R \(fmt(r / g.ppm, 1)) mm", MARK, lo: 2, hi: g.W - 2, size: 15)
                pill(ctx, at.x, at.y + 24, "⌀ \(fmt(2 * r / g.ppm, 1)) mm", ACCENT, lo: 2, hi: g.W - 2)
            } else {
                pill(ctx, all[2].x, all[2].y - 24, L("body sú na jednej priamke", "the points are on one line"), LABEL_DARK)
            }
        }
        for p in pts { dot(ctx, p, MARK) }
    }
}

// MARK: - uhlopriečka displeja

/// Obdĺžnik cez displej mobilu/tabletu → uhlopriečka v palcoch + údaje o vlastnom displeji.
final class DiagonalTool: Tool {
    var r: [CGFloat]?           // [x0, y0, x1, y1]
    var grabbed: [Int] = []     // indexy súradníc, ktoré sa ťahajú
    var last: [Int] = [2, 3]

    override var snapKind: SnapKind? { .point }
    override var hint: String {
        L("polož mobil či tablet na displej a potiahni obdĺžnik cez jeho obrazovku · píš čísla = presný rozmer · rohy a hrany sa dajú ťahať · šípky = 0,1 mm · Esc = zmazať",
          "lay a phone or tablet on the screen and drag a box over its display · type numbers = exact size · corners and edges can be dragged · arrows = 0.1 mm · Esc = clear")
    }
    override var inputFields: [Field] {
        [Field(name: L("Šírka", "Width"), unit: "mm", optional: false),
         Field(name: L("Výška", "Height"), unit: "mm", optional: false)]
    }

    private func handleAt(_ p: CGPoint) -> [Int] {
        guard let r else { return [] }
        for (ix, iy) in [(0, 1), (2, 1), (0, 3), (2, 3)] where abs(p.x - r[ix]) <= 10 && abs(p.y - r[iy]) <= 10 {
            return [ix, iy]
        }
        let loX = min(r[0], r[2]), hiX = max(r[0], r[2]), loY = min(r[1], r[3]), hiY = max(r[1], r[3])
        for ix in [0, 2] where abs(p.x - r[ix]) <= 6 && p.y > loY && p.y < hiY { return [ix] }
        for iy in [1, 3] where abs(p.y - r[iy]) <= 6 && p.x > loX && p.x < hiX { return [iy] }
        return []
    }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        grabbed = handleAt(canvas.raw ?? p)       // úchyty podľa skutočného kurzora, nie kroku
        if grabbed.isEmpty {
            r = [p.x, p.y, p.x, p.y]
            grabbed = [2, 3]
        }
        last = grabbed
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        guard r != nil else { return }
        for i in grabbed { r![i] = i % 2 == 0 ? p.x : p.y }
    }

    override func release(_ p: CGPoint) {
        grabbed = []
        if let r, abs(r[2] - r[0]) < 3, abs(r[3] - r[1]) < 3 { self.r = nil }
    }

    override func nudge(_ d: CGPoint) {
        guard r != nil else { return }
        for i in last { r![i] += i % 2 == 0 ? d.x : d.y }
    }

    override func reset() { r = nil }

    override func rescale(_ k: CGFloat) {
        guard let r else { return }
        let p = rescaled(canvas, [CGPoint(x: r[0], y: r[1]), CGPoint(x: r[2], y: r[3])], k)
        self.r = [p[0].x, p[0].y, p[1].x, p[1].y]
    }

    override func applyInput(_ nums: [Double]) -> String? {
        guard nums.count == 2, nums.min()! > 0 else {
            return L("Zadaj dve kladné čísla: šírka × výška", "Enter two positive numbers: width × height")
        }
        let ppm = canvas.ppm
        let o = r.map { CGPoint(x: min($0[0], $0[2]), y: min($0[1], $0[3])) } ?? canvas.inputOrigin()
        let b = CGPoint(x: o.x + CGFloat(nums[0]) * ppm, y: o.y + CGFloat(nums[1]) * ppm)
        let d = canvas.fitShift([o, b])
        r = [o.x + d.x, o.y + d.y, b.x + d.x, b.y + d.y]
        last = [2, 3]
        return nil
    }

    override func cursor(_ p: CGPoint) -> NSCursor {
        let h = handleAt(p)
        if h.count == 2 { return .openHand }
        if let i = h.first { return i % 2 == 0 ? .resizeLeftRight : .resizeUpDown }
        return .crosshair
    }

    override func staticKey() -> String {
        let s = canvas.currentScreen
        return "\(screenKey(s)),\(canvas.pxScale)"
    }

    override func paintStatic(_ g: G) {
        let s = canvas.currentScreen
        let wMM = s.frame.width / g.ppm, hMM = s.frame.height / g.ppm
        let wPx = Int((s.frame.width * g.px).rounded()), hPx = Int((s.frame.height * g.px).rounded())
        let source = Settings.calib[screenKey(s)] != nil ? L("kalibrované", "calibrated") : L("podľa EDID", "from EDID")
        let title = "\(L("Tento displej", "This display")): \(fmt(hypot(wMM, hMM) / MM_PER_IN, 1))″"
        let info = ["\(fmt(wMM, 1)) × \(fmt(hMM, 1)) mm · \(wPx) × \(hPx) px",
                    "\(fmt(g.ppm * MM_PER_IN * g.px, 1)) PPI · \(aspectName(CGFloat(wPx), CGFloat(hPx))) · \(source)"]
        let fTitle = uiFont(16, bold: true), fInfo = uiFont(12)
        let w = max(textSize(title, fTitle).width, info.map { textSize($0, fInfo).width }.max() ?? 0) + 44
        let card = CGRect(x: (g.W - w) / 2, y: 24, width: w, height: 88)
        fillPath(g.ctx, roundedPath(card, 14), tint(g.th.panel, 235), stroke: tint(g.th.ink, 40))
        textInRect(title, CGRect(x: card.minX, y: card.minY + 12, width: w, height: 24), font: fTitle, color: g.th.ink)
        for (k, t) in info.enumerated() {
            textInRect(t, CGRect(x: card.minX, y: card.minY + 42 + CGFloat(k) * 20, width: w, height: 20), font: fInfo, color: g.th.soft)
        }
    }

    override func paintDynamic(_ g: G) {
        let ctx = g.ctx
        if let h = canvas.hover, grabbed.isEmpty {
            let x = h.x.rounded(), y = h.y.rounded()
            crispLine(ctx, x0: x, y0: 0, x1: x, y1: g.H, tint(ACCENT, 90))
            crispLine(ctx, x0: 0, y0: y, x1: g.W, y1: y, tint(ACCENT, 90))
        }
        guard let r else { return }
        let rect = CGRect(x: min(r[0], r[2]), y: min(r[1], r[3]), width: abs(r[2] - r[0]), height: abs(r[3] - r[1]))
        let wMM = rect.width / g.ppm, hMM = rect.height / g.ppm
        let diag = hypot(wMM, hMM)
        fillPath(ctx, CGPath(rect: rect, transform: nil), tint(ACCENT, 30), stroke: ACCENT, width: 1.5)
        strokeLine(ctx, CGPoint(x: rect.minX, y: rect.minY), CGPoint(x: rect.maxX, y: rect.maxY), MARK, width: 2)
        for c in [CGPoint(x: rect.minX, y: rect.minY), CGPoint(x: rect.maxX, y: rect.minY),
                  CGPoint(x: rect.minX, y: rect.maxY), CGPoint(x: rect.maxX, y: rect.maxY)] {
            fillPath(ctx, CGPath(rect: CGRect(x: c.x - 4, y: c.y - 4, width: 8, height: 8), transform: nil), WHITE,
                     stroke: ACCENT, width: 1.5)
        }
        pill(ctx, rect.midX, rect.minY - 14, mmText(wMM), ACCENT, lo: 2, hi: g.W - 2)
        let text = mmText(hMM)
        pill(ctx, rect.minX - 8 - pillSize(text).width / 2, rect.midY, text, ACCENT, lo: 2)
        if diag > 1 {
            pill(ctx, rect.midX, rect.midY - 16, "\(fmt(diag / MM_PER_IN, 1))″", MARK, size: 24)
            pill(ctx, rect.midX, rect.midY + 20,
                 "\(fmt(diag / MM_PER_IN, 2))″ · \(fmt(diag / 10, 1)) cm · \(aspectName(wMM, hMM))", LABEL_DARK)
        }
    }
}

// MARK: - trasa (dĺžka a plocha)

/// Lomená čiara: dĺžka trasy; po uzavretí obvod a plocha.
final class PathTool: Tool {
    var pts: [CGPoint] = []
    var closed = false
    var done = false
    var grab: Int?

    override var snapKind: SnapKind? { .point }
    override var hint: String {
        L("klikaj body · píš čísla = presný úsek · dvojklik/Enter = koniec · klik na prvý bod = plocha · Backspace = späť · Shift = po 15° · Esc = zmazať",
          "click points · type numbers = exact segment · double-click/Enter = finish · click the first point = area · Backspace = undo · Shift = 15° steps · Esc = clear")
    }
    override var inputFields: [Field] {
        [Field(name: L("Dĺžka", "Length"), unit: "mm", optional: false),
         Field(name: L("Uhol", "Angle"), unit: "°", optional: true)]
    }

    var active: Bool { !pts.isEmpty && !(done || closed) }

    private func pointAt(_ p: CGPoint) -> Int? { pts.firstIndex { near(p, $0, 8) } }
    private func canClose(_ p: CGPoint) -> Bool { active && pts.count >= 3 && pointAt(p) == 0 }

    private func snapped(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) -> CGPoint {
        guard mods.contains(.shift), active, let o = pts.last else { return p }
        let a = (atan2(o.y - p.y, p.x - o.x) * 180 / .pi / 15).rounded() * 15
        return polar(o, a, p.dist(o))
    }

    override func press(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        let raw = canvas.raw ?? p                   // body podľa skutočného kurzora, nie kroku
        if canClose(raw) {
            closed = true
            return
        }
        if let i = pointAt(raw) {
            grab = i
            return
        }
        if !active {
            pts = []
            closed = false
            done = false
        }
        pts.append(snapped(p, mods))
    }

    override func drag(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if let grab { pts[grab] = p }
    }

    override func release(_ p: CGPoint) { grab = nil }

    override func double(_ p: CGPoint, _ mods: NSEvent.ModifierFlags) {
        if pts.count >= 2 && active { done = true }
    }

    override func key(_ k: ToolKey) -> Bool {
        switch k {
        case .enter:
            guard pts.count >= 2 && active else { return false }
            done = true
        case .backspace:
            guard !pts.isEmpty else { return false }
            if closed { closed = false } else { pts.removeLast() }
            done = false
        }
        return true
    }

    override func nudge(_ d: CGPoint) {
        if !pts.isEmpty { pts[pts.count - 1] = pts[pts.count - 1] + d }
    }

    override func rescale(_ k: CGFloat) {
        if !pts.isEmpty { pts = rescaled(canvas, pts, k) }
    }

    override func reset() {
        pts = []
        closed = false
        done = false
    }

    override func applyInput(_ nums: [Double]) -> String? {
        guard (1...2).contains(nums.count), nums[0] > 0 else {
            return L("Zadaj dĺžku (kladnú), prípadne aj uhol v stupňoch", "Enter a positive length, optionally an angle in degrees")
        }
        if !active {
            pts = [canvas.inputOrigin()]
            closed = false
            done = false
        }
        let o = pts[pts.count - 1]
        let angle: CGFloat
        if nums.count == 2 {
            angle = CGFloat(nums[1])
        } else if let h = canvas.raw, !near(h, o, 2) {        // smerom ku kurzoru
            angle = atan2(o.y - h.y, h.x - o.x) * 180 / .pi
        } else {
            angle = 0
        }
        pts.append(polar(o, angle, CGFloat(nums[0]) * canvas.ppm))
        return nil
    }

    override func cursor(_ p: CGPoint) -> NSCursor {
        if canClose(p) { return .pointingHand }
        return pointAt(p) != nil ? .openHand : .crosshair
    }

    override func paintDynamic(_ g: G) {
        guard !pts.isEmpty else { return }
        let ctx = g.ctx
        var all = pts
        let rubber = active && canvas.hover != nil && grab == nil
        if rubber, let h = canvas.hover { all.append(snapped(h, NSEvent.modifierFlags)) }
        var segs = Array(zip(all, all.dropFirst()))
        if closed { segs.append((all[all.count - 1], all[0])) }
        let total = segs.reduce(CGFloat(0)) { sum, s in sum + s.0.dist(s.1) } / g.ppm

        if closed { fillPath(ctx, polygonPath(all), tint(ACCENT, 40)) }
        for (k, (a, b)) in segs.enumerated() {
            strokeLine(ctx, a, b, MARK, width: 2, dash: rubber && k == segs.count - 1)
            let length = a.dist(b)
            if length > 70 {
                let mid = (a + b) / 2
                pill(ctx, mid.x, mid.y - 14, mmText(length / g.ppm), LABEL_DARK, size: 11)
            }
        }
        let closable = canvas.raw.map { canClose($0) } ?? false
        for (k, q) in pts.enumerated() {
            dot(ctx, q, k == 0 && closable ? ACCENT : MARK, r: k == 0 && closable ? 7 : 4.5)
        }
        if closed {
            let cross = segs.reduce(CGFloat(0)) { sum, s in sum + (s.0.x * s.1.y - s.1.x * s.0.y) }
            let area = abs(cross) / 2 / (g.ppm * g.ppm)
            let cen = all.reduce(CGPoint.zero, +) / CGFloat(all.count)
            pill(ctx, cen.x, cen.y, "\(L("obvod", "perimeter")) \(mmText(total)) · \(L("plocha", "area")) \(areaText(area))",
                 MARK, lo: 2, hi: g.W - 2, size: 14)
        } else if let end = all.last {
            pill(ctx, end.x, end.y - 28, "\(L("spolu", "total")) \(mmText(total))", MARK, lo: 2, hi: g.W - 2, size: 14)
        }
    }
}
