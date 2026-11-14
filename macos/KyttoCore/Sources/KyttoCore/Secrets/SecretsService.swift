import Foundation

/// One place a secret value appears.
public struct SecretUsage: Equatable, Sendable {
    public let serverID: String
    public let serverName: String
    public let clientID: ClientID
    public let pathDisplay: String
}

/// Something about where a secret is sitting that the user would want to know.
public enum SecretExposure: Equatable, Sendable {
    /// The config file can be read by users other than its owner.
    case readableByOthers(pathDisplay: String, mode: String)
    /// The config file is inside a Git working tree and is not ignored.
    case insideGitRepository(pathDisplay: String, repositoryPathDisplay: String)
}

/// A secret value, and everywhere it lives.
public struct SecretRecord: Identifiable, Sendable {
    public let id: String
    public let key: String
    /// Only ever this. The real value stays native-side (§6).
    public let maskedValue: String
    public let usages: [SecretUsage]
    /// Whether Kytto holds its own copy in the platform credential store.
    ///
    /// Named for the concept rather than for this platform's product: the same
    /// field crosses to a web layer shared with the Windows build, where the
    /// store is Credential Manager (§3.2).
    public let isInSecretStore: Bool
    public let exposure: [SecretExposure]

    /// The same value in more than one client — the case rotation exists for.
    public var isShared: Bool {
        Set(usages.map(\.clientID)).count > 1
    }
}

/// Finds secrets, changes them everywhere at once, and points out where they are
/// sitting badly.
///
/// What this deliberately does **not** do: replace the value in the config with a
/// reference. No MCP client understands one — they read the file literally — so
/// a "reference" would either break every server or require Kytto to wrap every
/// command in its own launcher, making a working setup depend on Kytto staying
/// installed. The honest version is this: Kytto is where you manage the value,
/// the file still holds it, and Kytto tells you exactly where "it" is.
public struct SecretsService: Sendable {
    private let home: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let ledger: DigestLedger
    private let store: any SecretStoring
    private let descriptors: [ClientDescriptor]
    /// Where switched-off copies live. Without it, a secret held only in a
    /// parked copy is refused rather than written into the file (§7.12).
    private let parkStore: ParkStore?

    public init(
        home: URL,
        writer: ConfigWriter,
        ledger: DigestLedger,
        store: any SecretStoring,
        parkStore: ParkStore? = nil,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        self.home = home
        self.writer = writer
        self.ledger = ledger
        self.store = store
        self.parkStore = parkStore
        self.descriptors = descriptors
    }

    // MARK: - Inventory

    /// Groups every sensitive-looking environment value by what it actually is.
    ///
    /// Keyed on name *and* value, so the same token under one name in three
    /// clients is one row — and two different tokens sharing a name are two,
    /// which is exactly the drift worth seeing.
    public func scan(servers: [Server]) -> [SecretRecord] {
        var groups: [String: (key: String, value: String, usages: [SecretUsage])] = [:]

        for server in servers {
            for (clientID, entries) in server.envByClient {
                guard let descriptor = descriptors.first(where: { $0.id == clientID }),
                      let source = descriptor.editableServerMap
                else { continue }

                for entry in entries {
                    guard let value = entry.value, !value.isEmpty,
                          Self.looksSensitive(key: entry.key, value: value)
                    else { continue }

                    let id = Self.identifier(key: entry.key, value: value)
                    let usage = SecretUsage(
                        serverID: server.id,
                        serverName: server.name,
                        clientID: clientID,
                        pathDisplay: resolver.displayServerMap(source.file, for: clientID)
                    )
                    groups[id, default: (entry.key, value, [])].usages.append(usage)
                }
            }
        }

        let known = store.storedIdentifiers()
        // Every secret in one config sits in the same file, and the Git answer
        // costs a process (up to `gitCheckTimeout`) on the main thread. Asked
        // once per file per scan, not once per secret.
        var repositories: [String: URL?] = [:]
        return groups
            .map { id, group in
                SecretRecord(
                    id: id,
                    key: group.key,
                    maskedValue: Self.mask(group.value),
                    usages: group.usages.sorted { $0.clientID.rawValue < $1.clientID.rawValue },
                    isInSecretStore: known.contains(id),
                    exposure: exposure(for: group.usages, repositories: &repositories)
                )
            }
            .sorted { ($0.key, $0.id) < ($1.key, $1.id) }
    }

