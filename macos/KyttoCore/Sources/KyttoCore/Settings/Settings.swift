import Foundation

/// Everything §7.6 lets the user change.
///
/// Defaults are chosen so that a user who never opens KyttoSettings has a sensible
/// app: nothing runs on its own, nothing is hidden, and the backup retention
/// matches §6.1.
public struct KyttoSettings: Codable, Equatable, Sendable {
    public enum Theme: String, Codable, CaseIterable, Sendable {
        case system
        case light
        case dark
    }

    /// How many backups to keep per client (§6.1).
    public var backupRetention: Int
    /// Above this, a server's context cost is called out in the matrix (§7.1).
    public var tokenWarningThreshold: Int
    public var theme: Theme
    public var launchAtLogin: Bool
    public var menuBarEnabled: Bool
    /// The client the menu bar item toggles servers in (§7.7).
    ///
    /// Nil means "the one with the most servers", recomputed as things change,
    /// which is right until the user says otherwise.
    public var menuBarClient: ClientID?
    /// Per-client replacements for the registry's paths (§7.6), keyed by the
    /// client's raw id.
    ///
    /// An escape hatch, and one that will be needed: these paths move between
    /// client releases, and a user should not have to wait for a Kytto update to
    /// point it at the right file.
    ///
    /// Raw strings rather than `ClientID` keys because Swift encodes a
    /// dictionary with enum keys as a flat array, which is unreadable in a file
    /// people are meant to be able to inspect.
    public var clientPathOverrides: [String: String]
    /// Additional user-selected configuration files. They are discovery-only:
    /// no write service receives descriptors built from this collection.
    public var customConfigSources: [CustomConfigSource]
    /// Cleared once the user has seen the first-run screen.
    public var hasCompletedOnboarding: Bool
    /// The matrix writes immediately, which is intentionally confirmed once
    /// before the first cell change rather than left as a tooltip surprise.
    public var hasConfirmedMatrixWrites: Bool
    /// Read-only sources are worth having and are rarely worth looking at: a
    /// machine that has run Claude Code in a dozen projects carries a dozen of
    /// them. Folding them away hides them from the sidebar and the matrix both,
    /// because half-hidden is what made them noise in the first place.
    public var showsCustomSources: Bool
    /// The sidebar's width in CSS pixels. User-draggable because the sidebar
    /// carries per-project Claude Code scopes, whose labels are paths — the one
    /// kind of text a fixed 196px cannot be right for. Remembered across
    /// launches, which is why it is a setting rather than web-layer state.
    public var sidebarWidth: Int

    public static let defaultSidebarWidth = 196
    public static let sidebarWidthRange = 170...480

    public static let `default` = KyttoSettings(
        backupRetention: 20,
        tokenWarningThreshold: 20_000,
        // §10: Kytto is an instrument panel, and its graphite dark appearance
        // is the first-run default. A saved user choice still wins on every
        // subsequent launch, including `system` and `light`.
        theme: .dark,
        launchAtLogin: false,
        menuBarEnabled: true,
        menuBarClient: nil,
        clientPathOverrides: [:],
        customConfigSources: [],
        hasCompletedOnboarding: false,
        hasConfirmedMatrixWrites: false,
        showsCustomSources: true,
        sidebarWidth: KyttoSettings.defaultSidebarWidth
    )

    public init(
        backupRetention: Int,
        tokenWarningThreshold: Int,
        theme: Theme,
        launchAtLogin: Bool,
        menuBarEnabled: Bool,
        menuBarClient: ClientID?,
        clientPathOverrides: [String: String],
        customConfigSources: [CustomConfigSource] = [],
        hasCompletedOnboarding: Bool,
        hasConfirmedMatrixWrites: Bool = false,
        showsCustomSources: Bool = true,
        sidebarWidth: Int = KyttoSettings.defaultSidebarWidth
    ) {
        self.backupRetention = backupRetention
        self.tokenWarningThreshold = tokenWarningThreshold
        self.theme = theme
        self.launchAtLogin = launchAtLogin
        self.menuBarEnabled = menuBarEnabled
        self.menuBarClient = menuBarClient
        self.clientPathOverrides = clientPathOverrides
        self.customConfigSources = customConfigSources
        self.hasCompletedOnboarding = hasCompletedOnboarding
        self.hasConfirmedMatrixWrites = hasConfirmedMatrixWrites
        self.showsCustomSources = showsCustomSources
        self.sidebarWidth = sidebarWidth
    }

