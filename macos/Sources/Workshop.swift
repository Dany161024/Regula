// Workshop.swift — dielenské nástroje: metrické skrutky a rozteč pinov.
import AppKit

// (názov, závit mm, stúpanie mm, matica = kľúč mm, imbus mm) — metrické ISO, hrubý závit
let SCREWS: [(String, Double, Double, Double, Double)] = [
    (L("M1,6", "M1.6"), 1.6, 0.35, 3.2, 1.5), ("M2", 2.0, 0.40, 4.0, 1.5), (L("M2,5", "M2.5"), 2.5, 0.45, 5.0, 2.0),
    ("M3", 3.0, 0.50, 5.5, 2.5), ("M4", 4.0, 0.70, 7.0, 3.0), ("M5", 5.0, 0.80, 8.0, 4.0),
    ("M6", 6.0, 1.00, 10.0, 5.0), ("M8", 8.0, 1.25, 13.0, 6.0),
]

let PITCHES: [(Double, String)] = [
    (2.54, L("2,54 mm (0,1″) · lámacie lišty, Dupont, DIP", "2.54 mm (0.1″) · pin headers, Dupont, DIP")),
    (2.50, L("2,50 mm · JST XH", "2.50 mm · JST XH")),
    (2.00, L("2,00 mm · JST PH, lišty 2 mm", "2.00 mm · JST PH, 2 mm headers")),
    (1.50, L("1,50 mm · JST ZH", "1.50 mm · JST ZH")),
    (1.27, L("1,27 mm (0,05″) · SOIC, lišty 1,27", "1.27 mm (0.05″) · SOIC, 1.27 mm headers")),
    (1.25, L("1,25 mm · Molex PicoBlade", "1.25 mm · Molex PicoBlade")),
    (1.00, L("1,00 mm · JST SH, Qwiic / STEMMA QT", "1.00 mm · JST SH, Qwiic / STEMMA QT")),
    (0.80, L("0,80 mm · QFP", "0.80 mm · QFP")),
    (0.65, L("0,65 mm · TSSOP, QFP", "0.65 mm · TSSOP, QFP")),
    (0.50, L("0,50 mm · FFC/FPC káble, QFN", "0.50 mm · FFC/FPC cables, QFN")),
]

/// Text zarovnaný vľavo a zvislo na stred obdĺžnika.
private func textLeft(_ s: String, _ rect: CGRect, font: NSFont, color: NSColor) {
    drawText(s, x: rect.minX, baseline: rect.midY + (font.ascender + font.descender) / 2, font: font, color: color)
}

// MARK: - skrutky

/// Metrické skrutky M1,6–M8: otvor, závit zboku, matica, dĺžka.
final class ScrewTool: Tool {
    let TOTAL_MM: CGFloat = 96

    override var hint: String {
        L("skrutku polož na kruh alebo zboku na pás · maticu na šesťuholník · dĺžku meraj od hrubej čiary · prejdi myšou = detaily",
          "stand a screw on a circle or lay it on a strip · a nut on the hexagon · measure length from the thick line · hover = details")
    }

    /// (ľavý okraj, horný okraj, stĺpce (stred x, šírka))
    private func geom(_ W: CGFloat, _ H: CGFloat, _ ppm: CGFloat) -> (CGFloat, CGFloat, [(CGFloat, CGFloat)]) {
        let widths = SCREWS.map { (CGFloat(max($0.3, 9)) + 5) * ppm }
        let ox = (W - widths.reduce(0, +)) / 2
        let oy = max(16, (H - DOCK_RESERVE - TOTAL_MM * ppm) / 2)
        var cols: [(CGFloat, CGFloat)] = [], x = ox
        for w in widths {
            cols.append((x + w / 2, w))
            x += w
        }
        return (ox, oy, cols)
    }

