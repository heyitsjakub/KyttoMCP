import Foundation

/// Pinning a runner-launched package to one exact release (§7.10).
///
/// `npx -y name` and `uvx name` fetch whatever release is newest each time a
/// client starts the server, so a new release — including a compromised one —
/// runs with the user's environment without anyone choosing it. The repair
/// rewrites the one argument that names the package; it never guesses which
/// version: the caller supplies one the user confirmed, and a version that is
/// not a single exact release for that ecosystem produces no edit at all.
public enum PackagePin {

    /// The launch MCP Doctor reports for this server: the merged definition's,
    /// or else the first client copy that has one. Nil when nothing launches a
    /// registry package or every copy is already pinned.
    public static func unpinnedLaunch(in server: Server) -> PackageLaunch? {
        guard server.transport == .stdio else { return nil }
        let candidates = [PackageLaunch.parse(command: server.command, args: server.args)]
            + copies(of: server).map { PackageLaunch.parse(command: $0.definition.command, args: $0.definition.args) }
        return candidates.compactMap { $0 }.first { !$0.isPinned }
    }

    /// Whether the repair can be offered: some editable copy is unpinned and
    /// its runner accepts a version in the argument that is already there.
    public static func canPin(_ server: Server) -> Bool {
        guard let launch = unpinnedLaunch(in: server) else { return false }
        return copies(of: server).contains { copy in
            guard !copy.definition.isBundled,
                  let own = PackageLaunch.parse(command: copy.definition.command, args: copy.definition.args)
            else { return false }
            return own.name == launch.name && own.ecosystem == launch.ecosystem && !own.isPinned && own.canPinInPlace
        }
    }

    /// One argument edit per client copy that launches the same package
    /// unpinned. Copies already pinned, launching something else, bundled or
    /// unable to take a version in place are left out; an empty result means
    /// there is nothing safe to write.
    public static func edits(
        for server: Server,
        version: String
    ) -> [ClientID: ServerAuthoring.ArgumentEdit] {
        guard let launch = unpinnedLaunch(in: server) else { return [:] }
        var result: [ClientID: ServerAuthoring.ArgumentEdit] = [:]
        for copy in copies(of: server) where !copy.definition.isBundled {
            guard let own = PackageLaunch.parse(command: copy.definition.command, args: copy.definition.args),
                  own.name == launch.name,
                  own.ecosystem == launch.ecosystem,
                  !own.isPinned,
                  let replacement = own.pinnedArgument(version: version)
            else { continue }
            result[copy.clientID] = ServerAuthoring.ArgumentEdit(
                index: own.argumentIndex,
                expected: own.argument,
                replacement: replacement
            )
        }
        return result
    }

    /// Every client holding this server, with that client's own definition.
    /// Falls back to the merged view for a server built without per-client
    /// evidence.
    private static func copies(of server: Server) -> [(clientID: ClientID, definition: ClientDefinition)] {
        server.enabledIn
            .filter { $0.value != .absent }
            .map(\.key)
            .sorted { $0.rawValue < $1.rawValue }
            .map { ($0, server.definitionsByClient[$0] ?? ClientDefinition(from: server)) }
    }
}
