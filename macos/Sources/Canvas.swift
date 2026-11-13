// Canvas.swift — hlavná plocha: kreslenie (statická vrstva v cache + dynamická), myš, klávesnica, krok.
import AppKit

@MainActor
final class Canvas: NSView {
    private(set) var tools: [String: Tool] = [:]
    private(set) var toolName = "cm"
    var tool: Tool { tools[toolName]! }
    var bg = "light"
    var hover: CGPoint?         // poloha kurzora po prichytení na krok
    var raw: CGPoint?           // skutočná poloha kurzora (na hľadanie bodov a úchytov)
    private(set) var snapLevel = 0
    private var cache: (key: String, image: NSImage)?
    private var lastPPM: CGFloat?

    let dock = DockView()
    let showDock = ShowDockView()
    let inputBar = InputBar()
    var onToggleFullscreen: () -> Void = {}
    private var panelTop: CGFloat = 0           // horný okraj panela (nad ním je riadok s nápovedou)

    override init(frame: NSRect) {
        super.init(frame: frame)
        tools = [
            "cm": RulerTool(self, unit: "cm"), "in": RulerTool(self, unit: "in"), "px": RulerTool(self, unit: "px"),
            "dual": RulerTool(self, unit: "dual"), "grid": RulerTool(self, unit: "cm", grid: true),
            "prot": ProtractorTool(self, full: false), "prot360": ProtractorTool(self, full: true),
            "circle": CircleTool(self), "radius": RadiusTool(self),
            "diag": DiagonalTool(self), "path": PathTool(self),
            "screw": ScrewTool(self), "pitch": PitchTool(self),
        ]
        bg = THEMES[Settings.bg] != nil ? Settings.bg : "light"
        snapLevel = (0...3).contains(Settings.snap) ? Settings.snap : 0
        buildDock()
        showDock.onClick = { [unowned self] in self.toggleDock() }
        showDock.isHidden = true
        inputBar.canvas = self
        for v in [dock, showDock, inputBar] as [NSView] { addSubview(v) }
        setTool(Settings.tool)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) nie je podporovaný") }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    var currentScreen: NSScreen { window?.screen ?? NSScreen.main ?? NSScreen.screens[0] }
    var ppm: CGFloat { ptPerMM(currentScreen) }
    var pxScale: CGFloat { window?.backingScaleFactor ?? currentScreen.backingScaleFactor }

    // MARK: - panel

    private func buildDock() {
        var items: [DockView.Item] = []
        for (key, title) in TOOLS {
            items.append(.init(id: key, title: title, icon: toolIcon(key), tool: true, separatorBefore: GROUP_STARTS.contains(key),
                               tooltip: "\(title)  (Tab / Shift+Tab = \(L("ďalší / predošlý", "next / previous")))") { [unowned self] _, _ in
                self.setTool(key)
            })
        }
        items.append(.init(id: "snap", title: L("Voľne", "Free"), icon: toolIcon("snap"), separatorBefore: true) { [unowned self] dock, rect in
            self.snapMenu().popUp(positioning: nil, at: CGPoint(x: rect.minX, y: rect.minY - 4), in: dock)
        })
        items.append(.init(id: "type", title: L("Zadať", "Type"), icon: toolIcon("type")) { [unowned self] _, _ in
            self.inputBar.open("")
        })
        items.append(.init(id: "bg", title: BG_NAMES[bg] ?? "", icon: toolIcon("bg"),
                           tooltip: L("Pozadie: svetlé / tmavé / zelené / priehľadné (B)", "Background: light / dark / green / clear (B)")) { [unowned self] _, _ in
            self.cycleBg()
        })
        items.append(.init(id: "calib", title: L("Kalibrácia", "Calibrate"), icon: toolIcon("calib"),
                           tooltip: L("Dokalibrovať mierku kartou (K)", "Calibrate the scale with a card (K)")) { [unowned self] _, _ in
            self.calibrate()
        })
        items.append(.init(id: "window", title: L("Okno", "Window"), icon: toolIcon("window"),
                           tooltip: L("Prepnúť okno ↔ celá obrazovka (⌃⌘F) — okno sa dá zväčšovať a zmenšovať",
                                      "Switch window ↔ fullscreen (⌃⌘F) — the window is resizable")) { [unowned self] _, _ in
            self.onToggleFullscreen()
        })
        items.append(.init(id: "hide", title: L("Skryť", "Hide"), icon: toolIcon("hide"), tooltip: L("Skryť panel (H)", "Hide the panel (H)")) {
            [unowned self] _, _ in self.toggleDock()
        })
        items.append(.init(id: "close", title: L("Zavrieť", "Close"), icon: toolIcon("close"), tooltip: L("Zavrieť (Q)", "Close (Q)")) { _, _ in
            NSApp.terminate(nil)
        })
        dock.items = items
        showDock.title = L("▴  Pravítka", "▴  Tools")
    }

