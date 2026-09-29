import Foundation

/// A registry package that a runner downloads and starts, as one command line
/// names it: `npx -y @scope/name@latest`, `uvx name@1.2.3`,
/// `pipx run --spec "name==1.2.3" name`.
///
/// Read from the command and its arguments only — nothing is run and no
/// registry is asked. This is the one parser for "which package does this
/// server launch"; provenance uses it for the package name (§7.3) and MCP Doctor
/// for whether the version is pinned (§7.10).
///
/// Deliberately narrow. What it does not recognize it does not report, so a
/// server it cannot read is never flagged:
/// - local paths, tarballs, `file:`/`git+…`/`github:` specs, npm aliases
///   (`name@npm:other`), GitHub shorthand (`user/repo`), PEP 508 direct
///   references (`name @ https://…`) and environment markers;
/// - more than one `--package`/`-p`, where which package is "the server" is a
///   guess;
/// - absolute executables, `node script.js`, `python -m module`, Docker images
///   and every other command that does not fetch a package by name.
public struct PackageLaunch: Equatable, Sendable {
    public enum Ecosystem: String, Sendable {
        case npm
        case python
    }

    /// How a version is attached to the name in this argument, which is also
    /// how a pin has to be spelled for this runner to accept it.
    public enum Spelling: Equatable, Sendable {
        /// `name@version` — npm-style runners.
        case npm
        /// `name@version` — `uvx`/`uv tool run`'s own shorthand.
        case uvAt
        /// `name==version` — a PEP 508 requirement (`--from`, `--spec`).
        case requirement
    }

    public let ecosystem: Ecosystem
    /// The runner as written, including its subcommand: `npx`, `pnpm dlx`.
    public let runner: String
    /// The registry name, exactly as written, without version or extras.
    public let name: String
    /// Python extras including brackets, e.g. `[cli]`; empty when there are none.
    public let extras: String
    /// The version request as written after the name — `latest`, `^1.2`,
    /// `>=1,<2`, `1.2.3` — or nil when the argument names no version at all.
    public let requestedVersion: String?
    /// Whether the request can only ever resolve to one release.
    public let isPinned: Bool
    /// Index into the server's `args` of the argument holding the spec.
    public let argumentIndex: Int
    /// That argument verbatim, which is what a rewrite must still find there.
    public let argument: String
    /// Text before the spec inside the same argument: `--package=`, `--from=`.
    public let argumentPrefix: String
    public let spelling: Spelling
    /// Whether replacing that one argument with `pinnedArgument(version:)` gives
    /// a command this runner accepts. False for `pipx run name`, where a version
    /// needs a `--spec` argument that is not there yet.
    public let canPinInPlace: Bool

    /// The argument rewritten to request exactly `version`, or nil when that
    /// version is not an exact release for this ecosystem.
    public func pinnedArgument(version: String) -> String? {
        guard canPinInPlace, Self.isExactVersion(version, ecosystem: ecosystem) else { return nil }
        switch spelling {
        case .npm, .uvAt:
            return argumentPrefix + name + extras + "@" + version
        case .requirement:
            return argumentPrefix + name + extras + "==" + version
        }
    }

    // MARK: - Parsing

    /// The package this command line launches, if it is one this parser can
    /// read with confidence.
    public static func parse(command: String?, args: [String]) -> PackageLaunch? {
        guard let command, !command.isEmpty else { return nil }
        guard let runner = Runner.detect(command: command, args: args) else { return nil }

        let rest = Array(args.dropFirst(runner.subcommandCount))
        guard let target = runner.scan(rest) else { return nil }
        let index = target.index + runner.subcommandCount

        switch runner.ecosystem {
        case .npm:
            guard let spec = NPMSpec(target.spec) else { return nil }
            return PackageLaunch(
                ecosystem: .npm,
                runner: runner.label,
                name: spec.name,
                extras: "",
                requestedVersion: spec.version,
                isPinned: spec.version.map { isExactVersion($0, ecosystem: .npm) } ?? false,
                argumentIndex: index,
                argument: args[index],
                argumentPrefix: target.prefix,
                spelling: .npm,
                canPinInPlace: true
            )
        case .python:
            guard let spec = PythonSpec(target.spec, allowingAt: runner.acceptsAtVersion && !target.fromFlag) else {
                return nil
            }
            let spelling: Spelling = spec.usedAt || (spec.specifier == nil && runner.acceptsAtVersion && !target.fromFlag)
                ? .uvAt
                : .requirement
            // `pipx run name` has nowhere to put a version without adding an
            // argument, and a pin that has to invent structure is not a splice.
            let canPin = target.fromFlag || spec.specifier != nil || runner.acceptsAtVersion
            return PackageLaunch(
                ecosystem: .python,
                runner: runner.label,
                name: spec.name,
                extras: spec.extras,
                requestedVersion: spec.specifier,
                isPinned: spec.isPinned,
                argumentIndex: index,
                argument: args[index],
                argumentPrefix: target.prefix,
                spelling: spelling,
                canPinInPlace: canPin
            )
        }
    }

