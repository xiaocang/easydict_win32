// Minimal OpenType/TrueType cmap Format 4 + hmtx parser.
// Reproduces PyMuPDF's Font.has_glyph(unicode) — returns the glyph index (GID)
// for a Unicode code point by reading the font file's 'cmap' table directly.
// Also parses 'hmtx' for per-glyph advance widths, enabling accurate character
// spacing in the PDF content stream (avoids the uniform 0.55 em fallback that
// letter-spaces narrow glyphs like 'l', 'i', '.').
// Used by MuPdfExportService to encode characters correctly for Identity-H fonts.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Font metric data extracted from an OpenType/TrueType font file.
/// </summary>
internal sealed record FontMetrics(
    IReadOnlyDictionary<char, ushort> GlyphMap,         // Unicode → GID
    IReadOnlyDictionary<ushort, ushort> AdvanceWidths,  // GID → advance width in font design units
    ushort UnitsPerEm                                    // denominator: em_fraction = advance / UnitsPerEm
);

/// <summary>
/// Parses the 'cmap' and 'hmtx' tables of an OpenType/TrueType font file to build
/// Unicode → GID (glyph index) and GID → advance width lookup dictionaries.
/// <para>
/// When MuPDF embeds a font via <c>InsertFont</c> it uses Identity-H encoding:
/// the character code written in a <c>[&lt;XXXX&gt;] TJ</c> operator must be the
/// glyph index (GID), not the Unicode code point.  This parser extracts those
/// mappings from the font's Format 4 cmap subtable, covering the full BMP
/// (U+0000–U+FFFF), which includes all CJK Unified Ideographs.
/// </para>
/// <para>
/// Advance widths from the 'hmtx' table are used for per-glyph spacing in the
/// PDF content stream, avoiding the uniform 0.55 em fallback that causes
/// letter-spaced rendering of narrow glyphs (l, i, ., etc.).
/// </para>
/// </summary>
internal static class TrueTypeCmapParser
{
    private static readonly ConcurrentDictionary<string, FontMetrics?> _metricsCache = new();

