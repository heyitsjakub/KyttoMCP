using System.Globalization;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core;

/// <summary>
/// Launch fields — command, arguments, environment, endpoint, transport — that changed
/// in a known file while Kytto was not the one writing it (§7, §22).
/// </summary>
/// <remarks>
/// <para>
/// A pending review holds only masked summaries: which field changed and, at most,
/// whether it is set or how many entries it has. The baselines are the definitions
/// actually observed during this session and are never written anywhere, so nothing
/// a review stores could leak a token, an argument or an endpoint.
/// </para>
/// <para>
/// Kytto's own writes record the file's new digest before the next read, so a file
/// whose digest still matches what Kytto last saw has not been changed by anyone else
/// and is never reported, whatever the definition in it now says. Comparisons start
/// with the first read of each session; pending reviews survive relaunches until they
/// are acknowledged.
/// </para>
/// </remarks>
public sealed class LaunchReviews
{
    public sealed record Field(string Name, string Before, string After);

    /// <param name="ObservedAt">ISO 8601, UTC, to the second.</param>
    public sealed record Alert(
        string Token,
        string ServerID,
        string ClientID,
        string File,
        string Scope,
        string ObservedAt,
        IReadOnlyList<Field> Fields);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly KyttoPaths _paths;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _lock = new();
    private Dictionary<Key, ClientDefinition> _known = [];
    private List<Alert> _alerts = [];
    private readonly bool _unreadable;

    public LaunchReviews(KyttoPaths paths, Func<DateTimeOffset>? now = null)
    {
        _paths = paths;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (!File.Exists(paths.LaunchReviewsFile)) return;
        try
        {
            _alerts = JsonSerializer.Deserialize<List<Alert>>(File.ReadAllText(paths.LaunchReviewsFile), Options)?
                .Where(alert => alert is { Token: not null, ServerID: not null, ClientID: not null, File: not null, Fields: not null })
                .ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never overwritten while unreadable: what is in it may still be the only
            // record of a change the user has not looked at.
            _unreadable = true;
            StorageError = "The saved launch reviews could not be read. New reviews are available for this session.";
        }
    }

    public IReadOnlyList<Alert> Alerts
    {
        get
        {
            lock (_lock) return _alerts.ToArray();
        }
    }

    public string? StorageError { get; private set; }

    /// <summary>Compares an accepted discovery pass with the one before it.</summary>
    /// <param name="prior">What Kytto last recorded for a file, before this pass.</param>
    /// <param name="incoming">What this pass read, by path; null where the file was missing.</param>
    public void Observe(
        DiscoveryResult result,
        Func<string, string?> prior,
        IReadOnlyDictionary<string, string?> incoming)
    {
        var read = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, digest) in incoming) read[Normalize(path)] = digest;
        // A client whose file could not be read this time keeps its baseline, so a
        // half-saved file does not make the next good read look unchanged.
        var failed = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.ClientID is not null)
            .Select(diagnostic => diagnostic.ClientID!.Value)
            .ToHashSet();
        var clients = new Dictionary<ClientKey, DiscoveredClient>();
        foreach (var client in result.Clients) clients.TryAdd(client.Id, client);

        lock (_lock)
        {
            var next = _known
                .Where(pair => failed.Contains(pair.Key.Client))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var changed = false;
            foreach (var server in result.Servers)
            {
                foreach (var (id, definition) in server.DefinitionsByClient)
                {
                    if (!server.Locations.TryGetValue(id, out var location) ||
                        !clients.TryGetValue(id, out var client))
                    {
                        continue;
                    }
                    var file = Normalize(location.File);
                    var key = new Key(server.Id, id, file.ToUpperInvariant());
                    next[key] = definition;
                    if (!_known.TryGetValue(key, out var old) ||
                        !read.TryGetValue(file, out var digest) || digest is null ||
                        prior(location.File) == digest)
                    {
                        continue;
                    }
                    var diff = Changes(old, definition);
                    if (diff.Count == 0) continue;

                    var index = _alerts.FindIndex(alert =>
                        string.Equals(alert.File, location.File, StringComparison.OrdinalIgnoreCase) &&
                        alert.ClientID == id.Raw() &&
                        alert.ServerID == server.Id);
                    // Fields keep what they were before the first unreviewed change.
                    var fields = index >= 0 ? _alerts[index].Fields.ToList() : [];
                    foreach (var field in diff)
                    {
                        var at = fields.FindIndex(existing => existing.Name == field.Name);
                        if (at >= 0) fields[at] = fields[at] with { After = field.After };
                        else fields.Add(field);
                    }
                    var alert = new Alert(
                        Guid.NewGuid().ToString("D").ToUpperInvariant(),
                        server.Id,
                        id.Raw(),
                        location.File,
                        DefinitionInventory.ScopeDescription(client, location),
                        _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        fields);
                    if (index >= 0) _alerts[index] = alert;
                    else _alerts.Add(alert);
                    changed = true;
                }
            }
            _known = next;
            if (changed && !_unreadable)
            {
                try
                {
                    Save(_alerts);
                    StorageError = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    StorageError = "Pending launch reviews could not be saved. They are available for this session.";
                }
            }
        }
    }

    /// <summary>Marks exactly the version reviewed as seen. Changes only Kytto's record.</summary>
    /// <exception cref="LaunchReviewStaleException">The review changed since it was shown.</exception>
    public void Acknowledge(string token)
    {
        lock (_lock)
        {
            if (!_alerts.Any(alert => alert.Token == token)) throw new LaunchReviewStaleException();
            var remaining = _alerts.Where(alert => alert.Token != token).ToList();
            if (!_unreadable)
            {
                Save(remaining);
                StorageError = null;
            }
            _alerts = remaining;
        }
    }

    private void Save(IReadOnlyList<Alert> entries)
    {
        KyttoStorage.EnsurePrivateRoot(_paths);
        AtomicWriter.Write(JsonSerializer.Serialize(entries, Options), _paths.LaunchReviewsFile);
    }

    private static List<Field> Changes(ClientDefinition old, ClientDefinition updated)
    {
        var fields = new List<Field>();
        static string Presence(string? text) => text is null ? "Absent" : "Set (masked)";
        if (old.Command != updated.Command)
        {
            fields.Add(new Field("Command", Presence(old.Command), Presence(updated.Command)));
        }
        if (!old.Args.SequenceEqual(updated.Args, StringComparer.Ordinal))
        {
            fields.Add(new Field("Arguments", $"{old.Args.Count} masked", $"{updated.Args.Count} masked"));
        }
        if (!EnvironmentPairs(old).SequenceEqual(EnvironmentPairs(updated)))
        {
            fields.Add(new Field("Environment", $"{old.Env.Count} masked", $"{updated.Env.Count} masked"));
        }
        if (old.Url != updated.Url)
        {
            fields.Add(new Field("Endpoint", Presence(old.Url), Presence(updated.Url)));
        }
        if (old.Transport != updated.Transport)
        {
            fields.Add(new Field("Transport", old.Transport.Raw(), updated.Transport.Raw()));
        }
        return fields;

        static IEnumerable<(string Key, string? Value)> EnvironmentPairs(ClientDefinition definition) =>
            definition.Env
                .Select(entry => (entry.Key, entry.Value))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private readonly record struct Key(string Server, ClientKey Client, string Path);
}

public sealed class LaunchReviewStaleException()
    : InvalidOperationException("The launch review changed. Review the latest observed fields.");
