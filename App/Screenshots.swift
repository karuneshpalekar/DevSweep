#if DEBUG
import AppKit
import DevSweepCore
import SwiftUI

/// Regenerates the README screenshots. Debug builds only:
///
///   DEVSWEEP_SHOTS=docs/screenshots build/.../DevSweep.app/Contents/MacOS/DevSweep
///
/// Walks the app through each screen in light and dark mode and saves PNGs
/// of its own windows (an app may capture its own windows without screen
/// recording permission).
@MainActor
enum ScreenshotTour {
    static func run(model: AppModel, to dir: URL) async {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.frame.width > 600 }) else {
            print("shots: no main window"); exit(1)
        }
        window.setContentSize(NSSize(width: 1280, height: 800))
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)

        for _ in 0..<120 where model.versions == nil || model.isCheckingVersions { await pause(0.5) }
        let panelItem = model.findings.first { $0.ruleID == "chrome-cache" } ?? model.findings.first
        let reviewIDs = pickReviewItems(model.findings)

        for mode in [AppearanceMode.light, .dark] {
            mode.apply()
            model.inspectedID = nil
            model.selection = .overview
            await pause(1.5)
            save(window, "overview-\(mode.rawValue)", dir)

            model.selection = .all
            await pause(1)
            model.inspectedID = panelItem?.id
            await pause(1.2)
            save(window, "items-\(mode.rawValue)", dir)

            model.inspectedID = nil
            model.selection = .runtimes
            await pause(0.8)
            model.selectedRuntimeID = model.versions?.runtimes.first { $0.steps.contains { $0.kind == .guided } }?.id
                ?? model.versions?.runtimes.first?.id
            await pause(1.4)
            save(window, "runtimes-\(mode.rawValue)", dir)
            model.selectedRuntimeID = nil

            model.selection = .history
            await pause(1)
            save(window, "history-\(mode.rawValue)", dir)

            model.selection = .all
            model.checked = reviewIDs
            model.outcomes = nil
            model.showReview = true
            await pause(1.5)
            saveWithSheet(window, "review-\(mode.rawValue)", dir)
            model.showReview = false
            model.checked = []
            await pause(0.8)

            await saveMenuBar(model, mode, dir)
        }
        AppearanceMode.current.apply()
        print("SHOTS OK")
        exit(0)
    }

    /// One item per kind of action, so the review sheet shows every group.
    private static func pickReviewItems(_ findings: [Finding]) -> Set<String> {
        var picked: [Finding] = []
        for kind in [CleanAction.Kind.command, .delete, .trash] {
            picked += findings.filter { $0.defaultAction?.kind == kind && $0.risk != .needsAdmin }.prefix(2)
        }
        if let chrome = findings.first(where: { $0.ruleID == "chrome-cache" }) { picked.append(chrome) }
        return Set(picked.map(\.id))
    }

    private static func pause(_ seconds: Double) async {
        try? await Task.sleep(for: .milliseconds(Int(seconds * 1000)))
    }

    private static func image(of window: NSWindow) -> CGImage? {
        CGWindowListCreateImage(.null, .optionIncludingWindow, CGWindowID(window.windowNumber),
                                [.boundsIgnoreFraming, .bestResolution])
    }

    private static func write(_ image: CGImage, _ name: String, _ dir: URL) {
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: dir.appendingPathComponent("\(name).png"))
        print("shots: \(name).png \(image.width)x\(image.height)")
    }

    private static func save(_ window: NSWindow, _ name: String, _ dir: URL) {
        guard let img = image(of: window) else { print("shots: failed \(name)"); return }
        write(img, name, dir)
    }

    /// The main window with its sheet drawn in place.
    private static func saveWithSheet(_ window: NSWindow, _ name: String, _ dir: URL) {
        guard let base = image(of: window) else { return }
        guard let sheet = window.attachedSheet, let sheetImg = image(of: sheet) else { write(base, name, dir); return }
        let scale = CGFloat(base.width) / window.frame.width
        guard let ctx = CGContext(data: nil, width: base.width, height: base.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        ctx.draw(base, in: CGRect(x: 0, y: 0, width: base.width, height: base.height))
        let f = sheet.frame, w = window.frame
        let rect = CGRect(x: (f.minX - w.minX) * scale, y: (f.minY - w.minY) * scale,
                          width: CGFloat(sheetImg.width), height: CGFloat(sheetImg.height))
        ctx.setShadow(offset: CGSize(width: 0, height: -8 * scale), blur: 30 * scale,
                      color: NSColor.black.withAlphaComponent(0.35).cgColor)
        ctx.draw(sheetImg, in: rect)
        if let out = ctx.makeImage() { write(out, name, dir) }
    }

    /// The menu bar popover, shown briefly in a real window so it renders
    /// exactly as on screen (offscreen drawing loses text and materials).
    private static func saveMenuBar(_ model: AppModel, _ mode: AppearanceMode, _ dir: URL) async {
        let content = MenuBarView()
            .environment(model)
            .background(.regularMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 12))
        let host = NSHostingView(rootView: content)
        host.frame.size = host.fittingSize
        let win = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        win.isOpaque = false
        win.backgroundColor = .clear
        win.hasShadow = false
        win.appearance = NSAppearance(named: mode == .dark ? .darkAqua : .aqua)
        win.contentView = host
        win.center()
        win.orderFrontRegardless()
        await pause(0.8)
        if let img = image(of: win) { write(img, "menubar-\(mode.rawValue)", dir) }
        win.orderOut(nil)
    }
}
#endif
