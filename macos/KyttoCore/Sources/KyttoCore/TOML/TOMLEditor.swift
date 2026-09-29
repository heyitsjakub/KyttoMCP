import Foundation

public enum TOMLEditError: Error, Equatable {
    case tableNotFound([String])
    case notATable([String])
    /// The entry exists, but as an inline table on one line rather than as a
    /// `[header]` of its own. Kytto reads those; it will not rewrite one,
    /// because doing it safely means re-serialising a line it did not write.
    case inlineDefinition(String)
    case valueNotFound([String])
    case ambiguousValue([String])
    /// The value is not what the caller last read there.
    case valueMismatch([String])
}

/// Structural edits, expressed as splices.
///
/// Same contract as `JSONEditor`: every operation returns the original text with
/// one range replaced, so comments, blank lines, key order and the forty-odd
/// `[projects."…"]` tables in a real Codex config all survive untouched (§6.3).
///
/// The fiddly part here is not commas but *lines*. A table is a header plus
/// everything under it until the next header, and a server's `env` lives in a
/// second table of its own — so removing one server means removing two tables
/// and exactly the right number of blank lines around them.
public extension TOMLDocument {

    // MARK: - Reading

    /// The servers defined under `key`, in file order.
    ///
    /// Both spellings count: a `[mcp_servers.name]` table, and a `name = { … }`
    /// inline table sitting under a `[mcp_servers]` header. Codex writes the
    /// first; a hand-edited file can hold either.
    func serverNames(under key: String) -> [String] {
        var names: [String] = []
        for table in childTables(of: [key]) where !table.isArrayElement {
            if let name = table.path.last { names.append(name) }
        }
        for pair in table(at: [key])?.pairs ?? [] {
            if let name = pair.name, pair.value.inlinePairs != nil, !names.contains(name) {
                names.append(name)
            }
        }
        return names
    }

    /// Everything that makes up one server: its own table and any sub-table of
    /// it, such as `env`.
    func serverTables(_ name: String, under key: String) -> [TOMLTable] {
        let path = [key, name]
        guard let own = table(at: path) else { return [] }
        return [own] + tables(under: path)
    }

    /// The source text of a server's definition, exactly as it appears.
    ///
    /// This is what gets parked when a server is switched off, so switching it
    /// back on is a restoration rather than a reconstruction.
    func serverDefinitionText(_ name: String, under key: String) -> String? {
        let tables = serverTables(name, under: key)
        guard let first = tables.first, let last = tables.last else {
            // An inline definition has no table of its own; park the pair.
            guard let pair = table(at: [key])?.pair(name) else { return nil }
            return slice(pair.span)
        }
        return slice(TOMLSpan(start: first.span.start, end: last.span.end))
    }

    // MARK: - Writing

    /// Replaces one environment value without reserializing its table or inline
    /// definition. This is the splice used by transactional secret rotation.
    func settingServerEnvironmentValue(
        _ environmentKey: String,
        to value: String,
        forServer serverName: String,
        under serversKey: String
    ) throws -> String {
        let tablePath = [serversKey, serverName, "env"]
        if let environmentTable = table(at: tablePath) {
            let matches = environmentTable.pairs.filter { $0.name == environmentKey }
            guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(tablePath + [environmentKey]) }
            guard let pair = matches.first else { throw TOMLEditError.valueNotFound(tablePath + [environmentKey]) }
            return replacing(pair.value.span, with: TOMLText.string(value))
        }

