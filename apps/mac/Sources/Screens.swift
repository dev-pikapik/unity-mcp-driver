import AppKit

extension NSToolbarItem.Identifier {
    static let addGame = Self("addGame"), search = Self("search"), connect = Self("connect"), toolsFor = Self("toolsFor")
}

/// A scrolling page whose content is rebuilt only when the data behind it changes.
@MainActor class Page: NSViewController, Screen {
    let body = stack([], spacing: 18, align: .centerX)
    var last = ""
    var column: CGFloat? { nil }
    var toolbarItems: [NSToolbarItem.Identifier] { [] }
    func toolbarItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem? { nil }
    func show(_ m: Model) {}

    override func loadView() {
        body.edgeInsets = .init(top: 20, left: 28, bottom: 28, right: 28)
        let doc = Flipped()
        doc.addSubview(body)
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.backgroundColor = .controlBackgroundColor
        scroll.documentView = doc
        for v in [doc, body] { v.translatesAutoresizingMaskIntoConstraints = false }
        NSLayoutConstraint.activate([
            doc.leadingAnchor.constraint(equalTo: scroll.contentView.leadingAnchor), doc.trailingAnchor.constraint(equalTo: scroll.contentView.trailingAnchor),
            doc.topAnchor.constraint(equalTo: scroll.contentView.topAnchor),
            body.leadingAnchor.constraint(equalTo: doc.leadingAnchor), body.trailingAnchor.constraint(equalTo: doc.trailingAnchor),
            body.topAnchor.constraint(equalTo: doc.topAnchor), body.bottomAnchor.constraint(equalTo: doc.bottomAnchor),
        ])
        view = scroll
    }

    func fill(_ key: String, _ views: @MainActor () -> [NSView]) {
        guard key != last else { return }
        last = key
        body.arrangedSubviews.forEach { $0.removeFromSuperview() }
        for v in views() {
            body.addArrangedSubview(v)
            if let column {
                v.widthAnchor.constraint(lessThanOrEqualTo: body.widthAnchor, constant: -56).isActive = true
                let w = v.widthAnchor.constraint(equalToConstant: column)
                w.priority = .init(400)
                w.isActive = true
            } else {
                v.widthAnchor.constraint(equalTo: body.widthAnchor, constant: -56).isActive = true
            }
        }
    }
}

final class Flipped: NSView { override var isFlipped: Bool { true } }

func header(_ title: String, _ sub: String? = nil) -> NSView {
    let t = label(title, size: 13, weight: .bold)
    t.setAccessibilityRole(.staticText)
    return sub.map { stack([t, wrapping($0)], spacing: 4) } ?? t
}

