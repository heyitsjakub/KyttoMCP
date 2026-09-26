import Foundation

/// Answers "is this app installed?" without discovery having to know how.
///
/// A protocol so tests can assert on a machine that has none of these clients,
/// and so M7 can swap in a registry lookup on Windows.
public protocol AppLocating: Sendable {
    func applicationExists(bundleIdentifier: String) -> Bool
    func executableExists(named name: String, home: URL) -> Bool
}

#if canImport(AppKit)
import AppKit

/// The real thing on macOS. This is the only file in KyttoCore that touches AppKit.
public struct SystemAppLocator: AppLocating {
    public init() {}

    public func applicationExists(bundleIdentifier: String) -> Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) != nil
    }

    public func executableExists(named name: String, home: URL) -> Bool {
        // A GUI app's PATH is not the user's shell PATH, so probing the usual
        // install locations is more reliable than `which`. The same gap is what
        // will make health checks fail in M5 unless the login shell is consulted.
        let candidates = [
            home.appending(path: ".local/bin"),
            URL(filePath: "/opt/homebrew/bin"),
            URL(filePath: "/usr/local/bin"),
            URL(filePath: "/usr/bin"),
        ]
        return candidates.contains { FileManager.default.isExecutableFile(atPath: $0.appending(path: name).path) }
    }
}
#endif

/// Fixed answers, for tests.
public struct StubAppLocator: AppLocating {
    private let installedBundleIDs: Set<String>
    private let installedExecutables: Set<String>

    public init(bundleIdentifiers: Set<String> = [], executables: Set<String> = []) {
        self.installedBundleIDs = bundleIdentifiers
        self.installedExecutables = executables
    }

    public func applicationExists(bundleIdentifier: String) -> Bool {
        installedBundleIDs.contains(bundleIdentifier)
    }

    public func executableExists(named name: String, home: URL) -> Bool {
        installedExecutables.contains(name)
    }
}
