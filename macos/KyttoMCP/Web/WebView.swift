import AppKit
import SwiftUI
import WebKit

/// Hosts the web UI. §3: the frame is native, only the content is web.
///
/// Everything a user would notice as foreign is disabled here — rubber-band
/// scrolling, image dragging, text selection on chrome, the WebKit context menu
/// in release builds.
///
/// The web view sits on a vibrant surface rather than an opaque one. The page
/// leaves its chrome — the toolbar band and the sidebar — transparent, so what
/// shows through there is a real macOS material reacting to the desktop behind
/// the window. It is the one thing HTML cannot fake, and it costs a view.
struct WebView: NSViewRepresentable {
    let router: CommandRouter

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> ChromeView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        configuration.setURLSchemeHandler(KyttoSchemeHandler(), forURLScheme: KyttoSchemeHandler.scheme)
        configuration.userContentController.addScriptMessageHandler(
            router,
            contentWorld: .page,
            name: CommandRouter.messageHandlerName
        )

        let webView = NoBounceWebView(frame: .zero, configuration: configuration)
        webView.setValue(false, forKey: "drawsBackground")
        // Without this the page flashes white between navigation and first paint,
        // which on a dark desktop is the most visible thing the app ever does.
        webView.underPageBackgroundColor = .clear
        webView.allowsBackForwardNavigationGestures = false
        webView.allowsMagnification = false
        webView.navigationDelegate = context.coordinator
        webView.uiDelegate = context.coordinator

        #if DEBUG
        // Right-click → Inspect Element while developing.
        webView.isInspectable = true
        #endif

        router.attach(to: webView)

        let chrome = ChromeView(webView: webView)
        context.coordinator.chrome = chrome

        let url = URL(string: "\(KyttoSchemeHandler.scheme)://\(KyttoSchemeHandler.host)/index.html")!
        webView.load(URLRequest(url: url))
        return chrome
    }

    func updateNSView(_ nsView: ChromeView, context: Context) {}

    static func dismantleNSView(_ nsView: ChromeView, coordinator: Coordinator) {
        let webView = nsView.hostedWebView
        webView.stopLoading()
        webView.configuration.userContentController.removeScriptMessageHandler(
            forName: CommandRouter.messageHandlerName,
            contentWorld: .page
        )
        webView.navigationDelegate = nil
        webView.uiDelegate = nil
        coordinator.chrome = nil
    }

    final class Coordinator: NSObject, WKNavigationDelegate, WKUIDelegate {
        weak var chrome: ChromeView?

        /// A load replaces `documentElement`, and with it the custom properties
        /// the window measured into it. They go back on as soon as there is a
        /// document to put them on.
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            chrome?.publishWindowMetrics(force: true)
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.cancel)
                return
            }

            if Self.isInternalMainFrameNavigation(navigationAction, url: url) {
                decisionHandler(.allow)
                return
            }

            if Self.isExternalWebURL(url) {
                NSWorkspace.shared.open(url)
            }
            decisionHandler(.cancel)
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationResponse: WKNavigationResponse,
            decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
        ) {
            guard navigationResponse.isForMainFrame,
                  let url = navigationResponse.response.url,
                  KyttoSchemeHandler.isInternalURL(url) else {
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }

        func webView(
            _ webView: WKWebView,
            createWebViewWith configuration: WKWebViewConfiguration,
            for navigationAction: WKNavigationAction,
            windowFeatures: WKWindowFeatures
        ) -> WKWebView? {
            if let url = navigationAction.request.url, Self.isExternalWebURL(url) {
                NSWorkspace.shared.open(url)
            }
            return nil
        }

        @available(macOS 12.0, *)
        func webView(
            _ webView: WKWebView,
            requestMediaCapturePermissionFor origin: WKSecurityOrigin,
            initiatedByFrame frame: WKFrameInfo,
            type: WKMediaCaptureType,
            decisionHandler: @escaping (WKPermissionDecision) -> Void
        ) {
            decisionHandler(.deny)
        }

        func webView(
            _ webView: WKWebView,
            didFailProvisionalNavigation navigation: WKNavigation!,
            withError error: any Error
        ) {
            showStartupFailure(in: webView)
        }

        func webView(
            _ webView: WKWebView,
            didFail navigation: WKNavigation!,
            withError error: any Error
        ) {
            showStartupFailure(in: webView)
        }

        private func showStartupFailure(in webView: WKWebView) {
            let html = """
            <!doctype html><meta charset="utf-8">
            <meta name="color-scheme" content="light dark">
            <title>Kytto could not start</title>
            <style>body{font:15px system-ui;padding:32px;max-width:42rem}h1{font-size:20px}</style>
            <h1>Kytto could not load its interface</h1>
            <p>Quit and reopen Kytto. If the problem continues, reinstall the application.</p>
            """
            // about:blank has no trusted bridge origin, so this diagnostic page
            // cannot invoke native commands even though it is in the same view.
            webView.loadHTMLString(html, baseURL: nil)
        }

        private static func isInternalMainFrameNavigation(
            _ action: WKNavigationAction,
            url: URL
        ) -> Bool {
            action.targetFrame?.isMainFrame == true && KyttoSchemeHandler.isInternalURL(url)
        }

        private static func isExternalWebURL(_ url: URL) -> Bool {
            url.scheme == "https" || url.scheme == "http"
        }
    }
}

