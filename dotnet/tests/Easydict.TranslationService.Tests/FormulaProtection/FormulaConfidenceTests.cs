using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaConfidenceTests
{
    [Theory]
    [InlineData(FormulaTokenType.InlineMath, true)]
    [InlineData(FormulaTokenType.DisplayMath, true)]
    [InlineData(FormulaTokenType.LaTeXEnv, true)]
    [InlineData(FormulaTokenType.Matrix, true)]
    [InlineData(FormulaTokenType.Fraction, true)]
    [InlineData(FormulaTokenType.SquareRoot, true)]
    [InlineData(FormulaTokenType.SumProduct, true)]
    [InlineData(FormulaTokenType.Integral, true)]
    [InlineData(FormulaTokenType.GreekLetter, true)]
    [InlineData(FormulaTokenType.MathOperator, true)]
    [InlineData(FormulaTokenType.MathFormatting, true)]
    [InlineData(FormulaTokenType.MathSuperscript, true)]
    [InlineData(FormulaTokenType.MathSubscript, true)]
    public void IsHighConfidence_HighConfidenceTypes_ReturnsTrue(FormulaTokenType type, bool expected)
    {
        FormulaDetector.IsHighConfidence(type).Should().Be(expected);
    }

    [Theory]
    [InlineData(FormulaTokenType.InlineEquation)]
    [InlineData(FormulaTokenType.SequenceToken)]
    [InlineData(FormulaTokenType.ImplicitTuple)]
    [InlineData(FormulaTokenType.UnitFragment)]
    public void IsHighConfidence_LowConfidenceTypes_ReturnsFalse(FormulaTokenType type)
    {
        FormulaDetector.IsHighConfidence(type).Should().BeFalse();
    }

    [Fact]
    public void ProtectTwoTier_ImplicitTuple_ProducesDollarWrapped()
    {
        var protector = new FormulaProtector();
        // "(x1, ..., xn)" is an implicit-subscript tuple → ImplicitTuple → low confidence
        var result = protector.ProtectTwoTier("The tuple (x1, ..., xn) is a sequence.", out var tokens);

        // Low confidence → no hard tokens, wrapped in $...$
        tokens.Should().BeEmpty();
        result.Should().Contain("$(x1");
    }

    [Fact]
    public void ProtectTwoTier_HighConfidence_ProducesPlaceholder()
    {
        var protector = new FormulaProtector();
        var result = protector.ProtectTwoTier("The formula $\\alpha + \\beta$ here.", out var tokens);

        result.Should().Contain("{v0}");
        tokens.Should().HaveCount(1);
        tokens[0].Raw.Should().Be("$\\alpha + \\beta$");
    }

    [Fact]
    public void ProtectTwoTier_LowConfidence_ProducesDollarWrapped()
    {
        var protector = new FormulaProtector();
        // "speed = 5" matches the simple equation pattern → InlineEquation → low confidence
        var result = protector.ProtectTwoTier("The speed = 5 is fast.", out var tokens);

        // Low confidence → no hard tokens, wrapped in $...$
        tokens.Should().BeEmpty();
        result.Should().Contain("$speed = 5$");
    }

    [Fact]
    public void ProtectTwoTier_Mixed_BothTypes()
    {
        var protector = new FormulaProtector();
        // \alpha is high confidence, "x = 5" is low confidence
        var result = protector.ProtectTwoTier("We have \\alpha and x = 5 here.", out var tokens);

        // \alpha → high confidence → {v0}
        tokens.Should().HaveCountGreaterOrEqualTo(1);
        result.Should().Contain("{v0}");
        // x = 5 → low confidence → $...$
        result.Should().Contain("$");
    }

    [Fact]
    public void Protect_BackwardCompatible_AllHard()
    {
        var protector = new FormulaProtector();
        // Old Protect method should still treat everything as hard
        var result = protector.Protect("The speed = 5 is fast.", out var tokens);

        // Even low-confidence matches become {vN} in the old API
        tokens.Should().HaveCountGreaterOrEqualTo(1);
        result.Should().Contain("{v0}");
        result.Should().NotContain("$speed");
    }
}
