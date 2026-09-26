namespace Kytto.Core.Health;

/// <summary>Recognises an MCP process that is waiting for an interactive sign-in.</summary>
/// <remarks>
/// Markers are required. An OAuth-looking link in a crash report is not evidence
/// that a browser round trip will repair the handshake (§7.3).
/// </remarks>
public static class AuthorizationSignal
{
    private static readonly string[] Markers =
    [
        "please authorize",
        "waiting for authorization",
        "authorization required",
        "authorization needed",
        "authentication required",
        "authorization_pending",
        "browser opened automatically",
    ];

    private static readonly char[] CandidateTerminators = ['"', '\'', '<', '>', '`', ')', ']', '}'];

    /// <summary>
    /// Returns whether stderr proves an interactive wait, and a validated HTTPS
    /// page when the server printed one. The state does not depend on a URL.
    /// </summary>
    public static bool TryDetect(string? stderr, out string? authorizationURL)
    {
        authorizationURL = null;
        if (string.IsNullOrEmpty(stderr) || !ContainsMarker(stderr)) return false;

        authorizationURL = ExtractURL(stderr);
        return true;
    }

    public static bool IsFailedHandshake(HealthFailureReason failure) => failure.Reason is
        "timedOut" or "closedBeforeAnswering" or "exited" or "serverRefused";

    public static bool IsValidAuthorizationURL(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    internal static string? ExtractURL(string stderr)
    {
        var offset = 0;
        while (offset < stderr.Length)
        {
            var start = stderr.IndexOf("https://", offset, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            var end = start;
            while (end < stderr.Length && !char.IsWhiteSpace(stderr[end]) &&
                   Array.IndexOf(CandidateTerminators, stderr[end]) < 0)
            {
                end++;
            }

            var candidate = stderr[start..end].TrimEnd('.', ',', ';', ':', '!');
            var lineStart = stderr.LastIndexOfAny(['\r', '\n'], start);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = stderr.IndexOfAny(['\r', '\n'], start);
            if (lineEnd < 0) lineEnd = stderr.Length;
            var line = stderr[lineStart..lineEnd];

            var qualifies = candidate.Contains("authoriz", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                || ContainsMarker(line);
            if (qualifies && IsValidAuthorizationURL(candidate)) return candidate;

            offset = Math.Max(end, start + "https://".Length);
        }
        return null;
    }

    private static bool ContainsMarker(string value) =>
        Markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
