namespace Kytto.Core.Clients;

/// <summary>
/// What a client puts into a session before the model has searched for a tool
/// (roadmap §16).
/// </summary>
/// <remarks>
/// Only documented behaviour is recorded; anything else stays <see cref="Unknown"/>,
/// because the second number Kytto shows depends on it.
/// </remarks>
/// <param name="Mode"><c>deferred</c>, <c>all</c> or <c>unknown</c>, as the wire spells it.</param>
/// <param name="Note">What is documented, and where; null when nothing is.</param>
public sealed record ToolLoading(string Mode, string? Note)
{
    /// <summary>Definitions wait behind tool search; the session starts with names or short entries.</summary>
    public static ToolLoading Deferred(string note) => new("deferred", note);

    /// <summary>Every enabled tool's full definition is sent with each session.</summary>
    public static ToolLoading LoadsAll(string note) => new("all", note);

    public static ToolLoading Unknown { get; } = new("unknown", null);
}
