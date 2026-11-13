// Panels.swift — panel s nástrojmi, tlačidlo na jeho návrat, políčka na zadanie čísel, kalibrácia a ikonky.
import AppKit

private let PANEL_BG = rgb(252, 252, 249, 245)
private let PANEL_EDGE = rgb(0, 0, 0, 38)
private let PANEL_TEXT = rgb(42, 42, 51)

/// Svetlá zaoblená plocha s jemným tieňom (panel, políčka).
private func drawPanel(_ rect: CGRect, radius: CGFloat) {
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowBlurRadius = 14
    shadow.shadowOffset = NSSize(width: 0, height: -4)
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
    shadow.set()
    PANEL_BG.setFill()
    NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
    NSGraphicsContext.restoreGraphicsState()
    PANEL_EDGE.setStroke()
    let edge = NSBezierPath(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), xRadius: radius, yRadius: radius)
    edge.lineWidth = 1
    edge.stroke()
}

// MARK: - panel s nástrojmi

@MainActor
final class DockView: NSView {
    struct Item {
        let id: String
        var title: String
        let icon: NSImage
        let tool: Bool
        let separatorBefore: Bool
        var tooltip: String
        var checked = false
        var enabled = true
        let action: (DockView, CGRect) -> Void

        init(id: String, title: String, icon: NSImage, tool: Bool = false, separatorBefore: Bool = false,
             tooltip: String = "", action: @escaping (DockView, CGRect) -> Void) {
            self.id = id
            self.title = title
            self.icon = icon
            self.tool = tool
            self.separatorBefore = separatorBefore
            self.tooltip = tooltip
            self.action = action
        }
    }

    var items: [Item] = [] { didSet { relayout() } }
    var compact = false                 // na užšej obrazovke len ikonky (názov je v bublinke)
    let margin: CGFloat = 16            // okraj na tieň okolo panela
    private let font = NSFont.systemFont(ofSize: 10.5)
    private var rects: [CGRect] = []
    private var separators: [CGFloat] = []
    private var hovered: Int?
    private var pressed: Int?
    private var tips: [NSString] = []          // bubliny: vlastník je samotný text (AppKit ho len prečíta)

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    private func width(_ item: Item) -> CGFloat {
        if compact { return 40 }
        return max(item.id == "snap" ? 76 : 50, ceil(textSize(item.title, font).width) + 12)
    }

    private var itemHeight: CGFloat { compact ? 40 : 50 }

    func panelSize() -> CGSize {
        let seps = CGFloat(items.filter(\.separatorBefore).count) * 11
        let w = items.map(width).reduce(0, +) + CGFloat(max(0, items.count - 1)) * 2 + seps + 16
        return CGSize(width: w, height: itemHeight + 12)
    }

    func relayout() {
        rects = []
        separators = []
        var x = margin + 8
        for (i, item) in items.enumerated() {
            if item.separatorBefore {
                separators.append(x + 4)
                x += 11
            }
            let w = width(item)
            rects.append(CGRect(x: x, y: margin + 6, width: w, height: itemHeight))
            x += w + (i < items.count - 1 ? 2 : 0)
        }
        removeAllToolTips()
        tips = items.map { it in
            let text = it.tooltip.isEmpty ? it.title
                : (compact && !it.tooltip.hasPrefix(it.title) ? "\(it.title) — \(it.tooltip)" : it.tooltip)
            return text as NSString
        }
        for (r, tip) in zip(rects, tips) { addToolTip(r, owner: tip, userData: nil) }
        needsDisplay = true
    }

    func check(tool id: String) {
        for i in items.indices where items[i].tool { items[i].checked = items[i].id == id }
        needsDisplay = true
    }

    func setChecked(_ id: String, _ on: Bool) {
        if let i = items.firstIndex(where: { $0.id == id }) { items[i].checked = on }
    }

