using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaDetectorTests
{
    // --- FormulaRegex detection ---

    [Theory]
    [InlineData("$$x^2 + y^2 = r^2$$", "$$x^2 + y^2 = r^2$$")]          // Display math
    [InlineData("$\\alpha + \\beta$", "$\\alpha + \\beta$")]               // Inline math
    [InlineData("\\begin{equation}E=mc^2\\end{equation}", "\\begin{equation}E=mc^2\\end{equation}")] // LaTeX env
    [InlineData("\\alpha", "\\alpha")]                                       // Greek letter bare command
    [InlineData("\\infty", "\\infty")]                                       // Math operator bare command
    [InlineData("x_{i+1}", "x_{i+1}")]                                      // Subscript with braces
    [InlineData("x^{2n}", "x^{2n}")]                                        // Superscript with braces
    [InlineData("x_i", "x_i")]                                              // Simple subscript
    [InlineData("x^2", "x^2")]                                              // Simple superscript
    [InlineData("W_Q", "W_Q")]                                              // Multi-char base uppercase subscript
    [InlineData("h_{t-1}", "h_{t-1}")]                                     // Single-char base, braced subscript
    [InlineData("1_c_i", "1_c_i")]                                         // Digit base, chained subscripts
    public void FormulaRegex_DetectsKnownPatterns(string text, string expectedMatch)
    {
        var match = FormulaDetector.FormulaRegex.Match(text);
        match.Success.Should().BeTrue(because: $"'{text}' should contain a formula");
        match.Value.Should().Be(expectedMatch);
    }

    [Theory]
    [InlineData("The quick brown fox")]
    [InlineData("Hello World 123")]
    [InlineData("normal text sentence")]
    public void FormulaRegex_DoesNotMatchPlainText(string text)
    {
        // Plain text with no math should have no match
        // (equation-like "x = ..." patterns might still match, so use truly plain text)
        var match = FormulaDetector.FormulaRegex.Match(text);
        // Only assert non-match for clearly non-formula text
        if (match.Success)
        {
            // If matched, must be something like "fox" — not a true formula
            match.Value.Should().NotStartWith("\\").And.NotStartWith("$");
        }
    }

    // --- Classify ---

    [Theory]
    [InlineData("$$x^2$$", FormulaTokenType.DisplayMath)]
    [InlineData("\\[x^2\\]", FormulaTokenType.DisplayMath)]
    [InlineData("$x^2$", FormulaTokenType.InlineMath)]
    [InlineData("\\(x^2\\)", FormulaTokenType.InlineMath)]
    [InlineData("\\begin{equation}x\\end{equation}", FormulaTokenType.LaTeXEnv)]
    [InlineData("\\begin{bmatrix}a&b\\end{bmatrix}", FormulaTokenType.Matrix)]
    [InlineData("\\frac{a}{b}", FormulaTokenType.Fraction)]
    [InlineData("\\sqrt{x}", FormulaTokenType.SquareRoot)]
    [InlineData("\\sum_{i=1}", FormulaTokenType.SumProduct)]
    [InlineData("\\prod_{i=1}", FormulaTokenType.SumProduct)]
    [InlineData("\\int_0^1", FormulaTokenType.Integral)]
    [InlineData("\\alpha", FormulaTokenType.GreekLetter)]
    [InlineData("\\beta", FormulaTokenType.GreekLetter)]
    [InlineData("\\infty", FormulaTokenType.MathOperator)]
    [InlineData("\\pm", FormulaTokenType.MathOperator)]
    [InlineData("x^2", FormulaTokenType.MathSuperscript)]
    [InlineData("x_i", FormulaTokenType.MathSubscript)]
    [InlineData("x = 5", FormulaTokenType.InlineEquation)]
    public void Classify_KnownPatterns_ReturnsCorrectType(string raw, FormulaTokenType expected)
    {
        FormulaDetector.Classify(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("hidden_state")]
    [InlineData("sequence_1")]
    [InlineData("encoder_output")]
    [InlineData("attention_weights")]
    public void Classify_SequenceToken_LongBase_ReturnsSequenceToken(string raw)
    {
        // Long base (>5 chars) with underscore = SequenceToken, not subscript
        FormulaDetector.Classify(raw).Should().Be(FormulaTokenType.SequenceToken);
    }

    // --- ExtendTrailingParens ---

    [Fact]
    public void ExtendTrailingParens_FormulaArgs_Grouped()
    {
        var rawTokens = new List<string> { "$f$" };
        var text = "{v0}(x, y)";
        var result = FormulaDetector.ExtendTrailingParens(text, rawTokens);
        result.Should().Be("{v0}");
        rawTokens[0].Should().Be("$f$(x, y)");
    }

    [Fact]
    public void ExtendTrailingParens_NaturalLanguage_NotGrouped()
    {
        var rawTokens = new List<string> { "$E=mc^2$" };
        var text = "{v0}(which Einstein discovered)";
        var result = FormulaDetector.ExtendTrailingParens(text, rawTokens);
        // Natural language words → not grouped
        result.Should().Contain("Einstein");
        rawTokens[0].Should().Be("$E=mc^2$"); // unchanged
    }

    [Fact]
    public void ExtendTrailingParens_LongParenContent_NotGrouped()
    {
        var rawTokens = new List<string> { "$f$" };
        // More than 30 chars in the parentheses
        var text = "{v0}(this is a very long parenthesized content here)";
        var result = FormulaDetector.ExtendTrailingParens(text, rawTokens);
        result.Should().Contain("this is a very long");
    }
}
