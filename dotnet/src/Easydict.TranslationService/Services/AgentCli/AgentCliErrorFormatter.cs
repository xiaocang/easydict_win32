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

    /// <summary>
    /// Returns ": &lt;excerpt&gt;" built from stderr (preferred) or the stdout control
    /// lines, capped at a display-friendly length; empty string when there is nothing.
    /// </summary>
    public static string BuildDetail(IReadOnlyList<string> controlLines, string stdErr)
    {
        var source = !string.IsNullOrWhiteSpace(stdErr)
            ? stdErr
            : string.Join('\n', controlLines);

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
            text = text[..MaxDetailLength] + "…";
        }

        return $": {text}";
    }
}