    func update(_ id: String, title: String? = nil, enabled: Bool? = nil, tooltip: String? = nil) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        if let title { items[i].title = title }
        if let enabled { items[i].enabled = enabled }
        if let tooltip { items[i].tooltip = tooltip }
    }

    override func draw(_ dirtyRect: NSRect) {
        let panel = bounds.insetBy(dx: margin, dy: margin)
        drawPanel(panel, radius: 18)
        for x in separators {
            PANEL_EDGE.setFill()
            NSRect(x: x, y: panel.minY + 12, width: 1, height: panel.height - 24).fill()
        }
        for (i, item) in items.enumerated() where i < rects.count {
            let r = rects[i]
            let highlight: NSColor? = item.checked ? tint(ACCENT, 62)
                : (item.enabled && (hovered == i || pressed == i)) ? tint(ACCENT, pressed == i ? 45 : 28) : nil
            if let highlight {
                highlight.setFill()
                NSBezierPath(roundedRect: r, xRadius: 12, yRadius: 12).fill()
            }
            let iconY = compact ? r.midY - 13 : r.minY + 5
            item.icon.draw(in: CGRect(x: r.midX - 13, y: iconY, width: 26, height: 26), from: .zero,
                           operation: .sourceOver, fraction: item.enabled ? 1 : 0.4, respectFlipped: true, hints: nil)
            if !compact {
                let color = !item.enabled ? rgb(160, 160, 170) : item.checked ? rgb(13, 94, 136) : PANEL_TEXT
                textInRect(item.title, CGRect(x: r.minX, y: r.minY + 32, width: r.width, height: 14), font: font, color: color)
            }
        }
    }

    private func index(at p: CGPoint) -> Int? { rects.firstIndex { $0.contains(p) } }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }

    override func mouseMoved(with e: NSEvent) {
        let i = index(at: convert(e.locationInWindow, from: nil))
        if i != hovered {
            hovered = i
            needsDisplay = true
        }
        NSCursor.arrow.set()
    }

    override func mouseExited(with e: NSEvent) {
        hovered = nil
        needsDisplay = true
    }

    override func mouseDown(with e: NSEvent) {
        if let i = index(at: convert(e.locationInWindow, from: nil)), items[i].enabled {
            pressed = i
            needsDisplay = true
        }
    }

    override func mouseUp(with e: NSEvent) {
        defer {
            pressed = nil
            needsDisplay = true
        }
        guard let p = pressed, index(at: convert(e.locationInWindow, from: nil)) == p else { return }
        items[p].action(self, rects[p])
    }
}

/// Malé tlačidlo dole, keď je panel skrytý.
@MainActor
final class ShowDockView: NSView {
    var title = "" { didSet { needsDisplay = true } }
    var onClick: () -> Void = {}
    private let font = NSFont.systemFont(ofSize: 12)

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func idealSize() -> CGSize {
        let s = textSize(title, font)
        return CGSize(width: ceil(s.width) + 28, height: ceil(s.height) + 10)
    }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds.insetBy(dx: 0.5, dy: 0.5)
        PANEL_BG.setFill()
        NSBezierPath(roundedRect: r, xRadius: 12, yRadius: 12).fill()
        PANEL_EDGE.setStroke()
        NSBezierPath(roundedRect: r, xRadius: 12, yRadius: 12).stroke()
        textInRect(title, bounds, font: font, color: PANEL_TEXT)
    }

    override func mouseUp(with e: NSEvent) { onClick() }
}

// MARK: - políčka na zadanie čísel

/// Políčka na presné zadanie z klávesnice: číslo, Enter, ďalšie číslo, Enter.
@MainActor
final class InputBar: NSView, NSTextFieldDelegate {
    unowned var canvas: Canvas!
    let margin: CGFloat = 14
    private var widgets: [NSView] = []
    private var fields: [NSTextField] = []
    private let help = NSTextField(labelWithString: "")

    override init(frame: NSRect) {
        super.init(frame: frame)
        isHidden = true
        help.font = .systemFont(ofSize: 11)
        addSubview(help)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) nie je podporovaný") }

    override var isFlipped: Bool { true }

    func open(_ text: String) {
        let spec = canvas.tool.inputFields
        guard !spec.isEmpty else { return }
        build(spec)
        fields[0].stringValue = toDigits(text)
        say(spec.count > 1 ? L("Enter = ďalšie políčko, potom použiť · Esc = zrušiť", "Enter = next field, then apply · Esc = cancel")
                           : L("Enter = použiť · Esc = zrušiť", "Enter = apply · Esc = cancel"))
        isHidden = false
        canvas.layoutDock()
        window?.makeFirstResponder(fields[0])
        if let editor = fields[0].currentEditor() {         // kurzor za prvú číslicu, nie výber celého textu
            editor.selectedRange = NSRange(location: (fields[0].stringValue as NSString).length, length: 0)
        }
        canvas.needsDisplay = true
    }

