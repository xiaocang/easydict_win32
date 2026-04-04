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
                Plan = plan
            };
        }

        // Prefer character-level detection when available (from CharacterParagraphBuilder)
        if (context.CharacterLevelProtectedText is not null &&
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
                Plan = charPlan
            };
        }

        // Fallback to regex-based detection with two-tier confidence:
        // High-confidence matches → {vN} hard placeholders
        // Low-confidence matches → $...$ inline LaTeX for LLM to decide
        var protectedText = _protector.ProtectTwoTier(context.Text, out var tokens);
        var isFormulaOnly = IsFormulaOnlyText(protectedText);

        var effectivePlan = isFormulaOnly
            ? plan with { Mode = PreservationMode.Opaque, SkipTranslation = true, Reason = "FormulaOnlyText" }
            : tokens.Count > 0 || protectedText.Contains('$')
                ? plan with { Mode = PreservationMode.InlineProtected }
                : plan;

        return new ProtectedBlock
        {
            OriginalText = context.Text,
            ProtectedText = protectedText,
            Tokens = tokens,
            Plan = effectivePlan
        };
    }

    /// <inheritdoc />
    public RestoreOutcome Restore(string translatedText, ProtectedBlock protectedBlock)
    {
        if (protectedBlock.Tokens.Count == 0)
        {
            return new RestoreOutcome
            {
                Text = translatedText,
                Status = RestoreStatus.FullRestore,
                MissingTokenCount = 0
            };
        }

        var restoredText = _restorer.Restore(
            translatedText,
            protectedBlock.Tokens,
            protectedBlock.OriginalText,
            useSimplified: false);

        // Determine status by checking if restoration fell back to original
        var status = restoredText == protectedBlock.OriginalText && translatedText != protectedBlock.OriginalText
            ? RestoreStatus.FallbackToOriginal
            : RestoreStatus.FullRestore;

        return new RestoreOutcome
        {
            Text = restoredText,
            Status = status,
            MissingTokenCount = 0
        };
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
}
