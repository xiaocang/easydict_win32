using Easydict.TranslationService.ContentPreservation;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.ContentPreservation;

public class FormulaPreservationServiceTests
{
    private readonly FormulaPreservationService _service = new();

    [Fact]
    public void Protect_WithCharacterLevelEvidence_PrefersCharacterLevel()
    {
        var context = new BlockContext
        {
            Text = "The value x equals 5",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "The value {v0} equals 5",
            CharacterLevelTokens =
            [
                new FormulaToken(FormulaTokenType.InlineMath, "x", "{v0}", "x")
            ]
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.ProtectedText.Should().Be("The value {v0} equals 5");
        result.Tokens.Should().HaveCount(1);
        result.Tokens[0].Raw.Should().Be("x");
        result.SoftSpans.Should().BeEmpty();
        result.Plan.Mode.Should().Be(PreservationMode.InlineProtected);
    }

    [Fact]
    public void Protect_WithoutCharacterLevelEvidence_FallsBackToRegex()
    {
        var context = new BlockContext
        {
            Text = "The value $\\alpha$ equals 5",
            BlockType = SourceBlockType.Paragraph
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

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
            CharacterLevelTokens =
            [
                new FormulaToken(FormulaTokenType.InlineMath, "x + y = z", "{v0}", "x + y = z")
            ]
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
        result.SoftSpans.Should().BeEmpty();
        result.Plan.SkipTranslation.Should().BeTrue();
    }

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

        plan.SkipTranslation.Should().BeTrue();
    }

    [Fact]
    public void RestoreAndResolve_AllPresent_ReturnsRestoredText()
    {
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "The \\alpha letter",
            ProtectedText = "The {v0} letter",
            Tokens = new[] { new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "alpha") },
            SoftSpans = Array.Empty<SoftProtectedSpan>(),
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("The {v0} letter in translation", protectedBlock);
        var finalText = _service.ResolveFallback(outcome, protectedBlock);

        finalText.Should().Be("The \\alpha letter in translation");
        outcome.Status.Should().Be(RestoreStatus.FullRestore);
        outcome.MissingTokenCount.Should().Be(0);
    }

    [Fact]
    public void Restore_MissingPlaceholder_ReportsPartial()
    {
        var tokens = new[]
        {
            new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "alpha"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\beta", "{v1}", "beta"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\gamma", "{v2}", "gamma"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\delta", "{v3}", "delta"),
        };
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "\\alpha \\beta \\gamma \\delta",
            ProtectedText = "{v0} {v1} {v2} {v3}",
            Tokens = tokens,
            SoftSpans = Array.Empty<SoftProtectedSpan>(),
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
            new FormulaToken(FormulaTokenType.GreekLetter, "\\alpha", "{v0}", "alpha"),
            new FormulaToken(FormulaTokenType.GreekLetter, "\\beta", "{v1}", "beta"),
        };
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = "\\alpha \\beta",
            ProtectedText = "{v0} {v1}",
            Tokens = tokens,
            SoftSpans = Array.Empty<SoftProtectedSpan>(),
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
        var context = new BlockContext
        {
            Text = "The h_{t-1} value.",
            BlockType = SourceBlockType.Paragraph,
            CharacterLevelProtectedText = "The {v0} value.",
            CharacterLevelTokens =
            [
                new FormulaToken(FormulaTokenType.MathSubscript, "h_{t-1}", "{v0}", "h_{t-1}")
            ],
            RetryAttempt = 1
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.Tokens.Should().BeEmpty();
        result.ProtectedText.Should().Contain("$h_{t-1}$");
    }

    [Fact]
    public void Protect_ImplicitTuple_AddsExactSoftSpanMetadata()
    {
        var context = new BlockContext
        {
            Text = "The tuple (x1, ..., xn) is a sequence.",
            BlockType = SourceBlockType.Paragraph
        };
        var plan = new ProtectionPlan { Mode = PreservationMode.None, SkipTranslation = false };

        var result = _service.Protect(context, plan);

        result.Tokens.Should().BeEmpty();
        result.SoftSpans.Should().ContainSingle();
        result.SoftSpans[0].RawText.Should().Be("(x1, ..., xn)");
        result.SoftSpans[0].RequiresExactPreservation.Should().BeTrue();
        result.ProtectedText.Should().Contain("$(x1, ..., xn)$");
    }

    [Fact]
    public void Restore_ExactSoftSpanStripsSyntheticDelimiters()
    {
        const string originalText = "The tuple (x1, ..., xn) is a sequence.";
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = originalText,
            ProtectedText = "The tuple $(x1, ..., xn)$ is a sequence.",
            Tokens = Array.Empty<FormulaToken>(),
            SoftSpans =
            [
                new SoftProtectedSpan
                {
                    RawText = "(x1, ..., xn)",
                    TokenType = FormulaTokenType.ImplicitTuple,
                    WrappedText = "$(x1, ..., xn)$",
                    SyntheticDelimiters = true,
                    RequiresExactPreservation = true
                }
            ],
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("The tuple $(x1, ..., xn)$ is a sequence.", protectedBlock);

        outcome.Text.Should().Be(originalText);
        outcome.Status.Should().Be(RestoreStatus.FullRestore);
        outcome.SoftValidationStatus.Should().Be(SoftValidationStatus.Normalized);
        outcome.SyntheticDelimiterStripCount.Should().Be(1);
    }

    [Fact]
    public void Restore_ExactSoftSpanMutation_FallsBackToOriginal()
    {
        const string originalText = "The tuple (x1, ..., xn) is a sequence.";
        var protectedBlock = new ProtectedBlock
        {
            OriginalText = originalText,
            ProtectedText = "The tuple $(x1, ..., xn)$ is a sequence.",
            Tokens = Array.Empty<FormulaToken>(),
            SoftSpans =
            [
                new SoftProtectedSpan
                {
                    RawText = "(x1, ..., xn)",
                    TokenType = FormulaTokenType.ImplicitTuple,
                    WrappedText = "$(x1, ..., xn)$",
                    SyntheticDelimiters = true,
                    RequiresExactPreservation = true
                }
            ],
            Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
        };

        var outcome = _service.Restore("The tuple sequence1 is a sequence.", protectedBlock);

        outcome.Text.Should().Be(originalText);
        outcome.Status.Should().Be(RestoreStatus.FallbackToOriginal);
        outcome.SoftValidationStatus.Should().Be(SoftValidationStatus.Failed);
        outcome.SoftFailureCount.Should().Be(1);
    }
}