func pretty(_ any: Any) -> String {
    String(decoding: (try? JSONSerialization.data(withJSONObject: any, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data(), as: UTF8.self)
}

func caption(_ text: String) -> NSTextField { label(text.uppercased(), size: 11, weight: .semibold, color: .secondaryLabelColor) }

func key(_ any: Any) -> String {
    String(decoding: (try? JSONSerialization.data(withJSONObject: any, options: .sortedKeys)) ?? Data(), as: UTF8.self)
}

let levels = ["watch", "play", "full"]
let levelNames = ["Watch", "Play", "Everything"]
let levelIcons = ["eye", "gamecontroller", "wrench.adjustable"]
let levelHints = [
    "Look only: see the screen and read the scene. Nothing is pressed or changed.",
    "Look, press buttons and play. Files stay untouched.",
    "Look, play and also change scenes, settings and project files.",
]

// MARK: - Games

@MainActor final class GamesScreen: NSViewController, Screen, NSCollectionViewDataSource, NSCollectionViewDelegate, NSSearchFieldDelegate {
    let grid = NSCollectionView()
    let inspector = stack([], spacing: 12)
    let actions = stack([], .horizontal)
    var games: [J] = []
    var agents: [J] = []
    var shown: [J] { filter.isEmpty ? games : games.filter { $0.s("name").localizedCaseInsensitiveContains(filter) } }
    var filter = ""
    var selected: String?
    var previews: [String: NSImage] = [:]
    var last = ""

    var toolbarItems: [NSToolbarItem.Identifier] { [.search, .addGame] }
    func toolbarItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem? {
        switch id {
        case .search:
            let s = NSSearchToolbarItem(itemIdentifier: id)
            s.searchField.delegate = self
            s.searchField.placeholderString = "Search"
            s.preferredWidthForSearchField = 180
            return s
        case .addGame:
            let i = NSToolbarItem(itemIdentifier: id)
            i.image = symbol("plus", "Add Game")
            i.label = "Add Game"
            i.toolTip = "Add a game or Unity project"
            i.target = self
            i.action = #selector(add)
            i.isBordered = true
            return i
        default: return nil
        }
    }

    @objc func add() { App.shared.chooseGame() }

    func controlTextDidChange(_ n: Notification) {
        filter = (n.object as? NSSearchField)?.stringValue ?? ""
        grid.reloadData()
    }

    override func loadView() {
        let layout = NSCollectionViewFlowLayout()
        layout.itemSize = .init(width: 230, height: 196)
        layout.minimumInteritemSpacing = 16
        layout.minimumLineSpacing = 16
        layout.sectionInset = .init(top: 20, left: 24, bottom: 24, right: 24)
        grid.collectionViewLayout = layout
        grid.dataSource = self
        grid.delegate = self
        grid.isSelectable = true
        grid.backgroundColors = [.controlBackgroundColor]
        grid.register(Card.self, forItemWithIdentifier: Card.id)
        grid.registerForDraggedTypes([.fileURL])
        grid.setDraggingSourceOperationMask(.copy, forLocal: false)
        grid.setAccessibilityLabel("Games")
        let menu = NSMenu()
        menu.addItem(withTitle: "Show in Finder", action: #selector(reveal), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Remove from UDriver…", action: #selector(remove), keyEquivalent: "").target = self
        grid.menu = menu
        let scroll = NSScrollView()
        scroll.documentView = grid
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false

        inspector.edgeInsets = .init(top: 20, left: 18, bottom: 18, right: 18)
        let side = NSVisualEffectView()
        side.material = .contentBackground
        side.addSubview(inspector)
        side.addSubview(actions)
        let line = NSBox(frame: .init(x: 0, y: 0, width: 1, height: 100))
        line.boxType = .separator
        let v = NSView()
        for x in [scroll, line, side, inspector, actions] as [NSView] { x.translatesAutoresizingMaskIntoConstraints = false }
        for x in [scroll, line, side] { v.addSubview(x) }
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: v.leadingAnchor), scroll.topAnchor.constraint(equalTo: v.topAnchor), scroll.bottomAnchor.constraint(equalTo: v.bottomAnchor),
            line.leadingAnchor.constraint(equalTo: scroll.trailingAnchor), line.topAnchor.constraint(equalTo: v.topAnchor), line.bottomAnchor.constraint(equalTo: v.bottomAnchor), line.widthAnchor.constraint(equalToConstant: 1),
            side.leadingAnchor.constraint(equalTo: line.trailingAnchor), side.trailingAnchor.constraint(equalTo: v.trailingAnchor),
            side.topAnchor.constraint(equalTo: v.safeAreaLayoutGuide.topAnchor), side.bottomAnchor.constraint(equalTo: v.bottomAnchor), side.widthAnchor.constraint(equalToConstant: 336),
            inspector.leadingAnchor.constraint(equalTo: side.leadingAnchor), inspector.trailingAnchor.constraint(equalTo: side.trailingAnchor), inspector.topAnchor.constraint(equalTo: side.topAnchor),
            actions.leadingAnchor.constraint(equalTo: side.leadingAnchor, constant: 18), actions.bottomAnchor.constraint(equalTo: side.bottomAnchor, constant: -18),
        ])
        view = v
    }

    override func viewDidLayout() {
        super.viewDidLayout()
        guard let layout = grid.collectionViewLayout as? NSCollectionViewFlowLayout else { return }
        let w = grid.enclosingScrollView?.contentSize.width ?? 600
        let cols = max(1, Int((w - 48 + 16) / 236))
        let itemW = floor((w - 48 - CGFloat(cols - 1) * 16) / CGFloat(cols))
        if layout.itemSize.width != itemW { layout.itemSize = .init(width: itemW, height: floor((itemW - 8) * 0.625) + 56) }
    }

    func show(_ m: Model) {
        loadViewIfNeeded()
        for g in m.games where g.s("state") != "not running" { fetchPreview(g.s("id")) }
        let k = key([m.games, m.agents.map { [$0.s("target"), $0.s("label")] }])
        guard k != last else { return }
        last = k
        games = m.games
        agents = m.agents
        if selected == nil || !games.contains(where: { $0.s("id") == selected }) { selected = games.first?.s("id") }
        grid.reloadData()
        if let i = shown.firstIndex(where: { $0.s("id") == selected }) { grid.selectionIndexPaths = [IndexPath(item: i, section: 0)] }
        inspect()
    }

    func fetchPreview(_ id: String) {
        Task {
            guard let (d, status) = try? await API.shared.data("/panel/api/games/preview?id=" + id), status == 200, let img = NSImage(data: d) else { return }
            previews[id] = img
            if let i = shown.firstIndex(where: { $0.s("id") == id }) { grid.reloadItems(at: [IndexPath(item: i, section: 0)]) }
            if id == selected { inspect() }
        }
    }

    func picture(_ g: J) -> NSImage { previews[g.s("id")] ?? scene(for: g.s("name"), kind: g.s("kind")) }
    func player(_ g: J) -> String? { agents.first { [g.s("name"), g.s("id")].contains($0.s("target")) }?.s("label").nilIfEmpty }
    func live(_ g: J) -> Bool { g.s("state") != "not running" }

    func meta(_ g: J) -> String {
        let kind = g.s("kind") == "project" ? "Project" : "Build"
        if !(g["exists"] as? Bool ?? true) { return kind + " · Missing on disk" }
        if let p = player(g) { return "\(kind) · \(p) is playing" }
        return kind + " · " + sentence(g.s("state")) + (g.s("access") == "none" ? " · Not allowed" : "")
    }

    func place(_ path: String) -> String {
        let home = API.shared.home + "/"
        if path.hasPrefix(home) { return "~/" + path.dropFirst(home.count) }
        return URL(fileURLWithPath: path).pathComponents.suffix(2).joined(separator: "/")
    }

    var current: J? { games.first { $0.s("id") == selected } }

    func inspect() {
        inspector.arrangedSubviews.forEach { $0.removeFromSuperview() }
        actions.arrangedSubviews.forEach { $0.removeFromSuperview() }
        guard let g = current else {
            inspector.addArrangedSubview(wrapping("Add a built game or a Unity project to let agents see and play it.", size: 13))
            return
        }
        let img = NSImageView(image: picture(g))
        img.imageScaling = .scaleProportionallyUpOrDown
        img.wantsLayer = true
        img.layer?.cornerRadius = 8
        img.layer?.masksToBounds = true
        img.setAccessibilityLabel("Preview of \(g.s("name"))")
        let path = label(place(g.s("path")), size: 12, color: .secondaryLabelColor)
        path.lineBreakMode = .byTruncatingMiddle
        path.toolTip = g.s("path")

        let lvl = levels.firstIndex(of: g.s("access")) ?? -1
        let options = stack(levels.indices.map { i -> NSView in
            let b = Button(levelNames[i]) { [weak self] in self?.access(i) }
            b.isBordered = false
            b.image = symbol(levelIcons[i], nil, size: 16)
            b.imagePosition = .imageAbove
            b.font = .systemFont(ofSize: 12, weight: i == lvl ? .semibold : .regular)
            b.contentTintColor = i == lvl ? .controlAccentColor : .labelColor
            b.setAccessibilityRole(.radioButton)
            b.setAccessibilityValue(i == lvl ? 1 : 0)
            let cell = box(b, padding: 6)
            cell.borderWidth = i == lvl ? 0.5 : 0
            cell.cornerRadius = 7
            cell.fillColor = i == lvl ? .controlBackgroundColor : .clear
            return cell
        }, .horizontal, spacing: 2)
        options.distribution = .fillEqually
        options.setAccessibilityLabel("Agents may")
        let seg = box(options, padding: 3)
        seg.borderWidth = 0
        seg.cornerRadius = 9
        seg.fillColor = .quaternarySystemFill

        func row(_ k: String, _ v: String, _ mark: NSView? = nil) -> NSView {
            let r = stack([label(k, color: .secondaryLabelColor), spacer()] + [mark, label(v)].compactMap { $0 }, .horizontal, spacing: 6)
            r.setAccessibilityElement(true)
            r.setAccessibilityLabel("\(k): \(v)")
            return r
        }
        let running = live(g)
        let start = Button(running ? (g["startedByUDriver"] as? Bool == true ? "Stop" : "Restart") : "Launch", symbol: running && g["startedByUDriver"] as? Bool == true ? "stop.fill" : "play.fill", primary: true) { [weak self] in self?.startStop() }
        let finder = Button("Show in Finder") { [weak self] in self?.reveal() }
        [start, finder].forEach(actions.addArrangedSubview)
        let hint = wrapping(lvl < 0 ? "Not allowed — set access to use it." : levelHints[lvl])
        hint.preferredMaxLayoutWidth = 300

        var views: [NSView] = [
            fixed(img, width: 300, height: 188),
            stack([label(g.s("name"), size: 17, weight: .bold), path], spacing: 3),
            caption("Agents may"),
            seg,
            hint,
            list([row("Status", sentence(g.s("state")), dot(running ? .systemGreen : .systemGray)), row("Agent", player(g) ?? "None"), row("Engine", g.s("engine").nilIfEmpty ?? "Unity")]),
        ]
        if g.s("native").nilIfEmpty != nil, g["exists"] as? Bool != false {
            let note = wrapping("This game calls code on the computer directly, so while Computer access is off agents can only look and play in it.", color: .systemOrange)
            note.preferredMaxLayoutWidth = 300
            views += [note, Button("Turn On Computer Access", symbol: "lock.open") { App.shared.act("/panel/api/levels/set", ["level": "3", "enabled": "true"]) }]
        }
        views.forEach(inspector.addArrangedSubview)
        for v in views.dropFirst() { v.widthAnchor.constraint(equalTo: inspector.widthAnchor, constant: -36).isActive = true }
        inspector.setCustomSpacing(18, after: views[1])
    }

    func access(_ i: Int) {
        guard let g = current, levels[i] != g.s("access") else { return }
        App.shared.act("/panel/api/games/edit", ["id": g.s("id"), "access": levels[i]])
    }

    func startStop() {
        guard let g = current else { return }
        let stop = live(g) && g["startedByUDriver"] as? Bool == true
        App.shared.act(stop ? "/panel/api/games/stop" : "/panel/api/games/start", ["id": g.s("id")])
    }

    var clicked: J? {
        let i = grid.indexPathForItem(at: grid.convert(grid.window?.mouseLocationOutsideOfEventStream ?? .zero, from: nil))?.item
        return i.flatMap { $0 < shown.count ? shown[$0] : nil } ?? current
    }

    @objc func reveal() {
        guard let g = clicked else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: g.s("path"))])
    }

    @objc func remove() {
        guard let g = clicked, let w = view.window else { return }
        let a = NSAlert()
        a.messageText = "Remove \(g.s("name")) from UDriver?"
        a.informativeText = "Agents lose access to it. The game itself stays on your Mac."
        a.addButton(withTitle: "Remove").hasDestructiveAction = true
        a.addButton(withTitle: "Cancel")
        a.beginSheetModal(for: w) { r in
            if r == .alertFirstButtonReturn { App.shared.act("/panel/api/games/remove", ["id": g.s("id")]) }
        }
    }

    func collectionView(_ c: NSCollectionView, numberOfItemsInSection s: Int) -> Int { shown.count + 1 }

    func collectionView(_ c: NSCollectionView, itemForRepresentedObjectAt p: IndexPath) -> NSCollectionViewItem {
        let card = c.makeItem(withIdentifier: Card.id, for: p) as! Card
        if p.item == shown.count { card.dropZone() } else {
            let g = shown[p.item]
            card.fill(g.s("name"), meta(g), picture(g), live: live(g))
        }
        return card
    }

    func collectionView(_ c: NSCollectionView, shouldSelectItemsAt paths: Set<IndexPath>) -> Set<IndexPath> {
        if paths.contains(where: { $0.item == shown.count }) { add(); return [] }
        return paths
    }

    func collectionView(_ c: NSCollectionView, didSelectItemsAt paths: Set<IndexPath>) {
        guard let i = paths.first?.item, i < shown.count else { return }
        selected = shown[i].s("id")
        inspect()
    }

    func collectionView(_ c: NSCollectionView, validateDrop info: NSDraggingInfo, proposedIndexPath p: AutoreleasingUnsafeMutablePointer<NSIndexPath>, dropOperation op: UnsafeMutablePointer<NSCollectionView.DropOperation>) -> NSDragOperation {
        op.pointee = .before
        return .copy
    }

    func collectionView(_ c: NSCollectionView, acceptDrop info: NSDraggingInfo, indexPath: IndexPath, dropOperation: NSCollectionView.DropOperation) -> Bool {
        let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        urls.forEach { App.shared.addGame($0) }
        return !urls.isEmpty
    }
}

