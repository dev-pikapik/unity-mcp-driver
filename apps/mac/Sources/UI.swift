import AppKit
import CoreImage.CIFilterBuiltins

func label(_ text: String, size: CGFloat = 13, weight: NSFont.Weight = .regular, color: NSColor = .labelColor, mono: Bool = false) -> NSTextField {
    let l = NSTextField(labelWithString: text)
    l.font = mono ? .monospacedSystemFont(ofSize: size, weight: weight) : .systemFont(ofSize: size, weight: weight)
    l.textColor = color
    l.lineBreakMode = .byTruncatingTail
    l.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    return l
}

func wrapping(_ text: String, size: CGFloat = 12, color: NSColor = .secondaryLabelColor) -> NSTextField {
    let l = NSTextField(wrappingLabelWithString: text)
    l.font = .systemFont(ofSize: size)
    l.textColor = color
    l.isSelectable = false
    return l
}

func stack(_ views: [NSView], _ axis: NSUserInterfaceLayoutOrientation = .vertical, spacing: CGFloat = 8, align: NSLayoutConstraint.Attribute? = nil) -> NSStackView {
    let s = NSStackView(views: views)
    s.orientation = axis
    s.spacing = spacing
    s.alignment = align ?? (axis == .vertical ? .leading : .centerY)
    return s
}

func spacer() -> NSView {
    let v = NSView()
    v.setContentHuggingPriority(.init(1), for: .horizontal)
    v.setContentHuggingPriority(.init(1), for: .vertical)
    return v
}

func symbol(_ name: String, _ label: String? = nil, size: CGFloat = 13) -> NSImage? {
    NSImage(systemSymbolName: name, accessibilityDescription: label)?.withSymbolConfiguration(.init(pointSize: size, weight: .regular))
}

/// A button that runs a closure. While its click waits on the server, App.act shows it as busy.
final class Button: NSButton {
    static weak var pressed: Button?
    var run: () -> Void = {}
    convenience init(_ title: String, symbol name: String? = nil, primary: Bool = false, run: @escaping () -> Void) {
        self.init(title: title, target: nil, action: nil)
        target = self
        action = #selector(fire)
        self.run = run
        bezelStyle = .push
        controlSize = .large
        if let name { image = symbol(name); imagePosition = .imageLeading }
        if primary { bezelColor = .controlAccentColor }
    }
    /// A click that opens a confirmation sheet stays pressed until the sheet's answer runs the action.
    @objc private func fire() {
        Button.pressed = self
        run()
        if window?.attachedSheet == nil { Button.pressed = nil }
    }
}

/// The rounded panel used for groups of rows.
func box(_ content: NSView, padding: CGFloat = 14) -> NSBox {
    let b = NSBox()
    b.boxType = .custom
    b.cornerRadius = 10
    b.borderWidth = 1
    b.borderColor = .separatorColor
    b.fillColor = .controlBackgroundColor
    b.contentViewMargins = .zero
    let inner = NSView()
    b.contentView = inner
    inner.addSubview(content)
    content.translatesAutoresizingMaskIntoConstraints = false
    inner.translatesAutoresizingMaskIntoConstraints = false
    NSLayoutConstraint.activate([
        inner.leadingAnchor.constraint(equalTo: b.leadingAnchor), inner.trailingAnchor.constraint(equalTo: b.trailingAnchor),
        inner.topAnchor.constraint(equalTo: b.topAnchor), inner.bottomAnchor.constraint(equalTo: b.bottomAnchor),
        content.leadingAnchor.constraint(equalTo: inner.leadingAnchor, constant: padding), content.trailingAnchor.constraint(equalTo: inner.trailingAnchor, constant: -padding),
        content.topAnchor.constraint(equalTo: inner.topAnchor, constant: padding), content.bottomAnchor.constraint(equalTo: inner.bottomAnchor, constant: -padding),
    ])
    return b
}

/// Rows separated by thin lines inside one panel.
func list(_ rows: [NSView]) -> NSBox {
    var views: [NSView] = []
    for (i, r) in rows.enumerated() {
        if i > 0 { let line = NSBox(); line.boxType = .separator; views.append(line) }
        views.append(r)
    }
    let s = stack(views, spacing: 10, align: .width)
    return box(s, padding: 12)
}

