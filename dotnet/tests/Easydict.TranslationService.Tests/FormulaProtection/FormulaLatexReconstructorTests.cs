using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaLatexReconstructorTests
{
    [Fact]
    public void ReconstructLatex_PlainText_ReturnsText()
    {
        var chars = new[]
        {
            new CharTextInfo("x", 12.0, 700, false),
            new CharTextInfo("+", 12.0, 700, false),
            new CharTextInfo("y", 12.0, 700, false),
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Be("x+y");
    }

    [Fact]
    public void ReconstructLatex_Subscript_AddsSubscriptNotation()
    {
        var chars = new[]
        {
            new CharTextInfo("x", 12.0, 700, false),
            new CharTextInfo("2", 8.0, 696, false),  // smaller + lower baseline → subscript
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Contain("_{");
        result.Should().Contain("2");
    }

    [Fact]
    public void ReconstructLatex_Superscript_AddsSuperscriptNotation()
    {
        var chars = new[]
        {
            new CharTextInfo("x", 12.0, 700, false),
            new CharTextInfo("2", 8.0, 706, false),  // smaller + higher baseline → superscript
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Contain("^{");
        result.Should().Contain("2");
    }

    [Fact]
    public void ReconstructLatex_GreekUnicode_MapsToLatexCommand()
    {
        var chars = new[]
        {
            new CharTextInfo("α", 12.0, 700, true),
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Contain("\\alpha");
    }

    [Fact]
    public void ReconstructLatex_MathOperator_MapsToLatexCommand()
    {
        var chars = new[]
        {
            new CharTextInfo("x", 12.0, 700, false),
            new CharTextInfo("∈", 12.0, 700, false),
            new CharTextInfo("ℝ", 12.0, 700, false),
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Contain("\\in");
    }

    [Fact]
    public void ReconstructLatex_Empty_ReturnsEmpty()
    {
        var result = FormulaLatexReconstructor.ReconstructLatex(Array.Empty<CharTextInfo>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void ReconstructLatex_LiteralUnderscore_Escaped()
    {
        var chars = new[]
        {
            new CharTextInfo("h", 12.0, 700, false),
            new CharTextInfo("_", 12.0, 700, false),
            new CharTextInfo("t", 12.0, 700, false),
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        result.Should().Contain("\\_");
    }

    [Fact]
    public void ReconstructLatex_MixedSubscriptAndNormal_ClosesGroup()
    {
        var chars = new[]
        {
            new CharTextInfo("x", 12.0, 700, false),
            new CharTextInfo("2", 8.0, 696, false),  // subscript
            new CharTextInfo("+", 12.0, 700, false),  // back to normal → closes subscript
            new CharTextInfo("y", 12.0, 700, false),
        };

        var result = FormulaLatexReconstructor.ReconstructLatex(chars);

        // Should have subscript group closed before +
        result.Should().Contain("}+");
    }
}