    /// Reveals a value, for the one case §6 allows: the user asked, explicitly.
    public func reveal(secretID: String, servers: [Server]) -> String? {
        for server in servers {
            for entries in server.envByClient.values {
                for entry in entries {
                    guard let value = entry.value, !value.isEmpty else { continue }
                    if Self.identifier(key: entry.key, value: value) == secretID { return value }
                }
            }
        }
        return try? store.value(for: secretID)
    }

    // MARK: - Rotation

    public struct RotationResult: Sendable {
        public let key: String
        public let updated: [SecretUsage]
        public let backupIDs: [String]
    }

    /// Writes a new value everywhere the old one appeared.
    ///
    /// The point of the whole feature: rotating a token today means finding every
    /// config that has it and editing each by hand, which is how one gets missed.
    public func rotate(
        secretID: String,
        to newValue: String,
        servers: [Server],
        alsoStoreInSecretStore: Bool
    ) throws -> RotationResult {
        try MutationCoordinator.sync {
        let records = scan(servers: servers)
        guard let record = records.first(where: { $0.id == secretID }) else {
            throw SecretsError.notFound
        }
        guard !newValue.isEmpty else { throw SecretsError.emptyValue }

        let newID = Self.identifier(key: record.key, value: newValue)
        if newID == secretID {
            if alsoStoreInSecretStore { try store.store(newValue, for: newID) }
            return RotationResult(key: record.key, updated: [], backupIDs: [])
        }

        var updated: [SecretUsage] = []
        var backupIDs: [String] = []
        var originals: [URL: Data?] = [:]
        /// Each file this rotation changed, with the digest it left behind.
        /// Rollback only touches a file that still has exactly those bytes.
        var written: [(url: URL, digest: String)] = []
        /// Each parked definition changed, with the text it had before and the
        /// text this rotation gave it.
        var parksWritten: [(previous: ParkStore.Entry, written: String)] = []
        let oldStoredValue = record.isInSecretStore ? reveal(secretID: secretID, servers: servers) : nil
        // Credential changes are undone only if they happened, and back to
        // what was there before: the destination id may already hold a value
        // the user saved earlier (AUD-06).
        var storedNewCredential = false
        var deletedOldCredential = false
        let previousNewCredential = try? store.value(for: newID)

        // Grouped per file, so a config holding the same token for two servers is
        // opened, backed up and written once rather than twice.
        let byClient = Dictionary(grouping: record.usages, by: \.clientID)
        do {
        for (clientID, usages) in byClient.sorted(by: { $0.key.rawValue < $1.key.rawValue }) {
            guard let descriptor = descriptors.first(where: { $0.id == clientID }),
                  let source = descriptor.editableServerMap
            else { continue }

            let url = resolver.resolveServerMap(source.file, for: clientID)
            originals[url] = try? Data(contentsOf: url)
            let display = resolver.displayServerMap(source.file, for: clientID)
            let receipt: WriteReceipt
            // Copies switched off in a presence-only client are not in the file.
            // Writing the value there would put a half-definition back — a server
            // switched on with nothing but an `env` — so they are found here and
            // changed where they live, after the files.
            var parkedUsages: [SecretUsage] = []
            switch source.format {
            case .json:
                receipt = try writer.edit(
                    url: url,
                    clientID: clientID,
                    pathDisplay: display,
                    expecting: ledger.digest(for: url)
                ) { document in
                    var text = document.sourceText
                    for usage in usages {
                        let current = try JSONDocument.parse(text)
                        // The spelling this file uses, which need not be the row's.
                        guard let name = try current.serverKey(for: usage.serverID, under: source.serversKey) else {
                            parkedUsages.append(usage)
                            continue
                        }
                        text = try Self.replacingEnvironmentValue(
                            record.key, with: newValue, in: current, at: [source.serversKey, name],
                            environmentKey: descriptor.serverSchema.environmentKey
                        )
                    }
                    return text
                }
            case .toml:
                receipt = try writer.edit(
                    url: url,
                    clientID: clientID,
                    pathDisplay: display,
                    expecting: ledger.digest(for: url),
                    as: TOMLDocument.self
                ) { document in
                    var text = document.sourceText
                    for usage in usages {
                        let current = try TOMLDocument.parse(text)
                        text = try current.settingServerEnvironmentValue(
                            record.key,
                            to: newValue,
                            forServer: try current.serverName(for: usage.serverID, under: source.serversKey)
                                ?? usage.serverName,
                            under: source.serversKey
                        )
                    }
                    return text
                }
            }
            ledger.record(receipt.digest, for: url)
            if receipt.didWrite { written.append((url, receipt.digest)) }

            for usage in parkedUsages {
                guard let parkStore,
                      let entry = parkStore.parked(clientID: clientID, serverName: usage.serverName)
                else { throw SecretsError.notFound }
                let text = try Self.replacingEnvironmentValue(
                    record.key, with: newValue, in: JSONDocument.parse(entry.sourceText), at: []
                )
                try parkStore.park(clientID: clientID, serverName: entry.serverName, sourceText: text)
                parksWritten.append((entry, text))
            }

            if let backupID = receipt.backupID { backupIDs.append(backupID) }
            updated.append(contentsOf: usages)
        }
        if alsoStoreInSecretStore {
            // Marked before each call: a store that fails part-way may still
            // have changed the record, and restoring the previous state is
            // harmless when it did not.
            storedNewCredential = true
            try store.store(newValue, for: newID)
            deletedOldCredential = true
            try store.delete(for: secretID)
        }
        } catch {
            var rollbackFailures: [String] = []
            for (previous, text) in parksWritten.reversed() {
                // Only a definition that still holds what this rotation wrote
                // is put back; anything else was changed after it.
                guard let parkStore,
                      parkStore.parked(clientID: previous.clientID, serverName: previous.serverName)?.sourceText == text
                else {
                    rollbackFailures.append("\(previous.clientID.rawValue) (switched-off copy of \(previous.serverName), changed since)")
                    continue
                }
                do {
                    try parkStore.park(
                        clientID: previous.clientID, serverName: previous.serverName, sourceText: previous.sourceText
                    )
                } catch {
                    rollbackFailures.append("\(previous.clientID.rawValue) (switched-off copy of \(previous.serverName))")
                }
            }
            for (url, digest) in written.reversed() {
                // A file another program saved after this rotation wrote it
                // keeps that save. Putting the old bytes back over it would
                // erase newer work to undo older work (AUD-03).
                do {
                    if let original = originals[url] ?? nil {
                        try AtomicWriter.write(original, to: url, expecting: .digest(digest))
                        ledger.record(ConfigWriter.digest(of: original), for: url)
                    } else if ConfigWriter.digest(of: url) == digest {
                        try FileManager.default.removeItem(at: url)
                        ledger.record(nil, for: url)
                    } else if FileManager.default.fileExists(atPath: url.path) {
                        throw AtomicWriter.WriteError.targetChanged
                    }
                } catch AtomicWriter.WriteError.targetChanged {
                    rollbackFailures.append("\(url.path) (changed by another app since; left as it is)")
                } catch {
                    rollbackFailures.append(url.path)
                }
            }
            if storedNewCredential {
                if let previousNewCredential {
                    try? store.store(previousNewCredential, for: newID)
                } else {
                    try? store.delete(for: newID)
                }
            }
            if deletedOldCredential, let oldStoredValue { try? store.store(oldStoredValue, for: secretID) }
            if !rollbackFailures.isEmpty {
                throw SecretsError.rollbackFailed(paths: rollbackFailures, backupIDs: backupIDs)
            }
            throw error
        }

        return RotationResult(key: record.key, updated: updated, backupIDs: backupIDs)
        }
    }