    /// Len pre --snapshot: vyplní políčka bez písania.
    func prefill(_ values: [String]) {
        for (f, v) in zip(fields, values) { f.stringValue = v }
    }

    private func build(_ spec: [Field]) {
        widgets.forEach { $0.removeFromSuperview() }
        widgets = []
        fields = []
        for f in spec {
            let label = NSTextField(labelWithString: f.name)
            label.font = .systemFont(ofSize: 13)
            label.textColor = PANEL_TEXT
            let field = NSTextField(string: "")
            field.font = .systemFont(ofSize: 15)
            field.bezelStyle = .roundedBezel
            field.placeholderString = f.optional ? L("nepovinné", "optional") : nil
            field.delegate = self
            let unit = NSTextField(labelWithString: f.unit)
            unit.font = .systemFont(ofSize: 13)
            unit.textColor = rgb(90, 90, 102)
            for v in [label, field, unit] {
                addSubview(v)
                widgets.append(v)
            }
            fields.append(field)
        }
    }

    func idealSize() -> CGSize {
        let mid = margin + 22
        var x = margin + 16
        for (i, v) in widgets.enumerated() {
            if let field = v as? NSTextField, fields.contains(field) {
                field.frame = CGRect(x: x, y: mid - 13, width: 110, height: 26)
                x += 118
            } else {
                (v as? NSControl)?.sizeToFit()
                v.frame.origin = CGPoint(x: x, y: mid - v.frame.height / 2)
                x += v.frame.width + (i % 3 == 2 ? 18 : 8)
            }
        }
        help.sizeToFit()
        help.frame.origin = CGPoint(x: x, y: mid - help.frame.height / 2)
        x += help.frame.width + 16
        return CGSize(width: x + margin, height: 44 + 2 * margin)
    }

    override func draw(_ dirtyRect: NSRect) { drawPanel(bounds.insetBy(dx: margin, dy: margin), radius: 16) }

    @discardableResult
    private func go(_ i: Int) -> Bool {
        guard fields.indices.contains(i) else { return false }
        window?.makeFirstResponder(fields[i])
        return true
    }

    private func nextOrApply(_ i: Int) {
        let spec = canvas.tool.inputFields
        if !spec[i].optional && parseNumbers(fields[i].stringValue).isEmpty {
            say("\(L("Vyplň", "Fill in")): \(spec[i].name)", error: true)     // z prázdneho povinného políčka sa nejde ďalej
            return
        }
        if !go(i + 1) { apply() }
    }

    private func say(_ text: String, error: Bool = false) {
        help.stringValue = text
        help.textColor = error ? rgb(210, 58, 72) : rgb(122, 122, 134)
        help.font = error ? .boldSystemFont(ofSize: 11) : .systemFont(ofSize: 11)
        canvas.layoutDock()
    }

    func closeBar() {
        isHidden = true
        window?.makeFirstResponder(canvas)
        canvas.needsDisplay = true
    }

    private func apply() {
        var nums: [Double] = []
        for (i, (field, spec)) in zip(fields, canvas.tool.inputFields).enumerated() {
            let values = parseNumbers(field.stringValue)
            if values.isEmpty && !spec.optional {
                say("\(L("Vyplň", "Fill in")): \(spec.name)", error: true)
                go(i)
                return
            }
            nums += values
        }
        if let error = canvas.tool.applyInput(nums) {
            say(error, error: true)
            return
        }
        closeBar()
    }

    func controlTextDidChange(_ n: Notification) {
        guard let field = n.object as? NSTextField, let i = fields.firstIndex(of: field) else { return }
        let separators: Set<Character> = [" ", "x", "X", "×", "*"]
        let typed = field.stringValue
        let clean = toDigits(String(typed.filter { !separators.contains($0) }))     // ľščť… → číslice
        if clean != typed { field.stringValue = clean }
        if typed.contains(where: { separators.contains($0) }) { go(i + 1) }        // medzera alebo x = ďalšie políčko
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        guard let field = control as? NSTextField, let i = fields.firstIndex(of: field) else { return false }
        switch selector {
        case #selector(NSResponder.insertNewline(_:)):
            nextOrApply(i)
            return true
        case #selector(NSResponder.cancelOperation(_:)):
            closeBar()
            return true
        case #selector(NSResponder.deleteBackward(_:)) where textView.string.isEmpty:
            return go(i - 1)
        default:
            return false
        }
    }
}