    private func snapMenu() -> NSMenu {
        let menu = NSMenu()
        guard let family = snapFamily() else { return menu }
        let options = [L("Voľne (bez kroku)", "Free (no snapping)")] + SNAP[family]!.map { "\(L("Krok", "Snap")) \($0.1)" }
        for (level, title) in options.enumerated() {
            menu.addItem(ActionItem(title, checked: level == snapLevel) { [unowned self] in self.setSnap(level) })
        }
        menu.addItem(.separator())
        let note = NSMenuItem(title: L("S = prepínanie", "S = cycle"), action: nil, keyEquivalent: "")
        note.isEnabled = false
        menu.addItem(note)
        return menu
    }

    /// Najmenšie okno: panel len s ikonkami musí ostať celý.
    func minimumSize() -> CGSize {
        let compact = dock.compact
        dock.compact = true
        let w = dock.panelSize().width
        dock.compact = compact
        return CGSize(width: w + 32, height: 520)
    }

    func layoutDock() {
        let W = bounds.width, H = bounds.height
        dock.compact = false
        if dock.panelSize().width > W - 32 { dock.compact = true }
        let ps = dock.panelSize()
        let panel = CGRect(x: ((W - ps.width) / 2).rounded(), y: H - ps.height - 14, width: ps.width, height: ps.height)
        dock.frame = panel.insetBy(dx: -dock.margin, dy: -dock.margin)
        dock.relayout()
        panelTop = panel.minY
        let ss = showDock.idealSize()
        showDock.frame = CGRect(x: ((W - ss.width) / 2).rounded(), y: H - ss.height - 10, width: ss.width, height: ss.height)
        let ib = inputBar.idealSize()
        let above = dock.isHidden ? showDock.frame.minY : panel.minY
        inputBar.frame = CGRect(x: ((W - ib.width) / 2).rounded(), y: above - ib.height + inputBar.margin - 12,
                                width: ib.width, height: ib.height)
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        layoutDock()
        for t in tools.values { t.relayout() }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        lastPPM = window == nil ? nil : ppm
    }

    /// Presun na inú obrazovku: merania ostanú v rovnakých mm, nie v rovnakých bodoch.
    func screenChanged() {
        let new = ppm
        if let old = lastPPM, abs(new / old - 1) > 1e-6 {
            for t in tools.values { t.rescale(new / old) }
            hover = nil
            raw = nil
        }
        lastPPM = new
        needsDisplay = true
    }

    // MARK: - stav

    func setTool(_ name: String) {
        toolName = tools[name] != nil ? name : "cm"
        Settings.tool = toolName
        inputBar.isHidden = true
        dock.check(tool: toolName)
        dock.update("type", enabled: !tool.inputFields.isEmpty,
                    tooltip: tool.inputFields.isEmpty ? L("Tento nástroj nemá čo zadať", "Nothing to type for this tool")
                        : L("Zadať presný rozmer z klávesnice (alebo rovno píš čísla)", "Type an exact size (or just start typing numbers)"))
        updateSnapButton()
        needsDisplay = true
    }

    func stepTool(_ delta: Int) {
        let keys = TOOLS.map { $0.0 }
        let i = keys.firstIndex(of: toolName) ?? 0
        setTool(keys[(i + delta + keys.count) % keys.count])
    }

    func resetTool() {
        if !inputBar.isHidden {
            inputBar.closeBar()
            return
        }
        tool.reset()
        needsDisplay = true
    }

    func setBackground(_ name: String) {
        bg = THEMES[name] != nil ? name : "light"
        Settings.bg = bg
        dock.update("bg", title: BG_NAMES[bg])
        layoutDock()
        needsDisplay = true
    }

    func cycleBg() {
        let i = THEME_ORDER.firstIndex(of: bg) ?? 0
        setBackground(THEME_ORDER[(i + 1) % THEME_ORDER.count])
    }

    func toggleDock() {
        dock.isHidden.toggle()
        showDock.isHidden = !dock.isHidden
        layoutDock()
        needsDisplay = true
    }

    func calibrate() {
        CalibrationController(screen: currentScreen).runModal()
        lastPPM = ppm                // kalibrácia meria to isté miesto znova — body sa neprepočítavajú
        needsDisplay = true
        window?.makeFirstResponder(self)
    }

    // MARK: - krok (prichytávanie)

