using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Parses structured grammar correction output from LLM services.
/// Expected format uses [CORRECTED]...[/CORRECTED] and [EXPLANATION]...[/EXPLANATION] markers.
/// Also supports the legacy shared prompt format: corrected text, a line containing "---",
/// then a brief explanation. Falls back to treating the entire output as corrected text
/// if no known structure is present.
/// </summary>
public static class GrammarCorrectionParser
{
    private const string CorrectedOpenTag = "[CORRECTED]";
    private const string CorrectedCloseTag = "[/CORRECTED]";
    private const string ExplanationOpenTag = "[EXPLANATION]";
    private const string ExplanationCloseTag = "[/EXPLANATION]";

    /// <summary>
    /// Parses the raw LLM output into a structured <see cref="GrammarCorrectionResult"/>.
    /// </summary>
    /// <param name="rawOutput">The complete accumulated LLM output.</param>
    /// <param name="originalText">The original input text.</param>
    /// <param name="serviceName">The service that produced this result.</param>
    /// <param name="timingMs">Time taken in milliseconds.</param>
    /// <returns>A structured grammar correction result.</returns>
    public static GrammarCorrectionResult Parse(
        string rawOutput, string originalText, string serviceName, long timingMs)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new GrammarCorrectionResult
            {
                OriginalText = originalText,
                CorrectedText = originalText,
                Explanation = null,
                ServiceName = serviceName,
                TimingMs = timingMs,
            };
        }

        var output = StripMisplacedLeadingSeparator(rawOutput);
        if (string.IsNullOrWhiteSpace(output))
        {
            return new GrammarCorrectionResult
            {
                OriginalText = originalText,
                CorrectedText = originalText,
                Explanation = null,
                ServiceName = serviceName,
                TimingMs = timingMs,
            };
        }

        var correctedText = ExtractSection(output, CorrectedOpenTag, CorrectedCloseTag);
        var explanation = ExtractSection(output, ExplanationOpenTag, ExplanationCloseTag);

        if (correctedText is null)
        {
            var legacy = TryParseLegacySeparatorFormat(output);
            if (legacy is not null)
            {
                correctedText = legacy.Value.CorrectedText;
                explanation = legacy.Value.Explanation;
            }
        }

        // Fallback: if no structure is found, treat entire output as corrected text.
        correctedText ??= output.Trim();

        return new GrammarCorrectionResult
        {
            OriginalText = originalText,
            CorrectedText = correctedText,
            Explanation = explanation,
            ServiceName = serviceName,
            TimingMs = timingMs,
        };
    }

    /// <summary>
    /// Extracts text between an open and close tag, trimming whitespace.
    /// Returns null if tags are not found.
    /// </summary>
    private static string? ExtractSection(string text, string openTag, string closeTag)
    {
        var startIndex = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) return null;

        startIndex += openTag.Length;

        var endIndex = text.IndexOf(closeTag, startIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0) return null;

        var section = text[startIndex..endIndex].Trim();
        return string.IsNullOrEmpty(section) ? null : section;
    }

    private static string StripMisplacedLeadingSeparator(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            return text;
        }

        if (trimmed.Length > 3 && !char.IsWhiteSpace(trimmed[3]))
        {
            return text;
        }

        return trimmed[3..].TrimStart();
    }

    private static (string CorrectedText, string? Explanation)? TryParseLegacySeparatorFormat(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var separatorIndex = Array.FindIndex(lines, line => line.Trim() == "---");
        if (separatorIndex < 0)
        {
            return null;
        }

        var correctedText = string.Join('\n', lines.Take(separatorIndex)).Trim();
        if (string.IsNullOrEmpty(correctedText))
        {
            return null;
        }

        var explanation = string.Join('\n', lines.Skip(separatorIndex + 1)).Trim();
        return (correctedText, string.IsNullOrEmpty(explanation) ? null : explanation);
    }
}