final class Card: NSCollectionViewItem {
    static let id = NSUserInterfaceItemIdentifier("card")
    let picture = NSImageView()
    let name = label("", weight: .semibold)
    let meta = label("", size: 12, color: .secondaryLabelColor)
    let badge = chip("Live", symbol: "circle.fill", tint: .systemGreen)

    override func loadView() {
        picture.unregisterDraggedTypes()
        picture.imageScaling = .scaleProportionallyUpOrDown
        picture.wantsLayer = true
        picture.layer?.cornerRadius = 8
        picture.layer?.masksToBounds = true
        picture.layer?.borderColor = NSColor.controlAccentColor.cgColor
        let v = NSView()
        let text = stack([name, meta], spacing: 3)
        for x in [picture, text, badge] as [NSView] { x.translatesAutoresizingMaskIntoConstraints = false; v.addSubview(x) }
        NSLayoutConstraint.activate([
            picture.leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: 4), picture.trailingAnchor.constraint(equalTo: v.trailingAnchor, constant: -4),
            picture.topAnchor.constraint(equalTo: v.topAnchor, constant: 4), picture.heightAnchor.constraint(equalTo: picture.widthAnchor, multiplier: 0.625),
            badge.leadingAnchor.constraint(equalTo: picture.leadingAnchor, constant: 8), badge.topAnchor.constraint(equalTo: picture.topAnchor, constant: 8),
            text.leadingAnchor.constraint(equalTo: picture.leadingAnchor, constant: 2), text.trailingAnchor.constraint(equalTo: picture.trailingAnchor),
            text.topAnchor.constraint(equalTo: picture.bottomAnchor, constant: 8),
        ])
        badge.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.55).cgColor
        view = v
        view.setAccessibilityElement(true)
        view.setAccessibilityRole(.button)
    }

    func fill(_ title: String, _ sub: String, _ img: NSImage, live: Bool) {
        name.stringValue = title
        meta.stringValue = sub
        picture.image = img
        badge.isHidden = !live
        view.setAccessibilityLabel("\(title), \(sub)\(live ? ", live" : "")")
    }

    func dropZone() {
        name.stringValue = ""
        meta.stringValue = ""
        badge.isHidden = true
        picture.image = NSImage(size: .init(width: 222, height: 138), flipped: true) { r in
            let p = NSBezierPath(roundedRect: r.insetBy(dx: 1, dy: 1), xRadius: 8, yRadius: 8)
            p.setLineDash([6, 4], count: 2, phase: 0)
            p.lineWidth = 1.5
            NSColor.tertiaryLabelColor.setStroke()
            p.stroke()
            let s = NSAttributedString(string: "Drop a game or project here", attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.secondaryLabelColor])
            s.draw(at: .init(x: r.midX - s.size().width / 2, y: r.midY + 14))
            if let plus = symbol("plus", nil, size: 22) { plus.draw(in: .init(x: r.midX - 12, y: r.midY - 22, width: 24, height: 24)) }
            return true
        }
        view.setAccessibilityLabel("Add a game or project")
    }

    override var isSelected: Bool {
        didSet { picture.layer?.borderWidth = isSelected ? 3 : 0 }
    }
}

