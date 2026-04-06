using System.Reflection;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
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
        var block = FindMuPdfPage2BlockByNeedle(checkpoint, IntroPhrase);

        using var sourceDoc = PdfPigDocument.Open(pdfPath);
        var pageHeight = Convert.ToDouble(sourceDoc.GetPages().Single(p => p.Number == 2).Height);
        var prepared = MuPdfExportService.PrepareBlockForRendering(block, pageHeight);

        prepared.BackgroundLineRects.Should().NotBeNull();
        prepared.BackgroundLineRects!.Should().HaveCountGreaterThan(1);
        prepared.BackgroundLineRects!.Min(rect => rect.Y).Should().BeLessThan(prepared.BoundingBox!.Value.Y);
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
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);

        OverrideTranslatedChunkForNeedle(
            checkpoint,
            IntroPhrase,
            "Translated section three paragraph that should wrap across multiple lines without leaving the original English source visible under the new content.");

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
            .FirstOrDefault(candidate => candidate.SourceText.Contains(needle, StringComparison.Ordinal));

        block.Should().NotBeNull();
        return block!;
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