    /// Whether `version` names exactly one release.
    ///
    /// npm: a full `major.minor.patch`, optionally with prerelease and build
    /// metadata — `1.2` is a range to npm, not a release. Python: a PEP 440
    /// release, which may have any number of components; wildcards are ranges.
    public static func isExactVersion(_ raw: String, ecosystem: Ecosystem) -> Bool {
        switch ecosystem {
        case .npm:
            var version = Substring(raw)
            if version.first == "=" || version.first == "v" { version = version.dropFirst() }
            return version.wholeMatch(of: /\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?/) != nil
        case .python:
            return raw.wholeMatch(
                of: /([0-9]+!)?[0-9]+(\.[0-9]+)*((a|b|rc)[0-9]+)?(\.post[0-9]+)?(\.dev[0-9]+)?(\+[0-9A-Za-z.]+)?/
            ) != nil
        }
    }
}

// MARK: - Runners

private struct Runner {
    let label: String
    let ecosystem: PackageLaunch.Ecosystem
    /// Arguments before the runner's own options: `dlx` in `pnpm dlx`.
    let subcommandCount: Int
    /// Options that carry the package spec instead of the first positional.
    let packageFlags: Set<String>
    /// Options known to consume the following argument as their value.
    let valueFlags: Set<String>
    /// `uvx name@1.2.3`. pipx has no such shorthand.
    let acceptsAtVersion: Bool

    struct Target {
        let index: Int
        let spec: String
        let prefix: String
        /// The spec came from `--package`/`--from`/`--spec` rather than the
        /// positional, which for Python means it is a PEP 508 requirement.
        let fromFlag: Bool
    }

    static func detect(command: String, args: [String]) -> Runner? {
        var base = URL(filePath: command).lastPathComponent.lowercased()
        for suffix in [".cmd", ".exe", ".ps1"] where base.hasSuffix(suffix) {
            base = String(base.dropLast(suffix.count))
        }
        let first = args.first
        let second = args.count > 1 ? args[1] : nil

        switch base {
        case "npx":
            return .npm(label: "npx", subcommandCount: 0)
        case "npm" where first == "exec" || first == "x":
            return .npm(label: "npm \(first!)", subcommandCount: 1)
        case "pnpm" where first == "dlx":
            return .npm(label: "pnpm dlx", subcommandCount: 1)
        case "yarn" where first == "dlx":
            return .npm(label: "yarn dlx", subcommandCount: 1)
        case "bunx":
            return .npm(label: "bunx", subcommandCount: 0)
        case "bun" where first == "x":
            return .npm(label: "bun x", subcommandCount: 1)
        case "uvx":
            return .uv(label: "uvx", subcommandCount: 0)
        case "uv" where first == "tool" && second == "run":
            return .uv(label: "uv tool run", subcommandCount: 2)
        case "pipx" where first == "run":
            return Runner(
                label: "pipx run",
                ecosystem: .python,
                subcommandCount: 1,
                packageFlags: ["--spec"],
                valueFlags: ["--python", "--index-url", "-i", "--pip-args", "--backend"],
                acceptsAtVersion: false
            )
        default:
            return nil
        }
    }

    private static func npm(label: String, subcommandCount: Int) -> Runner {
        Runner(
            label: label,
            ecosystem: .npm,
            subcommandCount: subcommandCount,
            packageFlags: ["-p", "--package"],
            valueFlags: [
                "-c", "--call", "--registry", "--cache", "--userconfig", "--prefix",
                "-w", "--workspace", "--node-options", "--loglevel", "--shell",
                "--dir", "-C", "--filter",
            ],
            acceptsAtVersion: true
        )
    }

    private static func uv(label: String, subcommandCount: Int) -> Runner {
        Runner(
            label: label,
            ecosystem: .python,
            subcommandCount: subcommandCount,
            packageFlags: ["--from"],
            valueFlags: [
                "--with", "-w", "--with-editable", "--with-requirements", "--python", "-p",
                "--index", "--index-url", "-i", "--extra-index-url", "--default-index",
                "--find-links", "-f", "--constraints", "--overrides", "--build-constraints",
                "--directory", "--project", "--cache-dir", "--config-file", "--env-file",
                "--python-preference", "--resolution", "--prerelease", "--index-strategy",
                "--keyring-provider", "--exclude-newer", "--refresh-package", "-P",
                "--upgrade-package", "--reinstall-package", "--no-build-package",
                "--no-binary-package", "--only-binary", "--no-binary", "--link-mode",
                "--color", "--allow-insecure-host", "--config-setting", "-C",
            ],
            acceptsAtVersion: true
        )
    }