        let serverPath = [serversKey, serverName]
        guard let serverPair = table(at: [serversKey])?.pairs.first(where: { $0.name == serverName }),
              let serverFields = serverPair.value.inlinePairs
        else { throw TOMLEditError.valueNotFound(serverPath) }
        let environments = serverFields.filter { $0.name == "env" }
        guard environments.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath + ["env"]) }
        guard let environment = environments.first, let values = environment.value.inlinePairs else {
            throw TOMLEditError.valueNotFound(serverPath + ["env"])
        }
        let matches = values.filter { $0.name == environmentKey }
        guard matches.count <= 1 else {
            throw TOMLEditError.ambiguousValue(serverPath + ["env", environmentKey])
        }
        guard let pair = matches.first else {
            throw TOMLEditError.valueNotFound(serverPath + ["env", environmentKey])
        }
        return replacing(pair.value.span, with: TOMLText.string(value))
    }

    /// Replaces one string in a server's `args` array without reserializing the
    /// array, its table or an inline definition. The splice MCP Doctor's package
    /// pin uses (§7.10).
    ///
    /// `position` counts string elements only, as `ServerFields` does, and
    /// `expecting` is what that element must still say.
    func settingServerArgument(
        at position: Int,
        expecting: String,
        to value: String,
        forServer serverName: String,
        under serversKey: String
    ) throws -> String {
        let serverPath = [serversKey, serverName]
        let fields: [TOMLPair]
        if let table = table(at: serverPath) {
            fields = table.pairs
        } else {
            let servers = table(at: [serversKey])?.pairs.filter { $0.name == serverName } ?? []
            guard servers.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath) }
            guard let inline = servers.first?.value.inlinePairs else {
                throw TOMLEditError.valueNotFound(serverPath)
            }
            fields = inline
        }

        let matches = fields.filter { $0.name == "args" }
        guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath + ["args"]) }
        guard let arguments = matches.first?.value.elements else {
            throw TOMLEditError.valueNotFound(serverPath + ["args"])
        }
        let strings = arguments.filter { $0.stringValue != nil }
        guard strings.indices.contains(position),
              strings[position].stringValue == expecting
        else { throw TOMLEditError.valueMismatch(serverPath + ["args"]) }
        return replacing(strings[position].span, with: TOMLText.string(value))
    }

    /// Adds or replaces a server's definition.
    ///
    /// `block` is a rendered table block, headers and all, as produced by
    /// `TOMLBuilder`. Replacing an existing server swaps the whole block, which
    /// is what makes an edit that drops an env var actually drop it.
    func settingServer(_ name: String, under key: String, to block: String) throws -> String {
        if table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }

        let existing = serverTables(name, under: key)
        if let first = existing.first, let last = existing.last {
            return replacing(
                TOMLSpan(start: lineStart(of: first.span.start), end: last.span.end),
                with: block
            )
        }

        // New server. Keep it with the others if there are others, so the file
        // stays grouped the way its owner left it.
        let siblings = childTables(of: [key])
        if let last = siblings.last {
            let anchor = (tables(under: last.path).last ?? last).span.end
            return replacing(TOMLSpan(start: anchor, end: anchor), with: "\n\n" + block)
        }

        let end = sourceBytes.count
        let separator = sourceText.isEmpty ? "" : (endsWithBlankLine ? "" : (endsWithNewline ? "\n" : "\n\n"))
        return replacing(TOMLSpan(start: end, end: end), with: separator + block + "\n")
    }

    /// Removes a server's tables entirely, leaving the file as if it had never
    /// been typed — no stranded header, no doubled blank line.
    func removingServer(_ name: String, under key: String) throws -> String {
        if table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }

        let tables = serverTables(name, under: key)
        guard let first = tables.first, let last = tables.last else { return sourceText }

        var start = lineStart(of: first.span.start)
        var end = endOfLine(from: last.span.end)

        // Take the blank lines that followed it. If there were none — because it
        // was last in the file — take the blank line that preceded it instead,
        // so the gap it leaves behind is the one it was occupying.
        let afterBlanks = skippingBlankLines(from: end)
        if afterBlanks > end {
            end = afterBlanks
        } else {
            start = startSkippingBlankLines(before: start)
        }

        return replacing(TOMLSpan(start: start, end: end), with: "")
    }

    /// Sets a boolean on the server's own table, creating the key if it is not
    /// there yet.
    ///
    /// This is Codex's off switch, and it is a better one than most: the
    /// definition stays in the file, so nothing has to be parked and switching
    /// back on cannot lose anything.
    func settingServerFlag(
        _ value: Bool,
        key flagKey: String,
        forServer name: String,
        under key: String
    ) throws -> String {
        let path = [key, name]
        guard let table = table(at: path) else { throw TOMLEditError.tableNotFound(path) }

        if let pair = table.pair(flagKey) {
            return replacing(pair.value.span, with: TOMLText.bool(value))
        }

        // After the last key of this table, which is before any `[…env]` header —
        // a bare key after a sub-table header would belong to the sub-table.
        let anchor = table.span.end
        return replacing(TOMLSpan(start: anchor, end: anchor), with: "\n\(TOMLText.key(flagKey)) = \(TOMLText.bool(value))")
    }

    // MARK: - Line geometry

    private var endsWithNewline: Bool {
        sourceBytes.last == 0x0A
    }

    private var endsWithBlankLine: Bool {
        guard sourceBytes.count >= 2 else { return false }
        var index = sourceBytes.count - 1
        guard sourceBytes[index] == 0x0A else { return false }
        index -= 1
        while index >= 0, sourceBytes[index] == 0x0D || sourceBytes[index] == 0x20 || sourceBytes[index] == 0x09 {
            index -= 1
        }
        return index >= 0 && sourceBytes[index] == 0x0A
    }

    private func lineStart(of offset: Int) -> Int {
        var index = min(offset, sourceBytes.count)
        while index > 0, sourceBytes[index - 1] != 0x0A { index -= 1 }
        return index
    }

    /// Just past the newline that ends the line containing `offset`.
    private func endOfLine(from offset: Int) -> Int {
        var index = min(offset, sourceBytes.count)
        while index < sourceBytes.count, sourceBytes[index] != 0x0A { index += 1 }
        return index < sourceBytes.count ? index + 1 : index
    }

    private func isBlankLine(startingAt offset: Int) -> Int? {
        var index = offset
        while index < sourceBytes.count, sourceBytes[index] == 0x20 || sourceBytes[index] == 0x09
            || sourceBytes[index] == 0x0D {
            index += 1
        }
        guard index < sourceBytes.count, sourceBytes[index] == 0x0A else { return nil }
        return index + 1
    }

    private func skippingBlankLines(from offset: Int) -> Int {
        var index = offset
        while let next = isBlankLine(startingAt: index) { index = next }
        return index
    }

    private func startSkippingBlankLines(before offset: Int) -> Int {
        var index = offset
        while index > 0 {
            let previousLine = lineStart(of: index - 1)
            guard isBlankLine(startingAt: previousLine) != nil else { break }
            index = previousLine
        }
        return index
    }
}