func avatar(_ name: String, color: NSColor) -> NSView {
    let v = NSTextField(labelWithString: String(name.prefix(1)).uppercased())
    v.font = .systemFont(ofSize: 14, weight: .semibold)
    v.textColor = .white
    v.alignment = .center
    v.wantsLayer = true
    v.layer?.backgroundColor = color.cgColor
    v.layer?.cornerRadius = 17
    v.setAccessibilityElement(false)
    let box = NSView()
    box.wantsLayer = true
    box.layer?.backgroundColor = color.cgColor
    box.layer?.cornerRadius = 17
    box.addSubview(v)
    v.translatesAutoresizingMaskIntoConstraints = false
    box.translatesAutoresizingMaskIntoConstraints = false
    NSLayoutConstraint.activate([
        box.widthAnchor.constraint(equalToConstant: 34), box.heightAnchor.constraint(equalToConstant: 34),
        v.centerXAnchor.constraint(equalTo: box.centerXAnchor), v.centerYAnchor.constraint(equalTo: box.centerYAnchor),
    ])
    return box
}

func chip(_ text: String, symbol name: String, tint: NSColor) -> NSView {
    let img = NSImageView(image: symbol(name, nil, size: 11) ?? NSImage())
    img.contentTintColor = tint
    let s = stack([img, label(text, size: 11, weight: .medium, color: tint)], .horizontal, spacing: 4)
    s.edgeInsets = .init(top: 3, left: 8, bottom: 3, right: 8)
    s.wantsLayer = true
    s.layer?.backgroundColor = tint.withAlphaComponent(0.13).cgColor
    s.layer?.cornerRadius = 10
    return s
}

func fixed(_ v: NSView, width: CGFloat? = nil, height: CGFloat? = nil) -> NSView {
    v.translatesAutoresizingMaskIntoConstraints = false
    if let width { v.widthAnchor.constraint(equalToConstant: width).isActive = true }
    if let height { v.heightAnchor.constraint(equalToConstant: height).isActive = true }
    return v
}

func clip(_ text: String) {
    NSPasteboard.general.clearContents()
    NSPasteboard.general.setString(text, forType: .string)
}

func alert(_ message: String, _ info: String = "", in window: NSWindow?) {
    let a = NSAlert()
    a.messageText = message
    a.informativeText = info
    if let window { a.beginSheetModal(for: window) } else { a.runModal() }
}

/// A box that takes files dropped on it.
final class DropBox: NSBox {
    var take: ([URL]) -> Void = { _ in }
    override func draggingEntered(_ s: NSDraggingInfo) -> NSDragOperation { .copy }
    override func performDragOperation(_ s: NSDraggingInfo) -> Bool {
        let urls = s.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        take(urls)
        return !urls.isEmpty
    }
}

let accent = NSColor.systemBlue
let violet = NSColor(srgbRed: 0.63, green: 0.47, blue: 0.95, alpha: 1)

/// A white symbol on a coloured rounded square.
func tile(_ name: String, _ color: NSColor, side: CGFloat = 28) -> NSView {
    let img = NSImageView(image: symbol(name, nil, size: side * 0.45) ?? NSImage())
    img.contentTintColor = .white
    img.unregisterDraggedTypes()
    let t = NSView()
    t.wantsLayer = true
    t.layer?.backgroundColor = color.cgColor
    t.layer?.cornerRadius = side * 0.25
    t.addSubview(img)
    img.translatesAutoresizingMaskIntoConstraints = false
    img.centerXAnchor.constraint(equalTo: t.centerXAnchor).isActive = true
    img.centerYAnchor.constraint(equalTo: t.centerYAnchor).isActive = true
    return fixed(t, width: side, height: side)
}

func dot(_ color: NSColor) -> NSView {
    let d = NSView()
    d.wantsLayer = true
    d.layer?.backgroundColor = color.cgColor
    d.layer?.cornerRadius = 3.5
    return fixed(d, width: 7, height: 7)
}

