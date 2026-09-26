import Foundation

/// Resolves an executable against the user's login-shell PATH without invoking
/// a shell for the real command. Shared by health checks and the gateway shim.
public enum ManagedExecutable {
    public static func resolve(_ command: String, path: String) throws -> String {
        guard command.contains("/") else {
            for directory in path.split(separator: ":") {
                let candidate = "\(directory)/\(command)"
                if FileManager.default.isExecutableFile(atPath: candidate) {
                    return candidate
                }
            }
            throw ProcessError.commandNotFound(command, searchedPath: path)
        }

        let expanded = (command as NSString).expandingTildeInPath
        // A relative command is resolved by the launching client against its own
        // working directory (§4, `Server.hasRelativePath`). Kytto is not that
        // directory, so checking the path from here answers a different question
        // than the one asked, and every answer it gives is wrong.
        guard expanded.hasPrefix("/") else {
            throw ProcessError.relativeCommand(command)
        }

        // `isExecutableFile` is equally false for "nothing is here" and for "something
        // is here without +x". Reporting both as "not executable" sends people to
        // chmod a file that either does not exist or is already executable.
        guard FileManager.default.fileExists(atPath: expanded) else {
            throw ProcessError.executableMissing(command, resolvedPath: expanded)
        }
        guard FileManager.default.isExecutableFile(atPath: expanded) else {
            throw ProcessError.notExecutable(expanded)
        }
        return expanded
    }
}
