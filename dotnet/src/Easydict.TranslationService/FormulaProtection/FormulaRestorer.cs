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
    {
        if (string.IsNullOrWhiteSpace(text) || tokens.Count == 0)
            return text;

        var presentIndices = new HashSet<int>();
        foreach (Match m in NumericPlaceholderRegex.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 0 && idx < tokens.Count)
                presentIndices.Add(idx);
        }

        // All placeholders present → full restore with validation
        if (presentIndices.Count == tokens.Count)
            return RestoreFull(text, tokens, originalText, useSimplified);

        // No placeholders or fewer than half → full fallback to original
        if (presentIndices.Count == 0 || presentIndices.Count * 2 < tokens.Count)
            return originalText;

        // Partial placeholders present (≥50%) → best-effort restore
        return RestorePartial(text, tokens, originalText, useSimplified);
    }

    /// <summary>
    /// Full restore path: all placeholders present, apply strict validation.
    /// </summary>
    private static string RestoreFull(
        string text,
        IReadOnlyList<FormulaToken> tokens,
        string originalText,
        bool useSimplified)
    {
        var restored = ReplaceTokens(text, tokens, useSimplified);

        // Fallback: if any placeholder remains unresolved, return original text
        if (NumericPlaceholderRegex.IsMatch(restored))
            return originalText;

        // Fallback: if formula delimiters are now unbalanced, return original text
        if (!AreFormulaDelimitersBalanced(restored))
            return originalText;

        return restored;
    }

    /// <summary>
    /// Partial restore path: some placeholders missing (LLM dropped them).
    /// Replace whatever placeholders are present; missing formulas are simply absent
    /// from the translated text rather than causing a full-block fallback.
    /// </summary>
    private static string RestorePartial(
        string text,
        IReadOnlyList<FormulaToken> tokens,
        string originalText,
        bool useSimplified)
    {
        var restored = ReplaceTokens(text, tokens, useSimplified);

        // If any {v\d+} remains after replacement (e.g. out-of-range index),
        // the text is corrupted — fall back.
        if (NumericPlaceholderRegex.IsMatch(restored))
            return originalText;

        return restored;
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