    override func paintStatic(_ g: G) {
        let ctx = g.ctx, ppm = g.ppm
        let (ox, oy, cols) = geom(g.W, g.H, ppm)
        let Y = { (mm: CGFloat) in oy + mm * ppm }
        let right = cols[cols.count - 1].0 + cols[cols.count - 1].1 / 2
        backdrop(ctx, CGRect(x: ox - 80, y: oy - 14, width: right - ox + 100, height: TOTAL_MM * ppm + 34), g.th)
        let small = uiFont(11)
        for (text, mm) in [(L("otvor", "hole"), 11.0), (L("zboku", "side"), 34), (L("matica", "nut"), 59), (L("dĺžka", "length"), 86)] {
            drawText(text, x: ox - 12 - textSize(text, small).width, baseline: Y(CGFloat(mm)) + 4, font: small, color: g.th.soft)
        }

        let fill = tint(g.th.ink, 28)
        for (screw, col) in zip(SCREWS, cols) {
            let (name, d, _, af, key) = screw
            let cx = col.0, r = CGFloat(d) / 2 * ppm
            centeredText(name, at: CGPoint(x: cx, y: Y(3)), font: uiFont(14, bold: true), color: g.th.ink)
            fillPath(ctx, circlePath(CGPoint(x: cx, y: Y(11)), r), fill, stroke: g.th.ink)
            fillPath(ctx, CGPath(rect: CGRect(x: cx - r, y: Y(19), width: 2 * r, height: 30 * ppm), transform: nil), fill, stroke: g.th.ink)
            let center = CGPoint(x: cx, y: Y(59)), corner = CGFloat(af) / sqrt(3) * ppm   // kľúč = vzdialenosť plôch
            fillPath(ctx, polygonPath(stride(from: 0, to: 360, by: 60).map { polar(center, CGFloat($0), corner) }), fill, stroke: g.th.ink)
            fillPath(ctx, circlePath(center, r), g.th.glass ? g.th.panel : g.th.bg, stroke: g.th.ink)
            centeredText("\(L("kľúč", "wrench")) \(fmtG(af))", at: CGPoint(x: cx, y: Y(69.5)), font: uiFont(10), color: g.th.soft)
            centeredText("\(L("imbus", "hex key")) \(fmtG(key))", at: CGPoint(x: cx, y: Y(73)), font: uiFont(10), color: g.th.soft)
        }

        // dĺžka: hlava sa opiera o hrubú čiaru
        let base = Y(92).rounded(), x0 = ox.rounded()
        ctx.saveGState()
        ctx.setShouldAntialias(false)
        ctx.setStrokeColor(g.th.ink.cgColor)
        ctx.setLineWidth(3)
        ctx.strokeLineSegments(between: [CGPoint(x: x0, y: Y(80).rounded()), CGPoint(x: x0, y: base)])
        ctx.restoreGState()
        crispLine(ctx, x0: x0, y0: base, x1: (ox + 70 * ppm).rounded(), y1: base, g.th.ink)
        for mm in 0...70 {
            let x = (ox + CGFloat(mm) * ppm).rounded()
            crispLine(ctx, x0: x, y0: base, x1: x, y1: base - (mm % 10 == 0 ? 14 : mm % 5 == 0 ? 10 : 6), g.th.ink)
            if mm % 5 == 0 && mm > 0 {
                centeredText("\(mm)", at: CGPoint(x: x, y: base + 11), font: uiFont(10), color: g.th.ink)
            }
        }
        drawText(L("← hlavu skrutky oprieť o hrubú čiaru", "← put the screw head against the thick line"),
                 x: ox + 73 * ppm, baseline: base - 4, font: small, color: g.th.soft)
    }

    override func paintDynamic(_ g: G) {
        guard let h = canvas.hover else { return }
        let ctx = g.ctx, ppm = g.ppm
        let (ox, oy, cols) = geom(g.W, g.H, ppm)
        let Y = { (mm: CGFloat) in oy + mm * ppm }
        if h.y >= Y(0) && h.y <= Y(75) {
            for (screw, col) in zip(SCREWS, cols) where abs(h.x - col.0) <= col.1 / 2 {
                let (name, d, pitch, af, key) = screw
                fillPath(ctx, roundedPath(CGRect(x: col.0 - col.1 / 2 + 2, y: Y(-1), width: col.1 - 4, height: 77 * ppm), 10),
                         tint(ACCENT, 34))
                let text = L("\(name) · závit \(fmtG(d)) mm · stúpanie \(fmtG(pitch)) mm · matica \(fmtG(af)) mm · imbus \(fmtG(key)) mm",
                             "\(name) · thread \(fmtG(d)) mm · pitch \(fmtG(pitch)) mm · nut \(fmtG(af)) mm · hex key \(fmtG(key)) mm")
                pill(ctx, h.x, h.y - 26, text, ACCENT, lo: 2, hi: g.W - 2)
            }
        } else if h.y >= Y(78) && h.y <= Y(96) && h.x >= ox && h.x <= ox + 70 * ppm {
            let x = h.x.rounded()
            crispLine(ctx, x0: x, y0: Y(78), x1: x, y1: Y(92), ACCENT)
            pill(ctx, h.x, Y(78) - 14, "\(L("dĺžka", "length")) \(fmt((h.x - ox) / ppm, 1)) mm", ACCENT, lo: 2, hi: g.W - 2)
        }
    }
}

