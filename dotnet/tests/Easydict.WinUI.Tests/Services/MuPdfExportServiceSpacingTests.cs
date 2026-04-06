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

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most  competitive neural",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: null);
        var ops = result.Operations;

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

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most competitive neural",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 12 },
            sourceBlockType: SourceBlockType.Paragraph,
            usesSourceFallback: true,
            detectedFontNames: ["TimesNewRomanPSMT"]);
        var ops = result.Operations;

        ops.Should().Contain("/latserifr");
        ops.Should().NotContain("/helv");
        ops.Should().NotContain("/SourceHanSerifCN");
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

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "\u800C\u5728 Transformer \u4E2D",
            fontId: "SourceHanSerifCN",
            fontSize: 12,
            bbox: new BlockRect(10, 10, 1000, 40),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 12 },
            sourceBlockType: SourceBlockType.Paragraph,
            usesSourceFallback: false,
            detectedFontNames: ["TimesNewRomanPSMT"]);
        var ops = result.Operations;

        Regex.Matches(ops, @"/latserifr").Count.Should().Be(1);
        ops.Should().Contain("/SourceHanSerifCN");
        ops.Should().Contain("/latserifr");
        ops.Should().NotContain("/helv");
        ops.Should().Contain("005400720061006E00730066006F0072006D00650072");
    }

    [Fact]
    public void PrepareBlockForRendering_WithBaselineDerivedRects_ExtendsBackgroundBeyondRawBoundingBox()
    {
        var block = new MuPdfExportService.TranslatedBlockData
        {
            ChunkIndex = 0,
            PageNumber = 2,
            SourceBlockId = "p2-body-b1",
            SourceText = "line one\nline two\nline three",
            TranslatedText = "first line\nsecond line\nthird line",
            BoundingBox = new BlockRect(100, 100, 220, 22),
            FontSize = 12,
            TranslationSkipped = false,
            TextStyle = new BlockTextStyle
            {
                FontSize = 12,
                LineSpacing = 14,
                LinePositions =
                [
                    new BlockLinePosition(118, 100, 300),
                    new BlockLinePosition(104, 100, 300),
                    new BlockLinePosition(90, 100, 300)
                ]
            },
            SourceBlockType = SourceBlockType.Paragraph,
            UsesSourceFallback = false,
            DetectedFontNames = ["TimesNewRomanPSMT"]
        };

        var prepared = MuPdfExportService.PrepareBlockForRendering(block, 400);

        prepared.BackgroundLineRects.Should().NotBeNull();
        prepared.BackgroundLineRects!.Should().HaveCount(3);
        prepared.BackgroundLineRects!.Min(r => r.Y).Should().BeLessThan(block.BoundingBox!.Value.Y);
    }

    [Fact]
    public void GenerateBlockTextOperations_WithShortLineRectsButEnoughTotalHeight_KeepsOriginalFontSize()
    {
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "\u5FAA\u73AF\u795E\u7ECF\u7F51\u7EDC\u5DF2\u7ECF\u5728\u5E8F\u5217\u5EFA\u6A21\u4EFB\u52A1\u4E2D\u88AB\u8BC1\u660E\u6709\u6548",
            fontId: "SourceHanSerifCN",
            fontSize: 14,
            bbox: new BlockRect(10, 10, 180, 44),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 14, LineSpacing = 16 },
            sourceBlockType: SourceBlockType.Paragraph,
            renderLineRects:
            [
                new BlockRect(10, 38, 180, 10),
                new BlockRect(10, 12, 180, 10)
            ]);

        result.Operations.Should().NotBeNullOrWhiteSpace();
        result.WasShrunk.Should().BeFalse();
        result.WasTruncated.Should().BeFalse();
        result.ChosenFontSize.Should().BeApproximately(14, 0.01);
    }

    [Fact]
    public void GenerateBlockTextOperations_WithSourceFallbackAndShortLineRects_DoesNotShrinkSolelyForLineSlotHeight()
    {
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most competitive neural models remain effective.",
            fontId: "SourceHanSerifCN",
            fontSize: 14,
            bbox: new BlockRect(10, 10, 200, 44),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 14, LineSpacing = 16 },
            sourceBlockType: SourceBlockType.Paragraph,
            usesSourceFallback: true,
            detectedFontNames: ["TimesNewRomanPSMT"],
            renderLineRects:
            [
                new BlockRect(10, 38, 200, 10),
                new BlockRect(10, 12, 200, 10)
            ]);

        result.Operations.Should().NotBeNullOrWhiteSpace();
        result.WasShrunk.Should().BeFalse();
        result.WasTruncated.Should().BeFalse();
        result.ChosenFontSize.Should().BeApproximately(14, 0.01);
    }

    [Fact]
    public void GenerateBlockTextOperations_WithConstrainedLineRects_ShrinksAndTruncatesInsteadOfOverflowing()
    {
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: false);

        var result = MuPdfExportService.GenerateBlockTextOperations(
            translatedText: "Most competitive neural sequence transduction models have an encoder decoder structure",
            fontId: "Arial",
            fontSize: 14,
            bbox: new BlockRect(10, 10, 55, 26),
            fonts: fonts,
            textStyle: new BlockTextStyle { FontSize = 14, LineSpacing = 16 },
            sourceBlockType: SourceBlockType.Paragraph,
            renderLineRects:
            [
                new BlockRect(10, 24, 55, 10),
                new BlockRect(10, 12, 55, 10)
            ]);

        result.Operations.Should().NotBeNullOrWhiteSpace();
        result.WasShrunk.Should().BeTrue();
        result.WasTruncated.Should().BeTrue();
        result.LinesRendered.Should().Be(2);
    }
}
