import AppKit
import UniformTypeIdentifiers

enum Section: Int, CaseIterable {
    case games, agents, tools, access, problems
    var title: String { ["Games", "Agents", "Tools", "Access from Anywhere", "Problems"][rawValue] }
    var icon: String { ["gamecontroller", "person.2", "switch.2", "globe", "exclamationmark.bubble"][rawValue] }
}

/// Everything the screens show, refreshed from the server every few seconds.
@MainActor final class Model {
    var games: [J] = []
    var agents: [J] = []
    var clients: [J] = []
    var remote: J = [:]
    var tools: [J] = []
    var levels: [J] = []
    var problems: [J] = []
    var up = false
    var problem: String?
    var remoteOn: Bool { remote["on"] as? Bool ?? false }
    var otherClients: [J] { clients.filter { $0.s("kind") != "stdio" } }
}

@MainActor protocol Screen: NSViewController {
    func show(_ m: Model)
    var toolbarItems: [NSToolbarItem.Identifier] { get }
    func toolbarItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem?
}

@main @MainActor final class App: NSObject, NSApplicationDelegate, NSToolbarDelegate, NSWindowDelegate {
    static var shared: App!

    static func main() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--write-icon"), i + 1 < args.count {
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8, samplesPerPixel: 4,
                                       hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            logo(1024).draw(in: .init(x: 0, y: 0, width: 1024, height: 1024))
            NSGraphicsContext.restoreGraphicsState()
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: args[i + 1]))
            return
        }
        let app = NSApplication.shared
        let d = App()
        app.delegate = d
        app.setActivationPolicy(.regular)
        app.run()
    }
    let api = API.shared
    let model = Model()
    var window: NSWindow!
    let sidebar = Sidebar()
    let content = NSViewController()
    lazy var screens: [Screen] = [GamesScreen(), AgentsScreen(), ToolsScreen(), AccessScreen(), ProblemsScreen()]
    var section = Section.games
    var approvals: Approvals!
    let split = NSSplitViewController()
    /// Shown instead of the screens until the first answer from the server.
    var loading: NSView? = {
        let spin = NSProgressIndicator()
        spin.style = .spinning
        spin.startAnimation(nil)
        let s = stack([spin, label("Starting UDriver…", color: .secondaryLabelColor)], spacing: 12, align: .centerX)
        let v = NSView()
        s.translatesAutoresizingMaskIntoConstraints = false
        v.addSubview(s)
        NSLayoutConstraint.activate([s.centerXAnchor.constraint(equalTo: v.centerXAnchor), s.centerYAnchor.constraint(equalTo: v.centerYAnchor)])
        return v
    }()

    func applicationDidFinishLaunching(_ n: Notification) {
        App.shared = self
        // A downloaded app keeps macOS's quarantine mark, and macOS then kills the server when an agent starts it.
        let x = Process()
        x.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        x.arguments = ["-dr", "com.apple.quarantine", Bundle.main.bundlePath]
        try? x.run()
        NSApp.mainMenu = menu()
        if let a = UserDefaults.standard.string(forKey: "appearance") { NSApp.appearance = NSAppearance(named: a == "dark" ? .darkAqua : .aqua) }
        content.view = NSView()
        let side = NSSplitViewItem(sidebarWithViewController: sidebar)
        side.minimumThickness = 256
        side.maximumThickness = 280
        split.addSplitViewItem(side)
        let main = NSSplitViewItem(viewController: content)
        main.minimumThickness = 560
        split.addSplitViewItem(main)

        window = NSWindow(contentRect: .init(x: 0, y: 0, width: 1120, height: 720),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.contentViewController = split
        window.setContentSize(.init(width: 1120, height: 720))
        window.minSize = .init(width: 820, height: 540)
        window.delegate = self
        let bar = NSToolbar(identifier: "main")
        bar.delegate = self
        bar.displayMode = .iconOnly
        window.toolbar = bar
        window.toolbarStyle = .unified
        window.center()
        window.setFrameAutosaveName("UDriver")
        approvals = Approvals(window: window)
        select(.games)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()

        Task {
            sidebar.status("Starting UDriver…", ok: nil)
            if let err = await api.ensureServer() {
                model.problem = err
                sidebar.status("UDriver is not running", ok: false)
                alert("UDriver could not start", err, in: window)
            }
            await refresh()
            loading = nil
            select(section)
            if model.up && model.games.isEmpty && !UserDefaults.standard.bool(forKey: "welcomed") { showWelcome() }
            while true {
                try? await Task.sleep(for: .milliseconds(1500))
                await poll()
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ s: NSApplication) -> Bool { true }

    func poll() async {
        if let p = try? await api.json("/panel/api/pending") { approvals.update(p.list("pending")) }
        await refresh()
    }

    func refresh() async {
        do {
            let g = try await api.json("/panel/api/games")
            let a = try await api.json("/panel/api/agents")
            let c = try await api.json("/panel/api/clients")
            let r = try await api.json("/panel/api/remote")
            let t = try await api.json("/panel/api/tools")
            let p = try await api.json("/panel/api/problems")
            model.games = g.list("games"); model.agents = a.list("agents")
            model.clients = c.list("clients")
            model.remote = r; model.tools = t.list("tools"); model.levels = t.list("levels"); model.problems = p.list("problems")
            model.up = true; model.problem = nil
        } catch {
            model.up = await api.running()
            if !model.up { _ = await api.ensureServer() }
            model.problem = model.up ? error.localizedDescription : "UDriver is not running"
        }
        sidebar.update(model)
        screens[section.rawValue].show(model)
    }

    /// Runs a server action, shows its error if any, then refreshes. Meanwhile the clicked button is disabled
    /// and says what is happening, and the sidebar shows a spinner with the same words.
    func act(_ path: String, _ form: [String: String] = [:], then: ((J) -> Void)? = nil) {
        let text = working(path)
        let b = Button.pressed?.window == nil ? nil : Button.pressed
        Button.pressed = nil
        let title = b?.title
        b?.isEnabled = false
        b?.title = text
        Task {
            await busy(text) {
                do { let j = try await api.json(path, form); then?(j) }
                catch { alert(error.localizedDescription, in: window) }
                await refresh()
            }
            b?.isEnabled = true
            if let title { b?.title = title }
        }
    }

    func busy<T>(_ text: String, _ work: () async throws -> T) async rethrows -> T {
        sidebar.working.append(text)
        sidebar.update(model)
        defer {
            if let i = sidebar.working.firstIndex(of: text) { sidebar.working.remove(at: i) }
            sidebar.update(model)
        }
        return try await work()
    }

    func working(_ path: String) -> String {
        switch path.replacingOccurrences(of: "/panel/api/", with: "") {
        case "games/add": "Adding the game…"
        case "games/start": "Launching the game…"
        case "games/stop": "Stopping the game…"
        case "games/remove": "Removing the game…"
        case "agents/disconnect": "Disconnecting the agent…"
        case "clients/revoke": "Removing the agent…"
        case "pending/approve": "Allowing the agent…"
        case "pending/deny": "Denying the agent…"
        case "remote/install": "Downloading the helper…"
        case "remote/login": "Signing in to Cloudflare…"
        case "remote/on": "Turning on Access from Anywhere…"
        case "remote/host": "Changing address…"
        case "remote/off": "Turning off Access from Anywhere…"
        case "remote/signout": "Signing out of Cloudflare…"
        case "local-clients": "Creating the setup…"
        case "problems/dismiss": "Dismissing…"
        default: "Saving…"
        }
    }

    /// Computer access for agents on this Mac; turning it on asks first, as the web panel does. cancelled runs when the owner says no.
    func computer(_ on: Bool, cancelled: @escaping () -> Void = {}) {
        guard on else { return act("/panel/api/levels/set", ["level": "3", "enabled": "false"]) }
        let a = NSAlert()
        a.messageText = "Turn on computer access?"
        a.informativeText = "Agents on this Mac can then run any game code and editor menu commands, which can reach the files, programs and network of this computer. Only for agents you trust. Never over the internet."
        a.addButton(withTitle: "Turn On").hasDestructiveAction = true
        a.addButton(withTitle: "Cancel")
        a.beginSheetModal(for: window) { r in
            if r == .alertFirstButtonReturn { self.act("/panel/api/levels/set", ["level": "3", "enabled": "true"]) } else { cancelled() }
        }
    }

    func select(_ s: Section) {
        section = s
        let vc = screens[s.rawValue]
        content.view.subviews.forEach { $0.removeFromSuperview() }
        for v in [vc.view, loading].compactMap({ $0 }) { v.frame = content.view.bounds; v.autoresizingMask = [.width, .height] }
        content.view.addSubview(loading ?? vc.view)
        window.title = s.title
        sidebar.select(s)
        if let bar = window.toolbar {
            while bar.items.count > 3 { bar.removeItem(at: bar.items.count - 1) }
            for id in vc.toolbarItems + [.space] { bar.insertItem(withItemIdentifier: id, at: bar.items.count) }
        }
        vc.show(model)
    }

    // MARK: toolbar

    func toolbarDefaultItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] { [.toggleSidebar, .sidebarTrackingSeparator, .flexibleSpace] }
    func toolbarAllowedItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarDefaultItemIdentifiers(t) + screens.flatMap(\.toolbarItems) + [.space]
    }
    func toolbar(_ t: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier, willBeInsertedIntoToolbar f: Bool) -> NSToolbarItem? {
        screens.lazy.compactMap { $0.toolbarItem(id) }.first
    }

    // MARK: games

    func chooseGame() {
        let p = NSOpenPanel()
        p.message = "Choose a built game or a Unity project folder"
        p.prompt = "Add"
        p.canChooseDirectories = true
        p.canChooseFiles = true
        p.treatsFilePackagesAsDirectories = false
        p.allowedContentTypes = [.application, .folder]
        p.beginSheetModal(for: window) { r in
            if r == .OK, let url = p.url { self.addGame(url) }
        }
    }

    func addGame(_ url: URL, done: (() -> Void)? = nil) {
        act("/panel/api/games/add", ["path": url.path]) { j in
            if let g = self.screens[Section.games.rawValue] as? GamesScreen { g.selected = j.s("id"); g.last = "" }
            if let w = j["warning"] as? String, !w.isEmpty { alert("Game added", w, in: self.window) }
            done?()
        }
    }

    // MARK: welcome

    func showWelcome() {
        let w = Welcome()
        w.done = { [weak self] in
            guard let self else { return }
            UserDefaults.standard.set(true, forKey: "welcomed")
            self.window.toolbar?.isVisible = true
            self.window.titlebarAppearsTransparent = false
            self.window.titlebarSeparatorStyle = .automatic
            let frame = self.window.frame
            self.window.contentViewController = self.split
            self.window.setFrame(frame, display: true)
            self.select(self.section)
        }
        let frame = window.frame
        window.toolbar?.isVisible = false
        window.titlebarAppearsTransparent = true
        window.titlebarSeparatorStyle = .none
        window.contentViewController = w
        window.setFrame(frame, display: true)
        window.title = ""
    }

    // MARK: menu

    func menu() -> NSMenu {
        let main = NSMenu()
        func sub(_ title: String, _ items: [NSMenuItem]) {
            let m = NSMenu(title: title)
            items.forEach(m.addItem)
            let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            i.submenu = m
            main.addItem(i)
        }
        let updates = NSMenuItem(title: "Check for Updates…", action: #selector(checkUpdates), keyEquivalent: "")
        updates.target = self
        sub("UDriver", [
            NSMenuItem(title: "About UDriver", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: ""),
            updates,
            .separator(),
            NSMenuItem(title: "Hide UDriver", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h"),
            NSMenuItem(title: "Quit UDriver", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"),
        ])
        let add = NSMenuItem(title: "Add Game…", action: #selector(addGameMenu), keyEquivalent: "o")
        add.target = self
        sub("File", [add, NSMenuItem(title: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")])
        sub("Edit", [
            NSMenuItem(title: "Undo", action: Selector(("undo:")), keyEquivalent: "z"),
            .separator(),
            NSMenuItem(title: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x"),
            NSMenuItem(title: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c"),
            NSMenuItem(title: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v"),
            NSMenuItem(title: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"),
        ])
        var view = Section.allCases.map { s -> NSMenuItem in
            let i = NSMenuItem(title: s.title, action: #selector(sectionMenu(_:)), keyEquivalent: "\(s.rawValue + 1)")
            i.tag = s.rawValue
            i.target = self
            return i
        }
        view += [.separator(), NSMenuItem(title: "Toggle Sidebar", action: #selector(NSSplitViewController.toggleSidebar(_:)), keyEquivalent: "s")]
        view.last?.keyEquivalentModifierMask = [.command, .control]
        sub("View", view)
        let win = NSMenu(title: "Window")
        win.addItem(NSMenuItem(title: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"))
        win.addItem(NSMenuItem(title: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: ""))
        let wi = NSMenuItem(title: "Window", action: nil, keyEquivalent: "")
        wi.submenu = win
        main.addItem(wi)
        NSApp.windowsMenu = win
        return main
    }

    @objc func addGameMenu() { chooseGame() }

    @objc func checkUpdates() {
        Task {
            do {
                let u = try await busy("Checking for updates…") { try await api.json("/panel/api/update") }
                if u["installed"] as? Bool != true { return alert("Updates are not available for this copy.", "Only a copy installed from a UDriver download can update itself.", in: window) }
                if let e = u["error"] as? String { return alert("Could not check for updates.", e, in: window) }
                guard u["available"] as? Bool == true, let next = u["version"] as? String else {
                    return alert("UDriver is up to date.", "You have version \(u["current"] as? String ?? "").", in: window)
                }
                let a = NSAlert()
                a.messageText = "UDriver \(next) is available."
                a.informativeText = "You have version \(u["current"] as? String ?? ""). UDriver will download the update, close, and open again."
                a.addButton(withTitle: "Update Now")
                a.addButton(withTitle: "Later")
                guard await a.beginSheetModal(for: window) == .alertFirstButtonReturn else { return }
                _ = try await busy("Downloading the update…") { try await api.json("/panel/api/update/apply", ["restart": "true"], timeout: 600) }
                NSApp.terminate(nil)
            } catch {
                alert("The update did not finish.", error.localizedDescription, in: window)
            }
        }
    }
    @objc func sectionMenu(_ i: NSMenuItem) { if let s = Section(rawValue: i.tag), window.contentViewController === split { select(s) } }
}

// MARK: - sidebar

@MainActor final class Sidebar: NSViewController, NSTableViewDataSource, NSTableViewDelegate {
    let table = NSTableView()
    let foot = label("", size: 12, color: .secondaryLabelColor)
    let dot = NSView()
    let spin = NSProgressIndicator()
    var working: [String] = []
    var counts: [String] = ["", "", "", "", ""]

    override func loadView() {
        table.addTableColumn(NSTableColumn(identifier: .init("c")))
        table.headerView = nil
        table.style = .sourceList
        table.rowHeight = 30
        table.refusesFirstResponder = true
        table.dataSource = self
        table.delegate = self
        table.setAccessibilityLabel("Sections")
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.drawsBackground = false
        dot.wantsLayer = true
        dot.layer?.cornerRadius = 4
        spin.style = .spinning
        spin.controlSize = .small
        let footer = stack([fixed(dot, width: 8, height: 8), fixed(spin, width: 12, height: 12), foot], .horizontal, spacing: 7)
        footer.edgeInsets = .init(top: 0, left: 18, bottom: 14, right: 12)
        let v = NSView()
        for x in [scroll, footer] as [NSView] { x.translatesAutoresizingMaskIntoConstraints = false; v.addSubview(x) }
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: v.topAnchor), scroll.leadingAnchor.constraint(equalTo: v.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: v.trailingAnchor), scroll.bottomAnchor.constraint(equalTo: footer.topAnchor),
            footer.leadingAnchor.constraint(equalTo: v.leadingAnchor), footer.trailingAnchor.constraint(equalTo: v.trailingAnchor),
            footer.bottomAnchor.constraint(equalTo: v.bottomAnchor),
        ])
        view = v
    }

    func status(_ text: String, ok: Bool?) {
        foot.stringValue = text
        dot.layer?.backgroundColor = (ok == true ? NSColor.systemGreen : .systemRed).cgColor
        dot.isHidden = ok == nil
        spin.isHidden = ok != nil
        if ok == nil { spin.startAnimation(nil) } else { spin.stopAnimation(nil) }
        dot.setAccessibilityElement(false)
    }

    func update(_ m: Model) {
        counts = [m.games.isEmpty ? "" : "\(m.games.count)", m.agents.isEmpty ? "" : "\(m.agents.count)", "", m.remoteOn ? "On" : "Off", m.problems.isEmpty ? "" : "\(m.problems.count)"]
        let sel = table.selectedRow
        table.reloadData()
        table.selectRowIndexes([sel], byExtendingSelection: false)
        if let w = working.last { status(w, ok: nil) } else if let p = m.problem { status(m.up ? "Ready on this Mac" : p, ok: m.up) } else { status("Ready on this Mac", ok: true) }
    }

    func select(_ s: Section) {
        if table.selectedRow != s.rawValue { table.selectRowIndexes([s.rawValue], byExtendingSelection: false) }
    }

    func numberOfRows(in t: NSTableView) -> Int { Section.allCases.count }

    func tableView(_ t: NSTableView, viewFor c: NSTableColumn?, row: Int) -> NSView? {
        let s = Section(rawValue: row)!
        let cell = NSTableCellView()
        let img = NSImageView(image: symbol(s.icon, nil, size: 14)!)
        img.contentTintColor = .controlAccentColor
        let name = label(s.title)
        let n = label(counts[row], size: 12, color: .secondaryLabelColor)
        n.setContentCompressionResistancePriority(.required, for: .horizontal)
        let row = stack([fixed(img, width: 20), name, spacer(), n], .horizontal, spacing: 6)
        cell.addSubview(row)
        cell.textField = name
        cell.imageView = img
        row.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 2), row.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -6),
            row.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
        ])
        cell.setAccessibilityLabel(counts[s.rawValue].isEmpty ? s.title : "\(s.title), \(counts[s.rawValue])")
        return cell
    }

    func tableViewSelectionDidChange(_ n: Notification) {
        if let s = Section(rawValue: table.selectedRow), s != App.shared.section { App.shared.select(s) }
    }
}

// MARK: - approval sheet

@MainActor final class Approvals {
    let window: NSWindow
    var sheet: NSWindow?
    var id: String?

    init(window: NSWindow) { self.window = window }

    func update(_ pending: [J]) {
        if let id, !pending.contains(where: { $0.s("id") == id }) { close() }
        if id == nil, let p = pending.first { present(p) }
    }

    func close() {
        if let sheet { window.endSheet(sheet) }
        sheet = nil
        id = nil
    }

    func decide(_ ok: Bool) {
        guard let id else { return }
        close()
        App.shared.act(ok ? "/panel/api/pending/approve" : "/panel/api/pending/deny", ["id": id])
    }

    func present(_ p: J) {
        id = p.s("id")
        let icon = NSImageView(image: logo(90))
        icon.setAccessibilityElement(false)
        let title = NSTextField(wrappingLabelWithString: "\(p.s("app")) wants to use your games")
        title.font = .systemFont(ofSize: 17, weight: .bold)
        title.alignment = .center
        let number = label(p.s("check"), size: 48, weight: .bold)
        number.alignment = .center
        number.setAccessibilityLabel("Check number \(p.s("check").map(String.init).joined(separator: " "))")
        let note = wrapping("Allow only if the sign-in page shows the same number.", size: 13)
        note.alignment = .center
        let globe = NSImageView(image: symbol("globe", nil, size: 18)!)
        globe.contentTintColor = .systemBlue
        let dest = p.s("to").nilIfEmpty ?? "this computer"
        let whereRow = box(stack([globe, stack([label("Access goes to", size: 11, color: .secondaryLabelColor), label(dest, weight: .semibold)], spacing: 2)], .horizontal, spacing: 10), padding: 10)
        let deny = Button("Deny") { [weak self] in self?.decide(false) }
        deny.keyEquivalent = "\u{1b}"
        let allow = Button("Allow", primary: true) { [weak self] in self?.decide(true) }
        if #available(macOS 26, *) { for b in [deny, allow] { b.controlSize = .extraLarge } }
        let buttons = NSStackView(views: [deny, allow])
        buttons.distribution = .fillEqually
        buttons.spacing = 8
        let s = stack([icon, title, number, note, whereRow, buttons], spacing: 14, align: .centerX)
        s.edgeInsets = .init(top: 24, left: 28, bottom: 22, right: 28)
        s.setCustomSpacing(4, after: number)
        for v in [title, number, note, whereRow, buttons] { v.widthAnchor.constraint(equalTo: s.widthAnchor, constant: -56).isActive = true }
        let vc = NSViewController()
        vc.view = s
        s.widthAnchor.constraint(equalToConstant: 420).isActive = true
        let w = NSWindow(contentViewController: vc)
        w.setAccessibilityLabel("Allow access")
        sheet = w
        window.beginSheet(w)
        NSApp.requestUserAttention(.criticalRequest)
    }
}

// MARK: - welcome

@MainActor final class Welcome: NSViewController {
    var done: () -> Void = {}

    override func loadView() {
        let icon = NSImageView(image: logo(128))
        icon.setAccessibilityLabel("UDriver")
        icon.unregisterDraggedTypes()
        let title = label("Welcome to UDriver", size: 32, weight: .bold)
        let sub = label("Let AI agents see and play your Unity games — with your permission.", size: 15, color: .secondaryLabelColor)
        func card(_ sym: String, _ color: NSColor, _ head: String, _ text: String) -> NSView {
            let body = wrapping(text, size: 13)
            let s = stack([tile(sym, color, side: 36), label(head, size: 14, weight: .semibold), body, spacer()], spacing: 10)
            s.distribution = .fill
            body.widthAnchor.constraint(equalToConstant: 200).isActive = true
            let p = box(s, padding: 18)
            p.borderWidth = 0
            p.cornerRadius = 16
            p.fillColor = .quaternarySystemFill
            return fixed(p, width: 236, height: 170)
        }
        let tiles = stack([
            card("gamecontroller", .systemGreen, "Add a game", "Drop a built game or a Unity project. UDriver adds a small helper to it."),
            card("person.2", .systemBlue, "Connect an agent", "You approve each one. You decide what it may do in every game."),
            card("globe", violet, "Use from anywhere", "Optional. Off until you turn it on, and closed with one switch."),
        ], .horizontal, spacing: 16, align: .top)
        let go = Button("Add Your First Game", primary: true) { App.shared.chooseGame() }
        if #available(macOS 26, *) { go.controlSize = .extraLarge }
        go.keyEquivalent = "\r"
        let later = Button("Not Now") { [weak self] in self?.done() }
        later.isBordered = false
        later.contentTintColor = .secondaryLabelColor
        let s = stack([icon, stack([title, sub], spacing: 8, align: .centerX), tiles, fixed(go, width: 260), later], spacing: 28, align: .centerX)
        s.setCustomSpacing(10, after: go)
        let v = DropBox(frame: .init(x: 0, y: 0, width: 1120, height: 720))
        v.registerForDraggedTypes([.fileURL])
        v.take = { $0.forEach { App.shared.addGame($0) } }
        v.boxType = .custom
        v.borderWidth = 0
        v.fillColor = .controlBackgroundColor
        v.contentView?.addSubview(s)
        s.translatesAutoresizingMaskIntoConstraints = false
        s.centerXAnchor.constraint(equalTo: v.centerXAnchor).isActive = true
        s.centerYAnchor.constraint(equalTo: v.centerYAnchor).isActive = true
        v.widthAnchor.constraint(greaterThanOrEqualToConstant: 860).isActive = true
        v.heightAnchor.constraint(greaterThanOrEqualToConstant: 620).isActive = true
        view = v
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        Task {
            while view.window != nil {
                try? await Task.sleep(for: .seconds(1))
                if !App.shared.model.games.isEmpty { done(); return }
            }
        }
    }
}
