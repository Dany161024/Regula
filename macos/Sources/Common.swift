// Common.swift — jazyk, nastavenia, mierka obrazovky, formátovanie, farby a kresliace pomôcky.
// Rovnaká logika ako regula.py (Linux), len v AppKite / Core Graphics.
import AppKit

// MARK: - jazyk

/// Jazyk rozhrania: --lang / REGULA_LANG / nastavenie „lang"; predvolene angličtina (tá ide na web).
let SK: Bool = {
    let args = CommandLine.arguments
    if let i = args.firstIndex(of: "--lang"), i + 1 < args.count { return args[i + 1] == "sk" }
    if let env = ProcessInfo.processInfo.environment["REGULA_LANG"] { return env == "sk" }
    return UserDefaults.standard.string(forKey: "lang") == "sk"
}()

/// Text v jazyku rozhrania.
func L(_ sk: String, _ en: String) -> String { SK ? sk : en }

// MARK: - nastavenia

enum Settings {
    /// pri --snapshot sa nesiaha na skutočné nastavenia
    static let store: UserDefaults = CommandLine.arguments.contains("--snapshot")
        ? (UserDefaults(suiteName: "sk.daniel.regula.snapshot") ?? .standard) : .standard

    static var tool: String {
        get { store.string(forKey: "tool") ?? "cm" }
        set { store.set(newValue, forKey: "tool") }
    }
    static var bg: String {
        get { store.string(forKey: "bg") ?? "light" }
        set { store.set(newValue, forKey: "bg") }
    }
    static var snap: Int {
        get { store.integer(forKey: "snap") }
        set { store.set(newValue, forKey: "snap") }
    }
    static var fullscreen: Bool {
        get { store.object(forKey: "fullscreen") as? Bool ?? true }
        set { store.set(newValue, forKey: "fullscreen") }
    }
    static var windowFrame: String? {
        get { store.string(forKey: "windowFrame") }
        set { store.set(newValue, forKey: "windowFrame") }
    }
    static var calib: [String: Double] {
        get { store.dictionary(forKey: "calib") as? [String: Double] ?? [:] }
        set { store.set(newValue, forKey: "calib") }
    }
}

// MARK: - mierka obrazovky

let MM_PER_IN: CGFloat = 25.4
let CARD_MM = CGSize(width: 85.60, height: 53.98)     // ID-1: platobná karta, občiansky, ISIC
let CARD_CORNER_MM: CGFloat = 3.18
let DOCK_RESERVE: CGFloat = 90                         // miesto dole pre panel (rozloženie uhlomera a kruhov)

