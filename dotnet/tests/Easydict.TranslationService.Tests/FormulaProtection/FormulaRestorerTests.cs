using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaRestorerTests
{
    private readonly FormulaRestorer _restorer = new();

    private static FormulaToken MakeToken(string raw, string simplified) =>
        new(FormulaTokenType.InlineMath, raw, "{v0}", simplified);

    [Fact]
    public void Restore_EmptyTokens_ReturnsOriginalText()
    {
        var result = _restorer.Restore("{v0}", Array.Empty<FormulaToken>(), "original", useSimplified: false);
        result.Should().Be("{v0}");
    }

    [Fact]
    public void Restore_UnresolvablePlaceholder_ReturnsFallback()
    {
        // {v5} doesn't exist in 2-token list → fallback
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
        };
        var result = _restorer.Restore("text {v5} more", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("FALLBACK");
    }

    [Fact]
    public void Restore_UseSimplifiedFalse_ReturnsRaw()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original", useSimplified: false);
        result.Should().Be("The \\alpha letter.");
    }

    [Fact]
    public void Restore_UseSimplifiedTrue_ReturnsSimplified()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original", useSimplified: true);
        result.Should().Be("The α letter.");
    }

    [Fact]
    public void Restore_MultipleTokens_AllRestored()
    {
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
        };
        var result = _restorer.Restore("{v0} and {v1}", tokens, "original", useSimplified: false);
        result.Should().Be("\\alpha and \\beta");
    }

    [Fact]
    public void Restore_ImbalancedDelimiters_ReturnsFallback()
    {
        // Restoring a raw token that creates unbalanced braces → fallback
        var tokens = new[] { MakeToken("{unbalanced", "{unbalanced") };
        var result = _restorer.Restore("{v0}", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("FALLBACK");
    }

    [Fact]
    public void Restore_BalancedDelimiters_ReturnsRestored()
    {
        var tokens = new[] { MakeToken("$x + y$", "x + y") };
        var result = _restorer.Restore("We have {v0} here.", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("We have $x + y$ here.");
    }

    [Fact]
    public void Restore_EmptyText_ReturnsEmptyText()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("");
    }

    [Fact]
    public void Restore_DefaultUsesRaw()
    {
        // Default: useSimplified = false
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original");
        result.Should().Be("The \\alpha letter.");
    }
}