/// The vibrant surface the page is drawn on, and the one place that knows how
/// this window is put together.
///
/// It also measures the two pieces of macOS geometry the layout needs — how tall
/// the title bar is and how far in the traffic lights reach — and hands them to
/// the page as CSS custom properties. The web layer positions against those
/// variables and never learns what produced them, which is what keeps §3.2 true:
/// on Windows the same properties are whatever that shell measures, or zero.
final class ChromeView: NSVisualEffectView {
    private let webView: WKWebView
    var hostedWebView: WKWebView { webView }
    private let dragView = WindowDragView()
    private var published: (titlebar: CGFloat, trafficLights: CGFloat)?
    private var configuredUITestFrame = false

    init(webView: WKWebView) {
        self.webView = webView
        super.init(frame: .zero)

        material = .sidebar
        blendingMode = .behindWindow
        // Chrome dims when the window loses focus, the way every other Mac app does.
        state = .followsWindowActiveState

        webView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(webView)
        NSLayoutConstraint.activate([
            webView.leadingAnchor.constraint(equalTo: leadingAnchor),
            webView.trailingAnchor.constraint(equalTo: trailingAnchor),
            webView.topAnchor.constraint(equalTo: topAnchor),
            webView.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])

        // The titlebar is visually drawn by the page, but it cannot be made
        // draggable by HTML. Keep a native hit-test surface above the web view
        // and size it to the measured titlebar band in `layout()`.
        addSubview(dragView)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("not loaded from a nib")
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        configureWindow()
        publishWindowMetrics()
    }

    /// Full screen and a window moved between displays both change the numbers.
    override func layout() {
        super.layout()
        publishWindowMetrics()
    }

    private func configureWindow() {
        guard let window else { return }

        // The content runs the full height of the window and the title bar becomes
        // a transparent strip over the top of it. That strip still belongs to
        // AppKit — it is what drags the window, and it is why the page keeps its
        // interactive controls below `--titlebar-height` rather than under the
        // traffic lights.
        window.styleMask.insert(.fullSizeContentView)
        window.isMovable = true
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.titlebarSeparatorStyle = .none

        // Xcode's UI-test display is smaller than the product's default window,
        // which otherwise launches zoomed and cannot prove that the drag strip
        // moves a normal window. This affects only the already-isolated test
        // home and never a production launch.
        if !configuredUITestFrame,
           ProcessInfo.processInfo.environment["KYTTO_TEST_HOME"] != nil,
           let visibleFrame = window.screen?.visibleFrame {
            configuredUITestFrame = true
            let size = NSSize(
                width: min(900, visibleFrame.width - 80),
                height: min(520, visibleFrame.height - 80)
            )
            let origin = NSPoint(
                x: visibleFrame.midX - size.width / 2,
                y: visibleFrame.midY - size.height / 2
            )
            window.setFrame(NSRect(origin: origin, size: size), display: true)
        }
    }