    func snapFamily() -> String? {
        switch tool.snapKind {
        case .angle: return "deg"
        case .point: return ["in", "px"].contains(tool.unit) ? tool.unit : "mm"
        case nil: return nil
        }
    }

    /// Krok v jednotke nástroja (mm, palce, px, stupne) alebo nil = voľne.
    func snapValue() -> Double? {
        guard let family = snapFamily(), snapLevel > 0 else { return nil }
        return SNAP[family]![snapLevel - 1].0
    }

    /// Krok v bodoch obrazovky (len pre body, nie uhly).
    private func snapPt() -> CGFloat? {
        guard let value = snapValue(), let family = snapFamily(), family != "deg" else { return nil }
        if family == "px" { return CGFloat(value) / pxScale }
        return CGFloat(value) * ppm * (family == "in" ? MM_PER_IN : 1)
    }

    func toolPos(_ raw: CGPoint) -> CGPoint {
        guard tool.snapKind == .point, let s = snapPt() else { return raw }
        return CGPoint(x: (raw.x / s).rounded() * s, y: (raw.y / s).rounded() * s)
    }

    func setSnap(_ level: Int) {
        snapLevel = level
        Settings.snap = level
        updateSnapButton()
        needsDisplay = true
    }

    func cycleSnap() {
        if snapFamily() != nil { setSnap((snapLevel + 1) % 4) }
    }

    private func updateSnapButton() {
        if let family = snapFamily() {
            dock.update("snap", title: snapLevel > 0 ? "\(L("Krok", "Snap")) \(SNAP[family]![snapLevel - 1].1)" : L("Voľne", "Free"),
                        enabled: true, tooltip: L("Krok pohybu — body skáču na presné hodnoty, napr. 20 × 50 mm (S)",
                                                  "Snapping — points jump to exact values, e.g. 20 × 50 mm (S)"))
        } else {
            dock.update("snap", title: L("Krok –", "Snap –"), enabled: false,
                        tooltip: L("Tento nástroj krok nepoužíva", "This tool doesn't snap"))
        }
        layoutDock()
    }

    // MARK: - zadávanie z klávesnice

    /// Odkiaľ začne zadaný rozmer: od kurzora, inak z ľavej hornej tretiny.
    func inputOrigin() -> CGPoint {
        var p = raw ?? CGPoint(x: -1, y: -1)
        if !(p.x >= 0 && p.x < bounds.width && p.y >= 0 && p.y < bounds.height - DOCK_RESERVE) {
            p = CGPoint(x: (bounds.width * 0.3).rounded(), y: (bounds.height * 0.3).rounded())
        }
        return toolPos(p)
    }

    /// Posun, aby sa zadaný rozmer zmestil do okna (ak sa dá).
    func fitShift(_ pts: [CGPoint]) -> CGPoint {
        let W = bounds.width - 20, H = bounds.height - DOCK_RESERVE - 20
        let xs = pts.map(\.x), ys = pts.map(\.y)
        let dx = xs.max()! > W ? W - xs.max()! : max(0, 60 - xs.min()!)
        let dy = ys.max()! > H ? H - ys.max()! : max(0, 60 - ys.min()!)
        return CGPoint(x: dx, y: dy)
    }