    /// Splices a new value over one environment variable of the definition at
    /// `path`, and nothing else.
    ///
    /// The variable has to be there already. Rotation changes a value it found;
    /// a missing path means the file moved since the scan, and creating it would
    /// write a definition nobody asked for.
    private static func replacingEnvironmentValue(
        _ key: String,
        with value: String,
        in document: JSONDocument,
        at path: [String],
        environmentKey: String = "env"
    ) throws -> String {
        let envPath = path + [environmentKey]
        guard let members = document.value(at: envPath)?.members,
              let member = try JSONDocument.soleMember(key, in: members, at: envPath)
        else { throw JSONEditError.pathNotFound(envPath + [key]) }
        return document.replacing(member.value.span, with: JSONText.string(value))
    }

    /// Keeps a copy of the current value, so a later scrub or mistake is recoverable.
    public func adopt(secretID: String, servers: [Server]) throws {
        guard let value = reveal(secretID: secretID, servers: servers) else {
            throw SecretsError.notFound
        }
        try store.store(value, for: secretID)
    }

    public func forget(secretID: String) throws {
        try store.delete(for: secretID)
    }

    // MARK: - Exposure

    /// Restricts a config file to its owner.
    ///
    /// Small, reversible, and the only hardening available while the value has to
    /// stay in the file: a token in a `0644` file is readable by anything running
    /// as another user on the machine.
    ///
    /// Throws rather than reporting success when nothing was changed: the UI
    /// tells the user the file is now private, and that sentence has to be true.
    @discardableResult
    public func restrictPermissions(clientID: ClientID) throws -> Bool {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw SecretsError.noConfigFile(clientID.rawValue) }

