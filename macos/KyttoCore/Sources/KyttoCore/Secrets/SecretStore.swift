import Foundation

/// Somewhere to keep a secret value that is not a config file.
///
/// A protocol so tests do not touch the real Keychain — which would prompt, and
/// would leave things behind — and so M7 can put Windows Credential Manager
/// behind the same door.
public protocol SecretStoring: Sendable {
    func store(_ value: String, for id: String) throws
    func value(for id: String) throws -> String?
    func delete(for id: String) throws
    func storedIdentifiers() -> Set<String>
}

#if canImport(Security)
import Security

/// The macOS Keychain.
///
/// Note what this is *not* doing: it is not what the client reads. No MCP client
/// understands a reference to a keychain item — they read the value out of the
/// config file, literally. So the Keychain here is Kytto's own copy: it is what
/// lets a value be rotated everywhere at once, restored after a scrub, or kept
/// for a server that is not deployed yet. Pretending otherwise would be the kind
/// of security theatre that is worse than none.
public struct KeychainSecretStore: SecretStoring {
    private let service: String

    public init(service: String = "app.kytto.secrets") {
        self.service = service
    }

    private func query(for id: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
        ]
    }

    public func store(_ value: String, for id: String) throws {
        let data = Data(value.utf8)
        var attributes = query(for: id)

        let status = SecItemCopyMatching(attributes as CFDictionary, nil)
        if status == errSecSuccess {
            let update = [kSecValueData as String: data]
            let updateStatus = SecItemUpdate(attributes as CFDictionary, update as CFDictionary)
            guard updateStatus == errSecSuccess else { throw SecretStoreError(status: updateStatus) }
            return
        }

        attributes[kSecValueData as String] = data
        // Available whenever the Mac is unlocked, and never synced to iCloud or
        // migrated to another machine — this is a local convenience copy, not
        // something that should travel.
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let addStatus = SecItemAdd(attributes as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw SecretStoreError(status: addStatus) }
    }

    public func value(for id: String) throws -> String? {
        var attributes = query(for: id)
        attributes[kSecReturnData as String] = true
        attributes[kSecMatchLimit as String] = kSecMatchLimitOne

        var item: CFTypeRef?
        let status = SecItemCopyMatching(attributes as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data else {
            throw SecretStoreError(status: status)
        }
        return String(data: data, encoding: .utf8)
    }

    public func delete(for id: String) throws {
        let status = SecItemDelete(query(for: id) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw SecretStoreError(status: status)
        }
    }

    public func storedIdentifiers() -> Set<String> {
        let attributes: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll,
        ]
        var items: CFTypeRef?
        guard SecItemCopyMatching(attributes as CFDictionary, &items) == errSecSuccess,
              let entries = items as? [[String: Any]]
        else { return [] }
        return Set(entries.compactMap { $0[kSecAttrAccount as String] as? String })
    }
}

public struct SecretStoreError: Error, LocalizedError {
    public let status: OSStatus

    public var errorDescription: String? {
        let detail = SecCopyErrorMessageString(status, nil) as String? ?? "error \(status)"
        return "Keychain refused the request: \(detail)"
    }
}
#endif

/// An in-memory store, for tests and for anywhere a real Keychain is unwanted.
public final class InMemorySecretStore: SecretStoring, @unchecked Sendable {
    private var values: [String: String] = [:]
    private let lock = NSLock()

    public init() {}

    public func store(_ value: String, for id: String) throws {
        lock.lock(); defer { lock.unlock() }
        values[id] = value
    }

    public func value(for id: String) throws -> String? {
        lock.lock(); defer { lock.unlock() }
        return values[id]
    }

    public func delete(for id: String) throws {
        lock.lock(); defer { lock.unlock() }
        values.removeValue(forKey: id)
    }

    public func storedIdentifiers() -> Set<String> {
        lock.lock(); defer { lock.unlock() }
        return Set(values.keys)
    }
}
