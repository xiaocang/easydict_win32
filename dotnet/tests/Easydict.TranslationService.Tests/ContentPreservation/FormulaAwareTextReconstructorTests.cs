using Easydict.TranslationService.ContentPreservation;
using Easydict.TranslationService.LongDocument;
using FluentAssertions;
using Xunit;
using LetterGeometry = Easydict.TranslationService.ContentPreservation.FormulaAwareTextReconstructor.LetterGeometry;

namespace Easydict.TranslationService.Tests.ContentPreservation;

public class FormulaAwareTextReconstructorTests
{
    // Synthesizes a single visual line of letters. Tokens are separated by a word gap;
    // indexes in SubscriptIndexes are emitted with a smaller point size and a lowered baseline
    // so the reconstructor can classify them as subscripts.
    private static List<LetterGeometry> BuildLineLetters(
        double baselineY,
        params (string Token, int[] SubscriptIndexes)[] tokens)
    {
        var letters = new List<LetterGeometry>();
        var x = 100d;

        foreach (var (token, subscriptIndexes) in tokens)
        {
            var subscriptSet = subscriptIndexes.ToHashSet();
            for (var index = 0; index < token.Length; index++)
            {
                var ch = token[index];
                var isSubscript = subscriptSet.Contains(index);
                var pointSize = isSubscript ? 8d : 12d;
                var bottom = isSubscript ? baselineY - 4d : baselineY;
                var top = bottom + pointSize;
                var letterBaselineY = isSubscript ? baselineY - 4d : baselineY;
                var width = char.IsLetterOrDigit(ch) ? 6d : (ch == '.' ? 2.5d : 3.5d);

                letters.Add(new LetterGeometry(
                    ch.ToString(),
                    x,
                    x + width,
                    bottom,
                    top,
                    letterBaselineY,
                    pointSize,
                    "TimesNewRoman"));

                x += width + 0.4d;
            }

            x += 4.5d;
        }

        return letters;
    }

    [Fact]
    public void Reconstruct_ShouldPreserveInlineTupleSequences()
    {
        var letters = new List<LetterGeometry>();
        letters.AddRange(BuildLineLetters(
            700,
            ("Here", []),
            (",", []),
            ("the", []),
            ("encoder", []),
            ("maps", []),
            ("an", []),
            ("input", []),
            ("sequence", []),
            ("of", []),
            ("symbol", []),
            ("representations", []),
            ("(", []),
            ("x1", [1]),
            (",", []),
            ("...", []),
            (",", []),
            ("xn", [1]),
            (")", []),
            ("to", []),
            ("a", []),
            ("sequence", [])));
        letters.AddRange(BuildLineLetters(
            682,
            ("of", []),
            ("continuous", []),
            ("representations", []),
            ("z", []),
            ("=", []),
            ("(", []),
            ("z1", [1]),
            (",", []),
            ("...", []),
            (",", []),
            ("zn", [1]),
            (")", []),
            (".", [])));

        var text = FormulaAwareTextReconstructor.Reconstruct(letters);

        text.Should().Contain("(x1, ..., xn)");
        text.Should().Contain("z = (z1, ..., zn)");
        text.Should().NotContain("sequence_1");
    }

    [Fact]
    public void Reconstruct_EmptyLetters_ReturnsEmpty()
    {
        FormulaAwareTextReconstructor.Reconstruct([]).Should().BeEmpty();
    }

