namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Replaces formula spans in text with numbered placeholders {v0}, {v1}, ...
/// and builds a token list for later restoration.
/// Replaces ProtectFormulaSpans in LongDocumentTranslationService.
/// </summary>
public sealed class FormulaProtector
{
    /// <summary>
    /// Protects all formula spans in <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The input text possibly containing formulas.</param>
    /// <param name="tokens">Ordered list of protected tokens, indexed by placeholder number.</param>
    /// <returns>Text with formula spans replaced by {v0}, {v1}, ... placeholders.</returns>
    public string Protect(string text, out IReadOnlyList<FormulaToken> tokens)
    {
        if (string.IsNullOrEmpty(text))
        {
            tokens = Array.Empty<FormulaToken>();
            return text;
        }

        var rawTokens = new List<string>();
        var counter = 0;

        var protectedText = FormulaDetector.FormulaRegex.Replace(text, match =>
        {
            var placeholder = $"{{v{counter}}}";
            rawTokens.Add(match.Value);
            counter++;
            return placeholder;
        });

        // Extend placeholders to include trailing parenthesized formula arguments
        protectedText = FormulaDetector.ExtendTrailingParens(protectedText, rawTokens);

        // Build final token list with classification and simplified form
        var result = new List<FormulaToken>(rawTokens.Count);
        for (var i = 0; i < rawTokens.Count; i++)
        {
            var raw = rawTokens[i];
            var type = FormulaDetector.Classify(raw);
            var placeholder = $"{{v{i}}}";
            var simplified = BuildSimplified(raw, type);
            result.Add(new FormulaToken(type, raw, placeholder, simplified));
        }

        tokens = result;
        return protectedText;
    }

    private static string BuildSimplified(string raw, FormulaTokenType type)
    {
        return type switch
        {
            FormulaTokenType.SequenceToken =>
                // Replace _ with hyphen so the renderer does NOT treat it as a subscript signal
                raw.Replace('_', '-'),
            FormulaTokenType.DisplayMath or FormulaTokenType.InlineMath or
            FormulaTokenType.LaTeXEnv or FormulaTokenType.Matrix =>
                LatexFormulaSimplifier.Simplify(raw, preserveScriptSignals: true),
            _ =>
                LatexFormulaSimplifier.SimplifyMathContent(raw, preserveScriptSignals: true),
        };
    }
}