// MARK: - Agents

@MainActor final class AgentsScreen: Page {
    override var toolbarItems: [NSToolbarItem.Identifier] { [.connect] }
    override func toolbarItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem? {
        guard id == .connect else { return nil }
        let i = NSToolbarItem(itemIdentifier: id)
        i.label = "Connect Agent"
        let b = Button("Connect Agent", symbol: "plus", primary: true) { App.shared.select(.access) }
        b.keyEquivalent = "n"
        b.toolTip = "Get a one-time code for a new agent"
        i.view = b
        return i
    }

    override func show(_ m: Model) {
        loadViewIfNeeded()
        let colors: [NSColor] = [.systemBlue, .systemPurple, .systemOrange, .systemTeal, .systemPink, .systemIndigo]
        func color(_ s: String) -> NSColor { colors[Int(s.unicodeScalars.reduce(0) { $0 &+ $1.value }) % colors.count] }
        fill(key([m.agents.map { [$0.s("id"), $0.s("label"), $0.s("target"), $0.s("from"), ($0["agoSec"] as? Int ?? 0) / 60, $0.s("disconnected")] }, m.otherClients, m.games.map { [$0.s("name"), $0.s("access")] }])) {
            let live = m.agents.filter { $0["disconnected"] as? Bool != true }
            @MainActor func name(_ a: J) -> String { m.clients.first { $0.s("id") == a.s("client") }?.s("name").nilIfEmpty ?? a.s("label").nilIfEmpty ?? "Agent" }
            @MainActor func activity(_ a: J) -> NSView {
                guard let t = a.s("target").nilIfEmpty else { return chip("Idle", symbol: "moon.zzz", tint: .systemGray) }
                let watch = m.games.first { [$0.s("name"), $0.s("id")].contains(t) }?.s("access") == "watch"
                return watch ? chip("Watching " + t, symbol: "eye", tint: .systemGray) : chip("Playing " + t, symbol: "gamecontroller.fill", tint: .systemBlue)
            }
            let here = Set(live.map { $0.s("client") }), hereNames = Set(live.map(name))
            var before: [(name: String, ids: [String], c: J)] = []
            for c in m.otherClients where !here.contains(c.s("id")) && !hereNames.contains(c.s("name")) && c.s("status") == "allowed" {
                if let i = before.firstIndex(where: { $0.name == c.s("name") }) { before[i].ids.append(c.s("id")) } else { before.append((c.s("name"), [c.s("id")], c)) }
            }
            let connected: NSView = live.isEmpty
                ? box(wrapping("No agent is connected right now. Agents on this Mac connect by themselves; for others choose Connect Agent.", size: 13))
                : list(live.map { a in
                    let name = name(a)
                    let from = ["", "this computer"].contains(a.s("from")) ? "On this Mac" : "From anywhere"
                    let sub = "\(from) · \(ago(a["agoSec"] as? Int))"
                    var row: [NSView] = [avatar(name, color: color(name)), stack([label(name, weight: .semibold), label(sub, size: 11, color: .secondaryLabelColor)], spacing: 2), spacer(), activity(a)]
                    let id = a.s("id")
                    let b = Button("Disconnect") { App.shared.act("/panel/api/agents/disconnect", ["id": id]) }
                    b.setAccessibilityLabel("Disconnect \(name)")
                    row.append(b)
                    let r = stack(row, .horizontal, spacing: 10)
                    r.setAccessibilityElement(true)
                    r.setAccessibilityRole(.group)
                    r.setAccessibilityLabel("\(name), \(sub)")
                    return r
                })
            var views: [NSView] = [header("Connected"), connected]
            if !before.isEmpty {
                views += [header("Allowed before", "These agents may come back without asking again. Remove one to make it ask next time."),
                          list(before.map { name, ids, c in
                              let name = name.nilIfEmpty ?? "Agent"
                              let kind = ["remote", "link"].contains(c.s("kind")) ? "From anywhere" : "On this Mac"
                              let b = Button("Remove") { [weak self] in self?.revoke(ids, name) }
                              b.setAccessibilityLabel("Remove \(name)")
                              let r = stack([avatar(name, color: .systemGray), stack([label(name, weight: .semibold), label("\(kind) · Not connected now", size: 11, color: .secondaryLabelColor)], spacing: 2), spacer(), b], .horizontal, spacing: 10)
                              r.setAccessibilityElement(true)
                              r.setAccessibilityRole(.group)
                              r.setAccessibilityLabel("\(name), \(kind), not connected now")
                              return r
                          })]
            }
            return views
        }
    }