func displayID(_ screen: NSScreen) -> CGDirectDisplayID {
    (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? CGMainDisplayID()
}

/// Kľúč kalibrácie: konkrétny monitor v konkrétnom rozlíšení.
func screenKey(_ screen: NSScreen) -> String {
    let id = displayID(screen)
    return "\(CGDisplayVendorNumber(id))-\(CGDisplayModelNumber(id))-\(CGDisplaySerialNumber(id))"
        + "@\(Int(screen.frame.width))x\(Int(screen.frame.height))"
}

/// Body (pt) na mm podľa fyzickej veľkosti, ktorú hlási displej (EDID).
func edidPPM(_ screen: NSScreen) -> CGFloat {
    let mm = CGDisplayScreenSize(displayID(screen))
    if mm.width >= 50 {
        let ppm = screen.frame.width / mm.width
        if ppm > 1, ppm < 40 { return ppm }
    }
    return 100 / MM_PER_IN
}

func ptPerMM(_ screen: NSScreen) -> CGFloat {
    if let c = Settings.calib[screenKey(screen)] { return CGFloat(c) }
    return edidPPM(screen)
}

// MARK: - formátovanie

func fmt(_ v: CGFloat, _ dec: Int) -> String {
    let text = String(format: "%.\(dec)f", Double(v))
    return SK ? text.replacingOccurrences(of: ".", with: ",") : text
}

func fmtG(_ v: Double) -> String {
    let text = String(format: "%g", v)
    return SK ? text.replacingOccurrences(of: ".", with: ",") : text
}

private func gcd(_ a: Int, _ b: Int) -> Int { b == 0 ? a : gcd(b, a % b) }

func frac16(_ inch: CGFloat) -> String {
    let n = Int((inch * 16).rounded())
    let whole = n / 16, rest = n % 16
    if rest == 0 { return "\(whole)″" }
    let g = gcd(rest, 16)
    let fr = "\(rest / g)/\(16 / g)"
    return whole > 0 ? "\(whole) \(fr)″" : "\(fr)″"
}

/// Hodnota merania v jednotke nástroja; pt = body na obrazovke, px = pixely na bod.
func readout(_ unit: String, _ ppm: CGFloat, _ pt: CGFloat, px: CGFloat) -> String {
    if unit == "px" { return "\(Int((pt * px).rounded())) px" }
    let mm = pt / ppm, inch = mm / MM_PER_IN
    if unit == "in" { return "\(fmt(inch, 3))″ ≈ \(frac16(inch))" }
    if unit == "dual" { return "\(fmt(mm, 1)) mm · \(fmt(inch, 3))″" }
    return "\(fmt(mm, 1)) mm"
}

func mmText(_ mm: CGFloat) -> String { mm < 1000 ? "\(fmt(mm, 1)) mm" : "\(fmt(mm / 1000, 3)) m" }

func areaText(_ mm2: CGFloat) -> String { mm2 < 100 ? "\(fmt(mm2, 1)) mm²" : "\(fmt(mm2 / 100, 2)) cm²" }

let ASPECTS: [(Double, Int)] = [(1, 1), (5, 4), (4, 3), (3, 2), (16, 10), (16, 9), (18, 9), (19.5, 9), (20, 9), (21, 9)]

func aspectName(_ w: CGFloat, _ h: CGFloat) -> String {
    let long = Double(max(w, h)), short = Double(min(w, h))
    if short <= 0 { return "" }
    let r = long / short
    let best = ASPECTS.min { abs($0.0 / Double($0.1) - r) < abs($1.0 / Double($1.1) - r) }!
    return abs(best.0 / Double(best.1) - r) / r < 0.025 ? "\(fmtG(best.0)):\(best.1)" : "\(fmt(CGFloat(r), 2)):1"
}

func pinsText(_ n: Int) -> String {
    if !SK { return n == 1 ? "1 pin" : "\(n) pins" }
    return n == 1 ? "1 pin" : n < 5 ? "\(n) piny" : "\(n) pinov"
}

// slovenská a česká klávesnica majú v hornom rade bez Shiftu písmená → preložia sa na číslice
private let skDigits: [Character: Character] = ["+": "1", "ľ": "2", "ě": "2", "š": "3", "č": "4", "ť": "5",
                                                "ř": "5", "ž": "6", "ý": "7", "á": "8", "í": "9", "é": "0"]

func toDigits(_ text: String) -> String { String(text.map { skDigits[$0] ?? $0 }) }

func isDigitKey(_ text: String) -> Bool {
    guard let c = text.first else { return false }
    return c.isNumber || skDigits[c] != nil || c == "." || c == ","
}

/// „20 x 50", „20,5×50", „ľé x ťé" → [20, 50]
func parseNumbers(_ text: String) -> [Double] {
    let t = toDigits(text).replacingOccurrences(of: ",", with: ".")
    guard let re = try? NSRegularExpression(pattern: #"-?\d+(?:\.\d+)?|-?\.\d+"#) else { return [] }
    return re.matches(in: t, range: NSRange(t.startIndex..., in: t)).compactMap { m in
        Range(m.range, in: t).flatMap { Double(t[$0]) }
    }
}

// MARK: - geometria

extension CGPoint {
    static func + (a: CGPoint, b: CGPoint) -> CGPoint { CGPoint(x: a.x + b.x, y: a.y + b.y) }
    static func - (a: CGPoint, b: CGPoint) -> CGPoint { CGPoint(x: a.x - b.x, y: a.y - b.y) }
    static func * (a: CGPoint, k: CGFloat) -> CGPoint { CGPoint(x: a.x * k, y: a.y * k) }
    static func / (a: CGPoint, k: CGFloat) -> CGPoint { CGPoint(x: a.x / k, y: a.y / k) }
    func dist(_ o: CGPoint) -> CGFloat { hypot(x - o.x, y - o.y) }
}

/// Bod na kružnici; uhol v stupňoch proti smeru hodiniek od osi x (y obrazovky ide dole).
func polar(_ c: CGPoint, _ deg: CGFloat, _ r: CGFloat) -> CGPoint {
    let a = deg * .pi / 180
    return CGPoint(x: c.x + r * cos(a), y: c.y - r * sin(a))
}

func near(_ p: CGPoint, _ q: CGPoint, _ r: CGFloat) -> Bool { abs(p.x - q.x) <= r && abs(p.y - q.y) <= r }

/// Kružnica cez 3 body → (stred, polomer), nil ak ležia na priamke.
func circle3(_ a: CGPoint, _ b: CGPoint, _ c: CGPoint) -> (CGPoint, CGFloat)? {
    let d = 2 * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y))
    if abs(d) < 1e-6 { return nil }
    let a2 = a.x * a.x + a.y * a.y, b2 = b.x * b.x + b.y * b.y, c2 = c.x * c.x + c.y * c.y
    let ux = (a2 * (b.y - c.y) + b2 * (c.y - a.y) + c2 * (a.y - b.y)) / d
    let uy = (a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / d
    return (CGPoint(x: ux, y: uy), hypot(a.x - ux, a.y - uy))
}

// MARK: - farby a témy

func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: Int = 255) -> NSColor {
    NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
}

