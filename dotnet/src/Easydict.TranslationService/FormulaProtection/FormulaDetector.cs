using System.Text.RegularExpressions;

namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Detects formula spans in text and classifies them by type.
/// Contains the consolidated regex pattern used for formula protection.
/// Aligned with pdf2zh converter.py formula patterns.
/// </summary>
public static class FormulaDetector
{
    /// <summary>
    /// Master formula detection regex.
    /// Patterns are ordered by priority (first match wins in Replace/Matches).
    /// </summary>
    public static readonly Regex FormulaRegex = new(
        @"(\$\$[^$]+\$\$" +                           // display math $$...$$
        @"|\$[^$]+\$" +                                // inline math $...$
        @"|\\\([^\)]+\\\)" +                           // inline math \(...\)
        @"|\\\[[^\]]+\\\]" +                           // display math \[...\]
        @"|\\begin\{[^}]+\}[\s\S]*?\\end\{[^}]+\}" +  // LaTeX environments
        @"|\\(?:alpha|beta|gamma|delta|epsilon|zeta|eta|theta|iota|kappa|lambda|mu|nu|xi|pi|rho|sigma|tau|upsilon|phi|chi|psi|omega|Gamma|Delta|Theta|Lambda|Xi|Pi|Sigma|Upsilon|Phi|Psi|Omega|sum|prod|int|infty|partial|nabla|forall|exists|subset|supset|cup|cap|times|cdot|leq|geq|neq|approx|equiv|sim|pm|mp|sqrt|frac|binom|log|ln|sin|cos|tan|lim|max|min)\b" + // LaTeX commands
        @"|\b[\p{L}\p{N}]+(?:[_^](?:\{[^}]+\}|[\p{L}\p{N}](?!\p{L}|\p{N})))+" + // subscript/superscript (multi-char base, multi-level): h_{t-1}, W_Q, 1_c_i
        @"|\b[\p{L}\p{N}]+\s*=\s*[^\s,;.]+)",          // simple equation: x = ...
        RegexOptions.Compiled);

    // Matches natural-language words (4+ letters) — used to decide if parenthesized
    // content is formula arguments vs prose.
    private static readonly Regex NaturalLanguageWordRegex = new(
        @"\b[a-zA-Z]{4,}\b",
        RegexOptions.Compiled);

    // Matches a formula placeholder followed by a parenthesized group, e.g. "{v0}(x, y)".
    // Aligned with pdf2zh converter.py:248-255 bracket grouping.
    private static readonly Regex TrailingParenRegex = new(
        @"\{v(\d+)\}\s*\(([^()]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);

    // Sequence token: long identifier with underscore (>5 char base) — should not render as subscript
    private static readonly Regex SequenceTokenRegex = new(
        @"\b[\p{L}]{6,}[_][\p{L}\p{N}]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Classifies a raw formula string into its <see cref="FormulaTokenType"/>.
    /// </summary>
    public static FormulaTokenType Classify(string rawFormula)
    {
        if (rawFormula.StartsWith("$$", StringComparison.Ordinal) || rawFormula.EndsWith("$$", StringComparison.Ordinal))
            return FormulaTokenType.DisplayMath;
        if (rawFormula.StartsWith("\\[", StringComparison.Ordinal) || rawFormula.EndsWith("\\]", StringComparison.Ordinal))
            return FormulaTokenType.DisplayMath;
        if (rawFormula.StartsWith("$", StringComparison.Ordinal) || rawFormula.EndsWith("$", StringComparison.Ordinal))
            return FormulaTokenType.InlineMath;
        if (rawFormula.StartsWith("\\(", StringComparison.Ordinal) || rawFormula.EndsWith("\\)", StringComparison.Ordinal))
            return FormulaTokenType.InlineMath;
        if (rawFormula.StartsWith("\\begin{", StringComparison.Ordinal))
        {
            if (rawFormula.Contains("matrix", StringComparison.OrdinalIgnoreCase))
                return FormulaTokenType.Matrix;
            return FormulaTokenType.LaTeXEnv;
        }
        if (rawFormula.StartsWith("\\frac", StringComparison.Ordinal))
            return FormulaTokenType.Fraction;
        if (rawFormula.StartsWith("\\sqrt", StringComparison.Ordinal))
            return FormulaTokenType.SquareRoot;
        if (rawFormula.StartsWith("\\sum", StringComparison.Ordinal) || rawFormula.StartsWith("\\prod", StringComparison.Ordinal))
            return FormulaTokenType.SumProduct;
        if (rawFormula.StartsWith("\\int", StringComparison.Ordinal))
            return FormulaTokenType.Integral;
        if (rawFormula.StartsWith("\\", StringComparison.Ordinal))
        {
            var cmd = rawFormula.TrimStart('\\').Split(new[] { ' ', '{', '\\' }, 2)[0];
            if (LatexFormulaSimplifier.SimplifyMathContent("\\" + cmd) != string.Empty)
            {
                // Classify by type
                var lower = cmd.ToLowerInvariant();
                if (IsGreekLetter(lower))
                    return FormulaTokenType.GreekLetter;
                return FormulaTokenType.MathOperator;
            }
            return FormulaTokenType.UnitFragment;
        }
        if (SequenceTokenRegex.IsMatch(rawFormula))
            return FormulaTokenType.SequenceToken;
        if (rawFormula.Contains('^'))
            return FormulaTokenType.MathSuperscript;
        if (rawFormula.Contains('_'))
            return FormulaTokenType.MathSubscript;
        if (rawFormula.Contains('='))
            return FormulaTokenType.InlineEquation;

        return FormulaTokenType.UnitFragment;
    }

    private static readonly HashSet<string> GreekLetterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta",
        "iota", "kappa", "lambda", "mu", "nu", "xi", "pi", "rho", "sigma",
        "tau", "upsilon", "phi", "chi", "psi", "omega",
    };

    private static bool IsGreekLetter(string cmd) => GreekLetterNames.Contains(cmd);

    /// <summary>
    /// Extends placeholders to include trailing parenthesized formula arguments.
    /// Aligned with pdf2zh converter.py:248-255 bracket grouping.
    /// </summary>
    public static string ExtendTrailingParens(string protectedText, IList<string> rawTokens)
    {
        return TrailingParenRegex.Replace(protectedText, match =>
        {
            var parenContent = match.Groups[2].Value;
            if (parenContent.Length <= 30 && !NaturalLanguageWordRegex.IsMatch(parenContent))
            {
                var idx = int.Parse(match.Groups[1].Value);
                if (idx >= 0 && idx < rawTokens.Count)
                {
                    rawTokens[idx] = rawTokens[idx] + "(" + parenContent + ")";
                }
                return $"{{v{idx}}}";
            }
            return match.Value;
        });
    }
}
