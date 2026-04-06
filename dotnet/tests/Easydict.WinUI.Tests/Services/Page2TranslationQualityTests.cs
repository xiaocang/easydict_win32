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

        MuPdfExportService.ShouldUseUnifiedRetryLayout(preparedBlocks).Should().BeTrue();

        var plan = MuPdfExportService.BuildUnifiedRetryPageLayout(
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

        var retryMetadata = SelectParagraphCandidate(checkpoint, 1);
        checkpoint.TranslatedChunks[retryMetadata.ChunkIndex] =
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

        retryPlanned.PlannedOperations.Should().NotBeNullOrWhiteSpace();
        affectedPlanned.PlannedOperations.Should().NotBeNullOrWhiteSpace();
        retryPlanned.TopLeftBounds.Should().NotBeNull();
        affectedPlanned.TopLeftBounds.Should().NotBeNull();
        affectedPlanned.TopLeftBounds!.Value.Top.Should().BeGreaterOrEqualTo(retryPlanned.TopLeftBounds!.Value.Bottom);
        AssertContinuousEraseBandCoverage(pageHeight, retryPrepared, retryPlanned);

        var finalRenderRects = MuPdfExportService.ToTopLeftRects(pageHeight, affectedPlanned.LayoutRenderLineRects)!;
        var finalEraseRects = MuPdfExportService.ToTopLeftRects(pageHeight, affectedPlanned.EraseRects)!;
        finalRenderRects.All(render => finalEraseRects.Any(erase => RectTestHelpers.ContainsRect(erase, render))).Should().BeTrue();
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

            page2Text.Should().Contain("Translated section three paragraph");
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
        var checkpoint = BuildPage2Section3RetryCheckpoint(source, pdfPath);
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
            Console.WriteLine($"Page 2 visual review PDF: {outputPdfPath}");
            Console.WriteLine($"Page 2 visual review PNG: {outputPngPath}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            Skip.If(true, $"MuPDF unavailable: {ex.Message}");
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
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 1,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = page.Blocks.Count <= 1
                        ? 1
                        : 1 - order / (double)(page.Blocks.Count - 1),
                    BoundingBox = block.BoundingBox,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters,
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
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            IntroPhrase,
            "Translated section three paragraph that should wrap across multiple lines without leaving the original English source visible under the new content.");
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

        MuPdfExportService.ShouldUseUnifiedRetryLayout(preparedBlocks).Should().BeTrue();

        var plan = MuPdfExportService.BuildUnifiedRetryPageLayout(
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
