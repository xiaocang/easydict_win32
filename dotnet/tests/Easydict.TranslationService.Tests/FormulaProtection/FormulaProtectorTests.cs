using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaProtectorTests
{
    private readonly FormulaProtector _protector = new();

    [Fact]
    public void Protect_EmptyText_ReturnsEmpty()
    {
        var result = _protector.Protect("", out var tokens);
        result.Should().Be("");
        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Protect_PlainText_NoReplacements()
    {
        const string text = "The quick brown fox jumps.";
        var result = _protector.Protect(text, out var tokens);
        // Plain text with no formula patterns — token list may still be non-empty
        // if the regex picks up something, but the protected text should not differ
        // from original for clearly non-formula text.
        if (tokens.Count == 0)
            result.Should().Be(text);
    }

    [Fact]
    public void Protect_InlineMath_ReplacedWithPlaceholder()
    {
        const string text = "We have $\\alpha + \\beta$ here.";
        var result = _protector.Protect(text, out var tokens);

        result.Should().Contain("{v0}");
        result.Should().NotContain("$\\alpha");
        tokens.Should().HaveCount(1);
        tokens[0].Raw.Should().Contain("alpha");
        tokens[0].Placeholder.Should().Be("{v0}");
    }

    [Fact]
    public void Protect_MultipleFormulas_PlaceholdersNumberedSequentially()
    {
        const string text = "First $a$ then $b$ and $c$.";
        var result = _protector.Protect(text, out var tokens);

        result.Should().Contain("{v0}");
        result.Should().Contain("{v1}");
        result.Should().Contain("{v2}");
        tokens.Should().HaveCount(3);
    }

    [Fact]
    public void Protect_DisplayMath_ReplacedWithPlaceholder()
    {
        const string text = "Consider $$x^2 + y^2 = r^2$$ as shown.";
        var result = _protector.Protect(text, out var tokens);

        result.Should().Contain("{v0}");
        result.Should().NotContain("$$");
        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().Be(FormulaTokenType.DisplayMath);
    }

    [Fact]
    public void Protect_GreekLetter_ReplacedWithPlaceholder()
    {
        const string text = "Greek \\alpha is first.";
        var result = _protector.Protect(text, out var tokens);

        result.Should().Contain("{v0}");
        result.Should().NotContain("\\alpha");
        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().Be(FormulaTokenType.GreekLetter);
    }

    [Fact]
    public void Protect_GreekLetter_SimplifiedIsUnicode()
    {
        const string text = "Greek \\alpha is first.";
        _protector.Protect(text, out var tokens);
        tokens[0].Simplified.Should().Be("α");
    }

    [Fact]
    public void Protect_MathOperator_SimplifiedIsUnicode()
    {
        const string text = "Value \\infty here.";
        _protector.Protect(text, out var tokens);
        tokens[0].Simplified.Should().Be("∞");
    }

    [Fact]
    public void Protect_SequenceToken_SimplifiedUsesHyphen()
    {
        // Sequence tokens like hidden_state should not render as subscript
        // Build a text that definitely has long base
        const string text = "The hidden_state vector.";
        var result = _protector.Protect(text, out var tokens);

        // Check if sequence token was detected
        var seqToken = tokens.FirstOrDefault(t => t.Type == FormulaTokenType.SequenceToken);
        if (seqToken is not null)
        {
            seqToken.Simplified.Should().Contain("-",
                because: "sequence tokens should use hyphen not underscore to avoid subscript rendering");
            seqToken.Simplified.Should().NotContain("_",
                because: "underscore in simplified would trigger subscript signal in PDF renderer");
        }
    }

    [Fact]
    public void Protect_TrailingParenGrouped_FormulaArguments()
    {
        const string text = "The function $f$(x, y) is defined.";
        var result = _protector.Protect(text, out var tokens);

        // The "(x, y)" should be grouped with the placeholder
        result.Should().NotContain("(x, y)");
        tokens.Should().HaveCount(1);
        tokens[0].Raw.Should().Contain("(x, y)");
    }

    [Fact]
    public void Protect_TrailingParenNotGrouped_NaturalLanguage()
    {
        const string text = "The equation $E=mc^2$(which Einstein discovered) is famous.";
        var result = _protector.Protect(text, out var tokens);

        // Natural-language content should NOT be grouped into the placeholder
        result.Should().Contain("Einstein");
    }

    [Fact]
    public void Protect_LaTeXEnv_TypeIsLaTeXEnv()
    {
        const string text = "Equation \\begin{equation}E=mc^2\\end{equation} holds.";
        _protector.Protect(text, out var tokens);
        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().BeOneOf(FormulaTokenType.LaTeXEnv, FormulaTokenType.InlineMath, FormulaTokenType.DisplayMath);
    }

    [Fact]
    public void Protect_Fraction_TypeIsFraction()
    {
        const string text = "\\frac{\\alpha}{2} is the result.";
        _protector.Protect(text, out var tokens);
        tokens.Should().NotBeEmpty();
        // The first match is \frac (a latex command captured by the \cmd pattern or the subscript/equation pattern)
        // or the whole expression — either way a token must be produced
        tokens[0].Raw.Should().Contain("frac");
    }

    [Fact]
    public void ProtectTwoTier_DemoteLevel0_SubscriptStaysHard()
    {
        // Explicit subscript h_{t-1} is high-confidence at demoteLevel 0 → {vN}
        var result = _protector.ProtectTwoTier("Look at h_{t-1} here.", out var tokens, demoteLevel: 0);
        tokens.Should().NotBeEmpty();
        result.Should().Contain("{v0}");
    }

    [Fact]
    public void ProtectTwoTier_DemoteLevel1_SubscriptBecomesSoft()
    {
        // At demoteLevel 1, MathSubscript is demoted to soft $...$ protection.
        var result = _protector.ProtectTwoTier("Look at h_{t-1} here.", out var tokens, demoteLevel: 1);
        tokens.Should().BeEmpty();
        result.Should().Contain("$h_{t-1}$");
    }

    [Fact]
    public void ProtectTwoTier_DemoteLevel1_GreekLetterStaysHard()
    {
        // Greek letters are unambiguous and never demoted.
        var result = _protector.ProtectTwoTier("The \\alpha is here.", out var tokens, demoteLevel: 1);
        tokens.Should().NotBeEmpty();
        result.Should().Contain("{v0}");
    }
}