func tint(_ c: NSColor, _ alpha: Int) -> NSColor { c.withAlphaComponent(CGFloat(alpha) / 255) }

let ACCENT = rgb(31, 158, 222)      // Mint-Y Aqua — rovnaká modrá ako na Linuxe
let MARK = rgb(228, 69, 83)
let INK = rgb(34, 34, 42)
let INK_SOFT = rgb(112, 112, 124)
let WHITE = NSColor.white
let LABEL_DARK = rgb(70, 72, 86)

struct Theme {
    let bg, panel, ink, soft: NSColor
    let glass: Bool
}

let THEMES: [String: Theme] = [
    "light": Theme(bg: rgb(251, 251, 246), panel: rgb(236, 236, 228), ink: INK, soft: INK_SOFT, glass: false),
    "dark": Theme(bg: rgb(29, 30, 37), panel: rgb(43, 44, 54), ink: rgb(226, 226, 234), soft: rgb(150, 150, 166), glass: false),
    // rezacia podložka: zelená s bielymi čiarami
    "green": Theme(bg: rgb(46, 107, 79), panel: rgb(37, 90, 65), ink: rgb(242, 247, 238), soft: rgb(186, 215, 197), glass: false),
    "clear": Theme(bg: rgb(255, 255, 255, 16), panel: rgb(251, 251, 246, 225), ink: INK, soft: INK_SOFT, glass: true),
]
let THEME_ORDER = ["light", "dark", "green", "clear"]
let BG_NAMES = ["light": L("Svetlé", "Light"), "dark": L("Tmavé", "Dark"), "green": L("Zelené", "Green"),
                "clear": L("Priehľadné", "Clear")]

// krok prichytávania pre úroveň 1–3 (0 = voľne): (hodnota v jednotke, popis)
let SNAP: [String: [(Double, String)]] = [
    "mm": [(0.5, L("0,5 mm", "0.5 mm")), (1, "1 mm"), (5, "5 mm")],
    "in": [(1.0 / 32, "1/32″"), (1.0 / 16, "1/16″"), (1.0 / 4, "1/4″")],
    "px": [(5, "5 px"), (10, "10 px"), (50, "50 px")],
    "deg": [(0.5, L("0,5°", "0.5°")), (1, "1°"), (5, "5°")],
]