    func publishWindowMetrics(force: Bool = false) {
        guard let windowMetrics else {
            dragView.frame = .zero
            return
        }

        layoutDragView(for: windowMetrics)

        // The rightmost of the three buttons decides where content may start.
        // Reading it beats hardcoding 78: the spacing has changed between macOS
        // releases and it differs again in full screen, where the buttons go away.
        let metrics = windowMetrics
        // `layout()` runs on every frame of a live resize; the page only needs to
        // hear about it when one of the two numbers actually moved.
        let unchanged = published.map { $0 == metrics } ?? false
        guard force || !unchanged else { return }
        published = metrics

        // Set on the root element rather than injected as a stylesheet, so a
        // resize updates the same properties instead of stacking rules.
        //
        // The alpha is what turns the page's chrome into a tint over the material
        // this view is drawing. It travels with the measurements because it is the
        // same fact about the shell: there is something behind the page here.
        webView.evaluateJavaScript(
            """
            document.documentElement.style.setProperty('--titlebar-height', '\(metrics.titlebar)px');
            document.documentElement.style.setProperty('--traffic-light-inset', '\(metrics.trafficLights)px');
            document.documentElement.style.setProperty('--chrome-alpha', '\(Self.chromeAlpha)');
            """
        )
    }

    private var windowMetrics: (titlebar: CGFloat, trafficLights: CGFloat)? {
        guard let window, let contentView = window.contentView else { return nil }

        let titlebar = contentView.bounds.height - window.contentLayoutRect.height
        let buttons: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
        let trafficLights = buttons
            .compactMap { window.standardWindowButton($0) }
            .filter { !$0.isHidden }
            .map(\.frame.maxX)
            .max() ?? 0

        return (titlebar: titlebar.rounded(), trafficLights: trafficLights.rounded())
    }

    private func layoutDragView(for metrics: (titlebar: CGFloat, trafficLights: CGFloat)) {
        let height = min(max(0, metrics.titlebar), bounds.height)
        let leading = min(
            bounds.width,
            max(16, metrics.trafficLights + 12)
        )

        dragView.frame = NSRect(
            x: leading,
            y: bounds.height - height,
            width: max(0, bounds.width - leading),
            height: height
        )
    }

    /// Enough tint to keep the chrome a surface and read as one colour, little
    /// enough that the material behind it still moves with the desktop.
    private static let chromeAlpha = "58%"
}

/// A native, non-interactive titlebar surface above the web view.
///
/// The page can draw the titlebar, but only AppKit can hand the original mouse
/// event to the Window Server so the window participates in normal macOS drag
/// behavior, including moving between Spaces.
private final class WindowDragView: NSView {
    override var mouseDownCanMoveWindow: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.performDrag(with: event)
    }
}

/// A web view that does not bounce.
///
/// The elastic overscroll is the single clearest tell that a window is a browser
/// rather than an app, and the matrix is a table that should sit still at its edges.
private final class NoBounceWebView: WKWebView {
    override func scrollWheel(with event: NSEvent) {
        // Let the page scroll, but never let the enclosing scroll view rubber-band.
        enclosingScrollView?.verticalScrollElasticity = .none
        enclosingScrollView?.horizontalScrollElasticity = .none
        super.scrollWheel(with: event)
    }

    #if !DEBUG
    override func willOpenMenu(_ menu: NSMenu, with event: NSEvent) {
        // WebKit's own menu offers Reload and Back, which mean nothing here.
        // Native per-row context menus arrive in M3.
        menu.items.removeAll()
    }
    #endif
}
