namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Replaces formula spans in text with numbered placeholders {v0}, {v1}, ...
/// and builds a token list for later restoration.
/// Replaces ProtectFormulaSpans in LongDocumentTranslationService.
/// </summary>
public sealed class FormulaProtector
{
    /// <summary>
    /// Protects all formula spans in <paramref name="text"/> (all matches treated as hard).
    /// Backward-compatible overload.
    /// </summary>
    public string Protect(string text, out IReadOnlyList<FormulaToken> tokens)
    {
        return ProtectWithConfidence(text, out tokens, splitByConfidence: false);
    }

    /// <summary>
    /// Protects formula spans with confidence-based two-tier output:
    /// <list type="bullet">
    /// <item>High-confidence matches → {vN} placeholder (added to <paramref name="hardTokens"/>)</item>
    /// <item>Low-confidence matches → $original_text$ inline LaTeX (LLM decides)</item>
    /// </list>
    /// </summary>
    /// <param name="text">The input text possibly containing formulas.</param>
    /// <param name="hardTokens">Tokens for high-confidence matches that need {vN} restoration.</param>
    /// <returns>Text with high-confidence formulas as {vN} and low-confidence as $...$.</returns>
    public string ProtectTwoTier(string text, out IReadOnlyList<FormulaToken> hardTokens)
    {
        return ProtectWithConfidence(text, out hardTokens, splitByConfidence: true);
    }

    private string ProtectWithConfidence(string text, out IReadOnlyList<FormulaToken> hardTokens, bool splitByConfidence)
    {
        if (string.IsNullOrEmpty(text))
        {
            hardTokens = Array.Empty<FormulaToken>();
            return text;
        }

        // First pass: collect all regex matches with their types
        var matches = new List<(string Raw, FormulaTokenType Type, int Start, int Length)>();
        foreach (System.Text.RegularExpressions.Match m in FormulaDetector.FormulaRegex.Matches(text))
        {
            var raw = m.Value;
            var type = FormulaDetector.Classify(raw);
            matches.Add((raw, type, m.Index, m.Length));
        }

        if (matches.Count == 0)
        {
            hardTokens = Array.Empty<FormulaToken>();
            return text;
        }

        // Second pass: build protected text, splitting by confidence
        var hardList = new List<FormulaToken>();
        var hardCounter = 0;
        var sb = new System.Text.StringBuilder();
        var lastEnd = 0;

        foreach (var (raw, type, start, length) in matches)
        {
            // Append text before this match
            sb.Append(text, lastEnd, start - lastEnd);

            var isHigh = !splitByConfidence || FormulaDetector.IsHighConfidence(type);

            if (isHigh)
            {
                // Hard protection: {vN} placeholder
                var placeholder = $"{{v{hardCounter}}}";
                var simplified = BuildSimplified(raw, type);
                hardList.Add(new FormulaToken(type, raw, placeholder, simplified));
                sb.Append(placeholder);
                hardCounter++;
            }
            else
            {
                // Soft protection: wrap in $...$ for LLM to decide
                // Escape any literal $ inside the raw text to avoid breaking LaTeX delimiters
                var escaped = raw.Replace("$", "\\$");
                sb.Append('$');
                sb.Append(escaped);
                sb.Append('$');
            }

            lastEnd = start + length;
        }

        // Append remaining text
        sb.Append(text, lastEnd, text.Length - lastEnd);
        var protectedText = sb.ToString();

        // Extend hard placeholders to include trailing parenthesized formula arguments
        if (hardList.Count > 0)
        {
            var rawTokens = hardList.Select(t => t.Raw).ToList();
            protectedText = FormulaDetector.ExtendTrailingParens(protectedText, rawTokens);
            // Rebuild tokens with potentially extended raw values
            for (var i = 0; i < hardList.Count; i++)
            {
                if (rawTokens[i] != hardList[i].Raw)
                {
                    var type = FormulaDetector.Classify(rawTokens[i]);
                    hardList[i] = new FormulaToken(type, rawTokens[i], $"{{v{i}}}", BuildSimplified(rawTokens[i], type));
                }
            }
        }

        hardTokens = hardList;
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
