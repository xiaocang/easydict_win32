using System.Reflection;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using MuPDF.NET;
using PdfSharpCore.Drawing;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class Page2TranslationQualityTests
{
    private const string PdfFixture = "1706.03762v7.pdf";
    private const string IntroPhrase = "Most competitive neural sequence transduction models";
    private const string ContinuousPhrase = "sequence of continuous representations";
    private const string Section3SourceSnippet = "sequence (y1, ..., ym) of symbols one element at a time";
    private const string Page2VisualReviewPdfName = "page2-visual-review-section3.pdf";
    private const string Page2VisualReviewPngName = "page2-visual-review-section3.png";
    private const string FailedFallbackFormulaText =
        "\u5FAA\u73AF\u6A21\u578B\u901A\u5E38\u6CBF\u7740\u8F93\u5165\u548C\u8F93\u51FA\u5E8F\u5217\u7684\u7B26\u53F7\u4F4D\u7F6E\u8FDB\u884C\u8BA1\u7B97\u7684\u5206\u89E3\u3002\u5B83\u4EEC\u6839\u636E\u524D\u4E00\u4E2A\u9690\u85CF\u72B6\u6001 $h_{t-1}$ \u548C\u4F4D\u7F6E t \u7684\u8F93\u5165\uFF0C\u751F\u6210\u4E00\u7CFB\u5217\u9690\u85CF\u72B6\u6001 $h_t$\u3002";
    private const string VisualPage2Heading = "3 \u6A21\u578B\u67B6\u6784";
    private const string VisualPage2Intro =
        "\u5927\u591A\u6570\u5177\u6709\u7ADE\u4E89\u529B\u7684\u795E\u7ECF\u5E8F\u5217\u8F6C\u6362\u6A21\u578B\u90FD\u91C7\u7528\u4E86\u7F16\u7801\u5668-\u89E3\u7801\u5668\u7ED3\u6784\u3002";
    private const string VisualPage2Encoder =
        "\u5176\u4E2D\uFF0C\u7F16\u7801\u5668\u5C06\u8F93\u5165\u7B26\u53F7\u8868\u793A\u5E8F\u5217 (x1, ..., xn) \u6620\u5C04\u4E3A\u4E00\u7EC4\u8FDE\u7EED\u8868\u793A z = (z1, ..., zn)\u3002";
    private const string VisualPage2Decoder =
        "\u7ED9\u5B9A z \u540E\uFF0C\u89E3\u7801\u5668\u518D\u9010\u4E2A\u751F\u6210\u8F93\u51FA\u5E8F\u5217 (y1, ..., ym) \u7684\u7B26\u53F7\u3002\u5728\u6BCF\u4E00\u6B65\u4E2D\uFF0C\u6A21\u578B\u90FD\u662F\u81EA\u56DE\u5F52\u7684\u3002";
    private const string VisualPage2Autoregressive =
        "[10]\uFF0C\u5728\u751F\u6210\u4E0B\u4E00\u4E2A\u7B26\u53F7\u65F6\uFF0C\u4F1A\u628A\u5148\u524D\u751F\u6210\u7684\u7B26\u53F7\u4F5C\u4E3A\u989D\u5916\u8F93\u5165\u3002";
    private const string VisualPage2Section3RetryParagraph =
        "\u5927\u591A\u6570\u5177\u6709\u7ADE\u4E89\u529B\u7684\u795E\u7ECF\u5E8F\u5217\u8F6C\u6362\u6A21\u578B\u90FD\u91C7\u7528\u4E86\u7F16\u7801\u5668-\u89E3\u7801\u5668\u7ED3\u6784\u3002\u5176\u4E2D\uFF0C\u7F16\u7801\u5668\u5C06\u8F93\u5165\u7B26\u53F7\u8868\u793A\u5E8F\u5217\u6620\u5C04\u4E3A\u8FDE\u7EED\u8868\u793A\uFF0C\u800C\u89E3\u7801\u5668\u4F1A\u9010\u4E2A\u751F\u6210\u8F93\u51FA\u7B26\u53F7\uFF0C\u5E76\u5728\u6BCF\u4E00\u6B65\u5229\u7528\u5148\u524D\u751F\u6210\u7684\u7ED3\u679C\u4F5C\u4E3A\u989D\u5916\u8F93\u5165\u3002";
    private const string StableChineseParagraph =
        "\u8FD9\u662F\u56DE\u5F52\u6D4B\u8BD5\u6BB5\u843D\uFF0C\u7528\u6765\u9A8C\u8BC1\u6B63\u5E38\u60C5\u51B5\u4E0B\u5B57\u53F7\u4FDD\u6301\u4E0D\u53D8\u3002";

    private static string GetPdfFixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "Pdf", PdfFixture);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    [SkippableFact]
    public async Task Page2_SourceExtraction_ShouldPreserveWordSpacingAndFormulas()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (_, page2Blocks) = await BuildPage2SourceBlocksAsync(pdfPath);
        var bodyBlocks = page2Blocks
            .Where(b => b.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .ToList();

        bodyBlocks.Should().NotBeEmpty();

        var bodyText = string.Join("\n", bodyBlocks.Select(b => b.Text));
        var normalized = Normalize(bodyText);

        normalized.Should().Contain(IntroPhrase);
        normalized.Should().Contain(ContinuousPhrase);
        bodyText.Should().Contain("(x1");
        bodyText.Should().Contain("xn)");
        bodyText.Should().Contain("z = (z1");
        bodyText.Should().Contain("(y1");
        bodyText.Should().NotContain("sequence_1");
        bodyText.Should().NotContain("z =_1");
    }

    [SkippableFact]
    public async Task Page1AndPage2_MuPdfLayout_ShouldPreserveOriginalFontSizeForFittingChineseParagraphs()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        foreach (var pageNumber in new[] { 1, 2 })
        {
            var metadata = SelectParagraphCandidate(checkpoint, pageNumber);
            checkpoint.TranslatedChunks[metadata.ChunkIndex] = StableChineseParagraph;

            var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
            var block = lookup[pageNumber].Single(candidate => candidate.ChunkIndex == metadata.ChunkIndex);
            var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(page => page.Number == pageNumber).Height);
            var prepared = MuPdfExportService.PrepareBlockForRendering(block, pageHeight);

            prepared.BoundingBox.Should().NotBeNull();

            var result = MuPdfExportService.GenerateBlockTextOperations(
                prepared.TranslatedText,
                fontId: "SourceHanSerifCN",
                fontSize: prepared.FontSize > 0 ? prepared.FontSize : 10.0,
                bbox: prepared.BoundingBox!.Value,
                fonts: fonts,
                textStyle: prepared.TextStyle,
                sourceBlockType: prepared.SourceBlockType,
                usesSourceFallback: prepared.UsesSourceFallback,
                detectedFontNames: prepared.DetectedFontNames,
                renderLineRects: prepared.RenderLineRects,
                backgroundLineRects: prepared.BackgroundLineRects);

            result.WasShrunk.Should().BeFalse($"page {pageNumber} paragraph should fit without shrinking");
            result.WasTruncated.Should().BeFalse();
            result.ChosenFontSize.Should().BeApproximately(prepared.FontSize > 0 ? prepared.FontSize : 10.0, 0.01);
        }
    }

    [SkippableFact]
    public async Task Page2_SourceFallback_ShouldPreferFallbackTextOverOriginalChunkText()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: true);

        checkpoint.FailedChunkIndexes.Should().HaveCount(1);

        var failedIndex = checkpoint.FailedChunkIndexes.First();
        var metadata = checkpoint.ChunkMetadata[failedIndex];

        metadata.FallbackText.Should().NotBeNullOrWhiteSpace();
        metadata.DetectedFontNames.Should().NotBeNullOrEmpty();
        checkpoint.TranslatedChunks.Should().NotContainKey(failedIndex);

        var found = PdfExportCheckpointTextResolver.TryGetRenderableText(
            checkpoint,
            failedIndex,
            out var renderText,
            out var usesSourceFallback);

        found.Should().BeTrue();
        usesSourceFallback.Should().BeTrue();
        renderText.Should().Be(metadata.FallbackText);
    }

    [SkippableFact]
    public async Task Page1_MuPdfFailedFallbackPlanner_ShouldNormalizeFormulaRenderableText()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildPage1FormulaFallbackCheckpoint(source, pdfPath);
        var formulaChunkIndex = checkpoint.FailedChunkIndexes.Should().ContainSingle().Which;

        var (_, _, plan) = BuildRetryPagePlan(checkpoint, pdfPath, 1);
        var plannedBlock = plan.Single(block => block.Block.ChunkIndex == formulaChunkIndex);
        var expectedRenderableText = MuPdfExportService.PrepareRenderableTextForPdf(FailedFallbackFormulaText);

        plannedBlock.Block.UsesSourceFallback.Should().BeTrue();
        plannedBlock.RenderableText.Should().Be(expectedRenderableText);
        plannedBlock.RenderableText.Should().Contain("h_t");
        plannedBlock.RenderableText.Should().Contain("_-");
        plannedBlock.RenderableText.Should().NotContain("$");
        plannedBlock.RenderableText.Should().NotContain("{");
        plannedBlock.RenderableText.Should().NotContain("}");

        var outputPath = Path.Combine(Path.GetTempPath(), $"page1-formula-fallback-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page1Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 1).Text);

            page1Text.Should().Contain("h");
            page1Text.Should().NotContain("$h_{t-1}$");
            page1Text.Should().NotContain("$h_t$");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfPrepareBlock_ShouldBuildExpandedBackgroundRectsForSection3Paragraph()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);
        var metadata = SelectParagraphCandidate(checkpoint, 2);
        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
        var block = lookup[2].Single(candidate => candidate.ChunkIndex == metadata.ChunkIndex);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(p => p.Number == 2).Height);
        var prepared = MuPdfExportService.PrepareBlockForRendering(block, pageHeight);

        prepared.BoundingBox.Should().NotBeNull();
        prepared.TranslatedText.Should().NotBeNullOrWhiteSpace();
        (prepared.BackgroundLineRects?.Count ?? prepared.RenderLineRects?.Count ?? 0)
            .Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Page2_MuPdfRetryPageLayout_ShouldProduceNonOverlappingBlocksForRetriedParagraph()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            IntroPhrase,
            "\u5927\u591A\u6570\u5177\u6709\u7ADE\u4E89\u529B\u7684\u795E\u7ECF\u5E8F\u5217\u8F6C\u6362\u6A21\u578B\u90FD\u91C7\u7528\u4E86\u7F16\u7801\u5668-\u89E3\u7801\u5668\u7ED3\u6784\uFF0C\u5E76\u4E14\u5728\u6B64\u57FA\u7840\u4E0A\u8FD8\u9700\u8981\u4E3A\u9875\u9762\u7EA7\u7EDF\u4E00\u6392\u7248\u7559\u51FA\u8DB3\u591F\u7684\u5782\u76F4\u7A7A\u95F4\u3002");
        MarkRetryForNeedle(checkpoint, IntroPhrase);

        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
        lookup.Should().ContainKey(2);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(page => page.Number == 2).Height);
        var preparedBlocks = lookup[2]
            .Select(block => block.BoundingBox is null ? block : MuPdfExportService.PrepareBlockForRendering(block, pageHeight))
            .ToList();
        var fonts = new MuPdfExportService.EmbeddedFontInfo(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

        var plan = PageBlockLayoutPlanner.PlanPageLayout(
            preparedBlocks,
            pageHeight,
            "SourceHanSerifCN",
            fonts);

        var retriedBlock = plan.Single(block => block.Block.SourceText.Contains(IntroPhrase, StringComparison.Ordinal));
        var nextFlowBlock = plan
            .Where(block =>
                block.Block.OrderInPage > retriedBlock.Block.OrderInPage &&
                block.TopLeftBounds is not null &&
                block.Block.SourceBlockType == SourceBlockType.Paragraph &&
                !block.Block.TranslationSkipped)
            .OrderBy(block => block.Block.OrderInPage)
            .First(block =>
            {
                var candidate = block.TopLeftBounds!.Value;
                var current = retriedBlock.TopLeftBounds!.Value;
                return Math.Min(candidate.Right, current.Right) - Math.Max(candidate.Left, current.Left) > 5;
            });

        retriedBlock.TopLeftBounds.Should().NotBeNull();
        nextFlowBlock.TopLeftBounds.Should().NotBeNull();
        nextFlowBlock.TopLeftBounds!.Value.Top.Should().BeGreaterOrEqualTo(retriedBlock.TopLeftBounds!.Value.Bottom);
    }

    [SkippableFact]
    public async Task Page1_MuPdfRetryPageLayout_ShouldReflowRealNeighborBlockWithoutOverlap()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);

        // Pick the longest multi-line paragraph on page 1 that has at least one
        // non-skipped paragraph after it in reading order — otherwise there's
        // nothing below to reflow against, which would make the test trivially
        // unsatisfiable. (The ML layout detector can legitimately place the
        // longest paragraph at the end of the page.)
        var retryMetadata = checkpoint.ChunkMetadata
            .Where(metadata =>
                metadata.PageNumber == 1 &&
                metadata.SourceBlockType == SourceBlockType.Paragraph &&
                metadata.BoundingBox is not null &&
                metadata.TextStyle?.LinePositions is { Count: > 1 } &&
                checkpoint.ChunkMetadata.Any(later =>
                    later.PageNumber == 1 &&
                    later.OrderInPage > metadata.OrderInPage &&
                    later.SourceBlockType != SourceBlockType.Formula &&
                    later.BoundingBox is not null))
            .OrderByDescending(metadata => checkpoint.SourceChunks[metadata.ChunkIndex].Length)
            .FirstOrDefault();
        retryMetadata.Should().NotBeNull("page 1 should have a multi-line paragraph followed by another paragraph");

        checkpoint.TranslatedChunks[retryMetadata!.ChunkIndex] =
            "\u8FD9\u4E2A\u9875\u9762\u4E00\u7684\u56DE\u5F52\u6BB5\u843D\u5728 retry \u540E\u9700\u8981\u66F4\u591A\u884C\u6570\u624D\u80FD\u5B8C\u6574\u663E\u793A\uFF0C\u56E0\u6B64\u5E94\u5F53\u89E6\u53D1 MuPDF \u7684\u9875\u7EA7\u7EDF\u4E00\u91CD\u6392\u7248\u903B\u8F91\uFF0C\u5E76\u628A\u540E\u9762\u7684\u6B63\u5E38\u6587\u672C\u5757\u4E00\u8D77\u5411\u4E0B\u907F\u8BA9\u3002";
        checkpoint.ChunkMetadata[retryMetadata.ChunkIndex].RetryCount = 1;

        var (pageHeight, preparedBlocks, plan) = BuildRetryPagePlan(checkpoint, pdfPath, 1);

        var retryPrepared = preparedBlocks.Single(block => block.ChunkIndex == retryMetadata.ChunkIndex);
        var retryPlanned = plan.Single(block => block.Block.ChunkIndex == retryMetadata.ChunkIndex);
        var affectedPlanned = plan
            .Where(block =>
                block.Block.OrderInPage > retryPlanned.Block.OrderInPage &&
                block.TopLeftBounds is not null &&
                !block.Block.TranslationSkipped)
            .OrderBy(block => block.Block.OrderInPage)
            .First(block =>
            {
                var candidate = block.TopLeftBounds!.Value;
                var current = retryPlanned.TopLeftBounds!.Value;
                return Math.Min(candidate.Right, current.Right) - Math.Max(candidate.Left, current.Left) > 5;
            });

        retryPlanned.TopLeftBounds.Should().NotBeNull();
        affectedPlanned.TopLeftBounds.Should().NotBeNull();
        affectedPlanned.TopLeftBounds!.Value.Top.Should().BeGreaterOrEqualTo(retryPlanned.TopLeftBounds!.Value.Bottom);

        if (retryPlanned.PlannedOperations is not null)
            AssertContinuousEraseBandCoverage(pageHeight, retryPrepared, retryPlanned);

        if (affectedPlanned.PlannedOperations is not null)
        {
            var finalRenderRects = MuPdfExportService.ToTopLeftRects(pageHeight, affectedPlanned.LayoutRenderLineRects)!;
            var finalEraseRects = MuPdfExportService.ToTopLeftRects(pageHeight, affectedPlanned.EraseRects)!;
            finalRenderRects.All(render => finalEraseRects.Any(erase => RectTestHelpers.ContainsRect(erase, render))).Should().BeTrue();
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldRenderFallbackBlocksWithoutBrokenLatinSpacing()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: true);

        var failedIndex = checkpoint.FailedChunkIndexes.First();
        var metadata = checkpoint.ChunkMetadata[failedIndex];

        var fallbackOrSource = metadata.FallbackText ?? checkpoint.SourceChunks[failedIndex];
        fallbackOrSource.Should().NotBeNullOrWhiteSpace();

        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-fallback-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            page2Text.Should().NotBeNullOrWhiteSpace();

            var expectedSnippet = Normalize(fallbackOrSource).Split(' ').Take(3).ToArray();
            if (expectedSnippet.Length >= 3)
                page2Text.Should().Contain(string.Join(" ", expectedSnippet));

            page2Text.Should().NotContain("Tr ansf or mer");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldPreserveInlineLatinTermsInTranslatedBlocks()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);

        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "ConvS2S",
            "\u8FD9\u4E9B\u6A21\u578B\u5305\u62EC ConvS2S\uFF0C\u5E76\u80FD\u591F\u5E76\u884C\u8BA1\u7B97\u6240\u6709\u8F93\u5165\u4E0E\u8F93\u51FA\u4F4D\u7F6E\u3002");
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "ByteNet",
            "\u76F8\u5173\u5DE5\u4F5C\u5982 ByteNet \u4E0E ConvS2S \u4E5F\u91C7\u7528\u4E86\u7C7B\u4F3C\u7ED3\u6784\u3002");
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "Transformer",
            "\u800C\u5728 Transformer \u4E2D\uFF0C\u8FD9\u4E00\u64CD\u4F5C\u88AB\u51CF\u5C11\u5230\u56FA\u5B9A\u6B21\u6570\u3002");

        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-mixed-latin-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            page2Text.Should().Contain("Transformer");
            page2Text.Should().Contain("ByteNet");
            page2Text.Should().Contain("ConvS2S");
            page2Text.Should().NotContain("Tr ansf or mer");
            page2Text.Should().NotContain("Byt eNet");
            page2Text.Should().NotContain("Conv S2S");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldEraseSection3SourceBleedThrough()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildPage2Section3RetryCheckpoint(source, pdfPath);
        var (pageHeight, preparedBlocks, plan) = BuildRetryPagePlan(checkpoint, pdfPath, 2);
        var retryPrepared = preparedBlocks.Single(block => block.SourceText.Contains(IntroPhrase, StringComparison.Ordinal));
        var retryPlanned = plan.Single(block => block.Block.ChunkIndex == retryPrepared.ChunkIndex);

        AssertContinuousEraseBandCoverage(pageHeight, retryPrepared, retryPlanned);

        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-overlap-fix-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            page2Text.Should().Contain("大多数具有竞争");
            page2Text.Should().NotContain(Section3SourceSnippet);
            page2Text.Should().NotContain("At each step the model is auto-regressive");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldEmitVisualReviewPng()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildPage2VisualReviewCheckpoint(source, pdfPath);
        var outputPdfPath = Path.Combine(Path.GetTempPath(), Page2VisualReviewPdfName);
        var outputPngPath = Path.Combine(Path.GetTempPath(), Page2VisualReviewPngName);

        TryDelete(outputPdfPath);
        TryDelete(outputPngPath);

        try
        {
            var exportService = new MuPdfExportService();
            exportService.Export(checkpoint, pdfPath, outputPdfPath, DocumentOutputMode.Monolingual);

            var muDoc = new Document(outputPdfPath);
            try
            {
                muDoc.PageCount.Should().BeGreaterOrEqualTo(2);

                var page2 = muDoc[1];
                var pix = page2.GetPixmap(new Matrix(1.5f, 1.5f));
                pix.Save(outputPngPath, "png");
            }
            finally
            {
                muDoc.Close();
            }

            File.Exists(outputPdfPath).Should().BeTrue();
            File.Exists(outputPngPath).Should().BeTrue();

            using var outputDoc = PdfPigDocument.Open(outputPdfPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);
            page2Text.Should().Contain("\u795E\u7ECF\u5E8F\u5217\u8F6C\u6362\u6A21\u578B");
            page2Text.Should().Contain("\u7ED9\u5B9A z");
            page2Text.Should().Contain("Transformer");
            page2Text.Should().Contain("ByteNet");
            page2Text.Should().NotContain("translated-");

            Console.WriteLine($"Page 2 visual review PDF: {outputPdfPath}");
            Console.WriteLine($"Page 2 visual review PNG: {outputPngPath}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            Skip.If(true, $"MuPDF unavailable: {ex.Message}");
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldExposeReadableChineseTextToPdfPig()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildPage2VisualReviewCheckpoint(source, pdfPath);
        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-cjk-extractability-{Guid.NewGuid():N}.pdf");

        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            page2Text.Should().Contain("\u795E\u7ECF\u5E8F\u5217\u8F6C\u6362\u6A21\u578B");
            page2Text.Should().Contain("\u7ED9\u5B9A z");
            page2Text.Should().Contain("Transformer");
            page2Text.Should().Contain("ByteNet");
            page2Text.Should().NotContain("translated-");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_PlanPageLayout_ShouldProduceNonOverlappingBoundsForAllBlocks()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildPage2VisualReviewCheckpoint(source, pdfPath);
        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
        lookup.Should().ContainKey(2);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(p => p.Number == 2).Height);
        var preparedBlocks = lookup[2]
            .Select(b => b.BoundingBox is null ? b : MuPdfExportService.PrepareBlockForRendering(b, pageHeight))
            .ToList();
        var fonts = CreatePrimaryCjkFonts();

        var plan = PageBlockLayoutPlanner.PlanPageLayout(
            preparedBlocks, pageHeight, "SourceHanSerifCN", fonts);

        var renderedBlocks = plan
            .Where(b => b.TopLeftBounds is not null && !b.Block.TranslationSkipped)
            .ToList();

        for (var i = 0; i < renderedBlocks.Count; i++)
        {
            for (var j = i + 1; j < renderedBlocks.Count; j++)
            {
                var a = renderedBlocks[i].TopLeftBounds!.Value;
                var b = renderedBlocks[j].TopLeftBounds!.Value;

                // Skip if no horizontal overlap
                var hOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                if (hOverlap <= 5) continue;

                // Assert no vertical overlap
                var vOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                vOverlap.Should().BeLessThanOrEqualTo(0.5,
                    $"blocks {renderedBlocks[i].Block.ChunkIndex} and {renderedBlocks[j].Block.ChunkIndex} should not vertically overlap");
            }
        }
    }

    private static async Task<(SourceDocument source, IReadOnlyList<SourceDocumentBlock> page2Blocks)>
        BuildPage2SourceBlocksAsync(string pdfPath)
    {
        using var service = new Easydict.WinUI.Services.LongDocumentTranslationService();
        var method = typeof(Easydict.WinUI.Services.LongDocumentTranslationService)
            .GetMethod("BuildSourceDocumentAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task<SourceDocument>)method!.Invoke(service,
        [
            LongDocumentInputMode.Pdf,
            pdfPath,
            LayoutDetectionMode.Auto,
            null, null, null, null,
            CancellationToken.None,
            null
        ])!;

        var source = await task;
        var page2 = source.Pages.Single(p => p.PageNumber == 2);
        return (source, page2.Blocks);
    }

    private static LongDocumentTranslationCheckpoint BuildMockTranslationCheckpoint(
        SourceDocument source,
        string pdfPath,
        bool failOneBlock)
    {
        var sourceChunks = new List<string>();
        var chunkMetadata = new List<LongDocumentChunkMetadata>();
        var translatedChunks = new Dictionary<int, string>();
        var failedIndexes = new HashSet<int>();
        var chunkIndex = 0;
        var failedBlockId = failOneBlock ? SelectFailedBlockId(source) : null;

        foreach (var page in source.Pages.OrderBy(p => p.PageNumber))
        {
            for (var order = 0; order < page.Blocks.Count; order++)
            {
                var block = page.Blocks[order];
                sourceChunks.Add(block.Text);

                chunkMetadata.Add(new LongDocumentChunkMetadata
                {
                    ChunkIndex = chunkIndex,
                    PageNumber = page.PageNumber,
                    SourceBlockId = block.BlockId,
                    SourceBlockType = block.BlockType,
                    IsFormulaLike = block.IsFormulaLike,
                    OrderInPage = order,
                    RegionType = InferFixtureRegionType(block),
                    RegionConfidence = 1,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = page.Blocks.Count <= 1
                        ? 1
                        : 1 - order / (double)(page.Blocks.Count - 1),
                    BoundingBox = block.BoundingBox,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters,
                    TranslationSkipped = block.BlockType == SourceBlockType.Formula,
                    PreserveOriginalTextInPdfExport = block.BlockType == SourceBlockType.Formula,
                    FallbackText = block.FallbackText,
                    DetectedFontNames = block.DetectedFontNames
                });

                var isFailedFallbackTarget = failOneBlock
                    && page.PageNumber == 2
                    && string.Equals(block.BlockId, failedBlockId, StringComparison.Ordinal);

                if (isFailedFallbackTarget)
                {
                    failedIndexes.Add(chunkIndex);
                }
                else
                {
                    translatedChunks[chunkIndex] = block.IsFormulaLike || block.BlockType == SourceBlockType.Formula
                        ? block.Text
                        : MockTranslate(block.Text);
                }

                chunkIndex++;
            }
        }

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Pdf,
            SourceFilePath = pdfPath,
            TargetLanguage = Language.SimplifiedChinese,
            SourceChunks = sourceChunks,
            ChunkMetadata = chunkMetadata,
            TranslatedChunks = translatedChunks,
            FailedChunkIndexes = failedIndexes
        };
    }

    private static string SelectFailedBlockId(SourceDocument source)
    {
        var page2Blocks = source.Pages
            .Single(p => p.PageNumber == 2)
            .Blocks
            .Where(b => b.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .ToList();

        page2Blocks.Should().NotBeEmpty();

        var preferred = page2Blocks.FirstOrDefault(
                b => !string.IsNullOrWhiteSpace(b.FallbackText))
            ?? page2Blocks.OrderByDescending(b => b.Text.Length).First();

        return preferred.BlockId;
    }

    private static LongDocumentChunkMetadata FindMetadataBySourceNeedle(
        LongDocumentTranslationCheckpoint checkpoint,
        int pageNumber,
        string needle)
    {
        var metadata = checkpoint.ChunkMetadata
            .FirstOrDefault(m =>
                m.PageNumber == pageNumber &&
                Normalize(checkpoint.SourceChunks[m.ChunkIndex]).Contains(Normalize(needle), StringComparison.Ordinal));

        metadata.Should().NotBeNull($"page {pageNumber} should contain '{needle}'");
        return metadata!;
    }

    private static LongDocumentChunkMetadata SelectParagraphCandidate(
        LongDocumentTranslationCheckpoint checkpoint,
        int pageNumber)
    {
        var candidate = checkpoint.ChunkMetadata
            .Where(metadata =>
                metadata.PageNumber == pageNumber &&
                metadata.SourceBlockType == SourceBlockType.Paragraph &&
                metadata.BoundingBox is not null &&
                metadata.TextStyle?.LinePositions is { Count: > 1 })
            .OrderByDescending(metadata => checkpoint.SourceChunks[metadata.ChunkIndex].Length)
            .FirstOrDefault();

        candidate.Should().NotBeNull($"page {pageNumber} should have a multi-line paragraph block");
        return candidate!;
    }

    private static MuPdfExportService.TranslatedBlockData FindMuPdfPage2BlockByNeedle(
        LongDocumentTranslationCheckpoint checkpoint,
        string needle)
    {
        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
        lookup.Should().ContainKey(2);

        var block = lookup[2]
            .FirstOrDefault(candidate =>
                candidate.SourceText.Contains(needle, StringComparison.Ordinal) &&
                candidate.BoundingBox is not null)
            ?? lookup[2].FirstOrDefault(candidate => candidate.SourceText.Contains(needle, StringComparison.Ordinal));

        block.Should().NotBeNull();
        return block!;
    }

    private static LongDocumentTranslationCheckpoint BuildPage2Section3RetryCheckpoint(
        SourceDocument source,
        string pdfPath)
    {
        var checkpoint = BuildPage2VisualReviewCheckpoint(source, pdfPath);
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            IntroPhrase,
            VisualPage2Section3RetryParagraph);
        return checkpoint;
    }

    private static LongDocumentTranslationCheckpoint BuildPage1FormulaFallbackCheckpoint(
        SourceDocument source,
        string pdfPath)
    {
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);
        var targetMetadata = SelectParagraphCandidate(checkpoint, 1);

        var updatedMetadata = checkpoint.ChunkMetadata
            .Select(metadata => metadata.ChunkIndex == targetMetadata.ChunkIndex
                ? CloneMetadata(
                    metadata,
                    fallbackText: FailedFallbackFormulaText,
                    detectedFontNames: metadata.DetectedFontNames ?? ["Times New Roman"])
                : CloneMetadata(metadata))
            .ToList();

        var updatedTranslations = checkpoint.TranslatedChunks
            .Where(pair => pair.Key != targetMetadata.ChunkIndex)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var updatedFailedIndexes = new HashSet<int> { targetMetadata.ChunkIndex };

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = checkpoint.InputMode,
            SourceFilePath = checkpoint.SourceFilePath,
            TargetLanguage = checkpoint.TargetLanguage,
            SourceChunks = [.. checkpoint.SourceChunks],
            ChunkMetadata = updatedMetadata,
            TranslatedChunks = updatedTranslations,
            FailedChunkIndexes = updatedFailedIndexes
        };
    }

    private static LongDocumentTranslationCheckpoint BuildPage2VisualReviewCheckpoint(
        SourceDocument source,
        string pdfPath)
    {
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);

        foreach (var metadata in checkpoint.ChunkMetadata.Where(metadata => metadata.PageNumber == 2))
        {
            if (!checkpoint.TranslatedChunks.ContainsKey(metadata.ChunkIndex))
                continue;

            checkpoint.TranslatedChunks[metadata.ChunkIndex] = CreateReadablePage2ChineseMock(
                checkpoint.SourceChunks[metadata.ChunkIndex],
                metadata.SourceBlockType,
                metadata.OrderInPage);
        }

        MarkRetryForNeedle(checkpoint, IntroPhrase);
        return checkpoint;
    }

    private static (
        double PageHeight,
        IReadOnlyList<MuPdfExportService.TranslatedBlockData> PreparedBlocks,
        IReadOnlyList<MuPdfExportService.PlannedPageBlock> Plan) BuildRetryPagePlan(
            LongDocumentTranslationCheckpoint checkpoint,
            string pdfPath,
            int pageNumber)
    {
        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);
        lookup.Should().ContainKey(pageNumber);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(page => page.Number == pageNumber).Height);
        var preparedBlocks = lookup[pageNumber]
            .Select(block => block.BoundingBox is null ? block : MuPdfExportService.PrepareBlockForRendering(block, pageHeight))
            .ToList();
        var fonts = CreatePrimaryCjkFonts();

        var plan = PageBlockLayoutPlanner.PlanPageLayout(
            preparedBlocks,
            pageHeight,
            "SourceHanSerifCN",
            fonts);

        return (pageHeight, preparedBlocks, plan);
    }

    private static void OverrideTranslatedChunkForNeedle(
        LongDocumentTranslationCheckpoint checkpoint,
        string needle,
        string translatedText)
    {
        var chunkIndex = checkpoint.ChunkMetadata
            .Where(m => m.PageNumber == 2)
            .Select(m => m.ChunkIndex)
            .FirstOrDefault(i => checkpoint.SourceChunks[i].Contains(needle, StringComparison.Ordinal));

        checkpoint.SourceChunks[chunkIndex].Should().Contain(needle);
        checkpoint.TranslatedChunks[chunkIndex] = translatedText;
    }

    private static void MarkRetryForNeedle(
        LongDocumentTranslationCheckpoint checkpoint,
        string needle,
        int retryCount = 1)
    {
        var metadata = checkpoint.ChunkMetadata
            .FirstOrDefault(m => checkpoint.SourceChunks[m.ChunkIndex].Contains(needle, StringComparison.Ordinal));

        metadata.Should().NotBeNull();
        metadata!.RetryCount = retryCount;
    }

    private static LongDocumentChunkMetadata CloneMetadata(
        LongDocumentChunkMetadata metadata,
        string? fallbackText = null,
        IReadOnlyList<string>? detectedFontNames = null)
    {
        return new LongDocumentChunkMetadata
        {
            ChunkIndex = metadata.ChunkIndex,
            PageNumber = metadata.PageNumber,
            SourceBlockId = metadata.SourceBlockId,
            SourceBlockType = metadata.SourceBlockType,
            IsFormulaLike = metadata.IsFormulaLike,
            OrderInPage = metadata.OrderInPage,
            RegionType = metadata.RegionType,
            RegionConfidence = metadata.RegionConfidence,
            RegionSource = metadata.RegionSource,
            ReadingOrderScore = metadata.ReadingOrderScore,
            BoundingBox = metadata.BoundingBox,
            TextStyle = metadata.TextStyle,
            FormulaCharacters = metadata.FormulaCharacters,
            TranslationSkipped = metadata.TranslationSkipped,
            PreserveOriginalTextInPdfExport = metadata.PreserveOriginalTextInPdfExport,
            RetryCount = metadata.RetryCount,
            FallbackText = fallbackText ?? metadata.FallbackText,
            DetectedFontNames = detectedFontNames ?? metadata.DetectedFontNames
        };
    }

    private static LayoutRegionType InferFixtureRegionType(SourceDocumentBlock block) =>
        block.BlockType switch
        {
            SourceBlockType.Formula => LayoutRegionType.Formula,
            SourceBlockType.TableCell => LayoutRegionType.TableLike,
            _ => LayoutRegionType.Body
        };

    private static string CreateReadablePage2ChineseMock(
        string sourceText,
        SourceBlockType sourceBlockType,
        int orderInPage)
    {
        var normalized = Normalize(sourceText);

        if (sourceBlockType == SourceBlockType.Heading &&
            normalized.Contains("Model Architecture", StringComparison.Ordinal))
        {
            return VisualPage2Heading;
        }

        if (normalized.Contains(IntroPhrase, StringComparison.Ordinal))
            return VisualPage2Intro;

        if (normalized.Contains("encoder maps an input sequence of symbol representations", StringComparison.Ordinal) ||
            normalized.Contains(ContinuousPhrase, StringComparison.Ordinal))
        {
            return VisualPage2Encoder;
        }

        if (normalized.Contains("Given z", StringComparison.Ordinal) ||
            normalized.Contains(Section3SourceSnippet, StringComparison.Ordinal) ||
            normalized.Contains("At each step the model is auto-regressive", StringComparison.Ordinal))
        {
            return VisualPage2Decoder;
        }

        if (normalized.Contains("consuming the previously generated symbols as additional input when generating the next", StringComparison.Ordinal))
            return VisualPage2Autoregressive;

        if (normalized.Contains("ByteNet", StringComparison.Ordinal) || normalized.Contains("ConvS2S", StringComparison.Ordinal))
            return "\u8FD9\u4E9B\u6A21\u578B\u5305\u62EC ConvS2S \u548C ByteNet\uFF0C\u5E76\u80FD\u591F\u5E76\u884C\u8BA1\u7B97\u6240\u6709\u8F93\u5165\u4E0E\u8F93\u51FA\u4F4D\u7F6E\u3002";

        if (normalized.Contains("Transformer", StringComparison.Ordinal))
            return "\u800C\u5728 Transformer \u4E2D\uFF0C\u8FD9\u4E00\u64CD\u4F5C\u88AB\u51CF\u5C11\u5230\u56FA\u5B9A\u6B21\u6570\u3002";

        if (normalized.Contains("Attention mechanisms have become an integral part", StringComparison.Ordinal))
            return "\u6CE8\u610F\u529B\u673A\u5236\u5DF2\u7ECF\u6210\u4E3A\u5404\u7C7B\u5E8F\u5217\u5EFA\u6A21\u4E0E\u8F6C\u6362\u4EFB\u52A1\u7684\u91CD\u8981\u7EC4\u6210\u90E8\u5206\u3002";

        if (normalized.Contains("In this work we propose the Transformer", StringComparison.Ordinal))
            return "\u5728\u8FD9\u9879\u5DE5\u4F5C\u4E2D\uFF0C\u6211\u4EEC\u63D0\u51FA\u4E86 Transformer \u8FD9\u4E00\u6A21\u578B\u67B6\u6784\u3002";

        if (normalized.Contains("The Transformer allows for significantly more parallelization", StringComparison.Ordinal))
            return "Transformer \u80FD\u591F\u5B9E\u73B0\u66F4\u9AD8\u7684\u5E76\u884C\u5316\u7A0B\u5EA6\uFF0C\u5E76\u5728\u7FFB\u8BD1\u8D28\u91CF\u4E0A\u53D6\u5F97\u4E86\u65B0\u7684\u6700\u4F18\u7ED3\u679C\u3002";

        if (normalized.Contains("The goal of reducing sequential computation also forms the foundation", StringComparison.Ordinal))
            return "\u964D\u4F4E\u987A\u5E8F\u8BA1\u7B97\u7684\u76EE\u6807\u4E5F\u6784\u6210\u4E86 Extended Neural GPU\u3001ByteNet \u4E0E ConvS2S \u7B49\u6A21\u578B\u7684\u57FA\u7840\u3002";

        if (normalized.Contains("Recurrent models typically factor computation along the symbol positions", StringComparison.Ordinal))
            return "\u5FAA\u73AF\u6A21\u578B\u901A\u5E38\u6CBF\u7740\u8F93\u5165\u548C\u8F93\u51FA\u5E8F\u5217\u7684\u7B26\u53F7\u4F4D\u7F6E\u8FDB\u884C\u8BA1\u7B97\u5206\u89E3\u3002";

        if (normalized.Contains("Aligning the positions to steps in computation time", StringComparison.Ordinal) ||
            normalized.Contains("previous hidden state", StringComparison.Ordinal))
        {
            return "\u901A\u8FC7\u5C06\u4F4D\u7F6E\u4E0E\u8BA1\u7B97\u65F6\u95F4\u6B65\u9AA4\u5BF9\u9F50\uFF0C\u5B83\u4EEC\u6839\u636E\u524D\u4E00\u4E2A\u9690\u85CF\u72B6\u6001 h_{t-1} \u548C\u4F4D\u7F6E t \u7684\u8F93\u5165\uFF0C\u751F\u6210\u4E00\u7CFB\u5217\u9690\u85CF\u72B6\u6001 h_t\u3002";
        }

        if (normalized.Contains("This inherently sequential nature precludes parallelization within training examples", StringComparison.Ordinal) ||
            normalized.Contains("The fundamental constraint of sequential computation", StringComparison.Ordinal))
        {
            return "\u8FD9\u79CD\u56FA\u6709\u7684\u987A\u5E8F\u6027\u963B\u788D\u4E86\u8BAD\u7EC3\u6837\u672C\u5185\u90E8\u7684\u5E76\u884C\u5316\uFF0C\u800C\u987A\u5E8F\u8BA1\u7B97\u7684\u6839\u672C\u7EA6\u675F\u4F9D\u7136\u5B58\u5728\u3002";
        }

        if (normalized.Contains("Recent work has achieved significant improvements", StringComparison.Ordinal) ||
            normalized.Contains("conditional computation", StringComparison.Ordinal))
        {
            return "\u6700\u8FD1\u7684\u7814\u7A76\u901A\u8FC7\u5206\u89E3\u6280\u5DE7\u548C\u6761\u4EF6\u8BA1\u7B97\uFF0C\u5728\u8BA1\u7B97\u6548\u7387\u4E0A\u53D6\u5F97\u4E86\u663E\u8457\u63D0\u5347\u3002";
        }

        if (normalized.Contains("Self-attention, sometimes called intra-attention", StringComparison.Ordinal))
            return "\u81EA\u6CE8\u610F\u529B\u6709\u65F6\u4E5F\u88AB\u79F0\u4E3A\u5185\u90E8\u6CE8\u610F\u529B\uFF0C\u662F\u4E00\u79CD\u5C06\u5355\u4E2A\u5E8F\u5217\u4E2D\u4E0D\u540C\u4F4D\u7F6E\u5173\u8054\u8D77\u6765\u4EE5\u8BA1\u7B97\u5E8F\u5217\u8868\u793A\u7684\u673A\u5236\u3002";

        if (normalized.Contains("Self-attention has been used successfully in a variety of tasks", StringComparison.Ordinal))
            return "\u81EA\u6CE8\u610F\u529B\u5DF2\u7ECF\u5728\u591A\u79CD\u4EFB\u52A1\u4E2D\u5F97\u5230\u6210\u529F\u5E94\u7528\uFF0C\u5305\u62EC\u9605\u8BFB\u7406\u89E3\u3001\u6458\u8981\u751F\u6210\u548C\u53E5\u5B50\u8868\u793A\u5B66\u4E60\u3002";

        if (normalized.Contains("End-to-end memory networks are based on a recurrent attention mechanism", StringComparison.Ordinal))
            return "\u7AEF\u5230\u7AEF\u8BB0\u5FC6\u7F51\u7EDC\u57FA\u4E8E\u5FAA\u73AF\u6CE8\u610F\u529B\u673A\u5236\uFF0C\u800C\u4E0D\u662F\u9010\u4F4D\u7F6E\u5BF9\u9F50\u7684\u5FAA\u73AF\u7ED3\u6784\u3002";

        if (normalized.Contains("To the best of our knowledge", StringComparison.Ordinal))
            return "\u7136\u800C\uFF0C\u636E\u6211\u4EEC\u6240\u77E5\uFF0CTransformer \u662F\u9996\u4E2A\u5B8C\u5168\u4F9D\u8D56\u81EA\u6CE8\u610F\u529B\u6765\u8BA1\u7B97\u8F93\u5165\u4E0E\u8F93\u51FA\u8868\u793A\u7684\u8F6C\u6362\u6A21\u578B\u3002";

        return sourceBlockType == SourceBlockType.Heading
            ? $"\u7B2C 2 \u9875\u6807\u9898\u6A21\u62DF {orderInPage + 1}"
            : $"\u8FD9\u662F\u7528\u4E8E\u7B2C 2 \u9875\u4EBA\u5DE5\u89C6\u89C9\u68C0\u67E5\u7684\u4E2D\u6587\u6A21\u62DF\u6BB5\u843D {orderInPage + 1}\u3002";
    }

    private static MuPdfExportService.EmbeddedFontInfo CreatePrimaryCjkFonts() =>
        new(
            PrimaryFontId: "SourceHanSerifCN",
            NotoFontId: null,
            PrimaryGlyphMap: null,
            NotoGlyphMap: null,
            PrimaryFontIsCjk: true);

    private static void AssertContinuousEraseBandCoverage(
        double pageHeight,
        MuPdfExportService.TranslatedBlockData preparedBlock,
        MuPdfExportService.PlannedPageBlock plannedBlock)
    {
        plannedBlock.TopLeftBounds.Should().NotBeNull();
        plannedBlock.EraseRects.Should().NotBeNullOrEmpty();

        var sourceEraseRects = GetSourceEraseRectsTopLeft(pageHeight, preparedBlock);
        var finalRenderRects = MuPdfExportService.ToTopLeftRects(pageHeight, plannedBlock.LayoutRenderLineRects);
        var finalEraseRects = MuPdfExportService.ToTopLeftRects(pageHeight, plannedBlock.EraseRects);

        finalRenderRects.Should().NotBeNullOrEmpty();
        finalEraseRects.Should().NotBeNullOrEmpty();

        var renderRects = finalRenderRects!;
        var eraseRects = finalEraseRects!;

        renderRects.All(render => eraseRects.Any(erase => RectTestHelpers.ContainsRect(erase, render))).Should().BeTrue();

        var expectedTop = Math.Min(sourceEraseRects.Min(rect => rect.Top), renderRects.Min(rect => rect.Top));
        var expectedBottom = Math.Max(sourceEraseRects.Max(rect => rect.Bottom), renderRects.Max(rect => rect.Bottom));
        var expectedLeft = Math.Min(sourceEraseRects.Min(rect => rect.Left), renderRects.Min(rect => rect.Left));
        var expectedRight = Math.Max(sourceEraseRects.Max(rect => rect.Right), renderRects.Max(rect => rect.Right));

        eraseRects.Should().Contain(rect =>
            rect.Top <= expectedTop + 0.01 &&
            rect.Bottom >= expectedBottom - 0.01 &&
            Math.Min(rect.Right, expectedRight) - Math.Max(rect.Left, expectedLeft) > 5);
    }

    private static IReadOnlyList<XRect> GetSourceEraseRectsTopLeft(
        double pageHeight,
        MuPdfExportService.TranslatedBlockData block)
    {
        var rects = MuPdfExportService.ToTopLeftRects(pageHeight, block.BackgroundLineRects ?? block.RenderLineRects);
        if (rects is { Count: > 0 })
            return rects;

        block.BoundingBox.Should().NotBeNull();
        return [MuPdfExportService.ToTopLeftRect(pageHeight, block.BoundingBox!.Value)];
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string MockTranslate(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        var placeholders = Regex.Matches(source, @"\{v\d+\}")
            .Cast<Match>()
            .Select(match => match.Value);

        return $"translated-{new string('x', Math.Max(6, source.Length / 4))}{string.Concat(placeholders)}";
    }
}