let TOOLS: [(String, String)] = [
    ("cm", L("Centimetre", "Centimetres")), ("in", L("Palce", "Inches")), ("px", L("Pixely", "Pixels")),
    ("dual", L("cm + palce", "cm + in")), ("grid", L("Mm papier", "Mm paper")),
    ("prot", L("Uhlomer", "Protractor")), ("prot360", L("Uhlomer 360°", "Protractor 360°")),
    ("circle", L("Priemer", "Diameter")), ("radius", L("Polomer", "Radius")),
    ("diag", L("Uhlopriečka", "Diagonal")), ("path", L("Trasa", "Path")),
    ("screw", L("Skrutky", "Screws")), ("pitch", L("Rozteč", "Pin pitch")),
]
let GROUP_STARTS: Set<String> = ["prot", "diag", "screw"]     // pred nimi je v paneli oddeľovač

// MARK: - kreslenie (súradnice zhora dole, ako na Linuxe)

/// Všetko, čo nástroj potrebuje na kreslenie.
struct G {
    let ctx: CGContext
    let W: CGFloat, H: CGFloat
    let th: Theme
    let ppm: CGFloat        // body na mm
    let px: CGFloat         // pixely na bod (Retina = 2)
}

func uiFont(_ size: CGFloat, bold: Bool = false) -> NSFont {
    bold ? .boldSystemFont(ofSize: size) : .systemFont(ofSize: size)
}

func textSize(_ s: String, _ font: NSFont) -> CGSize { (s as NSString).size(withAttributes: [.font: font]) }

/// Text s účiaria na y (ako QPainter.drawText(QPointF)).
func drawText(_ s: String, x: CGFloat, baseline y: CGFloat, font: NSFont, color: NSColor) {
    withAntialias {
        (s as NSString).draw(at: CGPoint(x: x, y: y - font.ascender), withAttributes: [.font: font, .foregroundColor: color])
    }
}

/// Text vycentrovaný na bod.
func centeredText(_ s: String, at p: CGPoint, font: NSFont, color: NSColor) {
    let size = textSize(s, font)
    withAntialias {
        (s as NSString).draw(at: CGPoint(x: p.x - size.width / 2, y: p.y - size.height / 2),
                             withAttributes: [.font: font, .foregroundColor: color])
    }
}

/// Text (aj viacriadkový) vycentrovaný v obdĺžniku.
func textInRect(_ s: String, _ rect: CGRect, font: NSFont, color: NSColor) {
    let style = NSMutableParagraphStyle()
    style.alignment = .center
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: style]
    let h = (s as NSString).boundingRect(with: rect.size, options: [.usesLineFragmentOrigin], attributes: attrs).height
    withAntialias {
        (s as NSString).draw(with: CGRect(x: rect.minX, y: rect.midY - h / 2, width: rect.width, height: h),
                             options: [.usesLineFragmentOrigin], attributes: attrs)
    }
}

private func withAntialias(_ body: () -> Void) {
    guard let ctx = NSGraphicsContext.current?.cgContext else { return body() }
    ctx.saveGState()
    ctx.setShouldAntialias(true)
    body()
    ctx.restoreGState()
}

func strokeLine(_ ctx: CGContext, _ a: CGPoint, _ b: CGPoint, _ color: NSColor, width: CGFloat = 1, dash: Bool = false) {
    ctx.saveGState()
    ctx.setStrokeColor(color.cgColor)
    ctx.setLineWidth(width)
    ctx.setLineCap(.round)
    if dash { ctx.setLineCap(.butt); ctx.setLineDash(phase: 0, lengths: [4 * width, 3 * width]) }
    ctx.strokeLineSegments(between: [a, b])
    ctx.restoreGState()
}

