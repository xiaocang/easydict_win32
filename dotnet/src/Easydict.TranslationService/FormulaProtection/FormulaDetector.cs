using System.Text.RegularExpressions;

namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Detects formula spans in text and classifies them by type.
/// Contains the consolidated regex pattern used for formula protection.
/// Aligned with pdf2zh converter.py formula patterns.
/// </summary>
public static class FormulaDetector
{
    // Shared body of a symbolic tuple sequence, e.g. "x1, ..., xn". Used by the master regex
    // (tuple-assignment + implicit-tuple alternatives) and by the anchored validators below.
    private const string TupleSequenceBody =
        @"[a-zA-Z]\d+\s*(?:,\s*(?:[a-zA-Z](?:\d+|[a-zA-Z])|\.{2,3}|\u2026|\\ldots|\\dots|\\cdots))+";

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
        @"|\b[a-zA-Z]\b\s*=\s*\(\s*" + TupleSequenceBody + @"\s*\)" + // assignment with sequence RHS: z = (z1, ..., zn)
        @"|\(\s*" + TupleSequenceBody + @"\s*\)" +                    // implicit-subscript tuple: (x1, ..., xn)
        @"|\b[\p{L}\p{N}]+\s*=\s*[^\s,;.(]+)",           // simple equation: x = ... (exclude '(' to avoid fusing "(z_1, ...)" into a broken token)
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

    private static readonly Regex ExactTupleAssignmentRegex = new(
        @"^[a-zA-Z]\s*=\s*\(\s*" + TupleSequenceBody + @"\s*\)$",
        RegexOptions.Compiled);

    private static readonly Regex ExactImplicitTupleRegex = new(
        @"^\(\s*" + TupleSequenceBody + @"\s*\)$",
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
        // Implicit-subscript tuple: (x1, ..., xn) — low confidence, goes to soft $...$ protection
        if (rawFormula.StartsWith("(", StringComparison.Ordinal))
            return FormulaTokenType.ImplicitTuple;

        return FormulaTokenType.UnitFragment;
    }

    /// <summary>
    /// Returns true if the token type represents a high-confidence formula detection
    /// that should use hard protection ({vN} placeholders).
    /// Low-confidence types use soft protection ($...$) and let the LLM decide.
    /// </summary>
    public static bool IsHighConfidence(FormulaTokenType type) => type switch
    {
        FormulaTokenType.InlineMath or          // $...$, \(...\)
        FormulaTokenType.DisplayMath or         // $$...$$, \[...\]
        FormulaTokenType.LaTeXEnv or            // \begin{...}\end{...}
        FormulaTokenType.Matrix or
        FormulaTokenType.Fraction or            // \frac{a}{b}
        FormulaTokenType.SquareRoot or          // \sqrt{x}
        FormulaTokenType.SumProduct or          // \sum, \prod
        FormulaTokenType.Integral or            // \int
        FormulaTokenType.GreekLetter or         // \alpha, \beta
        FormulaTokenType.MathOperator or        // \infty, \pm
        FormulaTokenType.MathFormatting or      // \mathbf{}, \mathrm{}
        FormulaTokenType.MathSuperscript or     // x^2 (explicit ^)
        FormulaTokenType.MathSubscript          // h_{t-1} (explicit _)
            => true,
        // Low confidence — InlineEquation, SequenceToken, ImplicitTuple, UnitFragment
        _ => false,
    };

    /// <summary>
    /// Returns true when a soft-protected span must survive translation verbatim.
    /// This is intentionally limited to symbolic tuple sequences.
    /// </summary>
    public static bool RequiresExactSoftPreservation(string rawFormula, FormulaTokenType type) => type switch
    {
        FormulaTokenType.ImplicitTuple => ExactImplicitTupleRegex.IsMatch(rawFormula),
        FormulaTokenType.InlineEquation => ExactTupleAssignmentRegex.IsMatch(rawFormula),
        _ => false,
    };

    /// <summary>
    /// Returns true when the text contains any low-confidence span that must survive translation verbatim.
    /// Used to keep tuple-style symbolic sequences on the regex soft-protection path even when
    /// character-level protection is available.
    /// </summary>
    internal static bool ContainsExactSoftPreservationCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match match in FormulaRegex.Matches(text))
        {
            var raw = match.Value;
            var type = Classify(raw);
            if (RequiresExactSoftPreservation(raw, type))
                return true;
        }

        return false;
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
