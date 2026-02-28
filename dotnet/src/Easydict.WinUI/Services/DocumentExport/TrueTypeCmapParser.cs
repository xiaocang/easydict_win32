// Minimal OpenType/TrueType cmap Format 4 parser.
// Reproduces PyMuPDF's Font.has_glyph(unicode) — returns the glyph index (GID)
// for a Unicode code point by reading the font file's 'cmap' table directly.
// Used by MuPdfExportService to encode characters correctly for Identity-H fonts.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Parses the 'cmap' table of an OpenType/TrueType font file to build a
/// Unicode → GID (glyph index) lookup dictionary.
/// <para>
/// When MuPDF embeds a font via <c>InsertFont</c> it uses Identity-H encoding:
/// the character code written in a <c>[&lt;XXXX&gt;] TJ</c> operator must be the
/// glyph index (GID), not the Unicode code point.  This parser extracts those
/// mappings from the font's Format 4 cmap subtable, covering the full BMP
/// (U+0000–U+FFFF), which includes all CJK Unified Ideographs.
/// </para>
/// </summary>
internal static class TrueTypeCmapParser
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<char, ushort>?> _cache = new();

    /// <summary>
    /// Loads the Unicode→GID map for a font file (cached by path).
    /// Returns <see langword="null"/> if the font cannot be parsed.
    /// </summary>
    public static IReadOnlyDictionary<char, ushort>? LoadGlyphMap(string fontPath)
    {
        return _cache.GetOrAdd(fontPath, static path =>
        {
            try
            {
                var data = File.ReadAllBytes(path);
                return ParseCmapFromBytes(data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrueTypeCmapParser] Failed to parse cmap for '{path}': {ex.Message}");
                return null;
            }
        });
    }

    // Internal for unit testing.
    internal static IReadOnlyDictionary<char, ushort>? ParseCmapFromBytes(byte[] data)
    {
        if (data.Length < 12)
            return null;

        // --- 1. Locate the 'cmap' table in the Offset Table + Table Records ---
        var numTables = ReadUInt16(data, 4);
        var cmapTableOffset = -1;

        for (var i = 0; i < numTables; i++)
        {
            var recBase = 12 + i * 16;
            if (recBase + 16 > data.Length) break;

            // Table tag is 4 ASCII bytes
            if (data[recBase] == 'c' && data[recBase + 1] == 'm' &&
                data[recBase + 2] == 'a' && data[recBase + 3] == 'p')
            {
                cmapTableOffset = (int)ReadUInt32(data, recBase + 8);
                break;
            }
        }

        if (cmapTableOffset < 0 || cmapTableOffset + 4 > data.Length)
            return null;

        // --- 2. Scan EncodingRecords for a Format 4 Unicode BMP subtable ---
        // Preference: Platform 3, Encoding 1 (Windows Unicode BMP)
        // Fallback:   Platform 0, Encoding 3 or 4 (Unicode)
        var cmapNumEncodings = ReadUInt16(data, cmapTableOffset + 2);
        var bestSubtableOffset = -1;
        var bestPriority = -1;

        for (var i = 0; i < cmapNumEncodings; i++)
        {
            var recBase = cmapTableOffset + 4 + i * 8;
            if (recBase + 8 > data.Length) break;

            var platformId = ReadUInt16(data, recBase);
            var encodingId = ReadUInt16(data, recBase + 2);
            var subtableRelOffset = (int)ReadUInt32(data, recBase + 4);
            var subtableAbs = cmapTableOffset + subtableRelOffset;

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
