using System.Numerics;
using System.Text.RegularExpressions;
using Kytto.Core.Health;

namespace Kytto.Core.Model;

public static class ProvenanceSourceKinds
{
    public const string Npm = "npm";
    public const string Python = "python";
    public const string LocalPackage = "localPackage";
    public const string LocalExecutable = "localExecutable";
    public const string Remote = "remote";
    public const string Unknown = "unknown";
}

public static class ProvenanceConfidences
{
    public const string Identified = "identified";
    public const string Inferred = "inferred";
    public const string Unknown = "unknown";
}

/// <summary>Native maintenance metadata inferred from a server definition.</summary>
/// <remarks>
/// Discovery only classifies what is already written. It never queries a registry;
/// latest-version data is filled only by an explicit provenance command (§7).
/// </remarks>
public sealed record ServerProvenance(
    string SourceKind,
    string? PackageName,
    string? SourceURL,
    string? InstalledVersion,
    string? LatestVersion,
    DateTimeOffset? LatestCheckedAt,
    string Confidence,
    string MaintenanceState)
{
    public static ServerProvenance Unknown => new(
        ProvenanceSourceKinds.Unknown,
        null,
        null,
        null,
        null,
        null,
        ProvenanceConfidences.Unknown,
        "unknownSource");

    public static ServerProvenance Infer(Server server) =>
        Infer(server.Transport, server.Command, server.Args, server.Url);

    public static ServerProvenance Infer(
        Transport transport,
        string? command,
        IReadOnlyList<string> args,
        string? url)
    {
        if (transport != Transport.Stdio)
        {
            return new ServerProvenance(
                ProvenanceSourceKinds.Remote,
                null,
                url,
                null,
                null,
                null,
                string.IsNullOrWhiteSpace(url)
                    ? ProvenanceConfidences.Unknown
                    : ProvenanceConfidences.Identified,
                "unchecked");
        }

        // Runners go through `PackageLaunch`, the parser MCP Doctor pins with, so
        // the name shown here and the name a version lookup asks about cannot
        // disagree (§7.10). What it declines to read falls through to the older,
        // looser inference below, which only ever names a package and never
        // drives a write.
        if (PackageLaunch.Parse(command, args) is { } launch)
        {
            return new ServerProvenance(
                launch.Ecosystem == PackageEcosystem.Npm
                    ? ProvenanceSourceKinds.Npm
                    : ProvenanceSourceKinds.Python,
                launch.Name,
                null,
                RequestedRelease(launch),
                null,
                null,
                ProvenanceConfidences.Inferred,
                "unchecked");
        }

        var executable = ExecutableName(command);
        if (executable is "npx" or "npm" or "pnpm" or "yarn" or "bunx" or "bun")
        {
            var package = FindNpmPackage(executable, args);
            return new ServerProvenance(
                ProvenanceSourceKinds.Npm,
                package.Name,
                null,
                package.Version,
                null,
                null,
                package.Name is null
                    ? ProvenanceConfidences.Unknown
                    : ProvenanceConfidences.Inferred,
                package.Name is null ? "unknownSource" : "unchecked");
        }

        if (executable is "uvx" or "pipx" or "python" or "python3" or "py")
        {
            var package = FindPythonPackage(executable, args);
            if (package.IsLocal) return LocalPackage(package.Version);
            return new ServerProvenance(
                ProvenanceSourceKinds.Python,
                package.Name,
                null,
                package.Version,
                null,
                null,
                    package.Name is null
                        ? ProvenanceConfidences.Unknown
                        : ProvenanceConfidences.Inferred,
                    package.Name is null ? "unknownSource" : "unchecked");
        }

        if (LooksLikeLocalPackage(command, args)) return LocalPackage(null);
        if (HasPath(command))
        {
            return new ServerProvenance(
                ProvenanceSourceKinds.LocalExecutable,
                null,
                null,
                null,
                null,
                null,
                ProvenanceConfidences.Identified,
                "unchecked");
        }

        return Unknown;
    }

    public ServerProvenance WithLatest(string? latestVersion, DateTimeOffset checkedAt) => this with
    {
        LatestVersion = latestVersion,
        LatestCheckedAt = checkedAt,
        MaintenanceState = MaintenanceAfterLatest(latestVersion),
    };

    public ServerProvenance WithHealth(HealthResult? health) => this with
    {
        MaintenanceState = health?.Status switch
        {
            HealthStatus.Failed => "unhealthy",
            HealthStatus.NeedsAuthorization => "unchecked",
            HealthStatus.Passed when SourceKind == ProvenanceSourceKinds.Unknown => "unknownSource",
            HealthStatus.Passed when LatestVersion is not null &&
                InstalledVersion is not null && VersionIsOlder(InstalledVersion, LatestVersion)
                => "staleButResponsive",
            HealthStatus.Passed => "healthy",
            _ => SourceKind == ProvenanceSourceKinds.Unknown ? "unknownSource" : "unchecked",
        },
    };

    private string MaintenanceAfterLatest(string? latestVersion)
    {
        if (SourceKind == ProvenanceSourceKinds.Unknown) return "unknownSource";
        if (MaintenanceState == "unhealthy") return "unhealthy";
        if (MaintenanceState is not ("healthy" or "staleButResponsive")) return "unchecked";
        return InstalledVersion is not null && latestVersion is not null &&
            VersionIsOlder(InstalledVersion, latestVersion)
            ? "staleButResponsive"
            : "healthy";
    }

    private static ServerProvenance LocalPackage(string? version) => new(
        ProvenanceSourceKinds.LocalPackage,
        null,
        null,
        version,
        null,
        null,
        ProvenanceConfidences.Inferred,
        "unchecked");

    /// <summary>The release a launch asks for, when it names one plainly.</summary>
    /// <remarks>
    /// <c>pkg@1.2.3</c> and <c>pkg==1.2.3</c> both read as <c>1.2.3</c>; a tag or a
    /// range is not a version anything is installed at.
    /// </remarks>
    private static string? RequestedRelease(PackageLaunch launch)
    {
        var requested = launch.RequestedVersion?.TrimStart('=').Trim();
        return requested is { Length: > 0 } && IsVersion(requested) ? requested : null;
    }

    private static (string? Name, string? Version) FindNpmPackage(
        string executable,
        IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "--package" or "-p")
            {
                if (index + 1 < args.Count && ParsePackage(args[index + 1]) is { } package) return package;
                continue;
            }
            if (arg.StartsWith('-'))
            {
                if (arg is "--prefix" or "--cache" or "--registry" or "--userconfig" &&
                    index + 1 < args.Count) index++;
                continue;
            }
            if (arg is "run") return (null, null);
            if (arg is "exec" or "dlx" or "x" or "install" or "i") continue;
            if (ParsePackage(arg) is { } result) return result;
        }

        // `bun x` and `bunx` use the same package spelling, while a bare `bun`
        // invocation with no package is correctly left unknown.
        _ = executable;
        return (null, null);
    }

    private static (string? Name, string? Version, bool IsLocal) FindPythonPackage(
        string executable,
        IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "-m" && index + 1 < args.Count)
            {
                return ParsePackage(args[index + 1]) is { } module
                    ? (module.Name, module.Version, false)
                    : (null, null, false);
            }
            if (arg == "--from" && index + 1 < args.Count)
            {
                return ParsePackage(args[index + 1]) is { } package
                    ? (package.Name, package.Version, false)
                    : (null, null, false);
            }
            if (arg is "run" or "exec") continue;
            if (arg.StartsWith('-'))
            {
                if (arg is "--python" && index + 1 < args.Count) index++;
                continue;
            }
            if (LooksLikeScript(arg)) return (null, null, true);
            if (executable is "uvx" or "pipx")
            {
                var package = ParsePackage(arg);
                return package is { } result
                    ? (result.Name, result.Version, false)
                    : (null, null, false);
            }
            return (null, null, false);
        }
        return (null, null, false);
    }

    private static (string Name, string? Version)? ParsePackage(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(char.IsControl) || value.StartsWith('-') ||
            (value.Contains('/') && !value.StartsWith('@')) ||
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith("../", StringComparison.Ordinal) ||
            value.Contains('\\') || value.Contains(':')) return null;

        var separator = value.Length > 0 && value[0] == '@'
            ? value.IndexOf('@', 1)
            : value.IndexOf('@');
        var name = separator > 0 ? value[..separator] : value;
        var version = separator > 0 ? value[(separator + 1)..] : null;
        if (name.Length == 0 || name.Any(char.IsWhiteSpace) ||
            !Regex.IsMatch(name, @"^@?[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)?$",
                RegexOptions.CultureInvariant)) return null;
        if (version is not null && !IsVersion(version)) return (name, null);
        return (name, version);
    }

    private static string ExecutableName(string? command)
    {
        var value = command?.Trim().Trim('"', '\'') ?? "";
        return Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
    }

    private static bool HasPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (Path.IsPathFullyQualified(value) || value.Contains('/') || value.Contains('\\'));

    private static bool LooksLikeLocalPackage(string? command, IReadOnlyList<string> args) =>
        LooksLikeScript(command) || args.Any(LooksLikeScript);

    private static bool LooksLikeScript(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim('"', '\'');
        return trimmed.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersion(string value)
    {
        var parts = value.TrimStart('v', 'V').Split('.', StringSplitOptions.None);
        return parts.Length is >= 1 and <= 4 && parts.All(part =>
            part.Length > 0 && part.All(character => character is >= '0' and <= '9'));
    }

    private static bool VersionIsOlder(string installed, string latest)
    {
        if (!TryVersion(installed, out var left) || !TryVersion(latest, out var right)) return false;
        for (var index = 0; index < 4; index++)
        {
            var comparison = (index < left.Length ? left[index] : BigInteger.Zero)
                .CompareTo(index < right.Length ? right[index] : BigInteger.Zero);
            if (comparison != 0) return comparison < 0;
        }
        return false;
    }

    private static bool TryVersion(string value, out BigInteger[] components)
    {
        components = [];
        var parts = value.TrimStart('v', 'V').Split('-', 2)[0]
            .Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4 || parts.Any(part =>
            part.Length == 0 || !part.All(character => character is >= '0' and <= '9'))) return false;
        components = parts.Select(part => BigInteger.Parse(part)).ToArray();
        return true;
    }
}
