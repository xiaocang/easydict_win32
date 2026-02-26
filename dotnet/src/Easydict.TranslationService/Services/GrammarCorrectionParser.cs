using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Parses structured grammar correction output from LLM services.
/// Expected format uses [CORRECTED]...[/CORRECTED] and [EXPLANATION]...[/EXPLANATION] markers.
/// Falls back to treating the entire output as corrected text if markers are absent.
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

        var correctedText = ExtractSection(rawOutput, CorrectedOpenTag, CorrectedCloseTag);
        var explanation = ExtractSection(rawOutput, ExplanationOpenTag, ExplanationCloseTag);

        // Fallback: if no markers found, treat entire output as corrected text
        correctedText ??= rawOutput.Trim();

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
}
