import AppKit
import Foundation
import KyttoCore
import UniformTypeIdentifiers
import WebKit

/// Serves the bundled web UI over `kytto://app/…`.
///
/// A custom scheme rather than `file://` because WebKit treats file URLs as
/// opaque origins and refuses to load ES modules from them. §3.1 wants modules
/// loaded directly with no build step, and this is what makes that possible.
final class KyttoSchemeHandler: NSObject, WKURLSchemeHandler {
    static let scheme = "kytto"
    static let host = "app"

    /// The one non-file route: `/icon/<clientID>.png`.
    private static let iconPrefix = "/icon/"

    /// `Resources/web` inside the app bundle.
    private let root: URL

    /// Client id → rendered PNG. The matrix rebuilds its DOM on every keystroke
    /// in the filter field, so this is asked for far more often than it changes —
    /// and it never changes within a launch.
    private var iconCache: [String: Data] = [:]

    override init() {
        guard let resourceURL = Bundle.main.resourceURL else {
            preconditionFailure("app bundle has no resource directory")
        }
        root = resourceURL.appending(path: "web").resolvingSymlinksInPath().standardizedFileURL
        super.init()
    }

    init(root: URL) {
        self.root = root.resolvingSymlinksInPath().standardizedFileURL
        super.init()
    }

    func webView(_ webView: WKWebView, start task: any WKURLSchemeTask) {
        guard let url = task.request.url, Self.isInternalURL(url) else {
            task.didFailWithError(SchemeError.badRequest)
            return
        }

        if url.path.hasPrefix(Self.iconPrefix) {
            serveIcon(named: String(url.path.dropFirst(Self.iconPrefix.count)), url: url, to: task)
            return
        }

        var relativePath = url.path
        if relativePath.isEmpty || relativePath == "/" {
            relativePath = "/index.html"
        }

        guard let candidate = Self.resolvedResourceURL(root: root, requestURL: url) else {
            task.didFailWithError(SchemeError.forbidden)
            return
        }

        guard let data = try? Data(contentsOf: candidate) else {
            task.didFailWithError(SchemeError.notFound(relativePath))
            return
        }

        let response = URLResponse(
            url: url,
            mimeType: Self.mimeType(for: candidate),
            expectedContentLength: data.count,
            textEncodingName: "utf-8"
        )
        task.didReceive(response)
        task.didReceive(data)
        task.didFinish()
    }

    func webView(_ webView: WKWebView, stop task: any WKURLSchemeTask) {}

    static func isInternalURL(_ url: URL) -> Bool {
        url.scheme == scheme && url.host == host && url.user == nil && url.password == nil && url.port == nil
    }

    static func resolvedResourceURL(root: URL, requestURL: URL) -> URL? {
        guard isInternalURL(requestURL) else { return nil }
        let cleanRoot = root.resolvingSymlinksInPath().standardizedFileURL
        let relative = requestURL.path.trimmingPrefix("/")
        let candidate = cleanRoot
            .appending(path: relative.isEmpty ? "index.html" : relative)
            .resolvingSymlinksInPath()
            .standardizedFileURL
        let rootPath = cleanRoot.path.hasSuffix("/") ? cleanRoot.path : cleanRoot.path + "/"
        guard candidate.path.hasPrefix(rootPath), candidate.path != cleanRoot.path else { return nil }
        return candidate
    }

    // MARK: - Client icons

    /// You recognise Cursor by its icon faster than by reading the word "Cursor",
    /// which is the whole argument for spending native code on this: the matrix
    /// has one column per client and their names do not fit in one.
    ///
    /// The web layer asks for an opaque client id and gets a picture back. It
    /// never learns that an icon comes from an application bundle, or that
    /// applications live in bundles at all (§3.2).
    private func serveIcon(named name: String, url: URL, to task: any WKURLSchemeTask) {
        let identifier = name.hasSuffix(".png") ? String(name.dropLast(4)) : name
        guard let data = icon(forClientID: identifier) else {
            task.didFailWithError(SchemeError.notFound(url.path))
            return
        }

        // An HTTP response rather than a bare URLResponse, purely so the
        // cache header is honoured — see `iconCache`.
        let response = HTTPURLResponse(
            url: url,
            statusCode: 200,
            httpVersion: "HTTP/1.1",
            headerFields: [
                "Content-Type": "image/png",
                "Content-Length": String(data.count),
                "Cache-Control": "max-age=86400",
            ]
        )
        guard let response else {
            task.didFailWithError(SchemeError.badRequest)
            return
        }
        task.didReceive(response)
        task.didReceive(data)
        task.didFinish()
    }

