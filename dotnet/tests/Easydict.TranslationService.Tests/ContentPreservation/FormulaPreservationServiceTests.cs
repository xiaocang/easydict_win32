using Easydict.TranslationService.ContentPreservation;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.ContentPreservation;

public class FormulaPreservationServiceTests
{
    private readonly FormulaPreservationService _service = new();

    // --- Character-level evidence preference tests ---

    [Fact]
    public void Protect_WithCharacterLevelEvidence_PrefersCharacterLevel()
    {
        var context = new BlockContext
        {
            Text = "The value x equals 5",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "The value {v0} equals 5",
            CharacterLevelTokens = new[]
            {
                new FormulaToken(FormulaTokenType.InlineMath, "x", "{v0}", "x")
            }
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.ProtectedText.Should().Be("The value {v0} equals 5");
        result.Tokens.Should().HaveCount(1);
        result.Tokens[0].Raw.Should().Be("x");
        result.Plan.Mode.Should().Be(PreservationMode.InlineProtected);
    }

    [Fact]
    public void Protect_WithoutCharacterLevelEvidence_FallsBackToRegex()
    {
        var context = new BlockContext
        {
            Text = "The value $\\alpha$ equals 5",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = null,
            CharacterLevelTokens = null
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        // Regex should detect $\alpha$ and protect it
        result.ProtectedText.Should().Contain("{v0}");
        result.Tokens.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public void Protect_WithEmptyCharacterLevelTokens_FallsBackToRegex()
    {
        var context = new BlockContext
        {
            Text = "The value $\\beta$ here",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "The value $\\beta$ here",
            CharacterLevelTokens = Array.Empty<FormulaToken>()
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        // Empty char-level tokens → falls back to regex
        result.ProtectedText.Should().Contain("{v0}");
        result.Tokens.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public void Protect_CharacterLevelFormulaOnly_MarksAsOpaque()
    {
        var context = new BlockContext
        {
            Text = "x + y = z",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "{v0}",
            CharacterLevelTokens = new[]
            {
                new FormulaToken(FormulaTokenType.InlineMath, "x + y = z", "{v0}", "x + y = z")
            }
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.Plan.SkipTranslation.Should().BeTrue();
        result.Plan.Mode.Should().Be(PreservationMode.Opaque);
    }

    [Fact]
    public void Protect_SkipTranslation_ReturnsOpaqueBlock()
    {
        var context = new BlockContext
        {
            Text = "formula block",
            BlockType = SourceBlockType.Formula
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.Opaque, SkipTranslation = true };

        var result = _service.Protect(context, plan);

        result.ProtectedText.Should().Be("formula block");
        result.Tokens.Should().BeEmpty();
        result.Plan.SkipTranslation.Should().BeTrue();
    }

    // --- Analyze tests ---

    [Fact]
    public void Analyze_FormulaBlockType_ReturnsOpaque()
    {
        var context = new BlockContext
        {
            Text = "x = 5",
            BlockType = SourceBlockType.Formula
        };

        var plan = _service.Analyze(context);

        plan.SkipTranslation.Should().BeTrue();
        plan.Mode.Should().Be(PreservationMode.Opaque);
    }

    [Fact]
    public void Analyze_ParagraphBlockType_ReturnsNone()
    {
        var context = new BlockContext
        {
            Text = "This is normal text.",
            BlockType = SourceBlockType.Paragraph
        };

        var plan = _service.Analyze(context);

        plan.SkipTranslation.Should().BeFalse();
        plan.Mode.Should().Be(PreservationMode.None);
    }

    [Fact]
    public void Analyze_MathFontDensity_ReturnsOpaque()
    {
        var context = new BlockContext
        {
            Text = "formula content",
            BlockType = SourceBlockType.Paragraph,
            DetectedFontNames = new[] { "CMSY10", "CMMI12", "Arial" }
        };

        var plan = _service.Analyze(context);

        // 2/3 = 67% > 30% threshold
        plan.SkipTranslation.Should().BeTrue();
    }

    // --- Restore + ResolveFallback tests ---

    [Fact]
    public void RestoreAndResolve_AllPresent_ReturnsRestoredText()
    {
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "The \\alpha letter",
            ProtectedText = "The {v0} letter",
            Tokens = new[] { new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "α") },
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("这个 {v0} 字母", protectedBlock);
        var finalText = _service.ResolveFallback(outcome, protectedBlock);

        finalText.Should().Be("这个 \\alpha 字母");
        outcome.Status.Should().Be(RestoreStatus.FullRestore);
        outcome.MissingTokenCount.Should().Be(0);
    }

    [Fact]
    public void Restore_MissingPlaceholder_ReportsPartial()
    {
        // 4 tokens, translated text contains only 3 placeholders (75%) → PartialRestore
        var tokens = new[]
        {
            new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "α"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\beta", "{v1}", "β"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\gamma", "{v2}", "γ"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\delta", "{v3}", "δ"),
        };
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "\\alpha \\beta \\gamma \\delta",
            ProtectedText = "{v0} {v1} {v2} {v3}",
            Tokens = tokens,
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("{v0} {v1} {v2}", protectedBlock);

        outcome.Status.Should().Be(RestoreStatus.PartialRestore);
        outcome.MissingTokenCount.Should().Be(1);
    }

    [Fact]
    public void Restore_AllMissing_ReportsFallback()
    {
        var tokens = new[]
        {
            new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "α"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\beta", "{v1}", "β"),
        };
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "\\alpha \\beta",
            ProtectedText = "{v0} {v1}",
            Tokens = tokens,
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("no placeholders here", protectedBlock);

        outcome.Status.Should().Be(RestoreStatus.FallbackToOriginal);
        outcome.MissingTokenCount.Should().Be(2);
        outcome.Text.Should().Be("\\alpha \\beta");
    }

    [Fact]
    public void Protect_RetryAttempt1_DemotesSubscript()
    {
        // On retry, MathSubscript is demoted to soft $...$ protection.
        var context = new BlockContext
        {
            Text = "We use h_{t-1} in the recurrence.",
            BlockType = SourceBlockType.Paragraph,
            RetryAttempt = 1
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.Tokens.Should().BeEmpty();
        result.ProtectedText.Should().Contain("$h_{t-1}$");
    }

    [Fact]
    public void Protect_RetryAttempt1_SkipsCharacterLevelPath()
    {
        // On retry, even if character-level tokens are present, the regex path is used
        // so demoteLevel can take effect. Character-level tokens are ignored.
        var context = new BlockContext
        {
            Text = "The h_{t-1} value.",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "The {v0} value.",
            CharacterLevelTokens = new[]
            {
                new FormulaToken(FormulaTokenType.MathSubscript, "h_{t-1}", "{v0}", "h_{t-1}")
            },
            RetryAttempt = 1
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        // Character-level tokens not used; regex path demotes subscript to soft protection.
        result.Tokens.Should().BeEmpty();
        result.ProtectedText.Should().Contain("$h_{t-1}$");
    }
}