    /// Finds the argument holding the package spec, skipping this runner's
    /// options. An unknown option is assumed to take no value — the common
    /// case, and the known value-takers are listed above.
    func scan(_ args: [String]) -> Target? {
        var packages: [Target] = []
        var positional: Int?
        var index = 0

        while index < args.count {
            let argument = args[index]
            if argument == "--" {
                if index + 1 < args.count { positional = index + 1 }
                break
            }
            if let flag = packageFlags.first(where: { argument.hasPrefix($0 + "=") }) {
                let prefix = flag + "="
                packages.append(Target(
                    index: index, spec: String(argument.dropFirst(prefix.count)), prefix: prefix, fromFlag: true
                ))
                index += 1
                continue
            }
            if packageFlags.contains(argument) {
                guard index + 1 < args.count else { return nil }
                packages.append(Target(index: index + 1, spec: args[index + 1], prefix: "", fromFlag: true))
                index += 2
                continue
            }
            if argument.hasPrefix("-") && argument.count > 1 {
                index += (valueFlags.contains(argument) && !argument.contains("=")) ? 2 : 1
                continue
            }
            positional = index
            break
        }

        // With `-p a -p b` there is no telling which package is the server.
        guard packages.count <= 1 else { return nil }
        if let package = packages.first { return package }
        guard let positional else { return nil }
        return Target(index: positional, spec: args[positional], prefix: "", fromFlag: false)
    }
}

// MARK: - Specs

/// `name`, `name@version`, `@scope/name`, `@scope/name@version`.
private struct NPMSpec {
    let name: String
    let version: String?

    init?(_ raw: String) {
        let spec = raw.trimmingCharacters(in: .whitespaces)
        guard !spec.isEmpty,
              !spec.contains(":"),                       // npm:, git+, github:, file:, URLs
              !spec.hasPrefix("."), !spec.hasPrefix("/"), !spec.hasPrefix("~"),
              !spec.hasSuffix(".tgz"), !spec.hasSuffix(".tar.gz")
        else { return nil }

        let nameEnd: String.Index
        if spec.hasPrefix("@") {
            nameEnd = spec.dropFirst().firstIndex(of: "@") ?? spec.endIndex
            guard spec[..<nameEnd].filter({ $0 == "/" }).count == 1 else { return nil }
        } else {
            nameEnd = spec.firstIndex(of: "@") ?? spec.endIndex
            // `user/repo` is GitHub shorthand to npm, not a registry name.
            guard !spec[..<nameEnd].contains("/") else { return nil }
        }

        let name = String(spec[..<nameEnd])
        guard name.count <= 214,
              !name.hasSuffix("/"),
              name.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "-._~@/".contains($0)) }),
              name.contains(where: { $0.isLetter || $0.isNumber })
        else { return nil }

        self.name = name
        version = nameEnd < spec.endIndex ? String(spec[spec.index(after: nameEnd)...]) : nil
    }
}

/// A PEP 508 requirement without markers or URL, or uv's `name@version`.
private struct PythonSpec {
    let name: String
    let extras: String
    /// `==1.2.3`, `>=1`, `latest` — whatever follows name and extras.
    let specifier: String?
    let usedAt: Bool
    let isPinned: Bool

    init?(_ raw: String, allowingAt: Bool) {
        let spec = raw.trimmingCharacters(in: .whitespaces)
        guard !spec.isEmpty,
              !spec.contains("://"), !spec.contains(";"), !spec.contains(" @"),
              !spec.hasPrefix("git+"), !spec.hasPrefix("."), !spec.hasPrefix("/"), !spec.hasPrefix("~"),
              !spec.hasSuffix(".whl"), !spec.hasSuffix(".tar.gz"), !spec.hasSuffix(".zip"),
              let match = spec.wholeMatch(
                  of: /([A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)(\[[A-Za-z0-9._,\s-]*\])?(.*)/
              )
        else { return nil }

        name = String(match.1)
        extras = match.2.map(String.init) ?? ""
        let tail = String(match.3).trimmingCharacters(in: .whitespaces)

        if tail.isEmpty {
            specifier = nil
            usedAt = false
            isPinned = false
        } else if tail.hasPrefix("@") {
            guard allowingAt else { return nil }
            let version = String(tail.dropFirst())
            guard !version.isEmpty else { return nil }
            specifier = version
            usedAt = true
            // uv reads `name@1.2` as `name==1.2`; only `@latest` floats.
            isPinned = version != "latest" && PackageLaunch.isExactVersion(version, ecosystem: .python)
        } else {
            guard let first = tail.first, "=<>!~".contains(first) else { return nil }
            specifier = tail
            usedAt = false
            // One clause, `==` or `===`, and no wildcard: exactly one release.
            if !tail.contains(","), let version = tail.wholeMatch(of: /={2,3}\s*(\S+)/)?.1 {
                isPinned = !version.contains("*")
            } else {
                isPinned = false
            }
        }
    }
}
