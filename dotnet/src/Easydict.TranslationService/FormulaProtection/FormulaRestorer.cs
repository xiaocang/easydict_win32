using System.Linq;
using System.Text.RegularExpressions;

namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Restores formula placeholders in translated text back to their original or simplified form.
/// Replaces RestoreFormulaSpans in LongDocumentTranslationService.
/// </summary>
public sealed class FormulaRestorer
{
    private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Restores formula placeholders in <paramref name="text"/>.
    /// Uses graduated fallback: all present → full restore, ≥50% present → partial restore,
    /// &lt;50% present → fall back to original.
    /// </summary>
    /// <param name="text">Translated text containing {v0}, {v1}, ... placeholders.</param>
    /// <param name="tokens">Ordered token list produced by <see cref="FormulaProtector.Protect"/>.</param>
    /// <param name="originalText">Original (pre-translation) text — used as fallback on restore failure.</param>
    /// <param name="useSimplified">
    /// When <c>true</c> (PDF render path), substitutes <see cref="FormulaToken.Simplified"/>.
    /// When <c>false</c> (translation fidelity path), substitutes <see cref="FormulaToken.Raw"/>.
    /// </param>
    /// <returns>Restored text, or <paramref name="originalText"/> if restoration fails.</returns>
    public string Restore(
        string text,
        IReadOnlyList<FormulaToken> tokens,
        string originalText,
        bool useSimplified = false)
        => RestoreWithDiagnostics(text, tokens, originalText, useSimplified).Text;

    /// <summary>
    /// Restores placeholders and reports diagnostics about any dropped tokens.
    /// Uses the same graduated fallback as <see cref="Restore"/> but returns status + missing indices
    /// so callers can trigger retry-with-softer-protection on partial results.
    /// </summary>
    public FormulaRestoreResult RestoreWithDiagnostics(
        string text,
        IReadOnlyList<FormulaToken> tokens,
        string originalText,
        bool useSimplified = false)
    {
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            // Preserve legacy behavior: empty/whitespace translation or no tokens → return input unchanged.
            return new FormulaRestoreResult(text, FormulaRestoreStatus.FullRestore, 0, Array.Empty<int>());
        }

        var presentIndices = new HashSet<int>();
        foreach (Match m in NumericPlaceholderRegex.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 0 && idx < tokens.Count)
                presentIndices.Add(idx);
        }

        var missingIndices = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!presentIndices.Contains(i)) missingIndices.Add(i);
        }
        var droppedCount = missingIndices.Count;

        // All placeholders present → full restore with validation
        if (presentIndices.Count == tokens.Count)
        {
            var full = ReplaceTokens(text, tokens, useSimplified);
            if (NumericPlaceholderRegex.IsMatch(full) || !AreFormulaDelimitersBalanced(full))
            {
                // Post-restore corruption — treat as full fallback.
                return new FormulaRestoreResult(
                    originalText,
                    FormulaRestoreStatus.FallbackToOriginal,
                    tokens.Count,
                    Enumerable.Range(0, tokens.Count).ToList());
            }
            return new FormulaRestoreResult(full, FormulaRestoreStatus.FullRestore, 0, Array.Empty<int>());
        }

        // No placeholders or fewer than half → full fallback to original
        if (presentIndices.Count == 0 || presentIndices.Count * 2 < tokens.Count)
        {
            return new FormulaRestoreResult(originalText, FormulaRestoreStatus.FallbackToOriginal, droppedCount, missingIndices);
        }

        // Partial placeholders present (≥50%) → best-effort restore
        var partial = ReplaceTokens(text, tokens, useSimplified);
        if (NumericPlaceholderRegex.IsMatch(partial))
        {
            // Corruption (e.g. out-of-range index remaining) → fall back.
            return new FormulaRestoreResult(originalText, FormulaRestoreStatus.FallbackToOriginal, droppedCount, missingIndices);
        }
        return new FormulaRestoreResult(partial, FormulaRestoreStatus.PartialRestore, droppedCount, missingIndices);
    }

    /// <summary>
    /// Replaces all valid {vN} placeholders with their token values.
    /// </summary>
    private static string ReplaceTokens(
        string text,
        IReadOnlyList<FormulaToken> tokens,
        bool useSimplified)
    {
        return NumericPlaceholderRegex.Replace(text, match =>
        {
            var indexStr = match.Groups[1].Value;
            if (int.TryParse(indexStr, out var index) && index >= 0 && index < tokens.Count)
            {
                return useSimplified ? tokens[index].Simplified : tokens[index].Raw;
            }
            return match.Value;
        });
    }

    private static bool AreFormulaDelimitersBalanced(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var stack = new Stack<char>();
        var dollarCount = 0;

        foreach (var c in text)
        {
            switch (c)
            {
                case '$':
                    dollarCount++;
                    break;
                case '(':
                case '[':
                case '{':
                    stack.Push(c);
                    break;
                case ')':
                    if (stack.Count == 0 || stack.Pop() != '(') return false;
                    break;
                case ']':
                    if (stack.Count == 0 || stack.Pop() != '[') return false;
                    break;
                case '}':
                    if (stack.Count == 0 || stack.Pop() != '{') return false;
                    break;
            }
        }

        return stack.Count == 0 && dollarCount % 2 == 0;
    }
}