// MARK: - kalibrácia kartou

@MainActor
final class CardView: NSView {
    var ppm: CGFloat = 4 { didSet { needsDisplay = true } }
    let maxPPM: CGFloat
    private let pad: CGFloat = 12

    init(maxPPM: CGFloat) {
        self.maxPPM = maxPPM
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) nie je podporovaný") }

    override var isFlipped: Bool { true }
    override var intrinsicContentSize: NSSize {
        NSSize(width: (CARD_MM.width * maxPPM + 2 * pad).rounded(), height: (CARD_MM.height * maxPPM + 2 * pad).rounded())
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let card = CGRect(x: pad, y: pad, width: CARD_MM.width * ppm, height: CARD_MM.height * ppm)
        fillPath(ctx, roundedPath(card, CARD_CORNER_MM * ppm), tint(ACCENT, 40), stroke: ACCENT)
        textInRect(L("85,60 × 53,98 mm", "85.60 × 53.98 mm"), card, font: uiFont(13, bold: true), color: INK)
        for mm in 0...85 {
            let x = (pad + CGFloat(mm) * ppm).rounded()
            crispLine(ctx, x0: x, y0: pad, x1: x, y1: pad + (mm % 10 == 0 ? 12 : mm % 5 == 0 ? 8 : 4), INK)
            if mm % 10 == 0 && mm > 0 {
                drawText("\(mm / 10)", x: x - 3, baseline: pad + 25, font: uiFont(10), color: INK)
            }
        }
    }
}

/// Posuvník, ktorý šípkami ide jemne (0,1 %, so Shiftom 1 %).
@MainActor
final class KeySlider: NSSlider {
    override func keyDown(with e: NSEvent) {
        let step: Double = e.modifierFlags.contains(.shift) ? 100 : 10
        switch e.keyCode {
        case 123, 125: doubleValue -= step
        case 124, 126: doubleValue += step
        default: return super.keyDown(with: e)
        }
        sendAction(action, to: target)
    }
}

@MainActor
final class CalibrationController: NSObject, NSWindowDelegate {
    let window: NSWindow
    private let screen: NSScreen
    private let base: CGFloat
    private var ppm: CGFloat
    private let card: CardView
    private let slider = KeySlider()
    private let status = NSTextField(labelWithString: "")

