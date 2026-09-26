import Foundation

/// One process-wide transaction lane for Kytto-owned state and client configs.
///
/// The stores are small and mutations are rare, while a lost config, park entry
/// or settings update is expensive. A recursive lock lets a config transaction
/// call into `BackupStore` without deadlocking and makes read-modify-write
/// sequences atomic across independently constructed service values.
enum MutationCoordinator {
    private static let lock = NSRecursiveLock()

    static func sync<Result>(_ operation: () throws -> Result) rethrows -> Result {
        lock.lock()
        defer { lock.unlock() }
        return try operation()
    }
}
