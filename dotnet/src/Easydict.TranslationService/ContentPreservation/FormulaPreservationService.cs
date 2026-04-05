using System.Text.RegularExpressions;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;

namespace Easydict.TranslationService.ContentPreservation;

/// <summary>
/// Formula-specific implementation of <see cref="IContentPreservationService"/>.
/// Consolidates detection heuristics, protection, restoration, and fallback policy
/// that were previously scattered across <c>LongDocumentTranslationService</c>.
/// </summary>
public sealed class FormulaPreservationService : IContentPreservationService
{
    // Shared regex from MathPatterns — single source of truth
    private static readonly Regex MathFontRegex = new(
        MathPatterns.MathFontPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MathUnicodeRegex = new(
        MathPatterns.MathUnicodePattern, RegexOptions.Compiled);

    private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);

    private readonly FormulaProtector _protector = new();
    private readonly FormulaRestorer _restorer = new();

    /// <inheritdoc />
    public ProtectionPlan Analyze(BlockContext context)
    {
        // Level 1: Block type signals (parser already decided)
        if (context.BlockType == SourceBlockType.Formula ||
            context.BlockType == SourceBlockType.TableCell ||
            context.IsFormulaLike)
        {
            return new ProtectionPlan
            {
                Mode = PreservationMode.Opaque,
                SkipTranslation = true,
                Reason = $"BlockType={context.BlockType}, IsFormulaLike={context.IsFormulaLike}"
            };
        }

        // Level 2: Font-based formula detection
        if (IsFontBasedFormula(context.DetectedFontNames, context.FormulaFontPattern))
        {
            return new ProtectionPlan
            {
                Mode = PreservationMode.Opaque,
                SkipTranslation = true,
                Reason = "MathFontDensity>30%"
            };
        }

        // Level 3: Character-based formula detection
        if (IsCharacterBasedFormula(context.Text, context.FormulaCharPattern))
        {
            return new ProtectionPlan
            {
                Mode = PreservationMode.Opaque,
                SkipTranslation = true,
                Reason = "MathCharDensity>20%"
            };
        }

        // Level 4: Subscript density
        if (IsSubscriptDenseFormula(context.FormulaCharacters))
        {
            return new ProtectionPlan
            {
                Mode = PreservationMode.Opaque,
                SkipTranslation = true,
                Reason = "SubscriptDensity>25%"
            };
        }

        // No block-level skip — text may still have inline formulas
        return new ProtectionPlan
        {
            Mode = PreservationMode.None,
            SkipTranslation = false
        };
    }

    /// <inheritdoc />
    public ProtectedBlock Protect(BlockContext context, ProtectionPlan plan)
    {
        if (plan.SkipTranslation)
        {
            return new ProtectedBlock
            {
                OriginalText = context.Text,
                ProtectedText = context.Text,
                Tokens = Array.Empty<FormulaToken>(),
                SoftSpans = Array.Empty<SoftProtectedSpan>(),
                Plan = plan
            };
        }

        // Prefer character-level detection when available (from CharacterParagraphBuilder).
        // Skipped on retry (RetryAttempt >= 1) because the character-level tokens were computed
        // under the original strict policy; the regex path lets us demote ambiguous types.
        if (context.RetryAttempt == 0 &&
            context.CharacterLevelProtectedText is not null &&
            context.CharacterLevelTokens is { Count: > 0 })
        {
            var isCharFormulaOnly = IsFormulaOnlyText(context.CharacterLevelProtectedText);
            var charPlan = isCharFormulaOnly
                ? plan with { Mode = PreservationMode.Opaque, SkipTranslation = true, Reason = "CharLevel:FormulaOnlyText" }
                : plan with { Mode = PreservationMode.InlineProtected };

            return new ProtectedBlock
            {
                OriginalText = context.Text,
                ProtectedText = context.CharacterLevelProtectedText,
                Tokens = context.CharacterLevelTokens,
                SoftSpans = Array.Empty<SoftProtectedSpan>(),
                Plan = charPlan
            };
        }

        // Fallback to regex-based detection with two-tier confidence:
        // High-confidence matches → {vN} hard placeholders
        // Low-confidence matches → $...$ inline LaTeX for LLM to decide
        // On retry, demoteLevel = RetryAttempt shifts more ambiguous types to soft protection.
        var protectedText = _protector.ProtectTwoTier(
            context.Text,
            out var tokens,
            out var softSpans,
            demoteLevel: context.RetryAttempt);
        var isFormulaOnly = IsFormulaOnlyText(protectedText);

        var effectivePlan = isFormulaOnly
            ? plan with { Mode = PreservationMode.Opaque, SkipTranslation = true, Reason = "FormulaOnlyText" }
            : tokens.Count > 0 || softSpans.Count > 0
                ? plan with { Mode = PreservationMode.InlineProtected }
                : plan;

        return new ProtectedBlock
        {
            OriginalText = context.Text,
            ProtectedText = protectedText,
            Tokens = tokens,
            SoftSpans = softSpans,
            Plan = effectivePlan
        };
    }

