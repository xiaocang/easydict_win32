using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class PdfExportServiceLayoutTests
{
    [Fact]
    public void WrapTextByWidth_PreservesExplicitSpacesAndHardBreaks()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Arial", 12);

        var lines = PdfExportService.WrapTextByWidth(gfx, "Most  competitive\nneural sequence", font, 1000).ToList();

        lines.Should().Equal("Most  competitive", "neural sequence");
    }

    [Fact]
    public void TryBuildLineRects_WithDistinctBaselines_BuildsRectsTopToBottomAndClampsToBlock()
    {
        var style = new BlockTextStyle
        {
            LineSpacing = 20,
            LinePositions =
            [
                new BlockLinePosition(700, 90, 300),
                new BlockLinePosition(680, 80, 280),
                new BlockLinePosition(660, 100, 320),
            ]
        };

        var pageHeight = 800d;
        var blockRect = new XRect(90, 50, 200, 120);

        var rects = PdfExportService.TryBuildLineRects(pageHeight, blockRect, style, fallbackLineHeight: 14);

        rects.Should().NotBeNull();
        rects!.Count.Should().Be(3);

        rects[0].Y.Should().BeLessThan(rects[1].Y);
        rects[1].Y.Should().BeLessThan(rects[2].Y);

        foreach (var r in rects)
        {
            r.X.Should().BeGreaterOrEqualTo(blockRect.X);
            r.Right.Should().BeLessOrEqualTo(blockRect.Right + 0.0001);
            r.Y.Should().BeGreaterOrEqualTo(blockRect.Y);
            r.Bottom.Should().BeLessOrEqualTo(blockRect.Bottom + 0.0001);
            r.Width.Should().BeGreaterThan(0);
            r.Height.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void TryBuildLineRects_WithSingleBaseline_CreatesVirtualLineRectsWithinBlockHeight()
    {
        var style = new BlockTextStyle
        {
            LinePositions =
            [
                new BlockLinePosition(700.0, 120, 320),
            ]
        };

        var pageHeight = 800d;
        var blockRect = new XRect(90, 50, 300, 120);

        var rects = PdfExportService.TryBuildLineRects(pageHeight, blockRect, style, fallbackLineHeight: 14);

        rects.Should().NotBeNull();
        rects!.Count.Should().BeGreaterThan(0);
        rects[0].Y.Should().Be(blockRect.Y);
        rects[^1].Bottom.Should().BeApproximately(blockRect.Bottom, 0.0001);

        for (var i = 1; i < rects.Count; i++)
        {
            rects[i - 1].Y.Should().BeLessThan(rects[i].Y);
        }

        foreach (var r in rects)
        {
            r.X.Should().BeGreaterThanOrEqualTo(blockRect.X);
            r.Right.Should().BeLessThanOrEqualTo(blockRect.Right);
            r.Height.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void TryBuildLineRects_WithDuplicateBaselines_ReturnsNullToAvoidGridLayouts()
    {
        var style = new BlockTextStyle
        {
            LinePositions =
            [
                new BlockLinePosition(700.0, 100, 200),
                new BlockLinePosition(700.2, 220, 320),
            ]
        };

        var rects = PdfExportService.TryBuildLineRects(800, new XRect(0, 0, 400, 400), style, fallbackLineHeight: 14);
        rects.Should().BeNull();
    }

    [Fact]
    public void LooksLikeGridLinePositions_WithSameBaselinePositions_ReturnsTrue()
    {
        var positions = new List<BlockLinePosition>
        {
            new(700.0, 100, 200),
            new(700.3, 220, 320),
        };

        PdfExportService.LooksLikeGridLinePositions(positions).Should().BeTrue();
    }

    [Fact]
    public void LooksLikeGridLinePositions_WithDistinctBaselines_ReturnsFalse()
    {
        var positions = new List<BlockLinePosition>
        {
            new(700.0, 100, 300),
            new(680.0, 100, 300),
        };

        PdfExportService.LooksLikeGridLinePositions(positions).Should().BeFalse();
    }

    [Fact]
    public void LooksLikeGridLinePositions_WithSinglePosition_ReturnsFalse()
    {
        var positions = new List<BlockLinePosition>
        {
            new(700.0, 100, 300),
        };

        PdfExportService.LooksLikeGridLinePositions(positions).Should().BeFalse();
    }

    [Theory]
    [InlineData("t , t\u22121", true)]
    [InlineData("[35, 2, 5]", true)]
    [InlineData("GPU", false)]
    [InlineData("states h as a function", false)]
    public void LooksLikeInlineScriptLine_ReturnsExpected(string input, bool expected)
    {
        PdfExportService.LooksLikeInlineScriptLine(input).Should().Be(expected);
    }

    [Fact]
    public void SplitLineRectsForInlineScriptProtection_ProtectsOnlyShortSmallScriptLine()
    {
        const string sourceText = "states h as a function\n t , t\u22121 ";
        var lineRects = new List<XRect>
        {
            new(100, 100, 200, 18),
            new(100, 121, 80, 8)
        };

        var result = PdfExportService.SplitLineRectsForInlineScriptProtection(sourceText, lineRects);

        result.ProtectedInlineRects.Should().HaveCount(1);
        result.ProtectedInlineRects[0].Should().Be(lineRects[1]);
        var renderLineRects = result.RenderLineRects;
        renderLineRects.Should().NotBeNull();
        var nonNullRenderLineRects = renderLineRects!;
        nonNullRenderLineRects.Should().HaveCount(1);
        nonNullRenderLineRects[0].Should().Be(lineRects[0]);
    }

    [Theory]
    [InlineData("t", "ₜ")]
    [InlineData("t\u22121", "ₜ₋₁")]
    [InlineData("i+1", "ᵢ₊₁")]
    public void TryConvertToUnicodeSubscript_ConvertsExpected(string input, string expected)
    {
        PdfExportService.TryConvertToUnicodeSubscript(input, out var subscript).Should().BeTrue();
        subscript.Should().Be(expected);
    }

    [Fact]
    public void HandleInlineScriptLinesForOverlay_AugmentsSubscriptsAndErasesOriginalScriptRects()
    {
        const string sourceText = "states h and previous state h\n t , t\u22121";
        const string translated = "state h and previous state h.\n t , t\u22121";
        var lineRects = new List<XRect>
        {
            new(100, 100, 200, 18),
            new(120, 121, 80, 8)
        };

        var result = PdfExportService.HandleInlineScriptLinesForOverlay(sourceText, translated, lineRects);

        result.TranslatedText.Should().Contain("hₜ");
        result.TranslatedText.Should().Contain("hₜ₋₁");
        result.ProtectedInlineRects.Should().BeEmpty();
        var backgroundLineRects = result.BackgroundLineRects;
        backgroundLineRects.Should().NotBeNull();
        backgroundLineRects!.Should().HaveCount(2);
        var renderLineRects = result.RenderLineRects;
        renderLineRects.Should().NotBeNull();
        var nonNullRenderLineRects = renderLineRects!;
        nonNullRenderLineRects.Should().HaveCount(1);
        nonNullRenderLineRects[0].Should().Be(lineRects[0]);
    }

    [Fact]
    public void HandleInlineScriptLinesForOverlay_FoldsCitationLinesAndErasesWhenPresentInTranslation()
    {
        const string sourceText = "long short-term memory\n [13]";
        const string translated = "long short-term memory\n [13]";
        var lineRects = new List<XRect>
        {
            new(100, 100, 200, 18),
            new(180, 92, 40, 8)
        };

        var result = PdfExportService.HandleInlineScriptLinesForOverlay(sourceText, translated, lineRects);

        result.TranslatedText.Should().Contain("[13]");
        result.TranslatedText.Should().NotContain("\n [13]");
        result.ProtectedInlineRects.Should().BeEmpty();
        result.BackgroundLineRects.Should().NotBeNull();
        result.BackgroundLineRects!.Should().HaveCount(2);
        result.RenderLineRects.Should().NotBeNull();
        result.RenderLineRects!.Should().HaveCount(1);
    }

    [Fact]
    public void ShouldApplyFormulaHole_NoOverlap_ReturnsTrue()
    {
        // Formula hole is completely separate from text block
        var formulaHole = new XRect(100, 100, 50, 20);
        var textBlockRect = new XRect(200, 200, 300, 100);

        PdfExportService.ShouldApplyFormulaHole(formulaHole, textBlockRect).Should().BeTrue();
    }

    [Fact]
    public void ShouldApplyFormulaHole_HighOverlap_ReturnsFalse()
    {
        // Formula hole is entirely within the text block (100% of hole area overlaps)
        var formulaHole = new XRect(120, 220, 40, 15);
        var textBlockRect = new XRect(100, 200, 300, 100);

        PdfExportService.ShouldApplyFormulaHole(formulaHole, textBlockRect).Should().BeFalse();
    }

    [Fact]
    public void ShouldApplyFormulaHole_SmallEdgeOverlap_ReturnsFalse()
    {
        // Even a tiny edge overlap means the formula intersects the text block → skip the hole
        var formulaHole = new XRect(50, 100, 60, 20);
        var textBlockRect = new XRect(100, 100, 300, 100);
        // Overlap: X=[100,110], Y=[100,120] → small but non-zero

        PdfExportService.ShouldApplyFormulaHole(formulaHole, textBlockRect).Should().BeFalse();
    }

    [Fact]
    public void ShouldApplyFormulaHole_AdjacentButNotOverlapping_ReturnsTrue()
    {
        // Formula hole is adjacent (touching edge) but not overlapping → apply the hole
        var formulaHole = new XRect(50, 100, 50, 20);
        var textBlockRect = new XRect(100, 100, 300, 100);
        // formulaHole.Right == textBlockRect.X → no intersection

        PdfExportService.ShouldApplyFormulaHole(formulaHole, textBlockRect).Should().BeTrue();
    }

    [Fact]
    public void NeedsMathFont_MathOperators_ReturnsTrue()
    {
        // U+2200 (∀) — Mathematical Operators
        PdfExportService.NeedsMathFont('\u2200').Should().BeTrue();
        // U+2212 (−) — Minus sign
        PdfExportService.NeedsMathFont('\u2212').Should().BeTrue();
        // U+03B1 (α) — Greek lowercase alpha
        PdfExportService.NeedsMathFont('\u03B1').Should().BeTrue();
    }

    [Fact]
    public void NeedsMathFont_RegularChars_ReturnsFalse()
    {
        PdfExportService.NeedsMathFont('A').Should().BeFalse();
        PdfExportService.NeedsMathFont('中').Should().BeFalse();
        PdfExportService.NeedsMathFont('1').Should().BeFalse();
    }

    [Fact]
    public void SegmentLineByFont_MixedContent_SplitsCorrectly()
    {
        // "α+β=γ" has math chars (α, β, γ) and non-math chars (+, =)
        var segments = PdfExportService.SegmentLineByFont("α+β=γ");
        segments.Should().HaveCountGreaterThan(1);
        // Should alternate between math and non-math segments
        segments.Should().Contain(s => s.NeedsMathFont);
        segments.Should().Contain(s => !s.NeedsMathFont);
    }

    [Fact]
    public void SegmentLineByFont_PureText_SingleSegment()
    {
        var segments = PdfExportService.SegmentLineByFont("Hello World");
        segments.Should().HaveCount(1);
        segments[0].NeedsMathFont.Should().BeFalse();
        segments[0].Text.Should().Be("Hello World");
    }

    [Fact]
    public void ParseFormulaFragments_SubscriptPattern_CreatesFragments()
    {
        var fragments = PdfExportService.ParseFormulaFragments("h_{t-1}");
        fragments.Should().HaveCountGreaterThan(1);
        fragments.Should().Contain(f => f.Kind == PdfExportService.FormulaFragmentKind.Subscript);
    }

    [Fact]
    public void ParseFormulaFragments_SuperscriptPattern_CreatesFragments()
    {
        var fragments = PdfExportService.ParseFormulaFragments("x^2");
        fragments.Should().HaveCountGreaterThan(1);
        fragments.Should().Contain(f => f.Kind == PdfExportService.FormulaFragmentKind.Superscript);
    }

    [Fact]
    public void ParseFormulaFragments_NoScript_SingleFragment()
    {
        var fragments = PdfExportService.ParseFormulaFragments("hello world");
        fragments.Should().HaveCount(1);
        fragments[0].Kind.Should().Be(PdfExportService.FormulaFragmentKind.Normal);
        fragments[0].Text.Should().Be("hello world");
    }
}
