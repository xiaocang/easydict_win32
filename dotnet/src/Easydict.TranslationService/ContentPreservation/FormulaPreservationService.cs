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
    // Shared math font regex — single source of truth.
    // Aligned with pdf2zh converter.py font detection.
    private static readonly Regex MathFontRegex = new(
        @"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX" +
        @"|\bBL\b|\bRM\b|\bEU\b|\bLA\b|\bRS\b" +  // word-boundary anchored
        @"|LINE|LCIRCLE" +
        @"|TeX-|rsfs|txsy|wasy|stmary" +
        @"|\w+Sym\w*|\b\w{1,5}Math\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Shared math Unicode regex — single source of truth.
    private static readonly Regex MathUnicodeRegex = new(
        @"[\u2200-\u22FF\u2100-\u214F\u0370-\u03FF\u2070-\u209F\u00B2\u00B3\u00B9\u2150-\u218F\u27C0-\u27EF\u2980-\u29FF" +
        @"\u02B0-\u02FF" +
        @"\u0300-\u036F" +
        @"\u02C6-\u02CF" +
        @"\u200B-\u200D]",  // narrowed: only ZWSP/ZWNJ/ZWJ
        RegexOptions.Compiled);

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

        var protectedText = _protector.Protect(context.Text, out var tokens);
        var isFormulaOnly = IsFormulaOnlyText(protectedText);

        var effectivePlan = isFormulaOnly
            ? plan with { Mode = PreservationMode.Opaque, SkipTranslation = true, Reason = "FormulaOnlyText" }
            : tokens.Count > 0
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
