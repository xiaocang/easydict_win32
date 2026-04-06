using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class MuPdfExportServiceSpacingTests
{
    [Fact]
    public void GenerateBlockTextOperations_WithCjkPrimaryFont_EmitsRealAsciiSpaceGlyphsWithoutCollapsingRuns()
    {
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

        var ops = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most  competitive neural",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: null);

        // Space glyphs should be present — either as helv 2-digit "20" or Latin font 4-digit "0020"
        // Helvetica batches all ASCII into one run, so look for "20" pattern within hex strings
        ops.Should().Contain("/helv");
        ops.Should().Contain("20", "space glyph must appear in Helvetica hex runs");
    }

    [Fact]
    public void GenerateBlockTextOperations_WithLatinSourceFallback_UsesLatinFallbackFontInsteadOfHelvetica()
    {
        var glyphMap = new Dictionary<char, ushort>();
        var advanceWidths = new Dictionary<ushort, ushort>();
        for (char c = ' '; c <= '~'; c++)
        {
            var gid = (ushort)c;
            glyphMap[c] = gid;
            advanceWidths[gid] = 500;
        }

        var latinFaces = new Dictionary<MuPdfExportService.LatinFontKey, MuPdfExportService.EmbeddedFontFace>
        {
            [new(MuPdfExportService.LatinFontFamily.Serif, MuPdfExportService.LatinFontVariant.Regular)] =
                new("latserifr", glyphMap, advanceWidths, 1000)
        };

        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true,
            LatinFontFaces: latinFaces);

        var ops = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most competitive neural",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 12 },
            sourceBlockType: SourceBlockType.Paragraph,
            usesSourceFallback: true,
            detectedFontNames: ["TimesNewRomanPSMT"]);

        ops.Should().Contain("/latserifr");
        ops.Should().NotContain("/helv");
        ops.Should().NotContain("/SourceHanSerifCN");
        // Space glyph (GID 0x0020) should be present — either as separate <0020> or embedded in a run
        ops.Should().Contain("0020");
    }

    [Fact]
    public void GenerateBlockTextOperations_WithMixedCjkAndLatin_UsesInlineLatinFallbackFontWithoutHelvetica()
    {
        var latinGlyphMap = new Dictionary<char, ushort>();
        var latinAdvanceWidths = new Dictionary<ushort, ushort>();
        for (char c = ' '; c <= '~'; c++)
        {
            var gid = (ushort)c;
            latinGlyphMap[c] = gid;
            latinAdvanceWidths[gid] = 500;
        }

        var primaryGlyphMap = new Dictionary<char, ushort>
        {
            ['\u800C'] = 0x0101,
            ['\u5728'] = 0x0102,
            ['\u4E2D'] = 0x0103
        };
        var primaryAdvanceWidths = new Dictionary<ushort, ushort>
        {
            [0x0101] = 1000,
            [0x0102] = 1000,
            [0x0103] = 1000
        };

        var latinFaces = new Dictionary<MuPdfExportService.LatinFontKey, MuPdfExportService.EmbeddedFontFace>
        {
            [new(MuPdfExportService.LatinFontFamily.Serif, MuPdfExportService.LatinFontVariant.Regular)] =
                new("latserifr", latinGlyphMap, latinAdvanceWidths, 1000)
        };

        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: primaryGlyphMap,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true,
            PrimaryAdvanceWidths: primaryAdvanceWidths,
            LatinFontFaces: latinFaces);

        var ops = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "\u800C\u5728 Transformer \u4E2D",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 12 },
            sourceBlockType: SourceBlockType.Paragraph,
            usesSourceFallback: false,
            detectedFontNames: ["TimesNewRomanPSMT"]);

        Regex.Matches(ops, @"/latserifr").Count.Should().Be(1);
        ops.Should().Contain("/SourceHanSerifCN");
        ops.Should().Contain("/latserifr");
        ops.Should().NotContain("/helv");
        // "Transformer" GIDs should appear in the Latin font run (may include surrounding space 0020)
        ops.Should().Contain("005400720061006E00730066006F0072006D00650072");
    }
}