    func revoke(_ ids: [String], _ name: String) {
        guard let w = view.window else { return }
        let a = NSAlert()
        a.messageText = "Remove \(name)?"
        a.informativeText = "It loses access at once and has to ask again to come back."
        a.addButton(withTitle: "Remove").hasDestructiveAction = true
        a.addButton(withTitle: "Cancel")
        a.beginSheetModal(for: w) { r in
            if r == .alertFirstButtonReturn { ids.forEach { App.shared.act("/panel/api/clients/revoke", ["id": $0]) } }
        }
    }
}

// MARK: - Tools

@MainActor final class ToolsScreen: Page {
    let forMenu = NSPopUpButton(frame: .zero, pullsDown: false)
    var client = ""
    var model: Model?

    override var toolbarItems: [NSToolbarItem.Identifier] { [.toolsFor] }
    override func toolbarItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem? {
        guard id == .toolsFor else { return nil }
        forMenu.target = self
        forMenu.action = #selector(pick)
        forMenu.setAccessibilityLabel("Settings for")
        let s = stack([label("For", color: .secondaryLabelColor), forMenu], .horizontal, spacing: 6)
        let i = NSToolbarItem(itemIdentifier: id)
        i.view = s
        i.label = "For"
        return i
    }

    @objc func pick() {
        client = forMenu.selectedItem?.representedObject as? String ?? ""
        if let model { show(model) }
    }

