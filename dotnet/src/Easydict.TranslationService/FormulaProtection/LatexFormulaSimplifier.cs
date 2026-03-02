using System.Text;
using System.Text.RegularExpressions;

namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Converts LaTeX math markup to a Unicode approximation suitable for PDF rendering.
/// Replaces SimplifyLatexMarkup + SimplifyMathContent in MuPdfExportService.
/// </summary>
public static class LatexFormulaSimplifier
{
    // Greek letter map — 24 lowercase + 24 uppercase
    private static readonly Dictionary<string, string> GreekMap = new(StringComparer.Ordinal)
    {
        // lowercase
        { "alpha",   "α" }, { "beta",    "β" }, { "gamma",   "γ" }, { "delta",   "δ" },
        { "epsilon", "ε" }, { "zeta",    "ζ" }, { "eta",     "η" }, { "theta",   "θ" },
        { "iota",    "ι" }, { "kappa",   "κ" }, { "lambda",  "λ" }, { "mu",      "μ" },
        { "nu",      "ν" }, { "xi",      "ξ" }, { "pi",      "π" }, { "rho",     "ρ" },
        { "sigma",   "σ" }, { "tau",     "τ" }, { "upsilon", "υ" }, { "phi",     "φ" },
        { "chi",     "χ" }, { "psi",     "ψ" }, { "omega",   "ω" },
        // uppercase
        { "Gamma",   "Γ" }, { "Delta",   "Δ" }, { "Theta",   "Θ" }, { "Lambda",  "Λ" },
        { "Xi",      "Ξ" }, { "Pi",      "Π" }, { "Sigma",   "Σ" }, { "Upsilon", "Υ" },
        { "Phi",     "Φ" }, { "Psi",     "Ψ" }, { "Omega",   "Ω" },
    };

    // Math operator map
    private static readonly Dictionary<string, string> OperatorMap = new(StringComparer.Ordinal)
    {
        { "infty",   "∞" }, { "pm",      "±" }, { "mp",      "∓" }, { "times",   "×" },
        { "div",     "÷" }, { "cdot",    "·" }, { "leq",     "≤" }, { "geq",     "≥" },
        { "neq",     "≠" }, { "approx",  "≈" }, { "equiv",   "≡" }, { "sim",     "∼" },
        { "subset",  "⊂" }, { "supset",  "⊃" }, { "cup",     "∪" }, { "cap",     "∩" },
        { "in",      "∈" }, { "notin",   "∉" }, { "forall",  "∀" }, { "exists",  "∃" },
        { "nabla",   "∇" }, { "partial", "∂" }, { "sum",     "Σ" }, { "prod",    "Π" },
        { "int",     "∫" }, { "oint",    "∮" }, { "sqrt",    "√" }, { "ldots",   "…" },
        { "cdots",   "⋯" }, { "vdots",   "⋮" }, { "ddots",   "⋱" }, { "to",      "→" },
        { "leftarrow","←"},{ "rightarrow","→"},{ "Leftarrow","⇐"},{ "Rightarrow","⇒"},
        { "leftrightarrow","↔"},{ "Leftrightarrow","⇔" },
        { "oplus",   "⊕" }, { "otimes",  "⊗" }, { "circ",    "∘" }, { "bullet",  "•" },
    };

