namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Lightweight character info for LaTeX reconstruction.
/// Carries only the data needed for subscript/superscript detection and Unicode→LaTeX mapping.
/// No PdfPig dependency — WinUI maps CharInfo to this type.
/// </summary>
public readonly record struct CharTextInfo(
    string Text,
    double PointSize,
    double BaselineY,
    bool IsMathFont);

/// <summary>
/// Reconstructs LaTeX notation from character-level data extracted from PDF.
/// Used for soft-protection tier: ambiguous formula spans are wrapped in $...$ so the LLM
/// can see the mathematical structure and decide whether to preserve or translate.
/// </summary>
public static class FormulaLatexReconstructor
{
    // Reverse mapping: Unicode character → LaTeX command
    private static readonly Dictionary<string, string> UnicodeToLatex = BuildReverseMap();

    private static Dictionary<string, string> BuildReverseMap()
    {
        var map = new Dictionary<string, string>
        {
            // Greek lowercase
            { "α", "\\alpha" }, { "β", "\\beta" }, { "γ", "\\gamma" }, { "δ", "\\delta" },
            { "ε", "\\epsilon" }, { "ζ", "\\zeta" }, { "η", "\\eta" }, { "θ", "\\theta" },
            { "ι", "\\iota" }, { "κ", "\\kappa" }, { "λ", "\\lambda" }, { "μ", "\\mu" },
            { "ν", "\\nu" }, { "ξ", "\\xi" }, { "π", "\\pi" }, { "ρ", "\\rho" },
            { "σ", "\\sigma" }, { "τ", "\\tau" }, { "υ", "\\upsilon" }, { "φ", "\\phi" },
            { "χ", "\\chi" }, { "ψ", "\\psi" }, { "ω", "\\omega" },
            // Greek uppercase
            { "Γ", "\\Gamma" }, { "Δ", "\\Delta" }, { "Θ", "\\Theta" }, { "Λ", "\\Lambda" },
            { "Ξ", "\\Xi" }, { "Π", "\\Pi" }, { "Σ", "\\Sigma" }, { "Υ", "\\Upsilon" },
            { "Φ", "\\Phi" }, { "Ψ", "\\Psi" }, { "Ω", "\\Omega" },
            // Operators
            { "∞", "\\infty" }, { "±", "\\pm" }, { "∓", "\\mp" }, { "×", "\\times" },
            { "÷", "\\div" }, { "·", "\\cdot" }, { "≤", "\\leq" }, { "≥", "\\geq" },
            { "≠", "\\neq" }, { "≈", "\\approx" }, { "≡", "\\equiv" }, { "∼", "\\sim" },
            { "⊂", "\\subset" }, { "⊃", "\\supset" }, { "∪", "\\cup" }, { "∩", "\\cap" },
            { "∈", "\\in" }, { "∉", "\\notin" }, { "∀", "\\forall" }, { "∃", "\\exists" },
            { "∇", "\\nabla" }, { "∂", "\\partial" }, { "∫", "\\int" }, { "√", "\\sqrt" },
            { "…", "\\ldots" }, { "⋯", "\\cdots" }, { "→", "\\to" }, { "←", "\\leftarrow" },
            { "⇐", "\\Leftarrow" }, { "⇒", "\\Rightarrow" }, { "↔", "\\leftrightarrow" },
            { "⊕", "\\oplus" }, { "⊗", "\\otimes" }, { "∘", "\\circ" },
        };
        return map;
    }

    private const double ScriptSizeRatio = 0.85;
    private const double BaselineThreshold = 0.5;

    /// <summary>
    /// Reconstructs LaTeX inline math from character-level data.
    /// Detects subscripts/superscripts from font size and baseline position,
    /// reverse-maps Greek/operator Unicode to LaTeX commands.
    /// Returns the content WITHOUT $...$ delimiters (caller adds them).
    /// </summary>
    public static string ReconstructLatex(IReadOnlyList<CharTextInfo> chars)
    {
        if (chars.Count == 0) return string.Empty;

        // Determine median font size as the "normal" baseline size
        var sizes = chars.Where(c => c.PointSize > 0).Select(c => c.PointSize).OrderBy(s => s).ToList();
        var medianSize = sizes.Count > 0 ? sizes[sizes.Count / 2] : 0;

        // Determine median baseline Y from NORMAL-sized characters only.
        // Using all characters would let a subscript/superscript drag the median
        // toward itself, defeating the sub/superscript detection below.
        var scriptSizeThreshold = medianSize * ScriptSizeRatio;
        var normalBaselines = chars
            .Where(c => c.PointSize > 0 && c.PointSize >= scriptSizeThreshold)
            .Select(c => c.BaselineY)
            .OrderBy(y => y)
            .ToList();
        var medianBaseline = normalBaselines.Count > 0
            ? normalBaselines[normalBaselines.Count / 2]
            : 0;

        var sb = new System.Text.StringBuilder();
        var inSubscript = false;
        var inSuperscript = false;

        foreach (var ch in chars)
        {
            var isSmall = medianSize > 0 && ch.PointSize > 0 && ch.PointSize < medianSize * ScriptSizeRatio;
            var isBelow = ch.BaselineY < medianBaseline - BaselineThreshold;
            var isAbove = ch.BaselineY > medianBaseline + BaselineThreshold;

            if (isSmall && isBelow && !inSubscript)
            {
                // End any superscript
                if (inSuperscript) { sb.Append('}'); inSuperscript = false; }
                sb.Append("_{");
                inSubscript = true;
            }
            else if (isSmall && isAbove && !inSuperscript)
            {
                // End any subscript
                if (inSubscript) { sb.Append('}'); inSubscript = false; }
                sb.Append("^{");
                inSuperscript = true;
            }
            else if (!isSmall && (inSubscript || inSuperscript))
            {
                // Return to normal size — close script group
                sb.Append('}');
                inSubscript = false;
                inSuperscript = false;
            }

            // Map Unicode to LaTeX command if possible
            var text = ch.Text;
            if (text.Length == 1 && UnicodeToLatex.TryGetValue(text, out var latex))
            {
                sb.Append(latex);
                // Add space after LaTeX command if next char is a letter
                sb.Append(' ');
            }
            else if (text == "_")
            {
                sb.Append("\\_");  // Escape literal underscore
            }
            else
            {
                sb.Append(text);
            }
        }

        // Close any open script group
        if (inSubscript || inSuperscript)
            sb.Append('}');

        return sb.ToString().TrimEnd();
    }
}
