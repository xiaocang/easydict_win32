using System.Text.RegularExpressions;

namespace Easydict.WinUI.Services;

/// <summary>
/// Cleans up raw text returned by LLM/VLM OCR engines (pure logic, no I/O).
/// </summary>
public static class OcrTextSanitizer
{
    /// <summary>
    /// A complete inline reasoning block, e.g. <c>&lt;think&gt;...&lt;/think&gt;</c>.
    /// </summary>
    private static readonly Regex ThinkingBlockRegex = new(
        @"<\s*(think|thinking|reasoning)\s*>.*?<\s*/\s*\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// An opener with no matching closer — the response ran out of tokens mid-thought,
    /// so everything from the tag onward is reasoning.
    /// </summary>
    private static readonly Regex UnclosedThinkingRegex = new(
        @"<\s*(think|thinking|reasoning)\s*>.*$",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A closer with no matching opener. Some gateways drop the opening tag and emit the
    /// reasoning as plain leading text, so everything up to the last closer goes.
    /// </summary>
    private static readonly Regex OrphanThinkingCloserRegex = new(
        @"^.*<\s*/\s*(think|thinking|reasoning)\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes inline reasoning markup so a thinking model's chain of thought does not end
    /// up in the recognized text. Returns the trimmed input when no markup is present.
    /// </summary>
    public static string StripThinkingMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = ThinkingBlockRegex.Replace(text, string.Empty);
        stripped = UnclosedThinkingRegex.Replace(stripped, string.Empty);
        stripped = OrphanThinkingCloserRegex.Replace(stripped, string.Empty);

        return stripped.Trim();
    }
}