func sentence(_ s: String) -> String { s.prefix(1).uppercased() + s.dropFirst() }

/// The app logo: a steering wheel on a dark rounded square, drawn from the 24-point mockup outline.
func logo(_ side: CGFloat, background: Bool = true) -> NSImage {
    NSImage(size: .init(width: side, height: side), flipped: true) { r in
        if background {
            let inset = r.insetBy(dx: side * 0.1, dy: side * 0.1)
            let shape = NSBezierPath(roundedRect: inset, xRadius: inset.width * 0.225, yRadius: inset.width * 0.225)
            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow()
            shadow.shadowColor = .black.withAlphaComponent(0.3)
            shadow.shadowBlurRadius = side * 0.04
            shadow.shadowOffset = .init(width: 0, height: -side * 0.015)
            shadow.set()
            shape.fill()
            NSGraphicsContext.restoreGraphicsState()
            NSGradient(starting: NSColor(white: 0.18, alpha: 1), ending: NSColor(white: 0.07, alpha: 1))!.draw(in: shape, angle: 70)
        }
        let k = side * (background ? 0.62 : 1) / 24, o = (side - 24 * k) / 2
        func p(_ x: CGFloat, _ y: CGFloat) -> NSPoint { .init(x: o + x * k, y: o + y * k) }
        let wheel = NSBezierPath(ovalIn: .init(x: o + 3.5 * k, y: o + 3.5 * k, width: 17 * k, height: 17 * k))
        let spokes = NSBezierPath()
        spokes.move(to: p(3.8, 10.5)); spokes.line(to: p(10, 12))
        spokes.move(to: p(20.2, 10.5)); spokes.line(to: p(14, 12))
        spokes.move(to: p(12, 14.2)); spokes.line(to: p(12, 20.5))
        for path in [wheel, spokes] { path.lineWidth = 2 * k; path.lineCapStyle = .round }
        (background ? NSColor.white : NSColor.labelColor).setStroke()
        wheel.stroke(); spokes.stroke()
        accent.setFill()
        NSBezierPath(ovalIn: .init(x: o + 9.7 * k, y: o + 9.7 * k, width: 4.6 * k, height: 4.6 * k)).fill()
        return true
    }
}

func qr(_ text: String, side: CGFloat) -> NSImage? {
    let f = CIFilter.qrCodeGenerator()
    f.message = Data(text.utf8)
    f.correctionLevel = "M"
    guard let out = f.outputImage else { return nil }
    let scaled = out.transformed(by: .init(scaleX: side / out.extent.width * 2, y: side / out.extent.height * 2))
    let rep = NSCIImageRep(ciImage: scaled)
    let img = NSImage(size: .init(width: side, height: side))
    img.addRepresentation(rep)
    return img
}

/// A stand-in picture for a game that has no live preview.
func scene(for name: String, kind: String) -> NSImage {
    let hues: [CGFloat] = [0.58, 0.72, 0.08, 0.45, 0.95, 0.15]
    let h = hues[Int(name.unicodeScalars.reduce(0) { $0 &+ $1.value }) % hues.count]
    return NSImage(size: .init(width: 320, height: 200), flipped: false) { r in
        NSGradient(starting: NSColor(hue: h, saturation: 0.45, brightness: 0.85, alpha: 1),
                   ending: NSColor(hue: h, saturation: 0.7, brightness: 0.45, alpha: 1))!.draw(in: r, angle: -90)
        if let s = NSImage(systemSymbolName: kind == "project" ? "cube.transparent" : "gamecontroller", accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: 54, weight: .light).applying(.init(paletteColors: [.white.withAlphaComponent(0.85)]))) {
            s.draw(in: NSRect(x: r.midX - s.size.width / 2, y: r.midY - s.size.height / 2, width: s.size.width, height: s.size.height))
        }
        return true
    }
}

func ago(_ sec: Int?) -> String {
    guard let sec else { return "just connected" }
    if sec < 60 { return "active now" }
    if sec < 3600 { return "idle for \(sec / 60) min" }
    return "idle for \(sec / 3600) h"
}