    /// <inheritdoc />
    public RestoreOutcome Restore(string translatedText, ProtectedBlock protectedBlock)
    {
        RestoreOutcome outcome;
        if (protectedBlock.Tokens.Count == 0)
        {
            outcome = new RestoreOutcome
            {
                Text = translatedText,
                Status = RestoreStatus.FullRestore,
                MissingTokenCount = 0
            };
        }
        else
        {
            var result = _restorer.RestoreWithDiagnostics(
                translatedText,
                protectedBlock.Tokens,
                protectedBlock.OriginalText,
                useSimplified: false);

            var status = result.Status switch
            {
                FormulaRestoreStatus.FullRestore => RestoreStatus.FullRestore,
                FormulaRestoreStatus.PartialRestore => RestoreStatus.PartialRestore,
                FormulaRestoreStatus.FallbackToOriginal => RestoreStatus.FallbackToOriginal,
                _ => RestoreStatus.FullRestore,
            };

            outcome = new RestoreOutcome
            {
                Text = result.Text,
                Status = status,
                MissingTokenCount = result.DroppedCount
            };
        }

        return ValidateSoftProtectedSpans(outcome, protectedBlock);
    }

    /// <inheritdoc />
    public string ResolveFallback(RestoreOutcome outcome, ProtectedBlock protectedBlock)
    {
        // The FormulaRestorer already handles fallback internally (graduated: full/partial/original).
        // This method is the hook point for future fallback policies (e.g., segment-based re-translation).
        return outcome.Text;
    }

    // --- Detection heuristics (moved from LongDocumentTranslationService) ---

    internal static bool IsFontBasedFormula(IReadOnlyList<string>? fontNames, string? customPattern)
    {
        if (fontNames is null || fontNames.Count == 0) return false;
        var pattern = !string.IsNullOrWhiteSpace(customPattern)
            ? new Regex(customPattern, RegexOptions.IgnoreCase)
            : MathFontRegex;
        var mathFontCount = fontNames.Count(f =>
        {
            // Strip PDF subset prefix (e.g. "ABCDE+CMSY10" → "CMSY10")
            var name = f;
            var plusIdx = name.IndexOf('+');
            if (plusIdx >= 0 && plusIdx < name.Length - 1)
                name = name[(plusIdx + 1)..];
            return pattern.IsMatch(name);
        });
        return mathFontCount > fontNames.Count * 0.3;
    }

    internal static bool IsCharacterBasedFormula(string text, string? customPattern)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pattern = !string.IsNullOrWhiteSpace(customPattern)
            ? new Regex(customPattern)
            : MathUnicodeRegex;
        var mathCharCount = pattern.Matches(text).Count;
        mathCharCount += text.Count(c => c == '\uFFFD');
        return text.Length > 0 && (double)mathCharCount / text.Length > 0.2;
    }

    internal static bool IsSubscriptDenseFormula(BlockFormulaCharacters? formulaChars)
    {
        if (formulaChars?.Characters is not { Count: > 0 } chars) return false;
        if (!formulaChars.HasMathFontCharacters) return false;

        var scriptCount = chars.Count(c => c.IsSubscript || c.IsSuperscript);
        return chars.Count >= 3 && (double)scriptCount / chars.Count > 0.25;
    }

    private static bool IsFormulaOnlyText(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText)) return false;
        var cleaned = NumericPlaceholderRegex.Replace(protectedText, string.Empty).Trim();
        return cleaned.Length == 0;
    }

    private static RestoreOutcome ValidateSoftProtectedSpans(RestoreOutcome outcome, ProtectedBlock protectedBlock)
    {
        if (protectedBlock.SoftSpans.Count == 0 || outcome.Status == RestoreStatus.FallbackToOriginal)
        {
            return outcome;
        }

        // Collect exact-preservation spans and count expected occurrences per raw text in a single pass.
        Dictionary<string, int>? expectedByRaw = null;
        List<SoftProtectedSpan>? exactSpans = null;
        foreach (var span in protectedBlock.SoftSpans)
        {
            if (!span.RequiresExactPreservation) continue;
            exactSpans ??= new List<SoftProtectedSpan>();
            expectedByRaw ??= new Dictionary<string, int>(StringComparer.Ordinal);
            exactSpans.Add(span);
            expectedByRaw[span.RawText] = expectedByRaw.TryGetValue(span.RawText, out var c) ? c + 1 : 1;
        }

        if (exactSpans is null) return outcome;

        // Strip synthetic $...$ delimiters by literal replace of WrappedText (set at protection time).
        var normalizedText = outcome.Text;
        var stripCount = 0;
        foreach (var span in exactSpans)
        {
            if (!span.SyntheticDelimiters) continue;
            var hits = CountOccurrences(normalizedText, span.WrappedText);
            if (hits == 0) continue;
            normalizedText = normalizedText.Replace(span.WrappedText, span.RawText);
            stripCount += hits;
        }

        var softFailureCount = 0;
        foreach (var (raw, expected) in expectedByRaw!)
        {
            var actual = CountOccurrences(normalizedText, raw);
            if (actual < expected) softFailureCount += expected - actual;
        }

        if (softFailureCount > 0)
        {
            return new RestoreOutcome
            {
                Text = protectedBlock.OriginalText,
                Status = RestoreStatus.FallbackToOriginal,
                MissingTokenCount = outcome.MissingTokenCount,
                SoftValidationStatus = SoftValidationStatus.Failed,
                SoftFailureCount = softFailureCount,
                SyntheticDelimiterStripCount = stripCount
            };
        }

        return new RestoreOutcome
        {
            Text = normalizedText,
            Status = outcome.Status,
            MissingTokenCount = outcome.MissingTokenCount,
            SoftValidationStatus = stripCount > 0 ? SoftValidationStatus.Normalized : SoftValidationStatus.Passed,
            SoftFailureCount = 0,
            SyntheticDelimiterStripCount = stripCount
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