    // MARK: - myš

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }

    /// Zapamätá skutočný kurzor a vráti polohu po prichytení na krok.
    private func track(_ e: NSEvent) -> CGPoint {
        let p = convert(e.locationInWindow, from: nil)
        let r = CGPoint(x: p.x.rounded(), y: p.y.rounded())
        raw = r
        return toolPos(r)
    }

    private func overPanels(_ p: CGPoint) -> Bool {
        for v in [dock, inputBar, showDock] as [NSView] where !v.isHidden && v.frame.contains(p) { return true }
        return false
    }

    override func mouseMoved(with e: NSEvent) {
        let pos = track(e)
        if let r = raw, overPanels(r) {       // nitkový kríž nemá ostať visieť pod panelom
            hover = nil
            raw = nil
            NSCursor.arrow.set()
        } else {
            hover = pos
            tool.cursor(raw ?? pos).set()
        }
        needsDisplay = true
    }

    override func mouseExited(with e: NSEvent) {
        hover = nil
        raw = nil
        needsDisplay = true
    }

    override func mouseDown(with e: NSEvent) {
        if !inputBar.isHidden { inputBar.closeBar() }
        window?.makeFirstResponder(self)
        let pos = track(e)
        if e.clickCount >= 2 { tool.double(pos, e.modifierFlags) } else { tool.press(pos, e.modifierFlags) }
        needsDisplay = true
    }

    override func mouseDragged(with e: NSEvent) {
        let pos = track(e)
        hover = pos
        tool.drag(pos, e.modifierFlags)
        needsDisplay = true
    }

    override func mouseUp(with e: NSEvent) {
        let pos = track(e)
        tool.release(pos)
        tool.cursor(raw ?? pos).set()
        needsDisplay = true
    }

    override func scrollWheel(with e: NSEvent) {
        let steps = e.hasPreciseScrollingDeltas ? e.scrollingDeltaY / 20 : e.scrollingDeltaY
        if steps != 0 {
            tool.wheel(steps)
            needsDisplay = true
        }
    }

    // MARK: - klávesnica

    override func keyDown(with e: NSEvent) {
        let mods = e.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if mods.contains(.command) || mods.contains(.control) {
            super.keyDown(with: e)
            return
        }
        switch e.keyCode {
        case 48:                                    // Tab
            stepTool(mods.contains(.shift) ? -1 : 1)
            return
        case 53:                                    // Esc
            resetTool()
            return
        case 36, 76:                                // Enter
            if tool.key(.enter) {
                needsDisplay = true
            } else if !tool.inputFields.isEmpty {
                inputBar.open("")
            }
            return
        case 51, 117:                               // Backspace, Delete
            if tool.key(.backspace) { needsDisplay = true }
            return
        case 103:                                   // F11
            onToggleFullscreen()
            return
        case 123...126:                             // šípky: o 0,1 mm (pri pixeloch o 1 px), so Shiftom 10×
            var step = tool.unit == "px" && tool.snapKind == .point ? 1 / pxScale : 0.1 * ppm
            if mods.contains(.shift) { step *= 10 }
            let d = [123: CGPoint(x: -step, y: 0), 124: CGPoint(x: step, y: 0),
                     125: CGPoint(x: 0, y: step), 126: CGPoint(x: 0, y: -step)][Int(e.keyCode)]!
            tool.nudge(d)
            needsDisplay = true
            return
        default:
            break
        }
        let typed = e.characters ?? ""
        if !tool.inputFields.isEmpty && isDigitKey(typed) {
            inputBar.open(typed)
            return
        }
        switch (e.charactersIgnoringModifiers ?? "").lowercased() {
        case "h": toggleDock()
        case "b": cycleBg()
        case "k": calibrate()
        case "s": cycleSnap()
        case "q": NSApp.terminate(nil)
        default: super.keyDown(with: e)
        }
    }

    // MARK: - kreslenie

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let W = bounds.width, H = bounds.height
        let th = THEMES[bg]!, ppm = self.ppm, px = pxScale
        tool.layout(W, H)
        let key = "\(toolName)|\(W)x\(H)|\(ppm)|\(bg)|\(px)|\(tool.staticKey())"
        if cache?.key != key {      // statická vrstva sa kreslí len pri zmene, nie pri každom pohybe myši
            cache = (key, renderStatic(W, H, th, ppm, px))
        }
        cache!.image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        tool.paintDynamic(G(ctx: ctx, W: W, H: H, th: th, ppm: ppm, px: px))

        if !dock.isHidden && inputBar.isHidden && !tool.hint.isEmpty {
            let font = uiFont(12)
            let w = textSize(tool.hint, font).width + 28
            let r = CGRect(x: ((W - w) / 2).rounded(), y: panelTop - 34, width: w, height: 24)
            fillPath(ctx, roundedPath(r, 12), tint(th.panel, 235))
            textInRect(tool.hint, r, font: font, color: th.soft)
        }
    }

    private func renderStatic(_ W: CGFloat, _ H: CGFloat, _ th: Theme, _ ppm: CGFloat, _ px: CGFloat) -> NSImage {
        let pw = max(1, Int((W * px).rounded())), ph = max(1, Int((H * px).rounded()))
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let bctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                   bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return NSImage(size: NSSize(width: W, height: H))
        }
        bctx.scaleBy(x: px, y: px)
        bctx.translateBy(x: 0, y: H)           // súradnice zhora dole ako vo view
        bctx.scaleBy(x: 1, y: -1)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: bctx, flipped: true)
        fillRect(bctx, CGRect(x: 0, y: 0, width: W, height: H), th.bg)
        tool.paintStatic(G(ctx: bctx, W: W, H: H, th: th, ppm: ppm, px: px))
        NSGraphicsContext.restoreGraphicsState()
        guard let image = bctx.makeImage() else { return NSImage(size: NSSize(width: W, height: H)) }
        return NSImage(cgImage: image, size: NSSize(width: W, height: H))
    }
}
