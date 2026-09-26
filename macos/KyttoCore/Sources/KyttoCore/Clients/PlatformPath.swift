import Foundation

/// A config location expressed once per platform, as data.
///
/// §3.2: the Windows entries are filled in now even though nothing reads them
/// until M7. Writing them while the macOS path is in front of you costs minutes;
/// reconstructing them later costs hours.
public struct PlatformPath: Equatable, Sendable {
    /// Tokens: `~` for the home directory.
    public let darwin: String
    /// Tokens: `%APPDATA%`, `%USERPROFILE%`.
    public let windows: String

    public init(darwin: String, windows: String) {
        self.darwin = darwin
        self.windows = windows
    }

    /// Resolves the path for the current platform against a home directory.
    ///
    /// `home` is a parameter rather than `FileManager.default.homeDirectoryForCurrentUser`
    /// so tests can point discovery at a temp directory and exercise the real code.
    public func resolve(home: URL) -> URL {
        #if os(Windows)
        var expanded = windows
        let appData = ProcessInfo.processInfo.environment["APPDATA"]
            ?? home.appending(path: "AppData/Roaming").path(percentEncoded: false)
        expanded = expanded.replacingOccurrences(of: "%APPDATA%", with: appData)
        expanded = expanded.replacingOccurrences(of: "%USERPROFILE%", with: home.path(percentEncoded: false))
        return URL(filePath: expanded.replacingOccurrences(of: "\\", with: "/"))
        #else
        guard darwin.hasPrefix("~/") else { return URL(filePath: darwin) }
        return home.appending(path: String(darwin.dropFirst(2)))
        #endif
    }

    /// What the UI shows. The web layer receives this string and never takes it
    /// apart — it has no business knowing about separators or `~` (§3.2).
    public func displayString() -> String {
        #if os(Windows)
        return windows
        #else
        return darwin
        #endif
    }
}
