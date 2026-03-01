using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class LatexFormulaSimplifierTests
{
    // --- Greek letter mapping ---

    [Theory]
    [InlineData("\\alpha", "α")]
    [InlineData("\\beta", "β")]
    [InlineData("\\gamma", "γ")]
    [InlineData("\\delta", "δ")]
    [InlineData("\\epsilon", "ε")]
    [InlineData("\\zeta", "ζ")]
    [InlineData("\\eta", "η")]
    [InlineData("\\theta", "θ")]
    [InlineData("\\iota", "ι")]
    [InlineData("\\kappa", "κ")]
    [InlineData("\\lambda", "λ")]
    [InlineData("\\mu", "μ")]
    [InlineData("\\nu", "ν")]
    [InlineData("\\xi", "ξ")]
    [InlineData("\\pi", "π")]
    [InlineData("\\rho", "ρ")]
    [InlineData("\\sigma", "σ")]
    [InlineData("\\tau", "τ")]
    [InlineData("\\upsilon", "υ")]
    [InlineData("\\phi", "φ")]
    [InlineData("\\chi", "χ")]
    [InlineData("\\psi", "ψ")]
    [InlineData("\\omega", "ω")]
    public void SimplifyMathContent_LowercaseGreek_MapsToUnicode(string latex, string expected)
    {
        LatexFormulaSimplifier.SimplifyMathContent(latex).Should().Be(expected);
    }

    [Theory]
    [InlineData("\\Gamma", "Γ")]
    [InlineData("\\Delta", "Δ")]
    [InlineData("\\Theta", "Θ")]
    [InlineData("\\Lambda", "Λ")]
    [InlineData("\\Xi", "Ξ")]
    [InlineData("\\Pi", "Π")]
    [InlineData("\\Sigma", "Σ")]
    [InlineData("\\Upsilon", "Υ")]
    [InlineData("\\Phi", "Φ")]
    [InlineData("\\Psi", "Ψ")]
    [InlineData("\\Omega", "Ω")]
    public void SimplifyMathContent_UppercaseGreek_MapsToUnicode(string latex, string expected)
    {
        LatexFormulaSimplifier.SimplifyMathContent(latex).Should().Be(expected);
    }

    // --- Math operator mapping ---

    [Theory]
    [InlineData("\\infty", "∞")]
    [InlineData("\\pm", "±")]
    [InlineData("\\mp", "∓")]
    [InlineData("\\times", "×")]
    [InlineData("\\div", "÷")]
    [InlineData("\\cdot", "·")]
    [InlineData("\\leq", "≤")]
    [InlineData("\\geq", "≥")]
    [InlineData("\\neq", "≠")]
    [InlineData("\\approx", "≈")]
    [InlineData("\\equiv", "≡")]
    [InlineData("\\sim", "∼")]
    [InlineData("\\subset", "⊂")]
    [InlineData("\\supset", "⊃")]
    [InlineData("\\cup", "∪")]
    [InlineData("\\cap", "∩")]
    [InlineData("\\in", "∈")]
    [InlineData("\\notin", "∉")]
    [InlineData("\\forall", "∀")]
    [InlineData("\\exists", "∃")]
    [InlineData("\\nabla", "∇")]
    [InlineData("\\partial", "∂")]
    [InlineData("\\sum", "Σ")]
    [InlineData("\\prod", "Π")]
    [InlineData("\\int", "∫")]
    [InlineData("\\to", "→")]
    [InlineData("\\oplus", "⊕")]
    [InlineData("\\otimes", "⊗")]
    [InlineData("\\circ", "∘")]
    [InlineData("\\bullet", "•")]
    public void SimplifyMathContent_MathOperator_MapsToUnicode(string latex, string expected)
    {
        LatexFormulaSimplifier.SimplifyMathContent(latex).Should().Be(expected);
    }

    // --- Structural rules ---

    [Fact]
    public void SimplifyMathContent_Frac_ConvertsToDivision()
    {
        LatexFormulaSimplifier.SimplifyMathContent(@"\frac{a}{b}").Should().Be("a/b");
    }

    [Fact]
    public void SimplifyMathContent_FracWithGreek_ConvertsToDivisionWithUnicode()
    {
        LatexFormulaSimplifier.SimplifyMathContent(@"\frac{\alpha}{\beta}").Should().Be("α/β");
    }

    [Fact]
    public void SimplifyMathContent_Sqrt_ConvertsToPrefixSqrt()
    {
        LatexFormulaSimplifier.SimplifyMathContent(@"\sqrt{x}").Should().Be("√x");
    }

    [Fact]
    public void SimplifyMathContent_SqrtN_ConvertsToNthRoot()
    {
        LatexFormulaSimplifier.SimplifyMathContent(@"\sqrt[3]{x}").Should().Be("ⁿ√x");
    }

    [Fact]
    public void SimplifyMathContent_MathFormatting_StripsCommand()
    {
        LatexFormulaSimplifier.SimplifyMathContent(@"\mathbf{x}").Should().Be("x");
        LatexFormulaSimplifier.SimplifyMathContent(@"\mathrm{R}").Should().Be("R");
        LatexFormulaSimplifier.SimplifyMathContent(@"\text{loss}").Should().Be("loss");
    }

    [Fact]
    public void SimplifyMathContent_Matrix_ReturnsPlaceholder()
    {
        var latex = @"\begin{bmatrix} a & b \\ c & d \end{bmatrix}";
        LatexFormulaSimplifier.SimplifyMathContent(latex).Should().Be("[matrix]");
    }

    [Fact]
    public void SimplifyMathContent_SubscriptGroup_Expands()
    {
        // _{abc} → _a_b_c (each char gets its own script signal)
        var result = LatexFormulaSimplifier.SimplifyMathContent(@"h_{t-1}");
        // After expansion, _{ ... } becomes per-char signals
        result.Should().Contain("_t").And.Contain("_-").And.Contain("_1");
    }

    [Fact]
    public void SimplifyMathContent_SuperscriptGroup_Expands()
    {
        var result = LatexFormulaSimplifier.SimplifyMathContent(@"x^{2n}");
        result.Should().Contain("^2").And.Contain("^n");
    }

    // --- Simplify() (outer delimiters) ---

    [Fact]
    public void Simplify_InlineMath_SimplifiesContent()
    {
        var result = LatexFormulaSimplifier.Simplify(@"$\alpha + \beta$");
        result.Should().Be("α + β");
    }

    [Fact]
    public void Simplify_DisplayMath_SimplifiesContentWithSpaces()
    {
        var result = LatexFormulaSimplifier.Simplify(@"$$\sum_{i=1}^{n} x_i$$");
        result.Should().Contain("Σ");
    }

    [Fact]
    public void Simplify_PlainText_Unchanged()
    {
        var result = LatexFormulaSimplifier.Simplify("Hello world");
        result.Should().Be("Hello world");
    }

    [Fact]
    public void Simplify_EmptyString_ReturnsEmpty()
    {
        LatexFormulaSimplifier.Simplify("").Should().Be("");
        LatexFormulaSimplifier.Simplify(null!).Should().BeNull();
    }

    // --- IsScriptSignal ---

    [Theory]
    [InlineData('^', true)]
    [InlineData('_', true)]
    [InlineData('a', false)]
    [InlineData('0', false)]
    [InlineData(' ', false)]
    public void IsScriptSignal_CorrectlyIdentifiesSignals(char c, bool expected)
    {
        LatexFormulaSimplifier.IsScriptSignal(c).Should().Be(expected);
    }
}
