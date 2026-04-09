using System.Diagnostics;
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
    private enum FormulaOnlyClassification
    {
        No,
        AllPlaceholders,
        BothSidesOfEquals,
        ResidueOnly,
    }

    private readonly record struct DisplayEquationDiagnostics(
        bool Candidate,
        bool HasEquals,
        bool HasMathFontChars,
        int NonMathWordCount);

    private readonly record struct EquationSoftDiagnostics(
        bool Candidate,
        bool HasEquals,
        bool HasMathFontChars,
        bool HasPlaceholderEvidence,
        int NonMathWordCount,
        bool LeftSuspicious,
        bool RightSuspicious);

    private const string EquationSoftOpenTag = "[[EQ_SOFT]]";
    private const string EquationSoftCloseTag = "[[/EQ_SOFT]]";

    // Shared regex from MathPatterns; single source of truth.
    private static readonly Regex MathFontRegex = new(
        MathPatterns.MathFontPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MathUnicodeRegex = new(
        MathPatterns.MathUnicodePattern, RegexOptions.Compiled);

    private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);

    private static readonly Regex NaturalWordRegex = new(@"\b[a-zA-Z]{4,}\b", RegexOptions.Compiled);

    private static readonly Regex ResidueSplitRegex = new(@"[\s=(),+\-*/^\[\]{}<>|]+", RegexOptions.Compiled);

    private static readonly Regex SideTokenRegex = new(
        @"\{v\d+\}|[A-Za-z]+\d+[A-Za-z\d]*|\d+[A-Za-z][A-Za-z\d]*|[A-Za-z][A-Za-z\d]*",
        RegexOptions.Compiled);

    private static readonly char[] SuspiciousEquationChars =
    [
        '(',
        ')',
        '[',
        ']',
        '{',
        '}',
        '/',
        '*',
        '^',
        ',',
        '+',
        '-',
        '\u221A',
    ];

    private static readonly HashSet<string> CommonShortEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "the",
        "for",
        "use",
        "with",
        "from",
        "into",
        "this",
        "that",
        "then",
        "only",
        "each",
        "are",
        "was",
        "were",
        "our",
        "its",
    };

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
            return SkipTranslation(context, $"BlockType={context.BlockType}, IsFormulaLike={context.IsFormulaLike}");
        }

        // Level 2: Font-based formula detection
        if (IsFontBasedFormula(context.DetectedFontNames, context.FormulaFontPattern))
        {
            return SkipTranslation(context, "MathFontDensity>30%");
        }

        // Level 3: Character-based formula detection
        if (IsCharacterBasedFormula(context.Text, context.FormulaCharPattern))
        {
            return SkipTranslation(context, "MathCharDensity>20%");
        }

        // Level 4: Subscript density
        if (IsSubscriptDenseFormula(context.FormulaCharacters))
        {
            return SkipTranslation(context, "SubscriptDensity>25%");
        }

        // Level 5: Short display equation fallback when ONNX/parser misses the block.
        var displayDiagnostics = GetDisplayEquationDiagnostics(context);
        if (displayDiagnostics.Candidate)
        {
            LogDebug(
                context,
                $"Analyze hit=DisplayEquationHeuristic len={context.Text.Length} nonMathWords={displayDiagnostics.NonMathWordCount}");
            return new ProtectionPlan
            {
                Mode = PreservationMode.Opaque,
                SkipTranslation = true,
                Reason = "DisplayEquationHeuristic"
            };
        }

        if (displayDiagnostics.HasEquals && displayDiagnostics.HasMathFontChars)
        {
            LogDebug(
                context,
                $"Analyze near-miss DisplayEquationHeuristic len={context.Text.Length} nonMathWords={displayDiagnostics.NonMathWordCount}");
        }

        // No block-level skip; text may still have inline formulas or suspicious equations.
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

        var hasExactSoftCandidates = context.RetryAttempt == 0 &&
            FormulaDetector.ContainsExactSoftPreservationCandidate(context.Text);

        // Prefer character-level detection when available (from CharacterParagraphBuilder).
        // Skipped on retry (RetryAttempt >= 1) because the character-level tokens were computed
        // under the original strict policy; the regex path lets us demote ambiguous types.
        // Also skipped when the raw block contains exact-preservation soft spans such as
        // tuple-style symbolic sequences; those must stay on the regex soft-protection path
        // so post-translation validation can enforce verbatim preservation.
        if (context.RetryAttempt == 0 &&
            !hasExactSoftCandidates &&
            context.CharacterLevelProtectedText is not null &&
            context.CharacterLevelTokens is { Count: > 0 })
        {
            var formulaOnlyClassification = GetFormulaOnlyClassification(context.CharacterLevelProtectedText);
            LogDebug(
                context,
                $"Protect path=CharacterLevel formulaOnly={formulaOnlyClassification} tokens={context.CharacterLevelTokens.Count}");

            if (formulaOnlyClassification != FormulaOnlyClassification.No)
            {
                return new ProtectedBlock
                {
                    OriginalText = context.Text,
                    ProtectedText = context.CharacterLevelProtectedText,
                    Tokens = context.CharacterLevelTokens,
                    SoftSpans = Array.Empty<SoftProtectedSpan>(),
                    Plan = plan with
                    {
                        Mode = PreservationMode.Opaque,
                        SkipTranslation = true,
                        Reason = "CharLevel:FormulaOnlyText"
                    }
                };
            }

            var equationSoftDiagnostics = GetEquationSoftProtectionDiagnostics(
                context.CharacterLevelProtectedText,
                context);
            LogEquationSoftDiagnostics(context, equationSoftDiagnostics);

            var charSoftSpans = Array.Empty<SoftProtectedSpan>();
            var charProtectedText = context.CharacterLevelProtectedText;
            if (equationSoftDiagnostics.Candidate)
            {
                charProtectedText = WrapEquationSoftProtectedText(charProtectedText);
                charSoftSpans =
                [
                    CreateEquationSoftSpan(context.Text, charProtectedText)
                ];
            }

            return new ProtectedBlock
            {
                OriginalText = context.Text,
                ProtectedText = charProtectedText,
                Tokens = context.CharacterLevelTokens,
                SoftSpans = charSoftSpans,
                Plan = plan with { Mode = PreservationMode.InlineProtected }
            };
        }

        // Fallback to regex-based detection with two-tier confidence:
        // High-confidence matches -> {vN} hard placeholders
        // Low-confidence matches -> $...$ inline LaTeX for LLM to decide
        // On retry, demoteLevel = RetryAttempt shifts more ambiguous types to soft protection.
        var protectedTextFromRegex = _protector.ProtectTwoTier(
            context.Text,
            out var tokens,
            out var softSpansFromRegex,
            demoteLevel: context.RetryAttempt);

        var formulaOnly = GetFormulaOnlyClassification(protectedTextFromRegex);
        LogDebug(
            context,
            $"Protect path=Regex formulaOnly={formulaOnly} hardTokens={tokens.Count} softSpans={softSpansFromRegex.Count}");

        if (formulaOnly != FormulaOnlyClassification.No)
        {
            return new ProtectedBlock
            {
                OriginalText = context.Text,
                ProtectedText = protectedTextFromRegex,
                Tokens = tokens,
                SoftSpans = softSpansFromRegex,
                Plan = plan with
                {
                    Mode = PreservationMode.Opaque,
                    SkipTranslation = true,
                    Reason = "FormulaOnlyText"
                }
            };
        }

        var equationSoftDiagnosticsRegex = GetEquationSoftProtectionDiagnostics(
            protectedTextFromRegex,
            context);
        LogEquationSoftDiagnostics(context, equationSoftDiagnosticsRegex);

        var protectedText = protectedTextFromRegex;
        IReadOnlyList<SoftProtectedSpan> softSpans = softSpansFromRegex;
        if (equationSoftDiagnosticsRegex.Candidate &&
            !HasEquationSoftWrapper(protectedText) &&
            !IsAlreadyFullySoftProtected(protectedText, tokens, softSpansFromRegex))
        {
            protectedText = WrapEquationSoftProtectedText(protectedText);
            var updatedSoftSpans = softSpansFromRegex.ToList();
            updatedSoftSpans.Add(CreateEquationSoftSpan(context.Text, protectedText));
            softSpans = updatedSoftSpans;
        }

        var effectivePlan = tokens.Count > 0 || softSpans.Count > 0
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

    private static ProtectionPlan SkipTranslation(BlockContext context, string reason)
    {
        LogDebug(context, $"Analyze hit={reason}");
        return new ProtectionPlan
        {
            Mode = PreservationMode.Opaque,
            SkipTranslation = true,
            Reason = reason
        };
    }

    private static FormulaOnlyClassification GetFormulaOnlyClassification(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return FormulaOnlyClassification.No;
        }

        var hasPlaceholders = NumericPlaceholderRegex.IsMatch(protectedText);
        var cleaned = NumericPlaceholderRegex.Replace(protectedText, string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return FormulaOnlyClassification.AllPlaceholders;
        }

        if (!hasPlaceholders)
        {
            return FormulaOnlyClassification.No;
        }

        if (HasFormulaPlaceholdersOnBothSidesOfEquals(protectedText))
        {
            return FormulaOnlyClassification.BothSidesOfEquals;
        }

        if (IsFormulaResidueOnly(cleaned))
        {
            return FormulaOnlyClassification.ResidueOnly;
        }

        return FormulaOnlyClassification.No;
    }

    private static bool HasFormulaPlaceholdersOnBothSidesOfEquals(string protectedText)
    {
        for (var i = 0; i < protectedText.Length; i++)
        {
            if (protectedText[i] != '=') continue;

            var left = protectedText[..i];
            var right = protectedText[(i + 1)..];
            if (NumericPlaceholderRegex.IsMatch(left) && NumericPlaceholderRegex.IsMatch(right))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFormulaResidueOnly(string cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        var tokens = ResidueSplitRegex.Split(cleaned)
            .Where(static token => !string.IsNullOrWhiteSpace(token));
        var hasMathFunction = false;
        var shortAlphaTokenCount = 0;

        foreach (var token in tokens)
        {
            if (MathPatterns.MathFunctionNames.Contains(token))
            {
                hasMathFunction = true;
                continue;
            }

            if (token.All(char.IsDigit))
            {
                continue;
            }

            if (token.Length <= 3)
            {
                if (token.All(char.IsLetter))
                {
                    if (CommonShortEnglishWords.Contains(token))
                    {
                        return false;
                    }

                    shortAlphaTokenCount++;
                }

                continue;
            }

            return false;
        }

        return hasMathFunction || shortAlphaTokenCount <= 1;
    }

    private static DisplayEquationDiagnostics GetDisplayEquationDiagnostics(BlockContext context)
    {
        var hasEquals = context.Text.Contains('=');
        var hasMathFontChars = context.FormulaCharacters?.HasMathFontCharacters == true;
        var nonMathWordCount = CountNonMathFunctionWords(context.Text);
        var candidate = context.Text.Length <= 200 &&
            hasEquals &&
            hasMathFontChars &&
            nonMathWordCount <= 1;

        return new DisplayEquationDiagnostics(candidate, hasEquals, hasMathFontChars, nonMathWordCount);
    }

    private static EquationSoftDiagnostics GetEquationSoftProtectionDiagnostics(string protectedText, BlockContext context)
    {
        if (string.IsNullOrWhiteSpace(protectedText) || protectedText.Length > 220)
        {
            return new EquationSoftDiagnostics(false, false, false, false, 0, false, false);
        }

        var hasEquals = protectedText.Contains('=');
        var hasMathFontChars = context.FormulaCharacters?.HasMathFontCharacters == true;
        var hasPlaceholderEvidence = NumericPlaceholderRegex.IsMatch(protectedText);
        var nonMathWordCount = CountNonMathFunctionWords(protectedText);

        if (!hasEquals)
        {
            return new EquationSoftDiagnostics(
                false,
                false,
                hasMathFontChars,
                hasPlaceholderEvidence,
                nonMathWordCount,
                false,
                false);
        }

        var leftSuspicious = false;
        var rightSuspicious = false;
        for (var i = 0; i < protectedText.Length; i++)
        {
            if (protectedText[i] != '=') continue;

            var left = protectedText[..i].Trim();
            var right = protectedText[(i + 1)..].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            var leftCandidate = IsSuspiciousEquationSide(left);
            var rightCandidate = IsSuspiciousEquationSide(right);
            leftSuspicious |= leftCandidate;
            rightSuspicious |= rightCandidate;
            if (leftCandidate && rightCandidate)
            {
                break;
            }
        }

        var candidate = hasEquals &&
            (hasMathFontChars || hasPlaceholderEvidence) &&
            nonMathWordCount <= 1 &&
            leftSuspicious &&
            rightSuspicious;

        return new EquationSoftDiagnostics(
            candidate,
            true,
            hasMathFontChars,
            hasPlaceholderEvidence,
            nonMathWordCount,
            leftSuspicious,
            rightSuspicious);
    }

    private static bool IsSuspiciousEquationSide(string side)
    {
        if (string.IsNullOrWhiteSpace(side))
        {
            return false;
        }

        side = side.Trim();
        var hasPlaceholder = NumericPlaceholderRegex.IsMatch(side);
        var hasEquationSyntax = side.IndexOfAny(SuspiciousEquationChars) >= 0;
        var tokens = SideTokenRegex.Matches(side)
            .Select(match => match.Value)
            .Where(static token => !NumericPlaceholderRegex.IsMatch(token))
            .ToList();

        var hasFunctionName = tokens.Any(MathPatterns.MathFunctionNames.Contains);
        var hasShortToken = tokens.Any(token => token.All(char.IsDigit) || token.Length <= 3);
        var hasLetterDigitMix = tokens.Any(HasLetterDigitMix);
        var hasRejectedNaturalWord = tokens.Any(token =>
            token.Length > 3 &&
            !MathPatterns.MathFunctionNames.Contains(token) &&
            !HasLetterDigitMix(token));

        if (!hasPlaceholder && hasRejectedNaturalWord && !hasEquationSyntax && !hasFunctionName)
        {
            return false;
        }

        return hasPlaceholder || hasEquationSyntax || hasFunctionName || hasShortToken || hasLetterDigitMix;
    }

    private static bool HasLetterDigitMix(string token)
    {
        var hasLetter = false;
        var hasDigit = false;
        foreach (var c in token)
        {
            if (char.IsLetter(c)) hasLetter = true;
            if (char.IsDigit(c)) hasDigit = true;
            if (hasLetter && hasDigit) return true;
        }

        return false;
    }

    private static int CountNonMathFunctionWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return NaturalWordRegex.Matches(text)
            .Select(match => match.Value)
            .Count(word => !MathPatterns.MathFunctionNames.Contains(word));
    }

    private static bool HasEquationSoftWrapper(string protectedText) =>
        protectedText.StartsWith(EquationSoftOpenTag, StringComparison.Ordinal) &&
        protectedText.EndsWith(EquationSoftCloseTag, StringComparison.Ordinal);

    private static bool IsAlreadyFullySoftProtected(
        string protectedText,
        IReadOnlyList<FormulaToken> tokens,
        IReadOnlyList<SoftProtectedSpan> softSpans) =>
        tokens.Count == 0 &&
        softSpans.Count == 1 &&
        string.Equals(softSpans[0].WrappedText, protectedText, StringComparison.Ordinal);

    private static string WrapEquationSoftProtectedText(string protectedText) =>
        $"{EquationSoftOpenTag}{protectedText}{EquationSoftCloseTag}";

    private static SoftProtectedSpan CreateEquationSoftSpan(string originalText, string wrappedProtectedText) =>
        new()
        {
            RawText = originalText,
            TokenType = FormulaTokenType.InlineEquation,
            WrappedText = wrappedProtectedText,
            SyntheticDelimiters = true,
            RequiresExactPreservation = true,
            WrapperKind = SoftProtectionWrapperKind.EquationSoftTag
        };

    private static RestoreOutcome ValidateSoftProtectedSpans(RestoreOutcome outcome, ProtectedBlock protectedBlock)
    {
        if (protectedBlock.SoftSpans.Count == 0 || outcome.Status == RestoreStatus.FallbackToOriginal)
        {
            return outcome;
        }

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

        if (exactSpans is null)
        {
            return outcome;
        }

        var normalizedText = outcome.Text;
        var stripCount = 0;
        foreach (var span in exactSpans)
        {
            if (!span.SyntheticDelimiters) continue;

            var wrappedRaw = GetWrappedRawText(span);
            var hits = CountOccurrences(normalizedText, wrappedRaw);
            if (hits == 0) continue;

            normalizedText = normalizedText.Replace(wrappedRaw, span.RawText);
            stripCount += hits;
        }

        // For exact-span comparison, collapse LaTeX-equivalent forms (subscript underscores,
        // ellipsis variants) so that e.g. "(y_1, \ldots, y_m)" matches the raw "(y1, ..., ym)".
        // Without this, a well-behaved LLM that normalizes tuple notation trips soft=Failed.
        var comparisonText = NormalizeForExactSpanComparison(normalizedText);
        var softFailureCount = 0;
        foreach (var (raw, expected) in expectedByRaw!)
        {
            var comparisonRaw = NormalizeForExactSpanComparison(raw);
            var actual = CountOccurrences(comparisonText, comparisonRaw);
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

    private static string GetWrappedRawText(SoftProtectedSpan span) => span.WrapperKind switch
    {
        SoftProtectionWrapperKind.EquationSoftTag => $"{EquationSoftOpenTag}{span.RawText}{EquationSoftCloseTag}",
        _ => span.WrappedText,
    };

    /// <summary>
    /// Normalizes a text fragment so that LaTeX-equivalent tuple notations compare equal.
    /// Accepts benign LLM reformatting like <c>(y_1, \ldots, y_m)</c> when the source was
    /// <c>(y1, ..., ym)</c>, without weakening hard-token validation or affecting prose.
    /// </summary>
    internal static string NormalizeForExactSpanComparison(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Replace LaTeX ellipsis commands and Unicode horizontal ellipsis with ASCII "...".
        var replaced = text
            .Replace("\\ldots", "...", StringComparison.Ordinal)
            .Replace("\\dots", "...", StringComparison.Ordinal)
            .Replace("\\cdots", "...", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal);

        // Strip subscript underscores between a letter base and a single letter/digit subscript
        // (e.g. "y_1" → "y1", "x_n" → "xn"). Only drops underscore when the context is clearly a
        // subscript; leaves identifier underscores like "my_var" intact.
        var sb = new System.Text.StringBuilder(replaced.Length);
        for (var i = 0; i < replaced.Length; i++)
        {
            var c = replaced[i];
            if (c == '_' &&
                i > 0 && char.IsLetter(replaced[i - 1]) &&
                i + 1 < replaced.Length && char.IsLetterOrDigit(replaced[i + 1]) &&
                (i + 2 >= replaced.Length || !char.IsLetterOrDigit(replaced[i + 2])))
            {
                // "y_1)" or "x_n," — subscript pattern, drop the underscore.
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
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

    [Conditional("DEBUG")]
    private static void LogEquationSoftDiagnostics(BlockContext context, EquationSoftDiagnostics diagnostics)
    {
        if (!diagnostics.HasEquals)
        {
            return;
        }

        if (diagnostics.Candidate)
        {
            LogDebug(
                context,
                "Protect equation-soft hit " +
                $"hasMathFontChars={diagnostics.HasMathFontChars} " +
                $"hasPlaceholderEvidence={diagnostics.HasPlaceholderEvidence} " +
                $"nonMathWords={diagnostics.NonMathWordCount}");
            return;
        }

        if (context.Text.Length <= 220 &&
            (diagnostics.HasMathFontChars || diagnostics.HasPlaceholderEvidence || diagnostics.LeftSuspicious || diagnostics.RightSuspicious))
        {
            LogDebug(
                context,
                "Protect equation-soft near-miss " +
                $"hasMathFontChars={diagnostics.HasMathFontChars} " +
                $"hasPlaceholderEvidence={diagnostics.HasPlaceholderEvidence} " +
                $"nonMathWords={diagnostics.NonMathWordCount} " +
                $"leftSuspicious={diagnostics.LeftSuspicious} " +
                $"rightSuspicious={diagnostics.RightSuspicious}");
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(BlockContext context, string message)
    {
        Debug.WriteLine($"[FormulaPreservation] {GetDebugLabel(context)} {message}");
    }

    private static string GetDebugLabel(BlockContext context)
    {
        var page = context.DebugPageNumber?.ToString() ?? "?";
        var block = string.IsNullOrWhiteSpace(context.DebugBlockId) ? "?" : context.DebugBlockId;
        return $"p{page}/{block}";
    }
}
