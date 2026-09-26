import Foundation

/// What the write pipeline needs to know about a config file, whatever it is
/// spelled in.
///
/// Deliberately tiny. The interesting operations — set this key, remove that
/// table — differ enough between JSON and TOML that pretending they are the same
/// call would produce a worst-of-both abstraction. What genuinely is the same is
/// the §6 pipeline around them: read, check nobody else moved it, transform,
/// check the result still parses, write atomically. That is all this covers.
public protocol ConfigDocument: Sendable {
    /// What a client that has never configured MCP starts from.
    static var emptySource: String { get }
    /// For the message when an edit produces something unparseable.
    static var formatName: String { get }

    static func parse(_ text: String) throws -> Self

    var sourceText: String { get }
}

extension JSONDocument: ConfigDocument {
    /// An object, because every JSON client's config is one and a bare `{}` is a
    /// valid starting point for splicing a servers key into.
    public static var emptySource: String { "{}\n" }
    public static var formatName: String { "JSON" }
}

extension TOMLDocument: ConfigDocument {
    /// Nothing. An empty TOML file is a valid TOML file holding no tables, and
    /// unlike JSON there is no punctuation to seed.
    public static var emptySource: String { "" }
    public static var formatName: String { "TOML" }
}