/// Ostrá 1-bodová čiara bez vyhladzovania (čiarky stupníc, nitkový kríž).
func crispLine(_ ctx: CGContext, x0: CGFloat, y0: CGFloat, x1: CGFloat, y1: CGFloat, _ color: NSColor) {
    ctx.saveGState()
    ctx.setShouldAntialias(false)
    ctx.setStrokeColor(color.cgColor)
    ctx.setLineWidth(1)
    let ox: CGFloat = x0 == x1 ? 0.5 : 0, oy: CGFloat = y0 == y1 ? 0.5 : 0
    ctx.strokeLineSegments(between: [CGPoint(x: x0 + ox, y: y0 + oy), CGPoint(x: x1 + ox, y: y1 + oy)])
    ctx.restoreGState()
}

func fillRect(_ ctx: CGContext, _ r: CGRect, _ color: NSColor) {
    ctx.setFillColor(color.cgColor)
    ctx.fill(r)
}

func roundedPath(_ r: CGRect, _ radius: CGFloat) -> CGPath {
    CGPath(roundedRect: r, cornerWidth: min(radius, r.width / 2), cornerHeight: min(radius, r.height / 2), transform: nil)
}

func fillPath(_ ctx: CGContext, _ path: CGPath, _ fill: NSColor?, stroke: NSColor? = nil, width: CGFloat = 1, dash: Bool = false) {
    ctx.saveGState()
    ctx.setShouldAntialias(true)
    if let fill {
        ctx.addPath(path)
        ctx.setFillColor(fill.cgColor)
        ctx.fillPath()
    }
    if let stroke {
        ctx.addPath(path)
        ctx.setStrokeColor(stroke.cgColor)
        ctx.setLineWidth(width)
        if dash { ctx.setLineDash(phase: 0, lengths: [4, 3]) }
        ctx.strokePath()
    }
    ctx.restoreGState()
}

func circlePath(_ c: CGPoint, _ r: CGFloat) -> CGPath {
    CGPath(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r), transform: nil)
}

func polygonPath(_ pts: [CGPoint], close: Bool = true) -> CGPath {
    let path = CGMutablePath()
    path.addLines(between: pts)
    if close { path.closeSubpath() }
    return path
}

func pillSize(_ text: String, _ size: CGFloat = 12) -> CGSize {
    let s = textSize(text, uiFont(size, bold: true))
    return CGSize(width: s.width + 14, height: s.height + 4)
}

/// Zaoblený štítok s bielym textom, vodorovne držaný medzi lo a hi.
func pill(_ ctx: CGContext, _ cx: CGFloat, _ cy: CGFloat, _ text: String, _ color: NSColor,
          lo: CGFloat = -1e9, hi: CGFloat = 1e9, size: CGFloat = 12) {
    let s = pillSize(text, size)
    let x = min(max(cx - s.width / 2, lo), hi - s.width)
    let r = CGRect(x: x, y: cy - s.height / 2, width: s.width, height: s.height)
    fillPath(ctx, roundedPath(r, s.height / 2), color)
    textInRect(text, r, font: uiFont(size, bold: true), color: WHITE)
}

func dot(_ ctx: CGContext, _ p: CGPoint, _ color: NSColor, r: CGFloat = 4.5) {
    fillPath(ctx, circlePath(p, r), color, stroke: WHITE, width: 1.5)
}

/// Pri priehľadnom pozadí podloží obsah, aby bol čitateľný.
func backdrop(_ ctx: CGContext, _ rect: CGRect, _ th: Theme) {
    if th.glass { fillPath(ctx, roundedPath(rect, 16), tint(th.panel, 215)) }
}

/// Položka menu s vlastnou akciou (bez selektorov v každej triede).
final class ActionItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, checked: Bool = false, key: String = "", _ handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: key)
        target = self
        state = checked ? .on : .off
    }

    required init(coder: NSCoder) { fatalError("init(coder:) nie je podporovaný") }

    @objc private func fire() { handler() }
}