    [Fact]
    public void ShouldUseLetterBasedBlockText_ReturnsTrue_WhenMathFontCharactersPresent()
    {
        var formulaChars = new BlockFormulaCharacters
        {
            Characters = [],
            HasMathFontCharacters = true
        };

        FormulaAwareTextReconstructor
            .ShouldUseLetterBasedBlockText(["plain text"], formulaChars, characterLevelProtectedText: null)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldUseLetterBasedBlockText_ReturnsTrue_WhenCharacterLevelProtectedTextPresent()
    {
        FormulaAwareTextReconstructor
            .ShouldUseLetterBasedBlockText(["plain text"], formulaChars: null, characterLevelProtectedText: "x {v0}")
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldUseLetterBasedBlockText_ReturnsTrue_WhenLineTextContainsScriptHint()
    {
        FormulaAwareTextReconstructor
            .ShouldUseLetterBasedBlockText(["x_1 + y^2"], formulaChars: null, characterLevelProtectedText: null)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldUseLetterBasedBlockText_ReturnsTrue_WhenTupleContinuationEvidenceIsPresent()
    {
        FormulaAwareTextReconstructor
            .ShouldUseLetterBasedBlockText(
                [
                    "Here, the encoder maps an input sequence of symbol representations (x",
                    ", ..., xn)",
                    "to a sequence of continuous representations z = (z",
                    ", ..., zn)"
                ],
                formulaChars: null,
                characterLevelProtectedText: null)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldUseLetterBasedBlockText_ReturnsFalse_WhenNoEvidence()
    {
        FormulaAwareTextReconstructor
            .ShouldUseLetterBasedBlockText(["Plain prose without any hints."], formulaChars: null, characterLevelProtectedText: null)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(", ..., xn)", true)]
    [InlineData(", x_n)", true)]
    [InlineData("This is regular prose text.", false)]
    [InlineData("", false)]
    public void LooksLikeFormulaContinuationText_WorksForTypicalCases(string input, bool expected)
    {
        FormulaAwareTextReconstructor.LooksLikeFormulaContinuationText(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Here, the encoder maps an input sequence of symbol representations (x", true)]
    [InlineData("z = (z", true)]
    [InlineData("Regular sentence.", false)]
    [InlineData("", false)]
    public void PreviousLineLikelyExpectsFormulaTail_WorksForTypicalCases(string input, bool expected)
    {
        FormulaAwareTextReconstructor.PreviousLineLikelyExpectsFormulaTail(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Most competitive neural models", "Mostcompetitiveneural models", false)] // 1/3 = 0.33 < 0.8
    [InlineData("Most competitive neural models", "Most competitive neural models", true)]
    [InlineData("Most competitive neural models", "Most competitive models", false)] // 2/3 = 0.67 < 0.8
    [InlineData("a b c d e f g", "a b c d e g", true)] // 5/6 = 0.83 >= 0.8
    [InlineData("x1, ..., xn", "x1,...,xn", true)] // short text, few spaces → skip check
    [InlineData("a b c d e f g", "a b c d e f g", true)]
    [InlineData("", "anything", true)]
    [InlineData("two words", "twowords", true)] // fallbackSpaces=1, <=2 → skip
    [InlineData(
        "Most competitive neural sequence transduction models have an encoder-decoder structure",
        "Mostcompetitiveneural sequencetransductionmodels have anencoder-decoder structure",
        false)] // merged words "Mostcompetitiveneural" (21 chars) > mergeThreshold
    public void IsReconstructionQualityAcceptable_WorksForTypicalCases(
        string fallback, string reconstructed, bool expected)
    {
        FormulaAwareTextReconstructor.IsReconstructionQualityAcceptable(reconstructed, fallback)
            .Should().Be(expected);
    }

    [Fact]
    public void IsReconstructionQualityAcceptable_ReturnsTrue_WhenTupleAnchorsAreRestoredDespiteLowerSpaceDensity()
    {
        const string fallback =
            "Here, the encoder ma ps an in put sequence of symbol representations x1 ... xn to a sequence of continuous representations z = z1 ... zn";
        const string reconstructed =
            "Here, the encoder maps an input sequence of symbol representations (x1, ..., xn) to a sequence of continuous representations z = (z1, ..., zn)";

        FormulaAwareTextReconstructor.IsReconstructionQualityAcceptable(reconstructed, fallback)
            .Should().BeTrue();
    }

    [Fact]
    public void Reconstruct_WithLowerWordGapScale_ProducesMoreSpaces()
    {
        // Build letters with tight word gaps that the default threshold merges
        var letters = new List<LetterGeometry>();
        var x = 100d;
        var words = new[] { "Most", "competitive", "neural" };
        foreach (var word in words)
        {
            foreach (var ch in word)
            {
                var width = 6d;
                letters.Add(new LetterGeometry(
                    ch.ToString(), x, x + width, 690d, 702d, 690d, 12d, "TimesNewRoman"));
                x += width + 0.4d;
            }
            x += 2.5d; // tight word gap (< default threshold ~4.5)
        }

        var defaultResult = FormulaAwareTextReconstructor.Reconstruct(letters);
        var scaledResult = FormulaAwareTextReconstructor.Reconstruct(letters, wordGapScale: 0.5);

        var defaultSpaces = defaultResult.Count(c => c == ' ');
        var scaledSpaces = scaledResult.Count(c => c == ' ');

        scaledSpaces.Should().BeGreaterThanOrEqualTo(defaultSpaces,
            "lower wordGapScale should produce at least as many word breaks");
    }
}