// MARK: - rozteč pinov

/// Hrebene bežných roztečí pinov — na určenie konektora či púzdra čipu.
final class PitchTool: Tool {
    let ROW_MM: CGFloat = 9, COMB_MM: CGFloat = 60, LABEL_W: CGFloat = 330

    override var hint: String {
        L("prilož konektor alebo čip k hrebeňu · prejdi myšou po hrebeni = počet pinov a dĺžka",
          "hold a connector or chip to a comb · hover along the comb = pin count and length")
    }

    /// (ľavý okraj textov, začiatok hrebeňov, horný okraj)
    private func geom(_ W: CGFloat, _ H: CGFloat, _ ppm: CGFloat) -> (CGFloat, CGFloat, CGFloat) {
        let left = (W - LABEL_W - COMB_MM * ppm) / 2
        let oy = max(16, (H - DOCK_RESERVE - CGFloat(PITCHES.count) * ROW_MM * ppm) / 2)
        return (left, left + LABEL_W, oy)
    }

    private func pinCount(_ pitch: Double) -> Int { Int(Double(COMB_MM) / pitch + 1e-9) + 1 }

    private func paintPins(_ ctx: CGContext, _ x0: CGFloat, _ top: CGFloat, _ pitch: Double, _ ppm: CGFloat,
                           _ color: NSColor, count: Int? = nil) {
        let step = CGFloat(pitch) * ppm
        let pw = max(1, (CGFloat(pitch) * 0.45 * ppm).rounded())
        let y = (top + 2 * ppm).rounded(), ph = (4 * ppm).rounded()
        ctx.saveGState()
        ctx.setShouldAntialias(false)
        ctx.setFillColor(color.cgColor)
        for k in 0..<(count ?? pinCount(pitch)) {
            ctx.fill(CGRect(x: (x0 + CGFloat(k) * step - pw / 2).rounded(), y: y, width: pw, height: ph))
        }
        ctx.restoreGState()
    }

    override func paintStatic(_ g: G) {
        let ctx = g.ctx, ppm = g.ppm
        let (left, x0, oy) = geom(g.W, g.H, ppm)
        let row = ROW_MM * ppm
        backdrop(ctx, CGRect(x: left - 20, y: oy - 14, width: x0 - left + COMB_MM * ppm + 44,
                             height: CGFloat(PITCHES.count) * row + 28), g.th)
        for (i, (pitch, text)) in PITCHES.enumerated() {
            let top = oy + CGFloat(i) * row
            textLeft(text, CGRect(x: left, y: top + ppm, width: LABEL_W - 16, height: 6 * ppm), font: uiFont(12), color: g.th.ink)
            paintPins(ctx, x0, top, pitch, ppm, g.th.ink)
            let y = (top + 6 * ppm).rounded() + 1
            crispLine(ctx, x0: x0 - 4, y0: y, x1: x0 + COMB_MM * ppm + 4, y1: y, tint(g.th.ink, 110))
        }
    }

    override func paintDynamic(_ g: G) {
        guard let h = canvas.hover else { return }
        let ppm = g.ppm
        let (_, x0, oy) = geom(g.W, g.H, ppm)
        let row = ROW_MM * ppm
        let i = Int(((h.y - oy) / row).rounded(.down))
        guard i >= 0 && i < PITCHES.count else { return }
        let pitch = PITCHES[i].0, step = CGFloat(pitch) * ppm
        guard h.x >= x0 - step && h.x <= x0 + COMB_MM * ppm + step else { return }
        let n = max(1, min(pinCount(pitch), Int(((h.x - x0) / step + 0.5).rounded(.down)) + 1))
        let top = oy + CGFloat(i) * row
        paintPins(g.ctx, x0, top, pitch, ppm, ACCENT, count: n)
        pill(g.ctx, h.x, top + 7.6 * ppm, "\(pinsText(n)) · \(fmt(CGFloat(Double(n - 1) * pitch), 2)) mm", ACCENT, lo: 2, hi: g.W - 2)
    }
}
