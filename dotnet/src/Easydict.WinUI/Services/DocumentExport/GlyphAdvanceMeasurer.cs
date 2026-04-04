using Easydict.TextLayout;
using Easydict.TextLayout.Segmentation;
using Easydict.TranslationService.FormulaProtection;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Measures text advance widths using TrueType font metrics (glyph map + hmtx advance widths).
/// Used by MuPdfExportService for TextLayout integration.
/// </summary>
internal sealed class GlyphAdvanceMeasurer : ITextMeasurer
{
    private readonly IReadOnlyDictionary<char, ushort>? _primaryGlyphMap;
    private readonly IReadOnlyDictionary<ushort, ushort>? _primaryAdvanceWidths;
    private readonly ushort _primaryUnitsPerEm;
    private readonly IReadOnlyDictionary<char, ushort>? _notoGlyphMap;
    private readonly IReadOnlyDictionary<ushort, ushort>? _notoAdvanceWidths;
    private readonly ushort _notoUnitsPerEm;
    private readonly bool _primaryFontIsCjk;
    private readonly double _fontSize;

    public GlyphAdvanceMeasurer(
        IReadOnlyDictionary<char, ushort>? primaryGlyphMap,
        IReadOnlyDictionary<ushort, ushort>? primaryAdvanceWidths,
        ushort primaryUnitsPerEm,
        IReadOnlyDictionary<char, ushort>? notoGlyphMap,
        IReadOnlyDictionary<ushort, ushort>? notoAdvanceWidths,
        ushort notoUnitsPerEm,
        bool primaryFontIsCjk,
        double fontSize)
    {
        _primaryGlyphMap = primaryGlyphMap;
        _primaryAdvanceWidths = primaryAdvanceWidths;
        _primaryUnitsPerEm = primaryUnitsPerEm;
        _notoGlyphMap = notoGlyphMap;
        _notoAdvanceWidths = notoAdvanceWidths;
        _notoUnitsPerEm = notoUnitsPerEm;
        _primaryFontIsCjk = primaryFontIsCjk;
        _fontSize = fontSize;
    }

    public double MeasureSegment(string text)
    {
        var total = 0.0;
        foreach (var ch in text)
            total += MeasureChar(ch);
        return total;
    }

    public double MeasureGrapheme(string grapheme)
    {
        if (grapheme.Length == 0) return 0;
        return MeasureChar(grapheme[0]);
    }

    private double MeasureChar(char ch)
    {
        // Super/subscript signals are zero-width rendering signals
        if (LatexFormulaSimplifier.IsScriptSignal(ch))
            return 0;

        // Space: use a fixed 0.3em advance (matching MuPdfExportService behavior)
        if (ch == ' ')
            return _fontSize * 0.3;

        // ASCII routing: when primary font is CJK, ASCII uses half-width advance
        if (_primaryFontIsCjk && ch >= 0x20 && ch <= 0x7E)
        {
            if (_primaryGlyphMap?.TryGetValue(ch, out var gid) == true && gid != 0)
                return _fontSize * GetGlyphAdvanceEm(gid, _primaryAdvanceWidths, _primaryUnitsPerEm, 0.55);

            // Helvetica fallback
            return _fontSize * 0.55;
        }

        // CJK characters are full-width (1 em)
        if (ScriptClassifier.IsCjk(ch))
            return _fontSize;

        // Primary font lookup
        if (_primaryGlyphMap?.TryGetValue(ch, out var primaryGid) == true && primaryGid != 0)
            return _fontSize * GetGlyphAdvanceEm(primaryGid, _primaryAdvanceWidths, _primaryUnitsPerEm, 0.6);

        // Noto font fallback for non-CJK scripts
        if (_notoGlyphMap?.TryGetValue(ch, out var notoGid) == true && notoGid != 0)
            return _fontSize * GetGlyphAdvanceEm(notoGid, _notoAdvanceWidths, _notoUnitsPerEm, 0.6);

        // Final fallback
        return _fontSize * 0.6;
    }

    private static double GetGlyphAdvanceEm(
        ushort gid,
        IReadOnlyDictionary<ushort, ushort>? advanceWidths,
        ushort unitsPerEm,
        double fallbackEm)
    {
        if (advanceWidths is not null
            && advanceWidths.TryGetValue(gid, out var adv)
            && adv > 0
            && unitsPerEm > 0)
            return (double)adv / unitsPerEm;

        return fallbackEm;
    }
}