// MARK: - Rendering

/// Literals, spelled the way TOML spells them.
public enum TOMLText {
    public static func string(_ value: String) -> String {
        var out = "\""
        for scalar in value.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04X", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        return out + "\""
    }

    public static func bool(_ value: Bool) -> String {
        value ? "true" : "false"
    }

    public static func array(_ values: [String]) -> String {
        "[" + values.map(string).joined(separator: ", ") + "]"
    }

    /// A bare key where TOML allows one, a quoted key where it does not.
    ///
    /// Server names reach this from a config file Kytto did not write, so
    /// "allows one" has to be checked rather than assumed.
    public static func key(_ value: String) -> String {
        let bare = !value.isEmpty && value.unicodeScalars.allSatisfy { scalar in
            (scalar >= "A" && scalar <= "Z")
                || (scalar >= "a" && scalar <= "z")
                || (scalar >= "0" && scalar <= "9")
                || scalar == "_"
                || scalar == "-"
        }
        return bare ? value : string(value)
    }

    /// A dotted path, each component quoted only if it has to be.
    public static func path(_ components: [String]) -> String {
        components.map(key).joined(separator: ".")
    }
}

/// Renders a server definition as a TOML table block.
///
/// Shaped after what `codex mcp add` itself writes — a table per server with the
/// environment in a sub-table — so a config Kytto has edited still looks like
/// one Codex wrote, and a diff shows only what changed.
public enum TOMLBuilder {
    public static func serverBlock(
        name: String,
        under key: String,
        command: String?,
        args: [String],
        url: String?,
        env: [EnvEntry],
        enabled: Bool?
    ) -> String {
        var lines = ["[\(TOMLText.path([key, name]))]"]

        if let url, !url.isEmpty {
            lines.append("url = \(TOMLText.string(url))")
        } else if let command, !command.isEmpty {
            lines.append("command = \(TOMLText.string(command))")
            if !args.isEmpty {
                lines.append("args = \(TOMLText.array(args))")
            }
        }

        if let enabled {
            lines.append("enabled = \(TOMLText.bool(enabled))")
        }

        let realEnv = env.filter { !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
        if !realEnv.isEmpty {
            lines.append("")
            lines.append("[\(TOMLText.path([key, name, "env"]))]")
            for entry in realEnv {
                lines.append("\(TOMLText.key(entry.key)) = \(TOMLText.string(entry.value ?? ""))")
            }
        }

        return lines.joined(separator: "\n")
    }
}