        let url = resolver.resolveServerMap(source.file, for: clientID)
        let display = resolver.displayServerMap(source.file, for: clientID)
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw SecretsError.noConfigFile(display)
        }
        // `setAttributes` follows a symbolic link, so a config kept in a dotfiles
        // folder is tightened where it actually lives.
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)

        // Checked, not assumed: a file on a volume that ignores modes takes the
        // call without complaint and stays readable.
        let attributes = try FileManager.default.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)
        let mode = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o777
        guard mode & 0o077 == 0 else {
            throw SecretsError.permissionsUnchanged(display)
        }
        return true
    }

    private func exposure(for usages: [SecretUsage], repositories: inout [String: URL?]) -> [SecretExposure] {
        var found: [SecretExposure] = []
        var seen: Set<ClientID> = []

        for usage in usages where !seen.contains(usage.clientID) {
            seen.insert(usage.clientID)
            guard let descriptor = descriptors.first(where: { $0.id == usage.clientID }),
                  let source = descriptor.editableServerMap
            else { continue }
            let url = resolver.resolveServerMap(source.file, for: usage.clientID)
            let display = resolver.displayServerMap(source.file, for: usage.clientID)

            if let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
               let permissions = attributes[.posixPermissions] as? NSNumber {
                let mode = permissions.uint16Value
                if mode & 0o077 != 0 {
                    found.append(
                        .readableByOthers(pathDisplay: display, mode: String(format: "%03o", mode & 0o777))
                    )
                }
            }

            let repository: URL?
            if let known = repositories[url.path] {
                repository = known
            } else {
                repository = gitRepository(containing: url)
                repositories[url.path] = .some(repository)
            }
            if let repository {
                found.append(
                    .insideGitRepository(
                        pathDisplay: display,
                        repositoryPathDisplay: abbreviate(repository.path(percentEncoded: false))
                    )
                )
            }
        }
        return found
    }

    /// The Git working tree this file sits in, if any and if the file is not ignored.
    ///
    /// Rare for the global configs v1 supports, but people do keep their home
    /// directory in a dotfiles repository, and a token committed there is the
    /// exact accident §6 opens with.
    private func gitRepository(containing url: URL) -> URL? {
        var directory = url.deletingLastPathComponent()
        while directory.path != "/" {
            if FileManager.default.fileExists(atPath: directory.appending(path: ".git").path) {
                return isIgnoredByGit(url, in: directory) ? nil : directory
            }
            let parent = directory.deletingLastPathComponent()
            if parent == directory { break }
            directory = parent
        }
        return nil
    }

    /// How long `git check-ignore` gets before the answer counts as unknown.
    /// The scan runs on the main thread behind every secrets screen, and a
    /// slow repository or a stuck credential helper must not freeze the app.
    static let gitCheckTimeout: TimeInterval = 3

    /// Unknown — no git, or no answer in time — is treated as not ignored, so
    /// the exposure is still reported. Not knowing is not the same as safe.
    private func isIgnoredByGit(_ url: URL, in repository: URL) -> Bool {
        let process = Process()
        process.executableURL = URL(filePath: "/usr/bin/git")
        process.currentDirectoryURL = repository
        process.arguments = ["check-ignore", "-q", url.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        guard (try? process.run()) != nil else { return false }
        guard finished.wait(timeout: .now() + Self.gitCheckTimeout) == .success else {
            process.terminate()
            return false
        }
        return process.terminationStatus == 0
    }

    private func abbreviate(_ path: String) -> String {
        path.hasPrefix(home.path) ? "~" + path.dropFirst(home.path.count) : path
    }

    // MARK: - Heuristics

    /// Whether a variable is worth treating as a secret.
    ///
    /// A heuristic, and it says so: the name is the strong signal, and a long
    /// opaque value is the weak one. It errs towards including things —
    /// mentioning a variable that turns out to be harmless costs a line in a
    /// list, while missing a real token costs the point of the feature.
    static func looksSensitive(key: String, value: String) -> Bool {
        let upper = key.uppercased()
        let names = ["TOKEN", "KEY", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH", "_PAT", "SESSION", "COOKIE", "PRIVATE"]
        if names.contains(where: upper.contains) {
            // `..._KEY_PATH` and friends point at secrets rather than being them.
            let pointers = ["PATH", "FILE", "DIR", "URL", "ENABLED", "DISABLED"]
            return !pointers.contains(where: upper.hasSuffix)
        }
        // An opaque blob is worth flagging whatever it is called.
        return value.count >= 24 && !value.contains(" ") && value.rangeOfCharacter(from: .decimalDigits) != nil
    }

    /// Stable across launches, and derived from the value so that changing the
    /// value produces a different secret rather than silently reusing the old id.
    static func identifier(key: String, value: String) -> String {
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in "\(key)\u{1}\(value)".utf8 {
            hash ^= UInt64(byte)
            hash &*= 0x100000001b3
        }
        return String(format: "%@-%016llx", key.lowercased(), hash)
    }

    /// Enough to recognise a value, not enough to use it.
    static func mask(_ value: String) -> String {
        "••••••••••••"
    }
}

public enum SecretsError: Error, LocalizedError, Equatable {
    case notFound
    case emptyValue
    case rollbackFailed(paths: [String], backupIDs: [String])
    /// There is no configuration file to restrict.
    case noConfigFile(String)
    /// The mode was set and did not take, so the file is still readable by others.
    case permissionsUnchanged(String)

    public var errorDescription: String? {
        switch self {
        case .notFound: "That secret is no longer in any configuration."
        case .emptyValue: "A secret cannot be set to an empty value."
        case .noConfigFile(let path):
            "There is no configuration file at \(path), so its permissions were not changed."
        case .permissionsUnchanged(let path):
            "\(path) is still readable by other users — the file system did not accept the change."
        case .rollbackFailed(let paths, let backupIDs):
            "Secret rotation could not be fully rolled back. Recovery copies remain for \(paths.joined(separator: ", ")). Backup IDs: \(backupIDs.joined(separator: ", "))."
        }
    }
}