    /// <summary>
    /// Loads full font metrics (glyph map + advance widths) for a font file (cached by path).
    /// Returns <see langword="null"/> if the font cannot be parsed.
    /// </summary>
    public static FontMetrics? LoadFontMetrics(string fontPath)
    {
        return _metricsCache.GetOrAdd(fontPath, static path =>
        {
            try
            {
                var data = File.ReadAllBytes(path);
                return ParseFontMetricsFromBytes(data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrueTypeCmapParser] Failed to parse metrics for '{path}': {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// Loads the Unicode→GID map for a font file (cached by path).
    /// Returns <see langword="null"/> if the font cannot be parsed.
    /// </summary>
    public static IReadOnlyDictionary<char, ushort>? LoadGlyphMap(string fontPath)
        => LoadFontMetrics(fontPath)?.GlyphMap;

    // Internal for unit testing.
    internal static IReadOnlyDictionary<char, ushort>? ParseCmapFromBytes(byte[] data)
        => ParseFontMetricsFromBytes(data)?.GlyphMap;

    // Internal for unit testing.
    internal static FontMetrics? ParseFontMetricsFromBytes(byte[] data)
    {
        if (data.Length < 12) return null;

        var numTables = ReadUInt16(data, 4);

        // --- Locate required tables ---
        var cmapOffset = -1;
        var headOffset = -1;
        var hheaOffset = -1;
        var hmtxOffset = -1;

        for (var i = 0; i < numTables; i++)
        {
            var r = 12 + i * 16;
            if (r + 16 > data.Length) break;
            var t0 = data[r]; var t1 = data[r + 1]; var t2 = data[r + 2]; var t3 = data[r + 3];
            var off = (int)ReadUInt32(data, r + 8);
            if      (t0 == 'c' && t1 == 'm' && t2 == 'a' && t3 == 'p') cmapOffset = off;
            else if (t0 == 'h' && t1 == 'e' && t2 == 'a' && t3 == 'd') headOffset = off;
            else if (t0 == 'h' && t1 == 'h' && t2 == 'e' && t3 == 'a') hheaOffset = off;
            else if (t0 == 'h' && t1 == 'm' && t2 == 't' && t3 == 'x') hmtxOffset = off;
        }

        // cmap is required; others fall back gracefully
        if (cmapOffset < 0) return null;

        var glyphMap = ParseCmapTable(data, cmapOffset);
        if (glyphMap is null) return null;

        // --- head table: unitsPerEm (offset +18, uint16) ---
        ushort unitsPerEm = 1000; // Type1 default
        if (headOffset >= 0 && headOffset + 20 <= data.Length)
            unitsPerEm = ReadUInt16(data, headOffset + 18);

        // --- hhea table: numberOfHMetrics (offset +34, uint16) ---
        ushort numberOfHMetrics = 0;
        if (hheaOffset >= 0 && hheaOffset + 36 <= data.Length)
            numberOfHMetrics = ReadUInt16(data, hheaOffset + 34);

        // --- hmtx table: GID → advance width ---
        var advanceWidths = new Dictionary<ushort, ushort>();
        if (hmtxOffset >= 0 && numberOfHMetrics > 0)
        {
            for (var gid = 0; gid < numberOfHMetrics; gid++)
            {
                var pos = hmtxOffset + gid * 4;
                if (pos + 2 > data.Length) break;
                advanceWidths[(ushort)gid] = ReadUInt16(data, pos); // advanceWidth (uint16)
                // lsb (int16) at pos+2 is not needed
            }
        }

        return new FontMetrics(glyphMap, advanceWidths, unitsPerEm);
    }

    /// <summary>
    /// Scans the cmap table's encoding records for a Format 4 Unicode BMP subtable
    /// and returns the complete Unicode→GID map.
    /// </summary>
    private static IReadOnlyDictionary<char, ushort>? ParseCmapTable(byte[] data, int cmapOffset)
    {
        if (cmapOffset + 4 > data.Length) return null;

        // --- 2. Scan EncodingRecords for a Format 4 Unicode BMP subtable ---
        // Preference: Platform 3, Encoding 1 (Windows Unicode BMP)
        // Fallback:   Platform 0, Encoding 3 or 4 (Unicode)
        var cmapNumEncodings = ReadUInt16(data, cmapOffset + 2);
        var bestSubtableOffset = -1;
        var bestPriority = -1;

        for (var i = 0; i < cmapNumEncodings; i++)
        {
            var recBase = cmapOffset + 4 + i * 8;
            if (recBase + 8 > data.Length) break;

            var platformId = ReadUInt16(data, recBase);
            var encodingId = ReadUInt16(data, recBase + 2);
            var subtableRelOffset = (int)ReadUInt32(data, recBase + 4);
            var subtableAbs = cmapOffset + subtableRelOffset;

            if (subtableAbs + 2 > data.Length) continue;
            if (ReadUInt16(data, subtableAbs) != 4) continue; // only Format 4

            var priority = (platformId == 3 && encodingId == 1) ? 2  // Windows Unicode BMP — best
                         : (platformId == 0 && (encodingId == 3 || encodingId == 4)) ? 1  // Unicode
                         : 0;

            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestSubtableOffset = subtableAbs;
            }
        }

        if (bestSubtableOffset < 0)
            return null;

        return ParseFormat4(data, bestSubtableOffset);
    }

    /// <summary>
    /// Parses a Format 4 cmap subtable and returns the complete Unicode→GID map.
    /// See the OpenType spec §cmap, Format 4 for the algorithm.
    /// </summary>
    private static Dictionary<char, ushort> ParseFormat4(byte[] data, int base0)
    {
        // Format 4 layout (all values big-endian):
        //   +0  format       (= 4)
        //   +2  length
        //   +4  language
        //   +6  segCountX2
        //   +8  searchRange
        //   +10 entrySelector
        //   +12 rangeShift
        //   +14 endCode[segCount]
        //   +14 + segCount*2: reservedPad (= 0)
        //   +16 + segCount*2: startCode[segCount]
        //   +16 + segCount*4: idDelta[segCount]   (signed)
        //   +16 + segCount*6: idRangeOffset[segCount]
        //   +16 + segCount*8: glyphIdArray[]

        var segCount = ReadUInt16(data, base0 + 6) / 2;

        var endCodeBase        = base0 + 14;
        var startCodeBase      = endCodeBase + segCount * 2 + 2; // +2 for reservedPad
        var idDeltaBase        = startCodeBase + segCount * 2;
        var idRangeOffsetBase  = idDeltaBase + segCount * 2;

        var glyphMap = new Dictionary<char, ushort>(capacity: 20_000);

        for (var i = 0; i < segCount; i++)
        {
            var endCode   = ReadUInt16(data, endCodeBase + i * 2);
            if (endCode == 0xFFFF) break; // terminal sentinel segment

            var startCode      = ReadUInt16(data, startCodeBase + i * 2);
            var idDelta        = ReadInt16 (data, idDeltaBase + i * 2);
            var idRangeOffset  = ReadUInt16(data, idRangeOffsetBase + i * 2);

            // Use int to avoid ushort overflow in the inner loop
            for (int code = startCode; code <= endCode; code++)
            {
                ushort gid;

                if (idRangeOffset == 0)
                {
                    gid = (ushort)((code + idDelta) & 0xFFFF);
                }
                else
                {
                    // The spec says: glyphId = *(idRangeOffset[i]/2 + (c - startCode[i]) + &idRangeOffset[i])
                    // In byte terms: the idRangeOffset entry for segment i sits at
                    //   idRangeOffsetBase + i*2
                    // and the value is a byte offset from that position to the glyphId slot.
                    var glyphIdBytePos = idRangeOffsetBase + i * 2 + idRangeOffset + (code - startCode) * 2;
                    if (glyphIdBytePos + 2 > data.Length)
                    {
                        gid = 0;
                    }
                    else
                    {
                        var rawGid = ReadUInt16(data, glyphIdBytePos);
                        gid = rawGid == 0 ? (ushort)0 : (ushort)((rawGid + idDelta) & 0xFFFF);
                    }
                }

                if (gid != 0)
                    glyphMap[(char)code] = gid;
            }
        }

        return glyphMap;
    }

    // Big-endian helpers
    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadInt16(byte[] data, int offset)
        => (short)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
