using System.Text.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core.Profiles;

/// <summary>A named, explicitly applied set of normalized server identities (§7.9).</summary>
public sealed record Profile(
    string Id,
    string Name,
    IReadOnlyList<string> ServerIDs,
    int? TokenBudget = null);

public sealed class ProfileException(string message) : Exception(message)
{
    public static ProfileException InvalidName() =>
        new("Profile names must contain between 1 and 80 characters.");

    public static ProfileException DuplicateName(string name) =>
        new($"A profile named \"{name}\" already exists.");

    public static ProfileException NotFound(string id) =>
        new($"No profile called \"{id}\" exists.");

    public static ProfileException UnreadableStore(string reason) =>
        new($"Kytto could not read the profile store, so it was left untouched: {reason}");
}

/// <summary>Atomic local storage for MCP stacks.</summary>
/// <remarks>Reading is strictly read-only; the file appears only after an explicit edit (§6.6).</remarks>
public sealed class ProfileStore(KyttoPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Lock _lock = new();

    public IReadOnlyList<Profile> All()
    {
        lock (_lock) return Load(strict: false);
    }

    public Profile Get(string id) =>
        All().FirstOrDefault(profile => profile.Id == id)
        ?? throw ProfileException.NotFound(id);

    public IReadOnlyList<Profile> Create(
        string name,
        IReadOnlyList<string> serverIds,
        int? tokenBudget = null)
    {
        lock (_lock)
        {
            var profiles = Load(strict: true).ToList();
            var cleanName = ValidateName(name, profiles, excludingId: null);
            profiles.Add(new Profile(
                Guid.NewGuid().ToString("N"),
                cleanName,
                Normalize(serverIds),
                NormalizeBudget(tokenBudget)));
            Sort(profiles);
            Save(profiles);
            return profiles;
        }
    }

    public IReadOnlyList<Profile> Update(
        string id,
        string name,
        IReadOnlyList<string> serverIds,
        int? tokenBudget = null)
    {
        lock (_lock)
        {
            var profiles = Load(strict: true).ToList();
            var index = profiles.FindIndex(profile => profile.Id == id);
            if (index < 0) throw ProfileException.NotFound(id);

            var cleanName = ValidateName(name, profiles, id);
            profiles[index] = profiles[index] with
            {
                Name = cleanName,
                ServerIDs = Normalize(serverIds),
                TokenBudget = NormalizeBudget(tokenBudget),
            };
            Sort(profiles);
            Save(profiles);
            return profiles;
        }
    }

    public IReadOnlyList<Profile> Delete(string id)
    {
        lock (_lock)
        {
            var profiles = Load(strict: true).ToList();
            if (profiles.RemoveAll(profile => profile.Id == id) == 0)
            {
                throw ProfileException.NotFound(id);
            }
            Save(profiles);
            return profiles;
        }
    }

    /// <summary>Keeps stacks pointing at the same server when its config key changes.</summary>
    public void RenameServer(string oldId, string newName)
    {
        var oldIdentity = Server.Identity(oldId);
        var newIdentity = Server.Identity(newName);
        if (oldIdentity == newIdentity) return;

        lock (_lock)
        {
            var profiles = Load(strict: true).ToList();
            var changed = false;
            for (var index = 0; index < profiles.Count; index++)
            {
                if (!profiles[index].ServerIDs.Contains(oldIdentity, StringComparer.Ordinal)) continue;
                profiles[index] = profiles[index] with
                {
                    ServerIDs = Normalize(profiles[index].ServerIDs
                        .Select(id => id == oldIdentity ? newIdentity : id)
                        .ToArray()),
                };
                changed = true;
            }
            if (changed) Save(profiles);
        }
    }

    private IReadOnlyList<Profile> Load(bool strict)
    {
        try
        {
            var text = File.ReadAllText(paths.ProfilesFile);
            return (JsonSerializer.Deserialize<IReadOnlyList<Profile>>(text, Options) ?? [])
                .Select(profile => profile with
                {
                    Name = profile.Name.Trim(),
                    ServerIDs = Normalize(profile.ServerIDs ?? []),
                    TokenBudget = NormalizeBudget(profile.TokenBudget),
                })
                .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (strict) throw ProfileException.UnreadableStore(error.Message);
            return [];
        }
    }

    private void Save(IReadOnlyList<Profile> profiles) =>
        AtomicWriter.Write(JsonSerializer.Serialize(profiles, Options), paths.ProfilesFile);

    private static string ValidateName(
        string name,
        IReadOnlyList<Profile> profiles,
        string? excludingId)
    {
        var clean = name.Trim();
        if (clean.Length is 0 or > 80) throw ProfileException.InvalidName();
        if (profiles.Any(profile => profile.Id != excludingId &&
                                   string.Equals(profile.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw ProfileException.DuplicateName(clean);
        }
        return clean;
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> ids) => ids
        .Select(Server.Identity)
        .Where(id => id.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static int? NormalizeBudget(int? budget) => budget is > 0
        ? Math.Min(budget.Value, Health.TokenWeight.ReferenceContextWindow)
        : null;

    private static void Sort(List<Profile> profiles) => profiles.Sort((left, right) =>
        StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
}