    init(screen: NSScreen) {
        self.screen = screen
        base = edidPPM(screen)
        ppm = ptPerMM(screen)
        card = CardView(maxPPM: base * 1.16)
        window = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        super.init()

        let info = NSTextField(wrappingLabelWithString: L(
            "Displej \(screen.localizedName). Prilož na obrazovku kartu formátu ID-1 — platobnú kartu, občiansky, ISIC alebo kartičku poistenca. Zarovnaj ju s ľavým horným rohom obdĺžnika a posúvaj, kým nesedí aj pravý okraj. Jemne: šípky ← →.",
            "Display \(screen.localizedName). Hold an ID-1 size card to the screen — a bank card, ID card or student card. Line it up with the top-left corner of the outline and move the slider until the right edge matches too. Fine steps: arrow keys ← →."))
        info.preferredMaxLayoutWidth = card.intrinsicContentSize.width
        slider.minValue = -1500             // stotiny percenta → ±15 %
        slider.maxValue = 1500
        slider.doubleValue = ((ppm / base - 1) * 10000).rounded()
        slider.target = self
        slider.action = #selector(changed)
        slider.isContinuous = true
        status.font = .systemFont(ofSize: 12)

        let reset = NSButton(title: L("Podľa displeja (EDID)", "From display (EDID)"), target: self, action: #selector(resetToEDID))
        let cancel = NSButton(title: L("Zrušiť", "Cancel"), target: self, action: #selector(cancelled))
        cancel.keyEquivalent = "\u{1b}"
        let save = NSButton(title: L("Uložiť", "Save"), target: self, action: #selector(saved))
        save.keyEquivalent = "\r"
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let buttons = NSStackView(views: [reset, spacer, cancel, save])
        buttons.orientation = .horizontal

        let stack = NSStackView(views: [info, card, slider, status, buttons])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 18, left: 18, bottom: 18, right: 18)
        for v in [slider, buttons] as [NSView] {
            v.widthAnchor.constraint(equalToConstant: card.intrinsicContentSize.width).isActive = true
        }
        window.contentView = stack
        window.title = L("Kalibrácia mierky", "Calibrate the scale")
        window.delegate = self
        window.level = .modalPanel
        window.setContentSize(stack.fittingSize)
        changed()
    }

    @objc private func changed() {
        ppm = base * (1 + CGFloat(slider.doubleValue.rounded()) / 10000)
        card.ppm = ppm
        status.stringValue = "\(fmt(ppm, 3)) pt/mm · \(fmt(ppm * MM_PER_IN, 1)) \(L("bodov na palec", "points per inch"))"
            + "   (\(L("displej hlási", "display reports")) \(fmt(base, 3)) pt/mm)"
    }

    @objc private func resetToEDID() {
        slider.doubleValue = 0
        changed()
    }

    @objc private func saved() {
        var calib = Settings.calib
        if slider.doubleValue.rounded() == 0 {
            calib.removeValue(forKey: screenKey(screen))
        } else {
            calib[screenKey(screen)] = Double(ppm)
        }
        Settings.calib = calib
        NSApp.stopModal()
    }

    @objc private func cancelled() { NSApp.stopModal() }

    func windowWillClose(_ notification: Notification) { NSApp.stopModal() }

    func runModal() {
        let v = screen.visibleFrame
        window.setFrameOrigin(CGPoint(x: v.midX - window.frame.width / 2, y: v.midY - window.frame.height / 2))
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(slider)
        NSApp.runModal(for: window)
        window.orderOut(nil)
    }
}

// MARK: - ikonky v paneli (kreslené, 26 × 26 bodov)

func toolIcon(_ kind: String) -> NSImage {
    NSImage(size: NSSize(width: 26, height: 26), flipped: true) { _ -> Bool in
        guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
        drawIcon(kind, ctx)
        return true
    }
}

private func drawIcon(_ kind: String, _ ctx: CGContext) {
        let ink = rgb(58, 58, 70), yellow = rgb(246, 200, 62), lightBlue = rgb(196, 230, 248)
        func P(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: y) }
        func rect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> CGRect { CGRect(x: x, y: y, width: w, height: h) }
        func shape(_ path: CGPath, _ fill: NSColor?, stroke: NSColor? = nil, width: CGFloat = 1.5) {
            fillPath(ctx, path, fill, stroke: stroke ?? ink, width: width)
        }
        func line(_ a: CGPoint, _ b: CGPoint, _ color: NSColor? = nil, _ width: CGFloat = 1.5, dash: Bool = false) {
            strokeLine(ctx, a, b, color ?? ink, width: width, dash: dash)
        }
        var cmMarks: [(CGFloat, CGFloat)] = []
        for i in 0..<9 { cmMarks.append((5 + 2 * CGFloat(i), i % 5 == 0 ? 5 : 3)) }
        let inMarks: [(CGFloat, CGFloat)] = [(5, 6), (9, 2.5), (13, 4), (17, 2.5), (21, 6)]

        switch kind {
        case "cm", "in", "dual":
            shape(roundedPath(rect(2, 7, 22, 12), 2), yellow)
            for (x, ln) in kind == "in" ? inMarks : cmMarks { line(P(x, 7), P(x, 7 + ln)) }
            if kind == "dual" { for (x, ln) in inMarks { line(P(x, 19), P(x, 19 - ln * 0.7)) } }
        case "px":
            shape(roundedPath(rect(3, 4, 20, 14), 2), nil)
            line(P(13, 18), P(13, 22))
            line(P(9, 22), P(17, 22))
            for (x, y) in [(6.5, 7.5), (9.5, 7.5), (6.5, 10.5), (12.5, 10.5), (15.5, 13.5), (18.5, 13.5)] as [(CGFloat, CGFloat)] {
                fillRect(ctx, rect(x, y, 2.4, 2.4), ACCENT)
            }
        case "grid":
            shape(CGPath(rect: rect(4, 4, 18, 18), transform: nil), WHITE)
            for k in [8.5, 13, 17.5] as [CGFloat] {
                line(P(k, 4), P(k, 22), ink, 0.7)
                line(P(4, k), P(22, k), ink, 0.7)
            }
        case "prot":
            let c = P(13, 19)
            shape(polygonPath((0...36).map { (i: Int) -> CGPoint in polar(c, CGFloat(i) * 5, 11) }), lightBlue)
            line(c, polar(c, 55, 10), MARK, 1.8)
        case "prot360":
            shape(circlePath(P(13, 13), 10), lightBlue)
            line(P(13, 13), P(22, 13), ACCENT, 1.8)
            line(P(13, 13), polar(P(13, 13), 130, 9), MARK, 1.8)
        case "circle":
            for r in [3.5, 7, 10.5] as [CGFloat] { shape(circlePath(P(13, 13), r), nil) }
            fillPath(ctx, circlePath(P(13, 13), 1), ink)
        case "radius":
            let c = P(6, 21)
            shape(polygonPath((0...18).map { (i: Int) -> CGPoint in polar(c, CGFloat(i) * 5, 16) }, close: false), nil)
            line(c, polar(c, 45, 16), ink, 1, dash: true)
            for a in [8, 45, 82] as [CGFloat] { fillPath(ctx, circlePath(polar(c, a, 16), 2.2), MARK) }
        case "diag":
            shape(roundedPath(rect(7, 2, 12, 22), 2.5), lightBlue)
            line(P(9, 4.5), P(17, 21.5), MARK, 1.8)
        case "path":
            let pts = [P(3, 20), P(9, 9), P(15, 17), P(23, 5)]
            shape(polygonPath(pts, close: false), nil)
            for q in pts { fillPath(ctx, circlePath(q, 2.2), MARK) }
        case "screw":
            let metal = rgb(200, 200, 208)
            shape(roundedPath(rect(2.5, 8, 6, 10), 1.5), metal)
            shape(CGPath(rect: rect(8.5, 10.5, 15, 5), transform: nil), metal)
            var x: CGFloat = 10.5
            while x < 23 {
                line(P(x, 10.5), P(x + 1.5, 15.5), ink, 1)
                x += 2.5
            }
        case "pitch":
            for x in [6.5, 10.5, 14.5, 18.5] as [CGFloat] {
                fillRect(ctx, rect(x - 0.9, 3.5, 1.8, 5), rgb(150, 150, 160))
                fillRect(ctx, rect(x - 0.9, 17.5, 1.8, 5), rgb(150, 150, 160))
            }
            fillPath(ctx, roundedPath(rect(4, 8, 18, 10), 1.5), rgb(60, 60, 72))
            fillPath(ctx, circlePath(P(7, 11), 1), WHITE)
        case "snap":
            for x in [5, 13, 21] as [CGFloat] {
                for y in [5, 13, 21] as [CGFloat] { fillPath(ctx, circlePath(P(x, y), 1.4), rgb(150, 150, 160)) }
            }
            line(P(13, 7), P(13, 19), ACCENT, 1.6)
            line(P(7, 13), P(19, 13), ACCENT, 1.6)
            fillPath(ctx, circlePath(P(13, 13), 2.6), MARK)
        case "type":
            shape(roundedPath(rect(2, 6, 22, 14), 2.5), WHITE)
            for y in [9, 12.5] as [CGFloat] {
                for k in 0..<5 { fillRect(ctx, rect(4.6 + CGFloat(k) * 3.6, y, 2.2, 2), ink) }
            }
            fillRect(ctx, rect(8, 16, 10, 1.8), ink)
        case "bg":
            shape(circlePath(P(13, 13), 9), nil)
            let half: [CGPoint] = (0...36).map { (i: Int) -> CGPoint in polar(P(13, 13), 90 + CGFloat(i) * 5, 9) }
            fillPath(ctx, polygonPath([P(13, 13)] + half), ink)
        case "calib":
            shape(roundedPath(rect(3, 7, 20, 13), 2), lightBlue)
            line(P(3.5, 11), P(22.5, 11), ink, 2.2)
        case "window":
            shape(roundedPath(rect(3, 5, 20, 16), 2), WHITE)
            line(P(3, 9), P(23, 9))
            line(P(15, 18.5), P(20.5, 13), ACCENT, 1.4)
            line(P(18, 18.5), P(20.5, 16), ACCENT, 1.4)
        case "hide":
            shape(polygonPath([P(7, 10), P(13, 16), P(19, 10)], close: false), nil)
        case "close":
            line(P(8, 8), P(18, 18))
            line(P(18, 8), P(8, 18))
        default:
            break
        }
}
