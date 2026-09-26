import Foundation

/// Turns a descriptor's path into a real one, honouring the user's overrides.
///
/// §7.6 offers per-client path overrides, and §4 explains why they will be
/// needed: these locations move between client releases. Without an override the
/// user has to wait for a Kytto update before the app can see their config
/// again, which for a tool people rely on is not an acceptable answer.
///
/// The override replaces only the client's own server map. Files Kytto merely
/// reads alongside it — a deny list, an extension bundle — keep their real
/// locations, because those are not what moved.
public struct ClientPathResolver: Sendable {
    public let home: URL
    /// Client raw id → replacement path. `~` is expanded.
    public let overrides: [String: String]

    public init(home: URL, overrides: [String: String] = [:]) {
        self.home = home
        self.overrides = overrides
    }

    public func resolve(_ file: PlatformPath) -> URL {
        file.resolve(home: home)
    }

    /// The server map for a client, after any override.
    public func resolveServerMap(_ file: PlatformPath, for clientID: ClientID) -> URL {
        guard let override = overrides[clientID.rawValue], !override.isEmpty else {
            return file.resolve(home: home)
        }
        if override.hasPrefix("~/") {
            return home.appending(path: String(override.dropFirst(2)))
        }
        return URL(filePath: override)
    }

    /// What the UI shows for that file.
    public func displayServerMap(_ file: PlatformPath, for clientID: ClientID) -> String {
        guard let override = overrides[clientID.rawValue], !override.isEmpty else {
            return file.displayString()
        }
        return override
    }

    /// The file Kytto writes on behalf of `descriptor`'s client that `url`
    /// refers to, or nil when it refers to none of them.
    ///
    /// Built from the same sources the write services use: the server map, both
    /// where it is now and where the registry puts it by default, so a backup
    /// taken before an override was set can still go home; the deny list beside
    /// it; and one settings file per extension bundle. Installed bundles, plugins
    /// and read-only sources are never written, so nothing in them qualifies.
    ///
    /// The result is the resolved registry path, not `url`. A caller that writes
    /// there cannot be steered by how `url` is spelled — `..` after a symbolic
    /// link compares equal on paper and lands somewhere else on disk.
    public func writeTarget(matching url: URL, for descriptor: ClientDescriptor) -> URL? {
        guard !descriptor.isReadOnly else { return nil }
        let candidate = url.standardizedFileURL
        func matches(_ known: URL) -> Bool {
            known.standardizedFileURL.path == candidate.path
        }

        for source in descriptor.sources {
            switch source {
            case .serverMap(let file, _, _, let enablement):
                for known in [resolveServerMap(file, for: descriptor.id), resolve(file)] where matches(known) {
                    return known
                }
                if case .denyList(let denyFile, _, _) = enablement {
                    let known = resolve(denyFile)
                    if matches(known) { return known }
                }
            case .extensionBundles(_, let settingsDirectory, _):
                let known = resolve(settingsDirectory).appending(path: candidate.lastPathComponent)
                if candidate.pathExtension == "json", matches(known) { return known }
            case .bundledPackages, .readOnlyAutoServerMap, .scopedJSONServerMap:
                continue
            }
        }
        return nil
    }
}