    override func show(_ m: Model) {
        loadViewIfNeeded()
        model = m
        let names = ["All Agents"] + m.clients.map { $0.s("name") }
        if forMenu.itemTitles != names {
            forMenu.removeAllItems()
            forMenu.addItem(withTitle: "All Agents")
            for c in m.clients {
                forMenu.addItem(withTitle: c.s("name"))
                forMenu.lastItem?.representedObject = c.s("id")
            }
            if !m.clients.contains(where: { $0.s("id") == client }) { client = "" }
            forMenu.selectItem(at: m.clients.firstIndex { $0.s("id") == client }.map { $0 + 1 } ?? 0)
        }
        let who = client
        let tools = m.tools
        let on = { (t: J) -> Bool in (t["enabled"] as? Bool ?? true) && (who.isEmpty || !((t["offFor"] as? [String]) ?? []).contains(who)) }
        fill(key([who, tools.map { [$0.s("name"), on($0)] }])) {
            @MainActor func group(_ title: String, _ icon: String, _ tint: NSColor, _ level: String) -> NSView {
                let mine = tools.filter { $0.s("group").nilIfEmpty.map { $0 == title } ?? ($0.s("access") == level) }
                var features: [(name: String, tools: [J])] = []
                for t in mine {
                    let f = t.s("feature").nilIfEmpty ?? t.s("title").nilIfEmpty ?? t.s("name")
                    if let i = features.firstIndex(where: { $0.name == f }) { features[i].tools.append(t) } else { features.append((f, [t])) }
                }
                let rows = features.map { feature, ts -> NSView in
                    let names = ts.map { $0.s("name") }
                    let count = ts.filter(on).count
                    let mixed = count > 0 && count < ts.count
                    let sw = Toggle { v in
                        var f = ["tool": names.joined(separator: ","), "enabled": v ? "true" : "false"]
                        if !who.isEmpty { f["client"] = who }
                        App.shared.act("/panel/api/tools/set", f)
                    }
                    sw.state = count > 0 ? .on : .off
                    sw.isEnabled = who.isEmpty || ts.contains { $0["enabled"] as? Bool != false }
                    sw.setAccessibilityLabel(feature)
                    let desc = ts.map { "\($0.s("name")): \($0.s("title").nilIfEmpty ?? $0.s("description"))" }.joined(separator: "\n")
                    sw.toolTip = sw.isEnabled ? desc : "Switched off for every agent. Turn it on under All Agents first."
                    let note = label((mixed ? "some off · " : "") + names.joined(separator: " · "), size: 12, color: mixed ? .systemOrange : .secondaryLabelColor, mono: true)
                    note.lineBreakMode = .byWordWrapping
                    note.maximumNumberOfLines = 0
                    let text = stack([label(feature, weight: .semibold), note], spacing: 2)
                    text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
                    text.toolTip = desc
                    return stack([text, spacer(), sw], .horizontal, spacing: 10)
                }
                return stack([stack([tile(icon, tint, side: 24), label(title, size: 13, weight: .bold), spacer()], .horizontal, spacing: 8), list(rows)], spacing: 8, align: .width)
            }
            let hint = wrapping(who.isEmpty
                ? "A switched-off feature is closed for every agent, including over the internet. A switch shows on while any of its tools is on."
                : "These switches apply only to \(m.clients.first { $0.s("id") == who }?.s("name") ?? "this agent").", size: 13)
            let left = group("Look", "eye", .systemBlue, "watch")
            let play = group("Play", "gamecontroller", .systemGreen, "play"), change = group("Change", "wrench.adjustable", .systemOrange, "full")
            let right = stack([play, change], spacing: 20, align: .width)
            for g in [play, change] { g.widthAnchor.constraint(equalTo: right.widthAnchor).isActive = true }
            let cols = stack([left, right], .horizontal, spacing: 20, align: .top)
            cols.distribution = .fillEqually
            left.widthAnchor.constraint(equalTo: right.widthAnchor).isActive = true
            return [hint, cols]
        }
    }
}

final class Toggle: NSSwitch {
    var run: (Bool) -> Void = { _ in }
    convenience init(_ run: @escaping (Bool) -> Void) {
        self.init()
        self.run = run
        target = self
        action = #selector(fire)
    }
    @objc private func fire() { run(state == .on) }
}

// MARK: - Access from Anywhere

@MainActor final class AccessScreen: Page {
    override var column: CGFloat? { 720 }
    var code: J?
    var codeUntil = Date.distantPast
    var timer: Timer?
    let countdown = wrapping("", size: 13)

