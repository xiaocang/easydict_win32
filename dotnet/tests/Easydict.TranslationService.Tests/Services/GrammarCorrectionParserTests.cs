using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public class GrammarCorrectionParserTests
{
    private const string ServiceName = "TestService";

    [Fact]
    public void Parse_WithValidMarkers_ExtractsCorrectedTextAndExplanation()
    {
        var rawOutput = """
            [CORRECTED]
            He went to the store yesterday.
            [/CORRECTED]

            [EXPLANATION]
            Changed "go" to "went" (past tense required for past action).
            [/EXPLANATION]
            """;

        var result = GrammarCorrectionParser.Parse(rawOutput, "He go to the store yesterday.", ServiceName, 100);

        result.CorrectedText.Should().Be("He went to the store yesterday.");
        result.Explanation.Should().Contain("Changed \"go\" to \"went\"");
        result.OriginalText.Should().Be("He go to the store yesterday.");
        result.ServiceName.Should().Be(ServiceName);
        result.TimingMs.Should().Be(100);
        result.HasCorrections.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithNoCorrections_HasCorrectionsFalse()
    {
        var original = "The quick brown fox jumps over the lazy dog.";
        var rawOutput = $"""
            [CORRECTED]
            {original}
            [/CORRECTED]

            [EXPLANATION]
            No grammar issues found.
            [/EXPLANATION]
            """;

        var result = GrammarCorrectionParser.Parse(rawOutput, original, ServiceName, 50);

        result.CorrectedText.Should().Be(original);
        result.HasCorrections.Should().BeFalse();
        result.Explanation.Should().Contain("No grammar issues found");
    }

    [Fact]
    public void Parse_WithoutMarkers_FallsBackToEntireOutput()
    {
        var rawOutput = "He went to the store yesterday.";

        var result = GrammarCorrectionParser.Parse(rawOutput, "He go to the store yesterday.", ServiceName, 75);

        result.CorrectedText.Should().Be("He went to the store yesterday.");
        result.Explanation.Should().BeNull();
        result.HasCorrections.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithEmptyOutput_ReturnsOriginalText()
    {
        var original = "Some text.";

        var result = GrammarCorrectionParser.Parse("", original, ServiceName, 10);

        result.CorrectedText.Should().Be(original);
        result.HasCorrections.Should().BeFalse();
        result.Explanation.Should().BeNull();
    }

    [Fact]
    public void Parse_WithWhitespaceOutput_ReturnsOriginalText()
    {
        var original = "Some text.";

        var result = GrammarCorrectionParser.Parse("   \n  ", original, ServiceName, 10);

        result.CorrectedText.Should().Be(original);
        result.HasCorrections.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithOnlyCorrectedMarker_ExtractsCorrectedText()
    {
        var rawOutput = """
            [CORRECTED]
            She has been working here since 2020.
            [/CORRECTED]
            """;

        var result = GrammarCorrectionParser.Parse(
            rawOutput, "She have been working here since 2020.", ServiceName, 60);

        result.CorrectedText.Should().Be("She has been working here since 2020.");
        result.Explanation.Should().BeNull();
        result.HasCorrections.Should().BeTrue();
    }

    [Fact]
    public void Parse_CaseInsensitiveTags_Works()
    {
        var rawOutput = """
            [corrected]
            Fixed text.
            [/corrected]

            [explanation]
            Some fix.
            [/explanation]
            """;

        var result = GrammarCorrectionParser.Parse(rawOutput, "Broken text.", ServiceName, 30);

        result.CorrectedText.Should().Be("Fixed text.");
        result.Explanation.Should().Be("Some fix.");
    }

    [Fact]
    public void Parse_WithMultilineCorrection_PreservesNewlines()
    {
        var rawOutput = """
            [CORRECTED]
            First line corrected.
            Second line corrected.
            Third line corrected.
            [/CORRECTED]

            [EXPLANATION]
            Line 1: Fixed subject-verb agreement.
            Line 2: Fixed spelling.
            [/EXPLANATION]
            """;

        var result = GrammarCorrectionParser.Parse(
            rawOutput, "First line broken.\nSecond line broken.", ServiceName, 120);

        result.CorrectedText.Should().Contain("First line corrected.");
        result.CorrectedText.Should().Contain("Third line corrected.");
        result.Explanation.Should().Contain("Line 1:");
        result.Explanation.Should().Contain("Line 2:");
    }

    [Fact]
    public void Parse_WithMalformedOpenTagOnly_FallsBackToEntireOutput()
    {
        var rawOutput = "[CORRECTED]\nSome text but no closing tag";

        var result = GrammarCorrectionParser.Parse(rawOutput, "Original.", ServiceName, 20);

        // No closing tag means extraction fails, fallback to entire output
        result.CorrectedText.Should().Be(rawOutput.Trim());
    }
}
