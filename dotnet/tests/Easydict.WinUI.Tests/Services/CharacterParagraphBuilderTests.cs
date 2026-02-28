using Easydict.WinUI.Services;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class CharacterParagraphBuilderTests
{
    // Helper to create a minimal CharInfo for testing
    private static CharInfo MakeChar(string text, double x, double y, double pointSize = 12.0,
        string fontName = "TimesNewRoman", int charCode = 0x41)
    {
        return new CharInfo
        {
            Text = text,
            CharacterCode = charCode,
            Cid = charCode,
            Font = null!,  // Not used in paragraph builder logic
            FontResourceName = fontName,
            FontSize = pointSize,
            PointSize = pointSize,
            TextMatrix = TransformationMatrix.Identity,
            CurrentTransformationMatrix = TransformationMatrix.Identity,
            X0 = x,
            Y0 = y,
            X1 = x + 6.0,  // ~6pt character width
            Y1 = y + pointSize,
        };
    }

    [Fact]
    public void Build_EmptyInput_ReturnsEmptyResult()
    {
        var result = CharacterParagraphBuilder.Build(Array.Empty<CharInfo>());

        result.Paragraphs.Should().BeEmpty();
        result.AllFormulaGroups.Should().BeEmpty();
        result.TotalCharacters.Should().Be(0);
        result.FormulaCharacters.Should().Be(0);
    }

    [Fact]
    public void Build_SingleTextCharacter_CreatesSingleParagraph()
    {
        var chars = new[] { MakeChar("H", 100, 700) };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        result.Paragraphs[0].Text.Should().Be("H");
        result.Paragraphs[0].Characters.Should().HaveCount(1);
        result.FormulaCharacters.Should().Be(0);
    }

    [Fact]
    public void Build_PlainTextWord_MergesIntoSingleParagraph()
    {
        var chars = new[]
        {
            MakeChar("H", 100, 700),
            MakeChar("e", 106, 700),
            MakeChar("l", 112, 700),
            MakeChar("l", 118, 700),
            MakeChar("o", 124, 700),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        result.Paragraphs[0].Text.Should().Be("Hello");
        result.Paragraphs[0].Characters.Should().HaveCount(5);
        result.TotalCharacters.Should().Be(5);
    }

    [Fact]
    public void Build_MathFontCharacter_ClassifiedAsFormula()
    {
        var chars = new[]
        {
            MakeChar("f", 100, 700, fontName: "TimesNewRoman"),
            MakeChar("(", 106, 700, fontName: "TimesNewRoman"),
            MakeChar("x", 112, 700, fontName: "CMMI10"),  // Math font!
            MakeChar(")", 118, 700, fontName: "TimesNewRoman"),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        // "x" should be in a formula variable group
        result.AllFormulaGroups.Should().HaveCountGreaterOrEqualTo(1);
        result.FormulaCharacters.Should().BeGreaterOrEqualTo(1);
        result.Paragraphs[0].Text.Should().Contain("{v");
    }

    [Fact]
    public void Build_SubscriptCharacter_ClassifiedAsFormula()
    {
        var chars = new[]
        {
            MakeChar("x", 100, 700, pointSize: 12.0),  // Parent character
            MakeChar("2", 106, 696, pointSize: 8.0),    // Subscript: 8 < 12 * 0.79 = 9.48
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        // The subscript "2" should be detected as formula
        result.FormulaCharacters.Should().Be(1);
        result.Paragraphs[0].Text.Should().Contain("{v");
    }

    [Fact]
    public void Build_MathUnicodeCharacter_ClassifiedAsFormula()
    {
        var chars = new[]
        {
            MakeChar("x", 100, 700),
            MakeChar("\u2260", 106, 700),  // ≠ (not equal to) — math symbol
            MakeChar("y", 112, 700),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.FormulaCharacters.Should().BeGreaterOrEqualTo(1);
        result.Paragraphs[0].Text.Should().Contain("{v");
    }

    [Fact]
    public void Build_GreekLetter_ClassifiedAsFormula()
    {
        var chars = new[]
        {
            MakeChar("\u03B1", 100, 700),  // α (alpha) — Greek letter
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.FormulaCharacters.Should().Be(1);
        result.AllFormulaGroups.Should().HaveCount(1);
    }

    [Fact]
    public void Build_ExcludedRegion_TreatedAsFormula()
    {
        var chars = new[]
        {
            MakeChar("x", 100, 700),
            MakeChar("=", 106, 700),
            MakeChar("1", 112, 700),
        };

        // All characters in excluded region (cls=0)
        var result = CharacterParagraphBuilder.Build(chars, (_, _) => 0);

        // All should be formula (excluded region)
        result.FormulaCharacters.Should().Be(3);
    }

    [Fact]
    public void Build_LayoutClassChange_CreatesSeparateParagraphs()
    {
        var chars = new[]
        {
            MakeChar("A", 100, 700),
            MakeChar("B", 106, 700),
            MakeChar("C", 100, 500),
            MakeChar("D", 106, 500),
        };

        // First two chars in class 1, last two in class 2
        var result = CharacterParagraphBuilder.Build(chars, (x, y) => y > 600 ? 1 : 2);

        result.Paragraphs.Should().HaveCount(2);
        result.Paragraphs[0].Text.Should().Be("AB");
        result.Paragraphs[1].Text.Should().Be("CD");
    }

    [Fact]
    public void Build_BracketTracking_KeepsFormulaContentTogether()
    {
        // "f(" + math chars + ")" — brackets should keep formula together
        var chars = new[]
        {
            MakeChar("f", 100, 700, fontName: "TimesNewRoman"),
            MakeChar("(", 106, 700, fontName: "TimesNewRoman"),
            MakeChar("x", 112, 700, fontName: "CMMI10"),   // Math font starts formula
            MakeChar("+", 118, 700, fontName: "CMSY10"),    // Math font
            MakeChar("y", 124, 700, fontName: "CMMI10"),    // Math font
            MakeChar(")", 130, 700, fontName: "TimesNewRoman"),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        // All formula chars should be grouped together
        result.AllFormulaGroups.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public void Build_VerticalTextMatrix_ClassifiedAsFormula()
    {
        // Vertical text matrix: matrix[0]==0 && matrix[3]==0
        var verticalMatrix = TransformationMatrix.FromValues(0, 1, -1, 0, 100, 700);

        var chars = new[]
        {
            new CharInfo
            {
                Text = "x",
                CharacterCode = 0x78,
                Cid = 0x78,
                Font = null!,
                FontResourceName = "TimesNewRoman",
                FontSize = 12.0,
                PointSize = 12.0,
                TextMatrix = verticalMatrix,
                CurrentTransformationMatrix = TransformationMatrix.Identity,
                X0 = 100, Y0 = 700, X1 = 112, Y1 = 706,
            }
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.FormulaCharacters.Should().Be(1);
    }

    [Fact]
    public void Build_UnicodeReplacement_ClassifiedAsFormula()
    {
        var chars = new[] { MakeChar("\uFFFD", 100, 700) };

        var result = CharacterParagraphBuilder.Build(chars);

        result.FormulaCharacters.Should().Be(1);
    }

    [Fact]
    public void Build_MixedTextAndFormula_InsertsPlaceholders()
    {
        // "where " + formula_x + " is the" ...
        var chars = new[]
        {
            MakeChar("w", 100, 700),
            MakeChar("h", 106, 700),
            MakeChar("e", 112, 700),
            MakeChar("r", 118, 700),
            MakeChar("e", 124, 700),
            MakeChar(" ", 130, 700),
            MakeChar("x", 136, 700, fontName: "CMMI10"),  // formula
            MakeChar(" ", 142, 700),
            MakeChar("i", 148, 700),
            MakeChar("s", 154, 700),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        var text = result.Paragraphs[0].Text;
        text.Should().Contain("where ");
        text.Should().Contain("{v0}");
        text.Should().Contain("is");
    }

    // --- IsFormulaCharacter tests ---

    [Fact]
    public void IsFormulaCharacter_NormalText_ReturnsFalse()
    {
        var ch = MakeChar("A", 100, 700, fontName: "TimesNewRoman");
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 12.0, 1).Should().BeFalse();
    }

    [Fact]
    public void IsFormulaCharacter_MathFont_ReturnsTrue()
    {
        var ch = MakeChar("x", 100, 700, fontName: "CMMI10");
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 12.0, 1).Should().BeTrue();
    }

    [Fact]
    public void IsFormulaCharacter_SubsetPrefixMathFont_ReturnsTrue()
    {
        var ch = MakeChar("x", 100, 700, fontName: "ABCDEF+CMSY10");
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 12.0, 1).Should().BeTrue();
    }

    [Fact]
    public void IsFormulaCharacter_ExcludedRegion_ReturnsTrue()
    {
        var ch = MakeChar("A", 100, 700, fontName: "TimesNewRoman");
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 12.0, 0).Should().BeTrue();
    }

    [Fact]
    public void IsFormulaCharacter_Subscript_ReturnsTrue()
    {
        var ch = MakeChar("2", 100, 700, pointSize: 7.0, fontName: "TimesNewRoman");
        // 7.0 < 12.0 * 0.79 = 9.48 → subscript
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 12.0, 1).Should().BeTrue();
    }

    [Fact]
    public void IsFormulaCharacter_Subscript_NoParent_ReturnsFalse()
    {
        var ch = MakeChar("2", 100, 700, pointSize: 7.0, fontName: "TimesNewRoman");
        // No parent font size (0) → subscript detection skipped
        CharacterParagraphBuilder.IsFormulaCharacter(ch, 0, 1).Should().BeFalse();
    }

    // --- GetBracketDelta tests ---

    [Theory]
    [InlineData("(", 1)]
    [InlineData("[", 1)]
    [InlineData("{", 1)]
    [InlineData(")", -1)]
    [InlineData("]", -1)]
    [InlineData("}", -1)]
    [InlineData("A", 0)]
    [InlineData("", 0)]
    [InlineData("()", 0)]
    [InlineData("((", 2)]
    [InlineData("))", -2)]
    public void GetBracketDelta_ReturnsCorrectValue(string text, int expected)
    {
        CharacterParagraphBuilder.GetBracketDelta(text).Should().Be(expected);
    }

    [Fact]
    public void Build_FormulaGroupHasCorrectBounds()
    {
        var chars = new[]
        {
            MakeChar("x", 100, 700, fontName: "CMMI10"),
            MakeChar("+", 108, 700, fontName: "CMSY10"),
            MakeChar("y", 116, 700, fontName: "CMMI10"),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.AllFormulaGroups.Should().HaveCount(1);
        var group = result.AllFormulaGroups[0];
        group.Characters.Should().HaveCount(3);
        group.X0.Should().Be(100);
        group.X1.Should().Be(116 + 6.0);  // last char X + width
    }

    [Fact]
    public void Build_NoClassifier_DefaultsToTranslatable()
    {
        var chars = new[]
        {
            MakeChar("A", 100, 700),
            MakeChar("B", 106, 700),
        };

        // No classifier passed → all characters default to translatable (cls=1)
        var result = CharacterParagraphBuilder.Build(chars, classifyCharacter: null);

        result.Paragraphs.Should().HaveCount(1);
        result.Paragraphs[0].Text.Should().Be("AB");
        result.FormulaCharacters.Should().Be(0);
    }

    [Fact]
    public void Build_ProtectedText_MatchesParagraphText()
    {
        var chars = new[]
        {
            MakeChar("a", 100, 700),
            MakeChar("\u03B1", 106, 700),  // Greek alpha
            MakeChar("b", 112, 700),
        };

        var result = CharacterParagraphBuilder.Build(chars);

        result.Paragraphs.Should().HaveCount(1);
        // ProtectedText should equal Text (both contain {v*} placeholders)
        result.Paragraphs[0].ProtectedText.Should().Be(result.Paragraphs[0].Text);
    }
}
