// Character-level paragraph builder for pdf2zh-aligned PDF translation pipeline.
// Processes CharInfo data from ContentStreamInterpreter to build paragraphs
// with inline formula grouping, matching pdf2zh's receive_layout() Section A.

using System.Text.RegularExpressions;

namespace Easydict.WinUI.Services;

/// <summary>
/// A text paragraph built from consecutive characters in the same layout region.
/// Corresponds to pdf2zh's sstk[] (text stack) entries.
/// </summary>
public sealed class CharParagraph
{
    /// <summary>Concatenated text of all non-formula characters in this paragraph.</summary>
    public string Text { get; set; } = "";

    /// <summary>Layout region class ID (from the layout mask or ML detection).</summary>
    public int LayoutClass { get; set; }

    /// <summary>The characters belonging to this paragraph (text characters only).</summary>
    public List<CharInfo> Characters { get; } = new();

    /// <summary>
    /// Formula variable groups embedded within this paragraph.
    /// Each group corresponds to a {v*} placeholder in the protected text.
    /// The key is the variable index (0, 1, 2, ...).
    /// </summary>
    public Dictionary<int, FormulaVariableGroup> FormulaVariables { get; } = new();

    /// <summary>
    /// Text with formula spans replaced by {v0}, {v1}, ... placeholders.
    /// Set during paragraph construction in Build().
    /// </summary>
    public string ProtectedText { get; set; } = "";

    /// <summary>Left boundary of the paragraph (minimum X0 of all characters).</summary>
    public double X0 { get; set; } = double.MaxValue;

    /// <summary>Bottom boundary of the paragraph (minimum Y0 of all characters).</summary>
    public double Y0 { get; set; } = double.MaxValue;

    /// <summary>Right boundary of the paragraph (maximum X1 of all characters).</summary>
    public double X1 { get; set; } = double.MinValue;

    /// <summary>Top boundary of the paragraph (maximum Y1 of all characters).</summary>
    public double Y1 { get; set; } = double.MinValue;

    /// <summary>Font size of the most recently added parent (non-subscript) character.</summary>
    public double ParentFontSize { get; set; }

    public void UpdateBounds(CharInfo ch)
    {
        X0 = Math.Min(X0, ch.X0);
        Y0 = Math.Min(Y0, ch.Y0);
        X1 = Math.Max(X1, ch.X1);
        Y1 = Math.Max(Y1, ch.Y1);
    }
}

/// <summary>
/// A group of characters forming a formula variable (corresponds to pdf2zh's vstk[] entries).
/// These characters are preserved with their original font and position for exact rendering.
/// </summary>
public sealed class FormulaVariableGroup
{
    /// <summary>The variable index (used in {v*} placeholder).</summary>
    public int Index { get; set; }

    /// <summary>Characters in this formula group, in order.</summary>
    public List<CharInfo> Characters { get; } = new();

    /// <summary>Left boundary of the group.</summary>
    public double X0 => Characters.Count > 0 ? Characters.Min(c => c.X0) : 0;

    /// <summary>Bottom boundary of the group.</summary>
    public double Y0 => Characters.Count > 0 ? Characters.Min(c => c.Y0) : 0;

    /// <summary>Right boundary of the group.</summary>
    public double X1 => Characters.Count > 0 ? Characters.Max(c => c.X1) : 0;

    /// <summary>Top boundary of the group.</summary>
    public double Y1 => Characters.Count > 0 ? Characters.Max(c => c.Y1) : 0;
}

/// <summary>
/// Result of character-level paragraph building for a single page.
/// </summary>
public sealed class CharParagraphResult
{
    /// <summary>Text paragraphs with embedded formula variables.</summary>
    public required IReadOnlyList<CharParagraph> Paragraphs { get; init; }

    /// <summary>All formula variable groups across all paragraphs.</summary>
    public required IReadOnlyList<FormulaVariableGroup> AllFormulaGroups { get; init; }

    /// <summary>Total number of characters processed.</summary>
    public int TotalCharacters { get; init; }

    /// <summary>Number of characters classified as formula.</summary>
    public int FormulaCharacters { get; init; }
}