    private enum CodingKeys: String, CodingKey {
        case backupRetention
        case tokenWarningThreshold
        case theme
        case launchAtLogin
        case menuBarEnabled
        case menuBarClient
        case clientPathOverrides
        case customConfigSources
        case hasCompletedOnboarding
        case hasConfirmedMatrixWrites
        case showsCustomSources
        case sidebarWidth
    }

    /// Settings files written by older versions must retain every setting they
    /// have and fall back to defaults for the rest. Synthesized Codable would
    /// reject such a file entirely because a newer non-optional key is absent.
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        backupRetention = try values.decodeIfPresent(Int.self, forKey: .backupRetention) ?? Self.default.backupRetention
        tokenWarningThreshold = try values.decodeIfPresent(Int.self, forKey: .tokenWarningThreshold) ?? Self.default.tokenWarningThreshold
        theme = try values.decodeIfPresent(Theme.self, forKey: .theme) ?? Self.default.theme
        launchAtLogin = try values.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? Self.default.launchAtLogin
        menuBarEnabled = try values.decodeIfPresent(Bool.self, forKey: .menuBarEnabled) ?? Self.default.menuBarEnabled
        menuBarClient = try values.decodeIfPresent(ClientID.self, forKey: .menuBarClient)
        clientPathOverrides = try values.decodeIfPresent([String: String].self, forKey: .clientPathOverrides) ?? [:]
        customConfigSources = try values.decodeIfPresent([CustomConfigSource].self, forKey: .customConfigSources) ?? []
        hasCompletedOnboarding = try values.decodeIfPresent(Bool.self, forKey: .hasCompletedOnboarding) ?? false
        hasConfirmedMatrixWrites = try values.decodeIfPresent(Bool.self, forKey: .hasConfirmedMatrixWrites) ?? false
        showsCustomSources = try values.decodeIfPresent(Bool.self, forKey: .showsCustomSources) ?? true
        sidebarWidth = try values.decodeIfPresent(Int.self, forKey: .sidebarWidth) ?? Self.defaultSidebarWidth
    }

    public func pathOverride(for client: ClientID) -> String? {
        clientPathOverrides[client.rawValue]
    }

    /// Keeps a hand-edited settings file from producing nonsense.
    public func sanitized() -> KyttoSettings {
        var result = self
        result.backupRetention = min(max(backupRetention, 1), 200)
        result.tokenWarningThreshold = min(max(tokenWarningThreshold, 1_000), 200_000)
        result.sidebarWidth = min(max(sidebarWidth, Self.sidebarWidthRange.lowerBound), Self.sidebarWidthRange.upperBound)
        // An override that is not an absolute path would silently resolve
        // somewhere surprising.
        result.clientPathOverrides = clientPathOverrides.filter {
            $0.value.hasPrefix("/") || $0.value.hasPrefix("~/")
        }
        var seen: Set<UUID> = []
        result.customConfigSources = customConfigSources.compactMap { source in
            guard !seen.contains(source.id),
                  source.path.hasPrefix("/") || source.path.hasPrefix("~/")
            else { return nil }
            seen.insert(source.id)
            var cleaned = source
            cleaned.displayName = source.displayName.trimmingCharacters(in: .whitespacesAndNewlines)
            cleaned.scopeLabel = source.scopeLabel.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !cleaned.displayName.isEmpty else { return nil }
            return cleaned
        }
        return result
    }
}

/// Settings on disk.
///
/// Written only when something is changed — a first launch that touches nothing
/// leaves no trace (§6.6).
public final class SettingsStore: @unchecked Sendable {
    private let url: URL
    private var cached: KyttoSettings?
    private let lock = NSLock()

    public init(paths: KyttoPaths) {
        url = paths.root.appending(path: "settings.json")
    }

    public var current: KyttoSettings {
        lock.lock()
        defer { lock.unlock() }
        if let cached { return cached }
        let loaded = (try? Data(contentsOf: url))
            .flatMap { try? JSONDecoder().decode(KyttoSettings.self, from: $0) }?
            .sanitized() ?? .default
        cached = loaded
        return loaded
    }

    @discardableResult
    public func update(_ change: (inout KyttoSettings) -> Void) throws -> KyttoSettings {
        try MutationCoordinator.sync {
        let previous = current
        var settings = previous
        change(&settings)
        let sanitized = settings.sanitized()
        guard sanitized != previous else { return previous }

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(sanitized)
        if let text = String(data: data, encoding: .utf8) {
            try AtomicWriter.write(text, to: url)
        }

        lock.lock()
        cached = sanitized
        lock.unlock()
        return sanitized
        }
    }
}
