using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;

namespace Easydict.TranslationService.ContentPreservation;

/// <summary>
/// Pure text reconstruction from raw letter geometry: rebuilds block text with
/// sub/superscript markers and formula-aware line continuation.
/// </summary>
public static class FormulaAwareTextReconstructor
{
    /// <summary>
    /// Format-neutral geometry for a single glyph. Produced by the caller from its
    /// native PDF/text source — this type intentionally has no dependency on any
    /// PDF library so the reconstructor stays pure.
    /// </summary>
    public readonly record struct LetterGeometry(
        string Value,
        double Left,
        double Right,
        double Bottom,
        double Top,
        double BaselineY,
        double PointSize,
        string FontName)
    {
        public double Width => Math.Max(0.1d, Right - Left);
        public double Height => Math.Max(0.1d, Top - Bottom);
    }

    private static readonly Regex FormulaContinuationTextRegex = new(
        @"^[,.;:)\]\}\s_^\{\}A-Za-z0-9+\-=/\\*]+$",
        RegexOptions.Compiled);

    private static readonly Regex LongAlphabeticRunRegex = new(
        @"[A-Za-z]{4,}",
        RegexOptions.Compiled);

    // Normalization regexes used by NormalizeReconstructedSpacing. Hoisted to avoid
    // recompiling on every reading line.
    private static readonly Regex SpaceBeforeTrailingPunctuationRegex = new(
        @"\s+([,.;:!?%\)\]\}])", RegexOptions.Compiled);
    private static readonly Regex SpaceAfterLeadingBracketRegex = new(
        @"([(\[\{])\s+", RegexOptions.Compiled);
    private static readonly Regex CommaWithoutFollowingSpaceRegex = new(
        @",(?=[^\s\)\]\}])", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforeClosingQuoteRegex = new(
        @"\s+([’”])", RegexOptions.Compiled);
    private static readonly Regex SpaceAfterOpeningQuoteRegex = new(
        @"([“‘])\s+", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespaceRegex = new(
        @"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the block's line texts contain enough evidence (math fonts,
    /// character-level protected text, or inline script hints) that falling back to
    /// letter-geometry reconstruction is worthwhile.
    /// </summary>
    public static bool ShouldUseLetterBasedBlockText(
        IReadOnlyList<string> lineTexts,
        BlockFormulaCharacters? formulaChars,
        string? characterLevelProtectedText)
    {
        if (formulaChars?.HasMathFontCharacters == true)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(characterLevelProtectedText))
        {
            return true;
        }

        for (var i = 0; i < lineTexts.Count; i++)
        {
            if (LineContainsScriptHint(lineTexts[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds block text from letter geometry, encoding detected subscripts/superscripts
    /// as <c>_{...}</c> / <c>^{...}</c> and merging formula continuation lines.
    /// Returns the empty string if no letters were supplied.
    /// </summary>
    /// <param name="letters">Letter geometry from the PDF block.</param>
    /// <param name="wordGapScale">Multiplier for the word-gap threshold (default 1.0).
    /// Values &lt; 1 produce more word breaks; use when the default threshold merges
    /// adjacent words (e.g. tight academic paper layouts).</param>
    public static string Reconstruct(IReadOnlyList<LetterGeometry> letters, double wordGapScale = 1.0)
    {
        if (letters is null || letters.Count == 0)
        {
            return string.Empty;
        }

        var groupedLines = GroupLettersIntoReadingLines(letters);
        if (groupedLines.Count == 0)
        {
            return string.Empty;
        }

        var lineTexts = groupedLines
            .Select(line => BuildTextFromReadingLine(line, wordGapScale))
            .ToList();

        var mergedLineTexts = MergeContinuationLineTexts(groupedLines, lineTexts);
        return string.Join("\n", mergedLineTexts).Trim();
    }

    /// <summary>
    /// Checks whether the reconstructed text preserves adequate word spacing compared
    /// to the PdfPig fallback text. Returns false when too many spaces are lost,
    /// indicating the reconstruction quality is unacceptable.
    /// </summary>
    public static bool IsReconstructionQualityAcceptable(
        string reconstructedText, string fallbackText)
    {
        if (string.IsNullOrWhiteSpace(fallbackText))
            return true;

        var fallbackSpaces = fallbackText.Count(c => c == ' ');
        if (fallbackSpaces <= 2)
            return true;

        // Check 1: overall space density
        var reconstructedSpaces = reconstructedText.Count(c => c == ' ');
        if (reconstructedSpaces < fallbackSpaces * 0.8)
            return false;

        // Check 2: detect merged-word artifacts — abnormally long Latin-only tokens
        // (normal English words are < 15 chars; "Mostcompetitiveneural" = 21 chars is a merge)
        var fallbackMaxWordLen = 0;
        foreach (var word in fallbackText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > fallbackMaxWordLen && word.All(char.IsLetter))
                fallbackMaxWordLen = word.Length;
        }

        var mergeThreshold = Math.Max(16, fallbackMaxWordLen + 2);
        var reconstructedMaxWordLen = 0;
        foreach (var word in reconstructedText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > mergeThreshold && word.All(char.IsLetter))
                return false;
            if (word.Length > reconstructedMaxWordLen && word.All(char.IsLetter))
                reconstructedMaxWordLen = word.Length;
        }

        // Check 3: longest reconstructed word should not be much longer than fallback's longest
        // (e.g., "Mostcompetitive" = 15 vs "representations" = 15 is fine,
        //  but "Mostcompetitive" = 15 vs "competitive" = 11 suggests merge)
        if (fallbackMaxWordLen > 0 && reconstructedMaxWordLen > fallbackMaxWordLen * 1.3 + 2)
            return false;

        return true;
    }

    /// <summary>
    /// Heuristic: does <paramref name="text"/> look like the tail of an unfinished
    /// formula from the previous line (punctuation / short symbolic tail with no
    /// long alphabetic run)?
    /// </summary>
    public static bool LooksLikeFormulaContinuationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 24)
        {
            return false;
        }

        if (!FormulaContinuationTextRegex.IsMatch(trimmed))
        {
            return false;
        }

        if (LongAlphabeticRunRegex.IsMatch(trimmed))
        {
            return false;
        }

        return trimmed.Contains("...", StringComparison.Ordinal) ||
            trimmed.Contains('_', StringComparison.Ordinal) ||
            trimmed.Contains('^', StringComparison.Ordinal) ||
            ",.;:)]}".Contains(trimmed[0]);
    }

    /// <summary>
    /// Heuristic: does <paramref name="text"/> look like a line that ends mid-formula
    /// (unbalanced parens, trailing operator/comma/ellipsis)?
    /// </summary>
    public static bool PreviousLineLikelyExpectsFormulaTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var openParens = trimmed.Count(ch => ch == '(');
        var closeParens = trimmed.Count(ch => ch == ')');
        return openParens > closeParens ||
            trimmed.EndsWith("...", StringComparison.Ordinal) ||
            trimmed.EndsWith(",", StringComparison.Ordinal) ||
            trimmed.EndsWith("_", StringComparison.Ordinal) ||
            trimmed.EndsWith("^", StringComparison.Ordinal);
    }

    private static bool LineContainsScriptHint(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains('_', StringComparison.Ordinal) || text.Contains('^', StringComparison.Ordinal));

    private static List<ReconstructedLetterLine> GroupLettersIntoReadingLines(IReadOnlyList<LetterGeometry> letters)
    {
        var ordered = letters
            .OrderByDescending(letter => letter.Top)
            .ThenBy(letter => letter.Left)
            .ToList();

        var scaleSamples = ordered
            .Select(letter => letter.PointSize > 0 ? letter.PointSize : letter.Height)
            .Where(sample => sample > 0)
            .OrderBy(sample => sample)
            .ToList();
        var medianScale = scaleSamples.Count > 0 ? scaleSamples[scaleSamples.Count / 2] : 10d;
        var baselineTolerance = Math.Max(1.2d, medianScale * 0.22d);
        var scriptTolerance = Math.Max(2.4d, medianScale * 0.75d);

        var lines = new List<ReconstructedLetterLine>();
        foreach (var letter in ordered)
        {
            ReconstructedLetterLine? bestLine = null;
            var bestScore = double.MaxValue;

            foreach (var candidate in lines)
            {
                var baselineDistance = Math.Abs(candidate.BaselineY - letter.BaselineY);
                var sameBaseline = baselineDistance <= baselineTolerance;
                var looksLikeScript = letter.PointSize > 0 &&
                    letter.PointSize < candidate.MedianPointSize * 0.92d &&
                    baselineDistance <= scriptTolerance;

                if (!sameBaseline && !looksLikeScript)
                {
                    continue;
                }

                var verticalOverlap = Math.Max(0d, Math.Min(candidate.Top, letter.Top) - Math.Max(candidate.Bottom, letter.Bottom));
                var score = baselineDistance - verticalOverlap * 0.05d;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestLine = candidate;
                }
            }

            if (bestLine is null)
            {
                lines.Add(new ReconstructedLetterLine(letter));
            }
            else
            {
                bestLine.Add(letter);
            }
        }

        return lines
            .OrderByDescending(line => line.Top)
            .ThenBy(line => line.Left)
            .ToList();
    }

    private static string BuildTextFromReadingLine(ReconstructedLetterLine line, double wordGapScale = 1.0)
    {
        var sorted = line.Letters
            .OrderBy(letter => letter.Left)
            .ThenByDescending(letter => letter.Top)
            .ToList();
        if (sorted.Count == 0)
        {
            return string.Empty;
        }

        var widthSamples = sorted
            .Select(letter => letter.Width)
            .OrderBy(width => width)
            .ToList();
        var medianWidth = widthSamples.Count > 0 ? widthSamples[widthSamples.Count / 2] : 5d;
        var scaleSamples = sorted
            .Select(letter => letter.PointSize > 0 ? letter.PointSize : letter.Height)
            .Where(sample => sample > 0)
            .OrderBy(sample => sample)
            .ToList();
        var medianScale = scaleSamples.Count > 0 ? scaleSamples[scaleSamples.Count / 2] : 10d;
        var wordGapThreshold = Math.Max(1.0d,
            Math.Min(medianWidth * 0.75d * wordGapScale, medianScale * 0.45d * wordGapScale));

        var tokens = new List<List<LetterGeometry>>();
        var currentToken = new List<LetterGeometry> { sorted[0] };
        for (var index = 1; index < sorted.Count; index++)
        {
            var previous = sorted[index - 1];
            var current = sorted[index];
            var gap = current.Left - previous.Right;
            if (gap > wordGapThreshold)
            {
                tokens.Add(currentToken);
                currentToken = [];
            }

            currentToken.Add(current);
        }

        tokens.Add(currentToken);

        var tokenTexts = tokens
            .Select(BuildScriptAwareTokenText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return NormalizeReconstructedSpacing(string.Join(" ", tokenTexts));
    }

    private static string BuildScriptAwareTokenText(IReadOnlyList<LetterGeometry> letters)
    {
        if (letters.Count == 0)
        {
            return string.Empty;
        }

        if (letters.Count == 1)
        {
            return letters[0].Value;
        }

        var sorted = letters
            .OrderBy(letter => letter.Left)
            .ThenByDescending(letter => letter.Top)
            .ToList();

        var pointSizes = sorted
            .Select(letter => letter.PointSize > 0 ? letter.PointSize : letter.Height)
            .Where(size => size > 0)
            .OrderBy(size => size)
            .ToList();
        if (pointSizes.Count < 2 || pointSizes[^1] / pointSizes[0] < 1.10d)
        {
            return string.Concat(sorted.Select(letter => letter.Value));
        }

        var medianSize = pointSizes[pointSizes.Count / 2];
        var medianBaseline = sorted
            .Select(letter => letter.BaselineY)
            .OrderBy(y => y)
            .Skip(sorted.Count / 2)
            .First();
        var posThreshold = Math.Max(0.5d, medianSize * 0.15d);

        var subs = new bool[sorted.Count];
        var sups = new bool[sorted.Count];
        for (var index = 0; index < sorted.Count; index++)
        {
            var pointSize = sorted[index].PointSize > 0 ? sorted[index].PointSize : sorted[index].Height;
            if (pointSize >= medianSize * 0.87d)
            {
                continue;
            }

            var baseline = sorted[index].BaselineY;
            subs[index] = baseline < medianBaseline - posThreshold;
            sups[index] = baseline > medianBaseline + posThreshold;
        }

        if (!subs.Any(isSub => isSub) && !sups.Any(isSup => isSup))
        {
            return string.Concat(sorted.Select(letter => letter.Value));
        }

        if (TryBuildSimpleIndexedTokenText(sorted, subs, sups, out var indexedToken))
        {
            return indexedToken;
        }

        var builder = new StringBuilder();
        var tokenIndex = 0;
        while (tokenIndex < sorted.Count)
        {
            if (tokenIndex == 0 || (!subs[tokenIndex] && !sups[tokenIndex]))
            {
                builder.Append(sorted[tokenIndex].Value);
                tokenIndex++;
                continue;
            }

            var isSub = subs[tokenIndex];
            var runEnd = tokenIndex;
            while (runEnd + 1 < sorted.Count &&
                ((isSub && subs[runEnd + 1]) || (!isSub && sups[runEnd + 1])))
            {
                runEnd++;
            }

            var runText = string.Concat(
                Enumerable.Range(tokenIndex, runEnd - tokenIndex + 1)
                    .Select(i => sorted[i].Value));

            if (!MathPatterns.IsMathToken(runText))
            {
                builder.Append(runText);
            }
            else
            {
                var signal = isSub ? '_' : '^';
                builder.Append(runText.Length == 1
                    ? $"{signal}{runText}"
                    : $"{signal}{{{runText}}}");
            }

            tokenIndex = runEnd + 1;
        }

        return builder.ToString();
    }

    private static bool TryBuildSimpleIndexedTokenText(
        IReadOnlyList<LetterGeometry> letters,
        IReadOnlyList<bool> subs,
        IReadOnlyList<bool> sups,
        out string text)
    {
        text = string.Empty;
        if (letters.Count < 2 || sups.Any(isSup => isSup))
        {
            return false;
        }

        if (letters[0].Value.Length != 1 || !char.IsLetter(letters[0].Value[0]))
        {
            return false;
        }

        for (var index = 1; index < letters.Count; index++)
        {
            if (!subs[index] || letters[index].Value.Length != 1 || !char.IsLetterOrDigit(letters[index].Value[0]))
            {
                return false;
            }
        }

        text = string.Concat(letters.Select(letter => letter.Value));
        return true;
    }

    private static string NormalizeReconstructedSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = SpaceBeforeTrailingPunctuationRegex.Replace(text, "$1");
        normalized = SpaceAfterLeadingBracketRegex.Replace(normalized, "$1");
        normalized = CommaWithoutFollowingSpaceRegex.Replace(normalized, ", ");
        normalized = SpaceBeforeClosingQuoteRegex.Replace(normalized, "$1");
        normalized = SpaceAfterOpeningQuoteRegex.Replace(normalized, "$1");
        normalized = CollapseWhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static List<string> MergeContinuationLineTexts(
        IReadOnlyList<ReconstructedLetterLine> lines,
        IReadOnlyList<string> lineTexts)
    {
        var merged = new List<string>();
        for (var index = 0; index < lineTexts.Count; index++)
        {
            var text = lineTexts[index].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (merged.Count > 0 &&
                ShouldAppendToPreviousLine(
                    merged[^1],
                    text,
                    lines[index - 1],
                    lines[index]))
            {
                merged[^1] = AppendContinuationText(merged[^1], text);
                continue;
            }

            merged.Add(text);
        }

        return merged;
    }

    private static bool ShouldAppendToPreviousLine(
        string previousText,
        string currentText,
        ReconstructedLetterLine previousLine,
        ReconstructedLetterLine currentLine)
    {
        var verticalGap = Math.Abs(previousLine.Bottom - currentLine.Top);
        var maxGap = Math.Max(6d, previousLine.MedianPointSize * 0.85d);
        if (verticalGap > maxGap)
        {
            return false;
        }

        return LooksLikeFormulaContinuationText(currentText) || PreviousLineLikelyExpectsFormulaTail(previousText);
    }

    private static string AppendContinuationText(string previousText, string continuationText)
    {
        var trimmedPrevious = previousText.TrimEnd();
        var trimmedContinuation = continuationText.Trim();
        if (trimmedPrevious.Length == 0)
        {
            return trimmedContinuation;
        }

        if (trimmedContinuation.Length == 0)
        {
            return trimmedPrevious;
        }

        if (",.;:)]}".Contains(trimmedContinuation[0]) ||
            trimmedContinuation.StartsWith("_", StringComparison.Ordinal) ||
            trimmedContinuation.StartsWith("^", StringComparison.Ordinal))
        {
            return $"{trimmedPrevious}{trimmedContinuation}";
        }

        if ((char.IsLetterOrDigit(trimmedContinuation[0]) || trimmedContinuation[0] == '(') &&
            (char.IsLetterOrDigit(trimmedPrevious[^1]) || trimmedPrevious[^1] is '(' or '_' or '^'))
        {
            return $"{trimmedPrevious}{trimmedContinuation}";
        }

        return $"{trimmedPrevious} {trimmedContinuation}";
    }

    private sealed class ReconstructedLetterLine
    {
        private double _baselineY;
        private double _medianPointSize;
        private bool _mediansDirty;

        public ReconstructedLetterLine(LetterGeometry letter)
        {
            Letters.Add(letter);
            Top = letter.Top;
            Bottom = letter.Bottom;
            Left = letter.Left;
            Right = letter.Right;
            _mediansDirty = true;
        }

        public List<LetterGeometry> Letters { get; } = [];
        public double Top { get; private set; }
        public double Bottom { get; private set; }
        public double Left { get; private set; }
        public double Right { get; private set; }

        public double BaselineY
        {
            get
            {
                EnsureMedians();
                return _baselineY;
            }
        }

        public double MedianPointSize
        {
            get
            {
                EnsureMedians();
                return _medianPointSize;
            }
        }

        public void Add(LetterGeometry letter)
        {
            Letters.Add(letter);
            if (letter.Top > Top) Top = letter.Top;
            if (letter.Bottom < Bottom) Bottom = letter.Bottom;
            if (letter.Left < Left) Left = letter.Left;
            if (letter.Right > Right) Right = letter.Right;
            _mediansDirty = true;
        }

        private void EnsureMedians()
        {
            if (!_mediansDirty)
            {
                return;
            }

            var baselines = Letters
                .Select(letter => letter.BaselineY)
                .OrderBy(y => y)
                .ToList();
            _baselineY = baselines[baselines.Count / 2];

            var pointSizes = Letters
                .Select(letter => letter.PointSize > 0 ? letter.PointSize : letter.Height)
                .Where(size => size > 0)
                .OrderBy(size => size)
                .ToList();
            _medianPointSize = pointSizes.Count > 0 ? pointSizes[pointSizes.Count / 2] : 10d;
            _mediansDirty = false;
        }
    }
}
