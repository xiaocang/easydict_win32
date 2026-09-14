using System.Text.RegularExpressions;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Formats CLI failure output into a short human-readable detail suffix.
/// </summary>
internal static class AgentCliErrorFormatter
{
    private const int MaxDetailLength = 300;
    private static readonly Regex NamedSecretRegex = new(
        @"(?i)(?<name>api[_ -]?key|access[_ -]?token|oauth[_ -]?token|authorization|bearer)(?<separator>[""'\s:=]+)(?<secret>[A-Za-z0-9+/_\-.]{8,})",
        RegexOptions.CultureInvariant);
    private static readonly Regex PrefixedSecretRegex = new(
        @"(?i)\b(?:sk|key)-[A-Za-z0-9_-]{12,}\b",
        RegexOptions.CultureInvariant);

    // Startup metadata lines (session init / status pings) carry no failure
    // information but can be large (e.g. --verbose lists every slash command),
    // so they crowd out the actual error when control lines are joined and
    // capped. Excluded only as a fallback; a parsed `result` line is preferred
    // by the caller whenever one is available.
    private static readonly string[] MetadataLineMarkers =
    [
        "\"subtype\":\"init\"",
        "\"subtype\":\"status\"",
    ];

    /// <summary>
    /// Returns ": &lt;excerpt&gt;" built from stderr (preferred) or the stdout control
    /// lines, capped at a display-friendly length; empty string when there is nothing.
    /// </summary>
    public static string BuildDetail(IReadOnlyList<string> controlLines, string stdErr)
    {
        var hasStdErr = !string.IsNullOrWhiteSpace(stdErr);
        var source = hasStdErr
            ? stdErr
            : string.Join('\n', FilterMetadataLines(controlLines));

        var text = string.Join(
            ' ',
            source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        text = NamedSecretRegex.Replace(text, "${name}${separator}[redacted]");
        text = PrefixedSecretRegex.Replace(text, "[redacted]");

        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.Length > MaxDetailLength)
        {
            // stderr commonly starts with the error followed by usage text.
            // Control lines instead tend to carry the actual failure at the end.
            text = hasStdErr
                ? text[..MaxDetailLength] + "…"
                : "…" + text[^MaxDetailLength..];
        }

        return $": {text}";
    }

    private static IEnumerable<string> FilterMetadataLines(IReadOnlyList<string> controlLines)
    {
        var filtered = controlLines
            .Where(line => !MetadataLineMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
            .ToList();

        // If every line looked like metadata, fall back to the unfiltered set
        // rather than showing nothing.
        return filtered.Count > 0 ? filtered : controlLines;
    }
}