    override func show(_ m: Model) {
        loadViewIfNeeded()
        let r = m.remote
        if Date() > codeUntil { code = nil }
        fill(key([r.filter { $0.key != "onlineSec" }, code ?? [:]])) {
            let on = r["on"] as? Bool ?? false
            let sw = Toggle { [weak self] v in self?.flip(v, r) }
            sw.state = on ? .on : .off
            sw.setAccessibilityLabel("Access from anywhere")
            let state = r.s("state")
            var sub = "Through a private, encrypted link. Turn off to close it at once."
            if let e = r.s("error").nilIfEmpty { sub = e } else if on && state != "online" && !state.isEmpty { sub = "Connecting…" }
            let top = stack([tile("globe", .systemBlue, side: 44),
                             stack([label("Agents on other devices can reach your games", size: 14, weight: .semibold), wrapping(sub)], spacing: 3), spacer(), sw], .horizontal, spacing: 12)
            var views: [NSView] = [box(top)]

            if !on {
                views.append(contentsOf: setup(r))
                return views + local()
            }
            let address = r.s("address")
            let addr = label(address, mono: true)
            addr.isSelectable = true
            addr.lineBreakMode = .byTruncatingMiddle
            views.append(box(stack([stack([caption("Address"), addr], spacing: 4), spacer(), Button("Copy", symbol: "doc.on.doc") { clip(address) }], .horizontal, spacing: 10)))

            if let c = code {
                let big = label(c.s("code"), size: 34, weight: .bold, mono: true)
                big.isSelectable = true
                big.setAccessibilityLabel("Code \(c.s("code").map(String.init).joined(separator: " "))")
                let qrImage = NSImageView(image: qr(c.s("panel").nilIfEmpty ?? c.s("address"), side: 120) ?? NSImage())
                qrImage.setAccessibilityLabel("QR code to open UDriver on your phone")
                let left = stack([caption("Code for a new agent"), big, countdown,
                                  stack([Button("Copy", symbol: "doc.on.doc") { clip(c.s("code")) }, Button("New Code", symbol: "arrow.clockwise") { [weak self] in self?.newCode() }], .horizontal)], spacing: 10)
                left.setCustomSpacing(4, after: left.arrangedSubviews[0])
                left.setCustomSpacing(16, after: countdown)
                let right = stack([box(fixed(qrImage, width: 120, height: 120), padding: 6), label("Open on your phone", size: 11, color: .secondaryLabelColor)], spacing: 6, align: .centerX)
                views.append(box(stack([left, spacer(), right], .horizontal, spacing: 20, align: .top)))
            } else {
                views.append(box(stack([stack([caption("Code for a new agent"), wrapping("A new agent asks for a code the first time it connects from another device. Each code works once.", size: 13)], spacing: 6), spacer(),
                                          Button("New Code", primary: true) { [weak self] in self?.newCode() }], .horizontal, spacing: 12)))
            }
            return views + local()
        }
    }

