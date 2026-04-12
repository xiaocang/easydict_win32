using System.Text.RegularExpressions;
using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class MathPatternsTests
{
    private static readonly Regex FontRegex = new(MathPatterns.MathFontPattern, RegexOptions.IgnoreCase);
    private static readonly Regex UnicodeRegex = new(MathPatterns.MathUnicodePattern);

    // --- MathFontPattern positive tests ---

    [Theory]
    [InlineData("CMSY10")]
    [InlineData("CMMI12")]
    [InlineData("CMEX10")]
    [InlineData("Symbol")]
    [InlineData("Mathematica1")]
    [InlineData("STIX-Regular")]
    [InlineData("MTPro2Math")]
    [InlineData("TeX-cmr10")]
    [InlineData("rsfs10")]
    public void MathFontPattern_MatchesMathFonts(string fontName)
    {
        FontRegex.IsMatch(fontName).Should().BeTrue(
            because: $"'{fontName}' is a recognized math font");
    }

    // --- MathFontPattern negative tests (word boundary fix) ---

    [Theory]
    [InlineData("Lato-Regular")]
    [InlineData("TimesNewRoman")]
    [InlineData("Helvetica-Regular")]
    [InlineData("NotoSans-Regular")]
    [InlineData("ArialMT")]
    [InlineData("Roboto-Medium")]
    [InlineData("SourceCodePro-Regular")]
    public void MathFontPattern_RejectsCommonTextFonts(string fontName)
    {
        FontRegex.IsMatch(fontName).Should().BeFalse(
            because: $"'{fontName}' is a common text font, not a math font");
    }

    // --- MathUnicodePattern positive tests ---

    [Theory]
    [InlineData("\u2200")]  // ∀
    [InlineData("\u03B1")]  // α
    [InlineData("\u00B2")]  // ²
    [InlineData("\u200B")]  // ZWSP
    public void MathUnicodePattern_MatchesMathChars(string text)
    {
        UnicodeRegex.IsMatch(text).Should().BeTrue(
            because: $"U+{(int)text[0]:X4} is a math/formula signal character");
    }

    // --- MathUnicodePattern negative tests (narrowed space range) ---

    [Theory]
    [InlineData("\u2002")]  // En space
    [InlineData("\u2003")]  // Em space
    [InlineData("\u2009")]  // Thin space
    [InlineData("\u200A")]  // Hair space
    public void MathUnicodePattern_RejectsGeneralSpaces(string text)
    {
        UnicodeRegex.IsMatch(text).Should().BeFalse(
            because: $"U+{(int)text[0]:X4} is a general space, not a math signal");
    }
}
