// App.swift — Regula pre macOS: okno (celá obrazovka alebo okno), menu, jazyk a vývojové snímky.
//
//   Regula                              normálne spustenie
//   Regula --snapshot out.png [voľby]   vykreslí plochu do PNG a skončí (overovanie cez SSH, bez okna)
//       --tool cm|in|px|dual|grid|prot|prot360|circle|radius|diag|path|screw|pitch
//       --bg light|dark|green|clear   --snap 0–3   --demo (ukážkový stav)   --typing (otvorené políčka)
//       --size 1280x800   --lang en|sk   --calib (namiesto plochy okno kalibrácie)
import AppKit

@main
enum Main {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(CommandLine.arguments.contains("--snapshot") ? .accessory : .regular)
        withExtendedLifetime(delegate) { app.run() }
    }
}

/// Bezrámové okno musí vedieť prijímať klávesy.
final class RegulaWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var window: RegulaWindow!
    private var canvas: Canvas!
    private var fullscreen = true

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--snapshot") {
            Snapshot.run()
            return
        }
        buildMenu()
        let screen = screenUnderMouse()
        window = RegulaWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false                 // kvôli priehľadnému pozadiu; plochu kreslí Canvas
        window.backgroundColor = .clear
        window.isReleasedWhenClosed = false
        window.title = "Regula"
        window.delegate = self
        window.acceptsMouseMovedEvents = true
        canvas = Canvas(frame: CGRect(origin: .zero, size: screen.frame.size))
        canvas.onToggleFullscreen = { [unowned self] in self.toggleFullscreen() }
        window.contentView = canvas
        window.contentMinSize = canvas.minimumSize()
        setFullscreen(Settings.fullscreen, screen: screen)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(canvas)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationDidBecomeActive(_ notification: Notification) {
        if fullscreen && window != nil { NSApp.presentationOptions = [.hideDock, .hideMenuBar] }
    }

    private func screenUnderMouse() -> NSScreen {
        NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
    }

    // MARK: - celá obrazovka ↔ okno

    /// Celá obrazovka = bezrámové okno cez displej so skrytým Dockom a lištou (nie samostatná plocha macOS,
    /// tá by mala čierne pozadie a priehľadné pozadie by nefungovalo).
    private func setFullscreen(_ on: Bool, screen: NSScreen? = nil) {
        let s = screen ?? window.screen ?? screenUnderMouse()
        if on {
            if window.styleMask.contains(.titled) { Settings.windowFrame = NSStringFromRect(window.frame) }
            window.styleMask = [.borderless]
            window.hasShadow = false
            window.setFrame(s.frame, display: true)
            NSApp.presentationOptions = [.hideDock, .hideMenuBar]
        } else {
            NSApp.presentationOptions = []
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.hasShadow = true
            var frame = Settings.windowFrame.map(NSRectFromString) ?? .zero
            if frame.width < 200 || !NSScreen.screens.contains(where: { $0.visibleFrame.intersects(frame) }) {
                let v = s.visibleFrame, min = window.contentMinSize
                let w = Swift.min(v.width, Swift.max(min.width, v.width * 0.85))
                let h = Swift.min(v.height, Swift.max(min.height + 28, v.height * 0.85))
                frame = CGRect(x: v.midX - w / 2, y: v.midY - h / 2, width: w, height: h)
            }
            window.setFrame(frame, display: true)
        }
        fullscreen = on
        Settings.fullscreen = on
        canvas.dock.setChecked("window", !on)
        window.makeFirstResponder(canvas)
    }

    private func toggleFullscreen() { setFullscreen(!fullscreen) }

    @objc private func menuToggleFullscreen(_ sender: Any?) { toggleFullscreen() }

    /// Na ďalší monitor (celá obrazovka sa nedá potiahnuť myšou).
    @objc private func moveToNextDisplay(_ sender: Any?) {
        let screens = NSScreen.screens
        guard screens.count > 1, let current = window.screen, let i = screens.firstIndex(of: current) else { return }
        let next = screens[(i + 1) % screens.count]
        if fullscreen {
            window.setFrame(next.frame, display: true)
        } else {
            let v = next.visibleFrame, f = window.frame
            window.setFrame(CGRect(x: v.midX - f.width / 2, y: v.midY - f.height / 2,
                                   width: Swift.min(f.width, v.width), height: Swift.min(f.height, v.height)), display: true)
        }
    }

    func windowDidChangeScreen(_ notification: Notification) {
        canvas.screenChanged()
        if fullscreen, let s = window.screen, window.frame != s.frame { window.setFrame(s.frame, display: true) }
    }

    func windowDidResize(_ notification: Notification) {
        if !fullscreen && window.styleMask.contains(.titled) { Settings.windowFrame = NSStringFromRect(window.frame) }
    }

    func windowDidMove(_ notification: Notification) {
        if !fullscreen && window.styleMask.contains(.titled) { Settings.windowFrame = NSStringFromRect(window.frame) }
    }

    // MARK: - menu

    private func buildMenu() {
        let main = NSMenu()

        let app = NSMenu()
        app.addItem(withTitle: L("O aplikácii Regula", "About Regula"),
                    action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        app.addItem(.separator())
        let language = NSMenu()
        language.addItem(ActionItem("English", checked: !SK) { [unowned self] in self.setLanguage(sk: false) })
        language.addItem(ActionItem("Slovenčina", checked: SK) { [unowned self] in self.setLanguage(sk: true) })
        let languageItem = NSMenuItem(title: L("Jazyk", "Language"), action: nil, keyEquivalent: "")
        languageItem.submenu = language
        app.addItem(languageItem)
        app.addItem(.separator())
        app.addItem(withTitle: L("Skryť Regulu", "Hide Regula"), action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        app.addItem(withTitle: L("Ukončiť Regulu", "Quit Regula"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        main.addItem(submenu: app)

        // Upraviť: kvôli ⌘C / ⌘V v políčkach na čísla
        let edit = NSMenu(title: L("Upraviť", "Edit"))
        edit.addItem(withTitle: L("Vystrihnúť", "Cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("Kopírovať", "Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("Vložiť", "Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("Vybrať všetko", "Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu: edit)

        let view = NSMenu(title: L("Zobraziť", "View"))
        let full = NSMenuItem(title: L("Okno / celá obrazovka", "Window / Fullscreen"),
                              action: #selector(menuToggleFullscreen(_:)), keyEquivalent: "f")
        full.keyEquivalentModifierMask = [.control, .command]
        full.target = self
        view.addItem(full)
        let move = NSMenuItem(title: L("Na ďalší monitor", "Move to Next Display"),
                              action: #selector(moveToNextDisplay(_:)), keyEquivalent: "")
        move.target = self
        view.addItem(move)
        view.addItem(.separator())
        view.addItem(ActionItem(L("Skryť / ukázať panel", "Hide / Show Panel")) { [unowned self] in self.canvas.toggleDock() })
        view.addItem(ActionItem(L("Kalibrácia mierky…", "Calibrate Scale…")) { [unowned self] in self.canvas.calibrate() })
        main.addItem(submenu: view)

        NSApp.mainMenu = main
    }

    /// Jazyk sa prejaví po reštarte — appka sa hneď znova spustí.
    private func setLanguage(sk: Bool) {
        UserDefaults.standard.set(sk ? "sk" : "en", forKey: "lang")
        guard sk != SK else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config) { _, _ in
            Task { @MainActor in NSApp.terminate(nil) }
        }
    }
}

private extension NSMenu {
    func addItem(submenu: NSMenu) {
        let item = NSMenuItem(title: submenu.title, action: nil, keyEquivalent: "")
        item.submenu = submenu
        addItem(item)
    }
}

// MARK: - vývojové snímky (cez SSH nejde screencapture)

@MainActor
enum Snapshot {
    private static func arg(_ name: String) -> String? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: name), i + 1 < a.count else { return nil }
        return a[i + 1]
    }

    private static func flag(_ name: String) -> Bool { CommandLine.arguments.contains(name) }

    static func run() {
        UserDefaults.standard.removePersistentDomain(forName: "sk.daniel.regula.snapshot")
        let out = URL(fileURLWithPath: arg("--snapshot")!)
        let screen = NSScreen.main ?? NSScreen.screens[0]
        if flag("--calib") {
            let calib = CalibrationController(screen: screen)
            render(calib.window.contentView!, to: out)
            NSApp.terminate(nil)
            return
        }
        var size = screen.frame.size
        if let s = arg("--size")?.split(separator: "x"), s.count == 2, let w = Double(s[0]), let h = Double(s[1]) {
            size = CGSize(width: w, height: h)
        }
        let window = RegulaWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless],
                                  backing: .buffered, defer: false)
        let canvas = Canvas(frame: CGRect(origin: .zero, size: size))
        window.contentView = canvas
        canvas.setTool(arg("--tool") ?? "cm")
        canvas.setBackground(arg("--bg") ?? "light")
        canvas.setSnap(Int(arg("--snap") ?? "0") ?? 0)
        canvas.layoutDock()
        if flag("--demo") { demo(canvas) }
        if flag("--typing") {
            canvas.inputBar.open("20")
            canvas.inputBar.prefill(["20", "50"])
            canvas.layoutDock()
        }
        render(canvas, to: out)
        print("panel: \(canvas.dock.compact ? "len ikonky" : "s popismi"), \(Int(canvas.dock.panelSize().width)) bodov")
        NSApp.terminate(nil)
    }

    private static func render(_ view: NSView, to url: URL) {
        view.layoutSubtreeIfNeeded()
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
        print("snímka: \(url.path) (\(Int(view.bounds.width))×\(Int(view.bounds.height)) bodov, \(fmt(ptPerMM(NSScreen.main!), 3)) pt/mm)")
    }

    /// Ukážkový stav ako na webe: meranie, ramená, kurzor nad skrutkou…
    private static func demo(_ c: Canvas) {
        let W = c.bounds.width, H = c.bounds.height, ppm = c.ppm
        c.tool.layout(W, H)
        let none = NSEvent.ModifierFlags()
        switch c.tool {
        case let t as RulerTool:
            c.raw = CGPoint(x: (W * 0.3).rounded(), y: (H * 0.28).rounded())
            _ = t.applyInput(t.unit == "in" ? [2.4, 1.4] : t.unit == "px" ? [240, 140] : [60, 35])
            c.hover = t.b
        case let t as ProtractorTool:
            t.arms = t.full ? [20, 152] : [12, 64.5]
        case let t as DiagonalTool:
            c.raw = CGPoint(x: (W * 0.62).rounded(), y: (H * 0.1).rounded())
            _ = t.applyInput([64, 138])
            c.raw = nil
        case let t as PathTool:
            for (x, y) in [(0.29, 0.6), (0.4, 0.26), (0.61, 0.21), (0.74, 0.52), (0.55, 0.7), (0.29, 0.6)] {
                let p = CGPoint(x: (W * CGFloat(x)).rounded(), y: (H * CGFloat(y)).rounded())
                c.raw = p
                t.press(p, none)
                t.release(p)
            }
            c.raw = nil
        case let t as RadiusTool:
            let center = CGPoint(x: W * 0.5, y: H * 0.44), r = 31 * ppm
            for a in [25.0, 80, 150] {
                let p = polar(center, CGFloat(a), r)
                t.press(p, none)
                t.release(p)
            }
        case let t as CircleTool:
            if let center = t.c { c.hover = center + CGPoint(x: 23.25 / 2 * ppm * 0.8, y: -23.25 / 2 * ppm * 0.6) }
        case is ScrewTool:
            let widths = SCREWS.map { (CGFloat(max($0.3, 9)) + 5) * ppm }
            let ox = (W - widths.reduce(0, +)) / 2, oy = max(16, (H - DOCK_RESERVE - 96 * ppm) / 2)
            c.hover = CGPoint(x: ox + widths[0..<3].reduce(0, +) + widths[3] / 2, y: oy + 40 * ppm)
        case let t as PitchTool:
            let x0 = (W - t.LABEL_W - t.COMB_MM * ppm) / 2 + t.LABEL_W
            let oy = max(16, (H - DOCK_RESERVE - CGFloat(PITCHES.count) * t.ROW_MM * ppm) / 2)
            c.hover = CGPoint(x: x0 + 7 * 2.0 * ppm, y: oy + 2 * t.ROW_MM * ppm + 4 * ppm)
        default:
            break
        }
    }
}