    func local() -> [NSView] {
        let menu = NSMenu()
        menu.addItem(withTitle: "For Apps That Start UDriver Themselves", action: #selector(copyCommand), keyEquivalent: "").target = self
        menu.addItem(withTitle: "For Apps That Connect to UDriver…", action: #selector(copyKey), keyEquivalent: "").target = self
        let pick = NSComboButton(title: "Copy Setup", menu: menu, target: self, action: #selector(copyCommand))
        pick.controlSize = .large
        pick.toolTip = "Copies the setup for apps that start UDriver themselves. The arrow has the option for apps that connect over the network."
        pick.setAccessibilityLabel("Copy setup for an app on this Mac")
        let row = stack([tile("laptopcomputer", violet, side: 28),
                         stack([label("Apps on this Mac", size: 14, weight: .semibold), wrapping("Local models connect directly, without the internet.")], spacing: 3), spacer(), pick], .horizontal, spacing: 12)
        return [box(row)]
    }

    func setup(_ r: J) -> [NSView] {
        if r["cloudflared"] as? Bool == false {
            return [box(stack([wrapping("To reach your games from other devices, UDriver first downloads a small helper for the private link.", size: 13), spacer(),
                                 Button("Download Helper", primary: true) { App.shared.act("/panel/api/remote/install") }], .horizontal, spacing: 12))]
        }
        if r["saved"] as? Bool != true && r["signedIn"] as? Bool != true {
            let url = r.s("loginUrl")
            return [box(stack([wrapping("Sign in to Cloudflare once to create your private link. It is free.", size: 13), spacer(),
                                 Button(url.isEmpty ? "Sign In" : "Open Sign-In Page", primary: true) {
                                     if let u = URL(string: url), !url.isEmpty { NSWorkspace.shared.open(u) } else { App.shared.act("/panel/api/remote/login") }
                                 }], .horizontal, spacing: 12))]
        }
        return []
    }

    func flip(_ v: Bool, _ r: J) {
        guard let w = view.window else { return }
        if v {
            if r["saved"] as? Bool == true { App.shared.act("/panel/api/remote/on"); return }
            if r["signedIn"] as? Bool == true {
                let a = NSAlert()
                a.messageText = "Choose an address"
                a.informativeText = "Type the full address on your Cloudflare domain, for example games.example.com."
                let f = NSTextField(frame: .init(x: 0, y: 0, width: 260, height: 24))
                f.placeholderString = "games.example.com"
                a.accessoryView = f
                a.addButton(withTitle: "Turn On")
                a.addButton(withTitle: "Cancel")
                a.window.initialFirstResponder = f
                a.beginSheetModal(for: w) { res in
                    if res == .alertFirstButtonReturn { App.shared.act("/panel/api/remote/on", ["host": f.stringValue]) } else { self.last = "" }
                }
                return
            }
            last = ""
            alert("Finish setup first", "Follow the step below this switch, then turn it on.", in: w)
            show(App.shared.model)
            return
        }
        let a = NSAlert()
        a.messageText = "Turn off access from anywhere?"
        a.informativeText = "Agents and devices that connect over the internet lose access at once."
        a.addButton(withTitle: "Turn Off").hasDestructiveAction = true
        a.addButton(withTitle: "Cancel")
        a.beginSheetModal(for: w) { res in
            if res == .alertFirstButtonReturn { App.shared.act("/panel/api/remote/off") } else { self.last = ""; self.show(App.shared.model) }
        }
    }

    func newCode() {
        App.shared.act("/panel/api/remote/code") { [weak self] j in
            guard let self else { return }
            self.code = j
            self.codeUntil = Date().addingTimeInterval(TimeInterval(j["expiresSec"] as? Int ?? 600))
            self.timer?.invalidate()
            self.timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            self.tick()
        }
    }

    func tick() {
        let left = Int(codeUntil.timeIntervalSinceNow)
        if left <= 0 { timer?.invalidate(); code = nil; show(App.shared.model); return }
        countdown.stringValue = String(format: "Works once, expires in %d:%02d. Paste it on the page the agent opens.", left / 60, left % 60)
    }

    @objc func copyCommand() {
        let j: J = ["mcpServers": ["udriver": ["command": dotnetPath(), "args": [API.shared.dll]]]]
        clip(pretty(j))
        alert("Setup copied", "Paste it into the app's MCP settings. The app starts UDriver by itself when it needs it.", in: view.window)
    }

    @objc func copyKey() {
        guard let w = view.window else { return }
        let a = NSAlert()
        a.messageText = "Which app is this for?"
        a.informativeText = "UDriver makes a private key for this app. You can remove it later under Agents."
        let f = NSTextField(frame: .init(x: 0, y: 0, width: 260, height: 24))
        f.placeholderString = "App name"
        a.accessoryView = f
        a.addButton(withTitle: "Copy Setup")
        a.addButton(withTitle: "Cancel")
        a.window.initialFirstResponder = f
        a.beginSheetModal(for: w) { res in
            guard res == .alertFirstButtonReturn else { return }
            App.shared.act("/panel/api/local-clients", ["name": f.stringValue.nilIfEmpty ?? "App on this Mac"]) { j in
                clip(j["mcpJson"].map { $0 as? String ?? pretty($0) } ?? "")
                alert("Setup copied", "Paste it into the app's MCP settings. It connects while UDriver is running.", in: w)
            }
        }
    }

    func dotnetPath() -> String {
        let home = API.shared.home
        return [home + "/.udriver/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet"].first(where: FileManager.default.isExecutableFile) ?? "dotnet"
    }
}

// MARK: - Problems

@MainActor final class ProblemsScreen: Page {
    override var column: CGFloat? { 720 }

    override func show(_ m: Model) {
        loadViewIfNeeded()
        fill(key(m.problems.map { [$0.s("id"), $0.s("count"), $0.s("lastSeen")] })) {
            let foot = wrapping("When UDriver itself lets an agent down, a short note appears here. Nothing is sent on its own: Send to developer opens a filled-in page on GitHub, and you decide whether to press Submit there. Passwords, keys and your user name are removed first.")
            guard !m.problems.isEmpty else { return [box(wrapping("No problems found.", size: 13)), foot] }
            return [list(m.problems.map { p in
                let what = p.s("title").nilIfEmpty ?? p.s("what")
                let n = p["count"] as? Int ?? 1
                var sub = n > 1 ? "\(n) times" : "Once"
                if let d = ISO8601DateFormatter().date(from: String(p.s("lastSeen").prefix(19)) + "Z") {
                    sub += " · last " + d.formatted(.dateTime.day().month(.abbreviated).hour().minute().locale(Locale(identifier: "en_GB")))
                }
                let id = p.s("id"), url = p.s("issueUrl")
                let send = Button("Send to developer") { if let u = URL(string: url) { NSWorkspace.shared.open(u) } }
                let dismiss = Button("Dismiss") { App.shared.act("/panel/api/problems/dismiss", ["id": id]) }
                dismiss.setAccessibilityLabel("Dismiss: \(what)")
                let text = wrapping(what, size: 13, color: .labelColor)
                let r = stack([stack([text, label(sub, size: 11, color: .secondaryLabelColor)], spacing: 2), spacer(), send, dismiss], .horizontal, spacing: 10)
                text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
                r.setAccessibilityElement(true)
                r.setAccessibilityRole(.group)
                r.setAccessibilityLabel("\(what), \(sub)")
                return r
            }), foot]
        }
    }
}
