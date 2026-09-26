using Kytto.Core.Clients;

namespace Kytto.Core.Model;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A problem found while reading. A file that produced an <c>Error</c> is one
/// Kytto will not write to.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    ClientKey? ClientID,
    string PathDisplay,
    string Message)
{
    public string SeverityRaw => Severity == DiagnosticSeverity.Error ? "error" : "warning";
}
