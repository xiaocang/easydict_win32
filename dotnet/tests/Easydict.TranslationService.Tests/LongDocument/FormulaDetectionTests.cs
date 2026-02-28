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
        // 3 out of 4 are math fonts (75% > 30% threshold)
        var fontNames = new List<string> { "CMSY10", "CMMI12", "Symbol", "Arial" };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeTrue();
    }

    [Fact]
    public void IsFontBasedFormula_MixedFonts_AboveThreshold_ReturnsTrue()
    {
        // 2 out of 5 are math fonts (40% > 30% threshold, was false with old 50% threshold)
        var fontNames = new List<string> { "Arial", "Helvetica", "TimesNewRoman", "CMSY10", "CMMI12" };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeTrue();
    }

    [Fact]
    public void IsFontBasedFormula_MixedFonts_MinorityMath_ReturnsFalse()
    {
        // 1 out of 4 are math fonts (25% < 30% threshold)
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

    // --- Expanded FormulaRegex tests (via full pipeline) ---

    [Theory]
    [InlineData("Consider $$x^2 + y^2 = r^2$$ as shown.", "$$x^2 + y^2 = r^2$$")]       // Display math $$...$$
    [InlineData("We have $\\alpha + \\beta = \\gamma$ here.", "$\\alpha + \\beta = \\gamma$")] // Inline math $...$
    [InlineData("The \\begin{equation}E=mc^2\\end{equation} relation.", "\\begin{equation}E=mc^2\\end{equation}")] // LaTeX env
    [InlineData("Greek \\alpha is first.", "\\alpha")]                                       // LaTeX command
    [InlineData("Sum \\sum over all.", "\\sum")]                                             // LaTeX math operator
    [InlineData("The variable x_{i+1} is next.", "x_{i+1}")]                                // Subscript with braces
    [InlineData("Power x^{2n} is large.", "x^{2n}")]                                        // Superscript with braces
    [InlineData("Index x_i appears.", "x_i")]                                                 // Simple subscript
    [InlineData("Squared x^2 here.", "x^2")]                                                  // Simple superscript
    public async Task TranslateAsync_ExpandedFormulaRegex_ProtectsNewPatterns(string inputText, string expectedProtected)
    {
        var capturedRequest = string.Empty;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            capturedRequest = request.Text;
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-regex",
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
                            Text = inputText
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            EnableFormulaProtection = true
        });

        var protectedText = result.Ir.Blocks.Single().ProtectedText;
        // The protected pattern should have been replaced with {v0}, {v1}, etc.
        protectedText.Should().NotContain(expectedProtected,
            because: $"'{expectedProtected}' should have been replaced with a {{v*}} placeholder");
        protectedText.Should().Contain("{v0}");
    }

    [Fact]
    public async Task TranslateAsync_FormulaPrompt_InjectedWhenPlaceholdersPresent()
    {
        var capturedPrompt = string.Empty;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            capturedPrompt = request.CustomPrompt ?? "";
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-prompt",
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
                            Text = "The equation $E=mc^2$ is famous."
                        }
                    ]
                }
            ]
        };

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true
        });

        capturedPrompt.Should().Contain("formula placeholders",
            because: "when {v*} placeholders exist, the LLM should be instructed to preserve them");
    }

    [Fact]
    public async Task TranslateAsync_FormulaPrompt_NotInjectedWhenNoFormulas()
    {
        var capturedPrompt = string.Empty;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            capturedPrompt = request.CustomPrompt ?? "";
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-no-formula",
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
                            Text = "This is a plain text sentence without formulas."
                        }
                    ]
                }
            ]
        };

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true
        });

        capturedPrompt.Should().BeEmpty(
            because: "when no {v*} placeholders are generated, no formula prompt should be injected");
    }

    // --- Round 2: Font subset prefix stripping ---

    [Theory]
    [InlineData("ABCDE+CMSY10")]      // Standard prefix
    [InlineData("BCDEFG+CMMI12")]      // Different prefix
    [InlineData("XYZABC+Symbol")]      // Symbol font with prefix
    public void IsFontBasedFormula_WithSubsetPrefix_StillDetected(string fontName)
    {
        var fontNames = new List<string> { fontName };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeTrue(
            because: $"'{fontName}' should match after stripping the subset prefix");
    }

    [Theory]
    [InlineData("BCMSY+TimesNewRoman")]  // Prefix that accidentally contains "CMSY" but is not a math font
    [InlineData("ABCDE+Arial")]           // Normal font with prefix
    public void IsFontBasedFormula_WithSubsetPrefix_NormalFont_ReturnsFalse(string fontName)
    {
        var fontNames = new List<string> { fontName };
        LongDocumentTranslationService.IsFontBasedFormula(fontNames, null).Should().BeFalse(
            because: $"'{fontName}' after prefix stripping should not be a math font");
    }

    // --- Round 2: Control character removal ---

    [Fact]
    public void RemoveControlCharacters_RemovesUnicodeControlChars()
    {
        var input = "Hello\u0000World\u0001!\u001FEnd";
        var result = LongDocumentTranslationService.RemoveControlCharacters(input);
        result.Should().Be("HelloWorld!End");
    }

    [Fact]
    public void RemoveControlCharacters_PreservesNewlineTabCarriageReturn()
    {
        var input = "Line1\nLine2\r\nLine3\tTabbed";
        var result = LongDocumentTranslationService.RemoveControlCharacters(input);
        result.Should().Be(input);
    }

    [Fact]
    public void RemoveControlCharacters_EmptyAndNull_ReturnsSame()
    {
        LongDocumentTranslationService.RemoveControlCharacters("").Should().Be("");
        LongDocumentTranslationService.RemoveControlCharacters(null!).Should().BeNull();
    }

    // --- Round 2: Subscript density formula detection ---

    [Fact]
    public void IsSubscriptDenseFormula_HighSubscriptDensity_ReturnsTrue()
    {
        // 3 out of 6 chars are subscripts (50% > 25%) + HasMathFontCharacters
        var chars = new List<FormulaCharacterInfo>
        {
            new("x", "CMMI10", 12, 0, 0, 6, 12, true, false, false),
            new("_", "CMSY10", 8, 6, -2, 4, 8, true, true, false),
            new("1", "CMMI10", 8, 10, -2, 5, 8, true, true, false),
            new("+", "CMSY10", 12, 15, 0, 8, 12, true, false, false),
            new("y", "CMMI10", 12, 23, 0, 6, 12, true, false, false),
            new("_", "CMSY10", 8, 29, -2, 4, 8, true, true, false),
        };
        var formulaChars = new BlockFormulaCharacters
        {
            Characters = chars,
            MedianTextFontSize = 12,
            MedianBaselineY = 0,
            HasMathFontCharacters = true
        };
        LongDocumentTranslationService.IsSubscriptDenseFormula(formulaChars).Should().BeTrue();
    }

    [Fact]
    public void IsSubscriptDenseFormula_NoSubscripts_ReturnsFalse()
    {
        var chars = new List<FormulaCharacterInfo>
        {
            new("a", "CMMI10", 12, 0, 0, 6, 12, true, false, false),
            new("+", "CMSY10", 12, 6, 0, 8, 12, true, false, false),
            new("b", "CMMI10", 12, 14, 0, 6, 12, true, false, false),
        };
        var formulaChars = new BlockFormulaCharacters
        {
            Characters = chars,
            MedianTextFontSize = 12,
            MedianBaselineY = 0,
            HasMathFontCharacters = true
        };
        LongDocumentTranslationService.IsSubscriptDenseFormula(formulaChars).Should().BeFalse();
    }

    [Fact]
    public void IsSubscriptDenseFormula_NoMathFont_ReturnsFalse()
    {
        var chars = new List<FormulaCharacterInfo>
        {
            new("x", "Arial", 12, 0, 0, 6, 12, false, true, false),
            new("1", "Arial", 8, 6, -2, 5, 8, false, true, false),
        };
        var formulaChars = new BlockFormulaCharacters
        {
            Characters = chars,
            MedianTextFontSize = 12,
            MedianBaselineY = 0,
            HasMathFontCharacters = false
        };
        LongDocumentTranslationService.IsSubscriptDenseFormula(formulaChars).Should().BeFalse();
    }

    [Fact]
    public void IsSubscriptDenseFormula_NullOrEmpty_ReturnsFalse()
    {
        LongDocumentTranslationService.IsSubscriptDenseFormula(null).Should().BeFalse();
        LongDocumentTranslationService.IsSubscriptDenseFormula(new BlockFormulaCharacters
        {
            Characters = new List<FormulaCharacterInfo>(),
            HasMathFontCharacters = true
        }).Should().BeFalse();
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
