using System.Text.RegularExpressions;

namespace Kytto.Core.Model;

public enum PackageEcosystem { Npm, Python }

/// <summary>
/// How a version is attached to the name in one argument, which is also how a
/// pin has to be spelled for that runner to accept it.
/// </summary>
public enum PackageSpelling
{
    /// <summary><c>name@version</c> — npm-style runners.</summary>
    Npm,

    /// <summary><c>name@version</c> — <c>uvx</c>/<c>uv tool run</c>'s own shorthand.</summary>
    UvAt,

    /// <summary><c>name==version</c> — a PEP 508 requirement (<c>--from</c>, <c>--spec</c>).</summary>
    Requirement,
}

/// <summary>
/// A registry package that a runner downloads and starts, as one command line
/// names it: <c>npx -y @scope/name@latest</c>, <c>uvx name@1.2.3</c>,
/// <c>pipx run --spec "name==1.2.3" name</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read from the command and its arguments only — nothing is run and no registry
/// is asked. This is the one parser for "which package does this server launch":
/// provenance takes the package name from it (§7), and MCP Doctor whether the
/// version is pinned (§7.10), so the two cannot disagree about the name.
/// </para>
/// <para>
/// The Windows spellings of the same launch are recognised too: <c>npx.cmd</c>,
/// <c>uvx.exe</c>, a full path such as <c>C:\Program Files\nodejs\npx.cmd</c>,
/// and the runner wrapped as <c>cmd /c npx -y pkg</c>, which is how a batch-file
/// runner is commonly written into a client that spawns without a shell.
/// </para>
/// <para>
/// Deliberately narrow. What it does not recognise it does not report, so a
/// server it cannot read is never flagged: local paths and script files,
/// tarballs, <c>file:</c>/<c>git+…</c>/<c>github:</c> specs, npm aliases
/// (<c>name@npm:other</c>), GitHub shorthand (<c>user/repo</c>), PEP 508 direct
/// references and environment markers; more than one <c>--package</c>/<c>-p</c>,
/// where which package is "the server" would be a guess; a command line quoted
/// into a single <c>cmd /c "…"</c> argument; and every command that does not fetch
/// a package by name — <c>node script.js</c>, <c>python -m module</c>, Docker.
/// </para>
/// </remarks>
/// <param name="Runner">The runner as written, including its subcommand: <c>npx</c>, <c>pnpm dlx</c>.</param>
/// <param name="Name">The registry name, exactly as written, without version or extras.</param>
/// <param name="Extras">Python extras including brackets, e.g. <c>[cli]</c>; empty when there are none.</param>
/// <param name="RequestedVersion">
/// The version request as written after the name — <c>latest</c>, <c>^1.2</c>,
/// <c>&gt;=1,&lt;2</c>, <c>1.2.3</c> — or null when the argument names no version.
/// </param>
/// <param name="IsPinned">Whether the request can only ever resolve to one release.</param>
/// <param name="ArgumentIndex">Index into the server's <c>args</c> of the argument holding the spec.</param>
/// <param name="Argument">That argument verbatim, which is what a rewrite must still find there.</param>
/// <param name="ArgumentPrefix">Text before the spec inside the same argument: <c>--package=</c>, <c>--from=</c>.</param>
/// <param name="CanPinInPlace">
/// Whether replacing that one argument with <see cref="PinnedArgument"/> gives a
/// command this runner accepts. False for <c>pipx run name</c>, where a version
/// needs a <c>--spec</c> argument that is not there yet.
/// </param>
public sealed record PackageLaunch(
    PackageEcosystem Ecosystem,
    string Runner,
    string Name,
    string Extras,
    string? RequestedVersion,
    bool IsPinned,
    int ArgumentIndex,
    string Argument,
    string ArgumentPrefix,
    PackageSpelling Spelling,
    bool CanPinInPlace)
{
    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly string[] ExecutableSuffixes = [".cmd", ".exe", ".bat", ".ps1"];
    private static readonly string[] ScriptSuffixes = [".js", ".mjs", ".cjs", ".ts", ".py"];

    private static readonly Regex NpmExact = new(
        @"\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex PythonExact = new(
        @"\A([0-9]+!)?[0-9]+(\.[0-9]+)*((a|b|rc)[0-9]+)?(\.post[0-9]+)?(\.dev[0-9]+)?(\+[0-9A-Za-z.]+)?\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex PythonRequirement = new(
        @"\A([A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)(\[[A-Za-z0-9._,\s-]*\])?(.*)\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex SingleEquality = new(
        @"\A={2,3}\s*(\S+)\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The argument rewritten to request exactly <paramref name="version"/>, or null
    /// when that version is not one exact release for this ecosystem.
    /// </summary>
    public string? PinnedArgument(string version)
    {
        if (!CanPinInPlace || !IsExactVersion(version, Ecosystem)) return null;
        var separator = Spelling == PackageSpelling.Requirement ? "==" : "@";
        return ArgumentPrefix + Name + Extras + separator + version;
    }

    // MARK: - Parsing

    /// <summary>
    /// The package this command line launches, if it is one this parser can read
    /// with confidence.
    /// </summary>
    public static PackageLaunch? Parse(string? command, IReadOnlyList<string> args)
    {
        var executable = ExecutableName(command);

        // `cmd /c npx -y pkg`: the runner is the first argument after `/c`, and
        // every index reported below still counts from the start of `args`.
        var offset = 0;
        if (executable == "cmd")
        {
            var index = 0;
            while (index < args.Count && args[index].StartsWith('/') &&
                   !string.Equals(args[index], "/c", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }
            if (index + 1 >= args.Count ||
                !string.Equals(args[index], "/c", StringComparison.OrdinalIgnoreCase)) return null;
            executable = ExecutableName(args[index + 1]);
            if (executable == "cmd") return null;
            offset = index + 2;
        }

        if (executable is null || Detect(executable, args, offset) is not { } runner) return null;

        var start = offset + runner.SubcommandCount;
        var rest = args.Skip(start).ToArray();
        if (runner.Scan(rest) is not { } target) return null;
        var argumentIndex = target.Index + start;

        if (runner.Ecosystem == PackageEcosystem.Npm)
        {
            if (ParseNpmSpec(target.Spec) is not { } npm) return null;
            return new PackageLaunch(
                PackageEcosystem.Npm,
                runner.Label,
                npm.Name,
                "",
                npm.Version,
                npm.Version is { } requested && IsExactVersion(requested, PackageEcosystem.Npm),
                argumentIndex,
                args[argumentIndex],
                target.Prefix,
                PackageSpelling.Npm,
                CanPinInPlace: true);
        }

        var allowingAt = runner.AcceptsAtVersion && !target.FromFlag;
        if (ParsePythonSpec(target.Spec, allowingAt) is not { } python) return null;
        var spelling = python.UsedAt || (python.Specifier is null && allowingAt)
            ? PackageSpelling.UvAt
            : PackageSpelling.Requirement;
        return new PackageLaunch(
            PackageEcosystem.Python,
            runner.Label,
            python.Name,
            python.Extras,
            python.Specifier,
            python.IsPinned,
            argumentIndex,
            args[argumentIndex],
            target.Prefix,
            spelling,
            // `pipx run name` has nowhere to put a version without adding an
            // argument, and a pin that has to invent structure is not a splice.
            CanPinInPlace: target.FromFlag || python.Specifier is not null || runner.AcceptsAtVersion);
    }

    /// <summary>Whether <paramref name="raw"/> names exactly one release.</summary>
    /// <remarks>
    /// npm: a full <c>major.minor.patch</c>, optionally with prerelease and build
    /// metadata — <c>1.2</c> is a range to npm, not a release. Python: a PEP 440
    /// release, which may have any number of components; wildcards are ranges.
    /// </remarks>
    public static bool IsExactVersion(string raw, PackageEcosystem ecosystem)
    {
        if (ecosystem == PackageEcosystem.Python) return PythonExact.IsMatch(raw);
        var version = raw.Length > 0 && raw[0] is '=' or 'v' ? raw[1..] : raw;
        return NpmExact.IsMatch(version);
    }

    /// <summary>
    /// The program's name without directory or Windows executable suffix,
    /// lower-cased: <c>C:\Program Files\nodejs\npx.cmd</c> is <c>npx</c>.
    /// </summary>
    private static string? ExecutableName(string? command)
    {
        var trimmed = command?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(trimmed)) return null;
        var name = trimmed[(trimmed.LastIndexOfAny(PathSeparators) + 1)..].ToLowerInvariant();
        foreach (var suffix in ExecutableSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return name.Length == 0 ? null : name;
    }

    private static bool NamesAFile(string spec) =>
        spec.StartsWith('.') || spec.StartsWith('/') || spec.StartsWith('\\') || spec.StartsWith('~') ||
        ScriptSuffixes.Any(suffix => spec.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    // MARK: - Runners

    private static PackageRunner? Detect(string executable, IReadOnlyList<string> args, int offset)
    {
        var first = args.Count > offset ? args[offset] : null;
        var second = args.Count > offset + 1 ? args[offset + 1] : null;

        return executable switch
        {
            "npx" => PackageRunner.Npm("npx", 0),
            "npm" when first is "exec" or "x" => PackageRunner.Npm($"npm {first}", 1),
            "pnpm" when first == "dlx" => PackageRunner.Npm("pnpm dlx", 1),
            "yarn" when first == "dlx" => PackageRunner.Npm("yarn dlx", 1),
            "bunx" => PackageRunner.Npm("bunx", 0),
            "bun" when first == "x" => PackageRunner.Npm("bun x", 1),
            "uvx" => PackageRunner.Uv("uvx", 0),
            "uv" when first == "tool" && second == "run" => PackageRunner.Uv("uv tool run", 2),
            "pipx" when first == "run" => PackageRunner.Pipx(),
            _ => null,
        };
    }

    // MARK: - Specs

    /// <summary><c>name</c>, <c>name@version</c>, <c>@scope/name</c>, <c>@scope/name@version</c>.</summary>
    private static (string Name, string? Version)? ParseNpmSpec(string raw)
    {
        var spec = raw.Trim(' ', '\t');
        if (spec.Length == 0 ||
            spec.Contains(':') ||                                   // npm:, git+, github:, file:, URLs, C:\
            NamesAFile(spec) ||
            spec.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
            spec.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return null;

        int nameEnd;
        if (spec[0] == '@')
        {
            var at = spec.IndexOf('@', 1);
            nameEnd = at < 0 ? spec.Length : at;
            if (spec[..nameEnd].Count(character => character == '/') != 1) return null;
        }
        else
        {
            var at = spec.IndexOf('@');
            nameEnd = at < 0 ? spec.Length : at;
            // `user/repo` is GitHub shorthand to npm, not a registry name.
            if (spec[..nameEnd].Contains('/')) return null;
        }

        var name = spec[..nameEnd];
        if (name.Length is 0 or > 214 ||
            name.EndsWith('/') ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || "-._~@/".Contains(character)) ||
            !name.Any(char.IsAsciiLetterOrDigit)) return null;

        return (name, nameEnd < spec.Length ? spec[(nameEnd + 1)..] : null);
    }

    private sealed record PythonSpec(
        string Name,
        string Extras,
        string? Specifier,
        bool UsedAt,
        bool IsPinned);

    /// <summary>A PEP 508 requirement without markers or URL, or uv's <c>name@version</c>.</summary>
    private static PythonSpec? ParsePythonSpec(string raw, bool allowingAt)
    {
        var spec = raw.Trim(' ', '\t');
        if (spec.Length == 0 ||
            spec.Contains("://", StringComparison.Ordinal) ||
            spec.Contains(';') ||
            spec.Contains(" @", StringComparison.Ordinal) ||
            spec.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
            NamesAFile(spec) ||
            spec.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) ||
            spec.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            spec.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;

        var match = PythonRequirement.Match(spec);
        if (!match.Success) return null;

        var name = match.Groups[1].Value;
        var extras = match.Groups[2].Success ? match.Groups[2].Value : "";
        var tail = match.Groups[3].Value.Trim(' ', '\t');

        if (tail.Length == 0) return new PythonSpec(name, extras, null, UsedAt: false, IsPinned: false);

        if (tail[0] == '@')
        {
            if (!allowingAt) return null;
            var version = tail[1..];
            if (version.Length == 0) return null;
            // uv reads `name@1.2` as `name==1.2`; only `@latest` floats.
            return new PythonSpec(
                name,
                extras,
                version,
                UsedAt: true,
                IsPinned: version != "latest" && IsExactVersion(version, PackageEcosystem.Python));
        }

        if ("=<>!~".IndexOf(tail[0]) < 0) return null;
        // One clause, `==` or `===`, and no wildcard: exactly one release.
        var pinned = !tail.Contains(',') &&
            SingleEquality.Match(tail) is { Success: true } equality &&
            !equality.Groups[1].Value.Contains('*');
        return new PythonSpec(name, extras, tail, UsedAt: false, IsPinned: pinned);
    }
}

/// <summary>One runner's command-line grammar, as far as finding the package needs it.</summary>
/// <param name="SubcommandCount">Arguments before the runner's own options: <c>dlx</c> in <c>pnpm dlx</c>.</param>
/// <param name="PackageFlags">Options that carry the package spec instead of the first positional.</param>
/// <param name="ValueFlags">Options known to consume the following argument as their value.</param>
/// <param name="AcceptsAtVersion"><c>uvx name@1.2.3</c>. pipx has no such shorthand.</param>
internal sealed record PackageRunner(
    string Label,
    PackageEcosystem Ecosystem,
    int SubcommandCount,
    IReadOnlySet<string> PackageFlags,
    IReadOnlySet<string> ValueFlags,
    bool AcceptsAtVersion)
{
    /// <param name="FromFlag">
    /// The spec came from <c>--package</c>/<c>--from</c>/<c>--spec</c> rather than the
    /// positional, which for Python means it is a PEP 508 requirement.
    /// </param>
    internal readonly record struct Target(int Index, string Spec, string Prefix, bool FromFlag);

    internal static PackageRunner Npm(string label, int subcommandCount) => new(
        label,
        PackageEcosystem.Npm,
        subcommandCount,
        Set("-p", "--package"),
        Set(
            "-c", "--call", "--registry", "--cache", "--userconfig", "--prefix",
            "-w", "--workspace", "--node-options", "--loglevel", "--shell",
            "--dir", "-C", "--filter"),
        AcceptsAtVersion: true);

    internal static PackageRunner Uv(string label, int subcommandCount) => new(
        label,
        PackageEcosystem.Python,
        subcommandCount,
        Set("--from"),
        Set(
            "--with", "-w", "--with-editable", "--with-requirements", "--python", "-p",
            "--index", "--index-url", "-i", "--extra-index-url", "--default-index",
            "--find-links", "-f", "--constraints", "--overrides", "--build-constraints",
            "--directory", "--project", "--cache-dir", "--config-file", "--env-file",
            "--python-preference", "--resolution", "--prerelease", "--index-strategy",
            "--keyring-provider", "--exclude-newer", "--refresh-package", "-P",
            "--upgrade-package", "--reinstall-package", "--no-build-package",
            "--no-binary-package", "--only-binary", "--no-binary", "--link-mode",
            "--color", "--allow-insecure-host", "--config-setting", "-C"),
        AcceptsAtVersion: true);

    internal static PackageRunner Pipx() => new(
        "pipx run",
        PackageEcosystem.Python,
        1,
        Set("--spec"),
        Set("--python", "--index-url", "-i", "--pip-args", "--backend"),
        AcceptsAtVersion: false);

    /// <summary>
    /// Finds the argument holding the package spec, skipping this runner's
    /// options. An unknown option is assumed to take no value — the common case,
    /// and the known value-takers are listed above.
    /// </summary>
    internal Target? Scan(IReadOnlyList<string> args)
    {
        var packages = new List<Target>();
        int? positional = null;
        var index = 0;

        while (index < args.Count)
        {
            var argument = args[index];
            if (argument == "--")
            {
                if (index + 1 < args.Count) positional = index + 1;
                break;
            }
            var joined = PackageFlags.FirstOrDefault(
                flag => argument.StartsWith(flag + "=", StringComparison.Ordinal));
            if (joined is not null)
            {
                var prefix = joined + "=";
                packages.Add(new Target(index, argument[prefix.Length..], prefix, FromFlag: true));
                index++;
                continue;
            }
            if (PackageFlags.Contains(argument))
            {
                if (index + 1 >= args.Count) return null;
                packages.Add(new Target(index + 1, args[index + 1], "", FromFlag: true));
                index += 2;
                continue;
            }
            if (argument.StartsWith('-') && argument.Length > 1)
            {
                index += ValueFlags.Contains(argument) && !argument.Contains('=') ? 2 : 1;
                continue;
            }
            positional = index;
            break;
        }

        // With `-p a -p b` there is no telling which package is the server.
        if (packages.Count > 1) return null;
        if (packages.Count == 1) return packages[0];
        if (positional is not { } found) return null;
        return new Target(found, args[found], "", FromFlag: false);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
