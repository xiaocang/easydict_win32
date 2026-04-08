using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
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
    public void PreservedFormulaBlock_UsesOriginalPdfTextInsteadOfRedraw()
    {
        var block = new MuPdfExportService.TranslatedBlockData
        {
            ChunkIndex = 0,
            PageNumber = 2,
            SourceBlockId = "p2-body-formula",
            SourceText = "Attention(Q, K, V) = softmax(QK^T)V",
            TranslatedText = "Attention(Q, K, V) = softmax(QK^T)V",
            BoundingBox = new BlockRect(100, 120, 320, 32),
            FontSize = 14,
            TranslationSkipped = true,
            RenderFromSourceText = false,
            SkipErase = false,
            PreserveOriginalTextInPdfExport = true,
            TextStyle = new BlockTextStyle
            {
                FontSize = 14,
                LineSpacing = 16,
                Alignment = TextAlignment.Center
            },
            SourceBlockType = SourceBlockType.Formula,
            UsesSourceFallback = false,
            DetectedFontNames = ["TimesNewRomanPSMT", "CMMI10"]
        };

        MuPdfExportService.ShouldRenderBlockText(block).Should().BeFalse();
        MuPdfExportService.ShouldEraseBlockBackground(block).Should().BeFalse();
        block.PreserveOriginalTextInPdfExport.Should().BeTrue();
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

    [Fact]
    public void ShouldUseUnifiedRetryLayout_ReturnsTrueForRetryOrFallbackPagesOnly()
    {
        MuPdfExportService.TranslatedBlockData[] normalPageBlocks =
        [
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 0,
                PageNumber = 1,
                SourceBlockId = "p1-body-b1",
                OrderInPage = 0,
                ReadingOrderScore = 1,
                SourceText = "source",
                TranslatedText = "translated",
                BoundingBox = new BlockRect(10, 10, 100, 20),
                FontSize = 12,
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 0,
                UsesSourceFallback = false
            }
        ];

        MuPdfExportService.TranslatedBlockData[] retryPageBlocks =
        [
            normalPageBlocks[0] with { RetryCount = 1 }
        ];

        MuPdfExportService.TranslatedBlockData[] fallbackPageBlocks =
        [
            normalPageBlocks[0] with { UsesSourceFallback = true }
        ];

        // Unified layout planner now handles all blocks — no ShouldUseUnifiedRetryLayout gate.
        // Verify PlanPageLayout accepts all block types without errors.
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial", NotoFontId: null, PrimaryGlyphMap: null,
            NotoGlyphMap: null, PrimaryFontIsCjk: false);
        PageBlockLayoutPlanner.PlanPageLayout(normalPageBlocks, 400, "Arial", fonts).Should().HaveCount(1);
        PageBlockLayoutPlanner.PlanPageLayout(retryPageBlocks, 400, "Arial", fonts).Should().HaveCount(1);
        PageBlockLayoutPlanner.PlanPageLayout(fallbackPageBlocks, 400, "Arial", fonts).Should().HaveCount(1);
    }

    [Fact]
    public void BuildUnifiedRetryPageLayout_WithGrowingLeadingParagraph_PushesUnknownFollowingTextBlockDown()
    {
        const double pageHeight = 400;
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: false);

        var first = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 0,
                PageNumber = 1,
                SourceBlockId = "p1-body-b1",
                OrderInPage = 0,
                ReadingOrderScore = 1,
                SourceText = "Source block one",
                TranslatedText = "This retried paragraph grows substantially after retry and now needs several lines of final layout to fit cleanly within the page column.",
                BoundingBox = new BlockRect(40, 300, 240, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(322, 40, 280),
                        new BlockLinePosition(308, 40, 280)
                    ]
                },
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 1,
                UsesSourceFallback = false
            },
            pageHeight);

        var second = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 1,
                PageNumber = 1,
                SourceBlockId = "p1-body-b2",
                OrderInPage = 1,
                ReadingOrderScore = 0.5,
                SourceText = "Source block two",
                TranslatedText = "Following paragraph remains short.",
                BoundingBox = new BlockRect(40, 248, 240, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(270, 40, 280),
                        new BlockLinePosition(256, 40, 280)
                    ]
                },
                SourceBlockType = SourceBlockType.Unknown,
                RetryCount = 0,
                UsesSourceFallback = false
            },
            pageHeight);

        var plan = PageBlockLayoutPlanner.PlanPageLayout(
            [first, second],
            pageHeight,
            "Arial",
            fonts);

        var firstPlanned = plan.Single(block => block.Block.ChunkIndex == 0);
        var secondPlanned = plan.Single(block => block.Block.ChunkIndex == 1);

        firstPlanned.TopLeftBounds.Should().NotBeNull();
        secondPlanned.TopLeftBounds.Should().NotBeNull();
        secondPlanned.PlannedOperations.Should().NotBeNullOrWhiteSpace();
        secondPlanned.TopLeftBounds!.Value.Y.Should().BeGreaterThan(
            MuPdfExportService.ToTopLeftRect(pageHeight, second.BoundingBox!.Value).Y);
        secondPlanned.TopLeftBounds!.Value.Top.Should().BeGreaterOrEqualTo(firstPlanned.TopLeftBounds!.Value.Bottom);
    }

    [Fact]
    public void BuildUnifiedRetryPageLayout_WithFallbackParagraph_UsesUnifiedPlanAndAvoidsOverlap()
    {
        const double pageHeight = 400;
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: false);

        var fallbackBlock = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 0,
                PageNumber = 1,
                SourceBlockId = "p1-body-b1",
                OrderInPage = 0,
                ReadingOrderScore = 1,
                SourceText = "Fallback source block",
                TranslatedText = "Fallback source block with enough content to occupy more than one line in the final unified layout.",
                BoundingBox = new BlockRect(40, 300, 230, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(322, 40, 270),
                        new BlockLinePosition(308, 40, 270)
                    ]
                },
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 0,
                UsesSourceFallback = true
            },
            pageHeight);

        var neighbor = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 1,
                PageNumber = 1,
                SourceBlockId = "p1-body-b2",
                OrderInPage = 1,
                ReadingOrderScore = 0.5,
                SourceText = "Neighbor source block",
                TranslatedText = "Neighbor paragraph.",
                BoundingBox = new BlockRect(40, 252, 230, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(274, 40, 270),
                        new BlockLinePosition(260, 40, 270)
                    ]
                },
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 0,
                UsesSourceFallback = false
            },
            pageHeight);

        var plan = PageBlockLayoutPlanner.PlanPageLayout(
            [fallbackBlock, neighbor],
            pageHeight,
            "Arial",
            fonts);

        var fallbackPlanned = plan.Single(block => block.Block.ChunkIndex == 0);
        var neighborPlanned = plan.Single(block => block.Block.ChunkIndex == 1);

        fallbackPlanned.EraseRects.Should().NotBeNullOrEmpty();
        neighborPlanned.TopLeftBounds.Should().NotBeNull();
        neighborPlanned.TopLeftBounds!.Value.Top.Should().BeGreaterOrEqualTo(fallbackPlanned.TopLeftBounds!.Value.Bottom);
    }

    [Fact]
    public void BuildUnifiedRetryPageLayout_ReusesPlannedOperationsDuringRendering()
    {
        const double pageHeight = 400;
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: false);

        var block = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 0,
                PageNumber = 1,
                SourceBlockId = "p1-body-b1",
                OrderInPage = 0,
                ReadingOrderScore = 1,
                SourceText = "Source block one",
                TranslatedText = "This retried block should be laid out once and then rendered from the final planned operations without a second fitting pass.",
                BoundingBox = new BlockRect(40, 300, 240, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(322, 40, 280),
                        new BlockLinePosition(308, 40, 280)
                    ]
                },
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 1,
                UsesSourceFallback = false
            },
            pageHeight);

        var planned = PageBlockLayoutPlanner.PlanPageLayout(
            [block],
            pageHeight,
            "Arial",
            fonts).Single();

        planned.PlannedOperations.Should().NotBeNullOrWhiteSpace();
        planned.LayoutRenderLineRects.Should().NotBeNullOrEmpty();

        var renderResult = MuPdfExportService.RenderPlannedBlockTextOperations(planned);

        renderResult.Operations.Should().Be(planned.PlannedOperations);
        renderResult.ChosenFontSize.Should().BeApproximately(planned.PlannedChosenFontSize, 0.01);
        renderResult.LinesRendered.Should().Be(planned.PlannedLinesRendered);
        renderResult.WasShrunk.Should().Be(planned.PlannedWasShrunk);
        renderResult.WasTruncated.Should().Be(planned.PlannedWasTruncated);
    }

    [Fact]
    public void BuildUnifiedRetryPageLayout_WhenBlockMoves_EraseRectsCoverFinalRenderAndSourceCleanup()
    {
        const double pageHeight = 400;
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "Arial",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: false);

        var first = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 0,
                PageNumber = 1,
                SourceBlockId = "p1-body-b1",
                OrderInPage = 0,
                ReadingOrderScore = 1,
                SourceText = "Source block one",
                TranslatedText = "This retried paragraph grows substantially after retry and now needs several lines of final layout to fit cleanly within the page column.",
                BoundingBox = new BlockRect(40, 300, 240, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(322, 40, 280),
                        new BlockLinePosition(308, 40, 280)
                    ]
                },
                SourceBlockType = SourceBlockType.Paragraph,
                RetryCount = 1,
                UsesSourceFallback = false
            },
            pageHeight);

        var second = MuPdfExportService.PrepareBlockForRendering(
            new MuPdfExportService.TranslatedBlockData
            {
                ChunkIndex = 1,
                PageNumber = 1,
                SourceBlockId = "p1-body-b2",
                OrderInPage = 1,
                ReadingOrderScore = 0.5,
                SourceText = "Source block two",
                TranslatedText = "Neighbor text block.",
                BoundingBox = new BlockRect(40, 252, 230, 28),
                FontSize = 12,
                TextStyle = new BlockTextStyle
                {
                    FontSize = 12,
                    LineSpacing = 14,
                    LinePositions =
                    [
                        new BlockLinePosition(274, 40, 270),
                        new BlockLinePosition(260, 40, 270)
                    ]
                },
                SourceBlockType = SourceBlockType.Unknown,
                RetryCount = 0,
                UsesSourceFallback = false
            },
            pageHeight);

        var planned = PageBlockLayoutPlanner.PlanPageLayout(
            [first, second],
            pageHeight,
            "Arial",
            fonts);

        var secondPlanned = planned.Single(block => block.Block.ChunkIndex == 1);
        var originalSourceEraseRects = MuPdfExportService.ToTopLeftRects(
            pageHeight,
            second.BackgroundLineRects ?? [second.BoundingBox!.Value])!;
        var finalRenderRects = MuPdfExportService.ToTopLeftRects(pageHeight, secondPlanned.LayoutRenderLineRects)!;
        var finalEraseRects = MuPdfExportService.ToTopLeftRects(pageHeight, secondPlanned.EraseRects)!;

        finalRenderRects.Should().NotBeNullOrEmpty();
        finalEraseRects.Should().NotBeNullOrEmpty();
        finalRenderRects.All(render => finalEraseRects.Any(erase => RectTestHelpers.ContainsRect(erase, render))).Should().BeTrue();
        finalEraseRects.Any(erase => !originalSourceEraseRects.Any(source => RectTestHelpers.NearlySameRect(erase, source))).Should().BeTrue();
    }

}
