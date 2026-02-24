using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class FormulaDetectionTests
{
    [Theory]
    [InlineData("CMSY10")]
    [InlineData("CMMI12")]
    [InlineData("CMEX10")]
    [InlineData("Symbol")]
    [InlineData("Mathematica1")]
    [InlineData("MTPro2Math")]
    [InlineData("STIX-Regular")]
    public void IsFontBasedFormula_SingleMathFont_ReturnsTrue(string fontName)
    {
        var fontNames = new List<string> { fontName };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeTrue();
    }

    [Theory]
    [InlineData("Arial")]
    [InlineData("TimesNewRoman")]
    [InlineData("Helvetica")]
    [InlineData("NotoSansSC-Regular")]
    public void IsFontBasedFormula_NormalFont_ReturnsFalse(string fontName)
    {
        var fontNames = new List<string> { fontName };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeFalse();
    }

    [Fact]
    public void IsFontBasedFormula_MixedFonts_MajorityMath_ReturnsTrue()
    {
        // 3 out of 4 are math fonts (75% > 50%)
        var fontNames = new List<string> { "CMSY10", "CMMI12", "Symbol", "Arial" };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeTrue();
    }

    [Fact]
    public void IsFontBasedFormula_MixedFonts_MinorityMath_ReturnsFalse()
    {
        // 1 out of 4 are math fonts (25% < 50%)
        var fontNames = new List<string> { "Arial", "Helvetica", "TimesNewRoman", "CMSY10" };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeFalse();
    }

    [Fact]
    public void IsFontBasedFormula_NullFontNames_ReturnsFalse()
    {
        LongDocumentTranslationService.IsFontBasedFormula(null, null).Should().BeFalse();
    }

    [Fact]
    public void IsFontBasedFormula_EmptyFontNames_ReturnsFalse()
    {
        LongDocumentTranslationService.IsFontBasedFormula(new List<string>(), null).Should().BeFalse();
    }

    [Fact]
    public void IsFontBasedFormula_CustomPattern_OverridesDefault()
    {
        // "MyCustomMathFont" won't match the default pattern
        var fontNames = new List<string> { "MyCustomMathFont" };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeFalse();

        // But matches with a custom pattern
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, "MyCustomMath").Should().BeTrue();
    }

    [Theory]
    [InlineData("\u2200x\u2208S \u2203y\u2208T", true)]       // ∀x∈S ∃y∈T — mostly math symbols
    [InlineData("\u03B1\u03B2\u03B3\u03B4\u03B5\u03B6", true)]  // αβγδεζ — Greek letters >50%
    [InlineData("\u222B\u222C\u2211\u220F\u221A", true)]       // ∫∬∑∏√ — pure math operators
    public void IsCharacterBasedFormula_MathContent_ReturnsTrue(string text, bool expected)
    {
        LongDocumentTranslationService.IsCharacterBasedFormula(text, null).Should().Be(expected);
    }

    [Theory]
    [InlineData("This is a normal English sentence.")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("Hello World 123")]
    public void IsCharacterBasedFormula_NormalText_ReturnsFalse(string text)
    {
        LongDocumentTranslationService.IsCharacterBasedFormula(text, null).Should().BeFalse();
    }

    [Fact]
    public void IsCharacterBasedFormula_EmptyText_ReturnsFalse()
    {
        LongDocumentTranslationService.IsCharacterBasedFormula("", null).Should().BeFalse();
        LongDocumentTranslationService.IsCharacterBasedFormula(null!, null).Should().BeFalse();
    }

    [Fact]
    public void IsCharacterBasedFormula_CustomPattern_OverridesDefault()
    {
        // Text with custom markers
        var text = "###FORMULA###";
        LongDocumentTranslationService.IsCharacterBasedFormula(text, null).Should().BeFalse();

        // Custom pattern matching '#'
        LongDocumentTranslationService.IsCharacterBasedFormula(text, "#").Should().BeTrue();
    }

    [Fact]
    public async Task BuildIr_FontBasedFormula_SkipsTranslation()
    {
        var source = new SourceDocument
        {
            DocumentId = "test",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Normal text",
                            DetectedFontNames = new List<string> { "Arial", "Helvetica" }
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "b2",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "x + y = z",
                            DetectedFontNames = new List<string> { "CMSY10", "CMMI12", "Symbol" }
                        }
                    ]
                }
            ]
        };

        var options = new LongDocumentTranslationOptions { ToLanguage = Language.English };

        // Use the translation service to test BuildIr indirectly through TranslateAsync
        // Since BuildIr is private, we test it through the full pipeline with a mock translator
        var translationCalled = new List<string>();
        var service = new LongDocumentTranslationService(
            translateWithService: (request, serviceId, ct) =>
            {
                translationCalled.Add(request.Text);
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = $"translated: {request.Text}",
                    ServiceName = serviceId
                });
            });

        var result = await service.TranslateAsync(source, options, CancellationToken.None);

        // b2 should be skipped (font-based formula detection) and not sent to translator
        translationCalled.Should().ContainSingle()
            .Which.Should().Be("Normal text");

        // Both blocks should be in result
        result.Pages.Should().HaveCount(1);
        result.Pages[0].Blocks.Should().HaveCount(2);

        // b2 should be marked as skipped
        result.Pages[0].Blocks[1].TranslationSkipped.Should().BeTrue();
    }
}