    // Regex for stripping outer delimiters to get math content
    private static readonly Regex DisplayMathDollar = new(@"\$\$([\s\S]*?)\$\$", RegexOptions.Compiled);
    private static readonly Regex DisplayMathBracket = new(@"\\\[([\s\S]*?)\\\]", RegexOptions.Compiled);
    private static readonly Regex InlineMathDollar = new(@"\$([^$\n]+)\$", RegexOptions.Compiled);
    private static readonly Regex InlineMathParen = new(@"\\\(([\s\S]*?)\\\)", RegexOptions.Compiled);
    private static readonly Regex ResidualCmdContent = new(@"\\[a-zA-Z]+\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex ResidualCmd = new(@"\\[a-zA-Z]+", RegexOptions.Compiled);
    private static readonly Regex SubscriptGroup = new(@"_\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SuperscriptGroup = new(@"\^\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex ExtraWhitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Regex for math content simplification
    private static readonly Regex FracPattern = new(@"\\frac\{([^}]*)\}\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SqrtNPattern = new(@"\\sqrt\[([^\]]+)\]\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SqrtPattern = new(@"\\sqrt\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex MatrixPattern = new(@"\\begin\{(?:bmatrix|pmatrix|matrix|vmatrix|Vmatrix|smallmatrix)\}[\s\S]*?\\end\{(?:bmatrix|pmatrix|matrix|vmatrix|Vmatrix|smallmatrix)\}", RegexOptions.Compiled);
    private static readonly Regex MathFormattingCmd = new(@"\\(?:text|mathrm|mathbf|mathit|mathbb|mathcal|operatorname)\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex GenericCmdContent = new(@"\\[a-zA-Z]+\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex GreekOrOperatorCmd = new(@"\\([a-zA-Z]+)", RegexOptions.Compiled);
    private static readonly Regex BraceGroup = new(@"[{}]", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespace = new(@"\s+", RegexOptions.Compiled);
    // Normalises implicit subscripts produced by PDF extraction: x1 → x_{1}, z2 → z_{2}.
    // Only matches single-letter base (prevents false positives on words like "mp4").
    private static readonly Regex ImplicitSubscriptPattern = new(@"\b([a-zA-Z])(\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// Checks whether a character is a super/subscript script signal used by the PDF renderer.
    /// </summary>
    public static bool IsScriptSignal(char c) => c == '^' || c == '_';

    /// <summary>
    /// Simplifies a LaTeX string to a Unicode approximation for PDF rendering.
    /// </summary>
    /// <param name="text">The full text potentially containing LaTeX markup.</param>
    /// <param name="preserveScriptSignals">
    /// When <c>true</c> (PDF render path), keeps <c>^</c> and <c>_</c> as renderer signals.
    /// When <c>false</c> (plain text path), they are left as-is or converted to Unicode super/subscript chars.
    /// </param>
    public static string Simplify(string text, bool preserveScriptSignals = true)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Display math: $$...$$ → simplified content (with spaces)
        text = DisplayMathDollar.Replace(text,
            m => " " + SimplifyMathContent(m.Groups[1].Value, preserveScriptSignals) + " ");
        // Display math: \[...\] → simplified content
        text = DisplayMathBracket.Replace(text,
            m => " " + SimplifyMathContent(m.Groups[1].Value, preserveScriptSignals) + " ");
        // Inline math: $...$ → simplified content
        text = InlineMathDollar.Replace(text,
            m => SimplifyMathContent(m.Groups[1].Value, preserveScriptSignals));
        // Inline math: \(...\) → simplified content
        text = InlineMathParen.Replace(text,
            m => SimplifyMathContent(m.Groups[1].Value, preserveScriptSignals));
        // Residual \cmd{content} outside math → keep content
        text = ResidualCmdContent.Replace(text, "$1");
        // Residual standalone \cmd → remove
        text = ResidualCmd.Replace(text, string.Empty);
        // Expand _{abc} → _a_b_c and ^{abc} → ^a^b^c so every char gets its own signal
        text = SubscriptGroup.Replace(text,
            m => string.Concat(m.Groups[1].Value.Select(c => "_" + c)));
        text = SuperscriptGroup.Replace(text,
            m => string.Concat(m.Groups[1].Value.Select(c => "^" + c)));
        // Remove lone $ \ { }; keep ^ _ as super/subscript rendering signals
        text = Regex.Replace(text, @"[\$\\{}]", string.Empty);
        // Collapse extra whitespace
        return ExtraWhitespace.Replace(text, " ").Trim();
    }

    /// <summary>
    /// Simplifies LaTeX math content (the inner part of $...$) to a Unicode approximation.
    /// </summary>
    public static string SimplifyMathContent(string latex, bool preserveScriptSignals = true)
    {
        if (string.IsNullOrEmpty(latex)) return latex;

        // Matrix environments → placeholder
        latex = MatrixPattern.Replace(latex, "[matrix]");

        // \frac{a}{b} → a/b
        latex = FracPattern.Replace(latex, "$1/$2");

        // \sqrt[n]{x} → ⁿ√x
        latex = SqrtNPattern.Replace(latex, "ⁿ√$2");

        // \sqrt{x} → √x
        latex = SqrtPattern.Replace(latex, "√$1");

        // \text{word}, \mathrm{word}, \mathbf{word}, etc. → word
        latex = MathFormattingCmd.Replace(latex, "$1");

        // Other \cmd{content} → content
        latex = GenericCmdContent.Replace(latex, "$1");

        // Greek letters and operators: \alpha → α, \infty → ∞, etc.
        latex = GreekOrOperatorCmd.Replace(latex, m =>
        {
            var cmd = m.Groups[1].Value;
            if (GreekMap.TryGetValue(cmd, out var greek)) return greek;
            if (OperatorMap.TryGetValue(cmd, out var op)) return op;
            return string.Empty; // Strip unknown commands
        });

        // Normalise implicit subscripts from PDF extraction before SubscriptGroup expansion.
        // Single-letter base rule: only matches x1, z2 — never mp4 or version1.
        latex = ImplicitSubscriptPattern.Replace(latex, "$1_{$2}");

        // Expand _{abc} → _a_b_c and ^{abc} → ^a^b^c (per-character signals)
        latex = SubscriptGroup.Replace(latex,
            m => string.Concat(m.Groups[1].Value.Select(c => "_" + c)));
        latex = SuperscriptGroup.Replace(latex,
            m => string.Concat(m.Groups[1].Value.Select(c => "^" + c)));

        // Remove { } braces; keep ^ _ = + - * / spaces and alphanumerics
        latex = BraceGroup.Replace(latex, string.Empty);

        // Collapse whitespace
        return CollapseWhitespace.Replace(latex, " ").Trim();
    }
}