    private func icon(forClientID identifier: String) -> Data? {
        if let cached = iconCache[identifier] { return cached }
        guard let clientID = ClientID(rawValue: identifier) else { return nil }

        if !clientID.isBuiltIn {
            let image = Self.tile(symbolName: "doc.text.magnifyingglass", background: .systemIndigo)
            guard let data = Self.png(image, side: Self.iconSide) else { return nil }
            iconCache[identifier] = data
            return data
        }

        guard let descriptor = ClientRegistry.descriptorIfKnown(for: clientID) else { return nil }
        let installed = descriptor.bundleIdentifiers.lazy
            .compactMap { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) }
            .first

        // Three cases, and they are genuinely different things rather than one
        // thing with a fallback: an installed app has an icon; a client that is a
        // command line tool never had one; a client that is neither is one whose
        // config outlived it, and saying so is more use than a blank square.
        let image: NSImage = if let installed {
            NSWorkspace.shared.icon(forFile: installed.path)
        } else if descriptor.bundleIdentifiers.isEmpty {
            Self.tile(symbolName: "apple.terminal.fill", background: .black)
        } else {
            Self.tile(symbolName: "questionmark.app.dashed", background: .quaternaryLabelColor)
        }

        guard let data = Self.png(image, side: Self.iconSide) else { return nil }
        iconCache[identifier] = data
        return data
    }

    /// Drawn above Retina resolution for the 22px matrix icon, so it stays sharp
    /// when the row height or display scale changes.
    private static let iconSide: CGFloat = 64

    private static func tile(symbolName: String, background: NSColor) -> NSImage {
        let side = iconSide
        let glyph = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)
            .flatMap { $0.withSymbolConfiguration(.init(pointSize: side * 0.42, weight: .medium)) }
            .map { tinting($0, .white) }

        return NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let squircle = NSBezierPath(
                roundedRect: rect.insetBy(dx: side * 0.05, dy: side * 0.05),
                xRadius: side * 0.22,
                yRadius: side * 0.22
            )
            background.setFill()
            squircle.fill()

            if let glyph {
                glyph.draw(in: NSRect(
                    x: rect.midX - glyph.size.width / 2,
                    y: rect.midY - glyph.size.height / 2,
                    width: glyph.size.width,
                    height: glyph.size.height
                ))
            }
            return true
        }
    }

    /// A template image draws as a silhouette in whatever is currently set, so
    /// the colour has to be painted over it rather than passed to it.
    private static func tinting(_ image: NSImage, _ color: NSColor) -> NSImage {
        let result = NSImage(size: image.size)
        result.lockFocus()
        let bounds = NSRect(origin: .zero, size: image.size)
        image.draw(in: bounds)
        color.set()
        bounds.fill(using: .sourceAtop)
        result.unlockFocus()
        return result
    }

    private static func png(_ image: NSImage, side: CGFloat) -> Data? {
        let pixels = Int(side)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixels,
            pixelsHigh: pixels,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { return nil }
        rep.size = NSSize(width: side, height: side)

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:])
    }

    private static func mimeType(for url: URL) -> String {
        // UTType misses the web types we care about most, so those are explicit.
        switch url.pathExtension.lowercased() {
        case "html": return "text/html"
        case "css": return "text/css"
        case "js", "mjs": return "text/javascript"
        case "json": return "application/json"
        case "svg": return "image/svg+xml"
        default:
            return UTType(filenameExtension: url.pathExtension)?.preferredMIMEType
                ?? "application/octet-stream"
        }
    }

    enum SchemeError: LocalizedError {
        case badRequest
        case forbidden
        case notFound(String)

        var errorDescription: String? {
            switch self {
            case .badRequest: "Malformed resource request."
            case .forbidden: "Resource is outside the app bundle."
            case .notFound(let path): "No bundled resource at \(path)."
            }
        }
    }
}