/// <summary>
/// Builds paragraphs from character-level data, with inline formula detection and grouping.
/// This is the C# equivalent of pdf2zh's converter.py receive_layout() Section A.
///
/// The algorithm processes characters one by one and decides whether each character
/// belongs to text (sstk) or formula (vstk), based on:
/// 1. Layout mask classification (cls == 0 means excluded region)
/// 2. Font-based formula detection (math fonts like CMSY, CMMI, etc.)
/// 3. Unicode-based formula detection (math symbols, Greek letters)
/// 4. Subscript detection (character size &lt; 0.79 × parent size)
/// 5. Vertical text matrix detection (matrix[0]==0 &amp;&amp; matrix[3]==0)
/// 6. Unicode replacement character detection (U+FFFD, unmapped CID)
/// </summary>
public static class CharacterParagraphBuilder
{
    // Reuse the same math font regex from LongDocumentTranslationService
    private static readonly Regex MathFontRegex = new(
        @"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX" +
        @"|BL|RM|EU|LA|RS" +
        @"|LINE|LCIRCLE" +
        @"|TeX-|rsfs|txsy|wasy|stmary" +
        @"|\w+Sym\w*|\w+Math\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Math Unicode characters for character-level formula detection
    private static readonly Regex MathUnicodeRegex = new(
        @"[\u2200-\u22FF\u2100-\u214F\u0370-\u03FF\u2070-\u209F\u00B2\u00B3\u00B9" +
        @"\u2150-\u218F\u27C0-\u27EF\u2980-\u29FF" +
        @"\u02B0-\u02FF\u0300-\u036F\u02C6-\u02CF\u2000-\u200B]",
        RegexOptions.Compiled);

    /// <summary>
    /// Subscript/superscript size ratio threshold.
    /// Aligned with pdf2zh converter.py:243: child.size &lt; pstk[-1].size * 0.79
    /// </summary>
    private const double SubscriptSizeRatio = 0.79;

    /// <summary>
    /// Builds paragraphs from character-level data with a layout classification callback.
    /// </summary>
    /// <param name="characters">Character data from ContentStreamInterpreter.</param>
    /// <param name="classifyCharacter">
    /// Callback that returns the layout class for a character given its page coordinates.
    /// Returns 0 for excluded regions (figure, table, isolated formula),
    /// or a positive value for translatable regions. Returns -1 if no layout info available.
    /// This corresponds to pdf2zh's layout[cy, cx] pixel lookup.
    /// </param>
    /// <returns>Paragraph building result.</returns>
    public static CharParagraphResult Build(
        IReadOnlyList<CharInfo> characters,
        Func<double, double, int>? classifyCharacter = null)
    {
        if (characters.Count == 0)
        {
            return new CharParagraphResult
            {
                Paragraphs = Array.Empty<CharParagraph>(),
                AllFormulaGroups = Array.Empty<FormulaVariableGroup>(),
                TotalCharacters = 0,
                FormulaCharacters = 0,
            };
        }

        var paragraphs = new List<CharParagraph>();
        var allFormulaGroups = new List<FormulaVariableGroup>();
        var currentParagraph = new CharParagraph();
        var currentFormulaGroup = (FormulaVariableGroup?)null;
        var formulaGroupIndex = 0;
        var totalFormulaChars = 0;

        // State tracking (matches pdf2zh's xt_cls, pstk, vbkt)
        var previousLayoutClass = -1;
        var bracketDepth = 0;  // vbkt in pdf2zh: tracks nested brackets in formula mode
        var inFormulaMode = false;

        for (var i = 0; i < characters.Count; i++)
        {
            var ch = characters[i];

            // Step 1: Get layout classification
            var cls = classifyCharacter?.Invoke(
                (ch.X0 + ch.X1) / 2.0,  // center X
                (ch.Y0 + ch.Y1) / 2.0   // center Y
            ) ?? 1; // Default to translatable if no classifier

            // Step 2: Detect if this character is formula-like (vflag in pdf2zh)
            var isFormula = IsFormulaCharacter(ch, currentParagraph.ParentFontSize, cls);

            // Step 3: Track bracket depth for formula continuation
            if (inFormulaMode || isFormula)
            {
                var bracketDelta = GetBracketDelta(ch.Text);
                bracketDepth += bracketDelta;
                if (bracketDepth < 0) bracketDepth = 0;

                // If we're in formula mode with open brackets, stay in formula mode
                if (inFormulaMode && bracketDepth > 0 && !isFormula)
                {
                    isFormula = true; // Content inside formula brackets stays as formula
                }
            }

            // Step 4: Detect paragraph boundary (layout class change)
            var isNewParagraph = false;
            if (previousLayoutClass >= 0 && cls != previousLayoutClass && cls > 0 && previousLayoutClass > 0)
            {
                isNewParagraph = true;
            }

            if (isNewParagraph)
            {
                // Finalize current formula group if any
                if (currentFormulaGroup is { Characters.Count: > 0 })
                {
                    currentParagraph.FormulaVariables[currentFormulaGroup.Index] = currentFormulaGroup;
                    allFormulaGroups.Add(currentFormulaGroup);
                    currentParagraph.Text += $"{{v{currentFormulaGroup.Index}}}";
                    currentFormulaGroup = null;
                    inFormulaMode = false;
                    bracketDepth = 0;
                }

                // Save current paragraph and start new one
                if (currentParagraph.Characters.Count > 0 || currentParagraph.FormulaVariables.Count > 0)
                {
                    currentParagraph.ProtectedText = currentParagraph.Text;
                    paragraphs.Add(currentParagraph);
                }
                currentParagraph = new CharParagraph();
                formulaGroupIndex = 0;
            }

            currentParagraph.LayoutClass = cls;

            // Step 5: Handle excluded regions (cls == 0)
            if (cls == 0)
            {
                // Character in excluded region — treat as formula/skip
                if (currentFormulaGroup is null)
                {
                    currentFormulaGroup = new FormulaVariableGroup { Index = formulaGroupIndex++ };
                }
                currentFormulaGroup.Characters.Add(ch);
                totalFormulaChars++;
                inFormulaMode = true;
                previousLayoutClass = cls;
                continue;
            }

            // Step 6: Route character to text or formula
            if (isFormula)
            {
                // Start new formula group if not already in one
                if (currentFormulaGroup is null)
                {
                    currentFormulaGroup = new FormulaVariableGroup { Index = formulaGroupIndex++ };
                }
                currentFormulaGroup.Characters.Add(ch);
                totalFormulaChars++;
                inFormulaMode = true;
            }
            else
            {
                // Finalize any pending formula group before adding text
                if (currentFormulaGroup is { Characters.Count: > 0 })
                {
                    currentParagraph.FormulaVariables[currentFormulaGroup.Index] = currentFormulaGroup;
                    allFormulaGroups.Add(currentFormulaGroup);
                    currentParagraph.Text += $"{{v{currentFormulaGroup.Index}}}";
                    currentFormulaGroup = null;
                    inFormulaMode = false;
                    bracketDepth = 0;
                }

                // Add text character to paragraph
                currentParagraph.Characters.Add(ch);
                currentParagraph.Text += ch.Text;
                currentParagraph.UpdateBounds(ch);
                currentParagraph.ParentFontSize = ch.PointSize;
            }

            previousLayoutClass = cls;
        }

        // Finalize last formula group
        if (currentFormulaGroup is { Characters.Count: > 0 })
        {
            currentParagraph.FormulaVariables[currentFormulaGroup.Index] = currentFormulaGroup;
            allFormulaGroups.Add(currentFormulaGroup);
            currentParagraph.Text += $"{{v{currentFormulaGroup.Index}}}";
        }

        // Save last paragraph
        if (currentParagraph.Characters.Count > 0 || currentParagraph.FormulaVariables.Count > 0)
        {
            currentParagraph.ProtectedText = currentParagraph.Text;
            paragraphs.Add(currentParagraph);
        }

        return new CharParagraphResult
        {
            Paragraphs = paragraphs,
            AllFormulaGroups = allFormulaGroups,
            TotalCharacters = characters.Count,
            FormulaCharacters = totalFormulaChars,
        };
    }

    /// <summary>
    /// Determines whether a character is formula-like, matching pdf2zh's vflag() logic.
    /// A character is classified as formula if any of these conditions is true:
    /// 1. It's in an excluded layout region (cls == 0)
    /// 2. It's a subscript/superscript (size &lt; 0.79 × parent size)
    /// 3. Its font matches the math font regex
    /// 4. Its text matches the math Unicode regex
    /// 5. Its text matrix indicates vertical text (matrix[0]==0 &amp;&amp; matrix[3]==0)
    /// 6. Its text contains the Unicode replacement character (U+FFFD, unmapped CID)
    /// </summary>
    internal static bool IsFormulaCharacter(CharInfo ch, double parentFontSize, int layoutClass)
    {
        // Condition 1: Excluded region
        if (layoutClass == 0)
            return true;

        // Condition 2: Subscript/superscript detection
        // Aligned with pdf2zh converter.py:243: child.size < pstk[-1].size * 0.79
        if (parentFontSize > 0 && ch.PointSize < parentFontSize * SubscriptSizeRatio)
        {
            // Only flag as formula if the parent paragraph already has some text
            // (standalone small text might just be a footnote, not a subscript)
            return true;
        }

        // Condition 3: Math font detection
        var fontName = ch.FontResourceName;
        var plusIdx = fontName.IndexOf('+');
        if (plusIdx >= 0 && plusIdx < fontName.Length - 1)
            fontName = fontName[(plusIdx + 1)..];
        if (MathFontRegex.IsMatch(fontName))
            return true;

        // Condition 4: Math Unicode character detection
        if (ch.Text.Length > 0 && MathUnicodeRegex.IsMatch(ch.Text))
            return true;

        // Condition 5: Vertical text matrix detection
        // pdf2zh converter.py:245: child.matrix[0] == 0 and child.matrix[3] == 0
        var tm = ch.TextMatrix;
        if (tm.A == 0 && tm.D == 0)
            return true;

        // Condition 6: Unicode replacement character (unmapped CID glyph)
        if (ch.Text.Contains('\uFFFD'))
            return true;

        return false;
    }

    /// <summary>
    /// Returns the bracket depth change for a character.
    /// Opening brackets (parentheses, square brackets, curly braces) return +1.
    /// Closing brackets return -1.
    /// Matches pdf2zh's vbkt tracking in converter.py.
    /// </summary>
    internal static int GetBracketDelta(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var delta = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '(' or '[' or '{':
                    delta++;
                    break;
                case ')' or ']' or '}':
                    delta--;
                    break;
            }
        }
        return delta;
    }
}
